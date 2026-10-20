namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Host
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Repository.Programming.Js.OpenCode

/// Per-instance session registry state for one plugin instance (HOST-012):
/// owned sessions, companions, verdicts, nudges,
/// quiescence permits and join interrupts. Shared cross-worktree state stays
/// in SharedState; everything here is per-instance and dies with the scope.
type PluginSessionScope
    (
        journal: Wanxiangshu.Persistence.Journal.AgentJournal option,
        isModelLeaseExternallyOwned: SessionId -> bool,
        jsLifecyclePersistence: IJsLifecyclePersistence option
    ) =
    let releaseInstanceModelLease sessionId =
        let session = SessionId.create sessionId

        if not (isModelLeaseExternallyOwned session) then
            ModelRouting.releaseExecution session |> ignore

    // HOST-012: 跨实例共享（模块级单例）——worktree 独立插件实例的 fork→verdict
    // 链必须读写同一份。每实例独有状态（OwnedSessions、Companions 等）保持
    // per-instance。
    // DSL-MUTABLE: resource — alias to SharedState session directory map.
    member val SessionDirectories = SharedState.SessionDirectories
    // DSL-MUTABLE: resource — per-instance owned session set.
    member val OwnedSessions = HashSet<string>()
    /// execution-model-routing-004: per-plugin-instance routing demands, including a root chat.message
    /// that may block before PromptIngress has had a chance to register ownership.
    /// This is cleanup bookkeeping only, never business/session authority.
    // DSL-MUTABLE: resource — per-instance routing session set.
    member val ModelRoutingSessions = HashSet<string>()
    // DSL-MUTABLE: resource — alias to SharedState session parent map.
    member val SessionParents = SharedState.SessionParents
    // DSL-MUTABLE: resource — per-instance companion registry.
    member val Companions = Dictionary<string, CompanionHost>()
    // DSL-MUTABLE: resource — lock gate object for companion operations.
    member val CompanionGate = obj ()
    // DSL-MUTABLE: single-flight — per-instance nudge sent set.
    member val NudgeSent = HashSet<string>()
    // DSL-MUTABLE: single-flight — per-instance join guard nudge set.
    member val JoinGuardNudges = HashSet<string>()
    // DSL-MUTABLE: resource — per-instance aborted session set.
    // HOST-004: process-local idle-derived continuation admission. Per plugin
    // instance like NudgeSent / LoopSensor; never journalled (HOST-007). A
    // worktree owner transfer starts a fresh gate — no old permit survives.
    // DSL-MUTABLE: resource — per-instance quiescence gate instance.
    member val Quiescence = SessionQuiescenceGate()
    /// EXEC-017: process-local attempt-scoped join registry. External user messages
    /// signal only the CURRENT active JoinAttempt (UserMessageArrived), without
    /// cancelling mailbox/runtime and without a future latch (not journaled).
    // DSL-MUTABLE: resource — per-instance join interrupt registry.
    member val JoinInterrupts: IJoinAttemptRegistry = JoinAttemptRegistry() :> IJoinAttemptRegistry

    member private _.SettleExecution(durable: AgentJournal, execution: ChatExecutionState) : Task =
        task {
            let! settled =
                match execution.startedEvidence with
                | Some started ->
                    ManagedChatProviderLifecycle.terminal
                        durable
                        execution.key
                        started
                        ChatExecutionTerminalDisposition.Cancelled
                    |> TaskValue.map (Result.map ignore >> Result.mapError (sprintf "%A"))
                | None ->
                    PreProviderSettlement.settle
                        durable
                        execution.key
                        execution.acceptedEvidence
                        ChatExecutionTerminalDisposition.Cancelled
                    |> TaskValue.map (Result.map ignore >> Result.mapError (sprintf "%A"))

            settled
            |> Result.defaultWith (fun failure ->
                invalidOp (
                    sprintf
                        "session execution settlement failed (%s/%s): %s"
                        (SessionId.value execution.key.SessionId)
                        (PhysicalUserMessageId.value execution.key.PhysicalUserMessageId)
                        failure
                ))

            ModelRouting.releasePhysicalExecution execution.key.SessionId execution.key.PhysicalUserMessageId
            |> ignore
        }

    member private this.SettleSessionExecutions(sessionId: string) : Task =
        let sid = SessionId.create sessionId
        ModelRouting.cancelUnacquiredExecution sid |> ignore

        let unfinished =
            journal
            |> Option.map (fun durable ->
                AgentJournal.snapshot durable
                |> fun projection -> projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.nonTerminal
                |> List.filter (fun execution -> execution.key.SessionId = sid)
                |> List.map (fun execution -> durable, execution))
            |> Option.defaultValue []

        task {
            for durable, execution in unfinished do
                do! this.SettleExecution(durable, execution)
        }

    /// No-op: language follows the live global preference, so there is no
    /// per-session identity left to drop. Kept as the deletion boundary's
    /// stable call shape.
    member _.DropSessionIdentity(sessionId: string) = ignore sessionId

    /// Session deletion drops every per-instance registry entry for this
    /// session (mirror of DisposeSession's per-session cleanup). Always drops
    /// session identity.
    ///
    /// managed-session-lifecycle-027: teardown is best-effort-complete but
    /// never error-silent — remember the first real failure, continue every
    /// safe independent obligation (Main self, each linked leaf, the
    /// unconditional per-session registry group), rethrow last.
    member this.ClearSession(sessionId: string) : Task =
        task {
            // DSL-MUTABLE: algorithm-scratch — first close failure accumulator.
            let mutable firstFailure: exn option = None

            let remember (failure: exn) =
                firstFailure <- Option.orElse firstFailure (Some failure)

            let attempt (work: unit -> Task) =
                task {
                    try
                        do! work ()
                    with failure ->
                        remember failure
                }

            let attemptSync (work: unit -> unit) =
                try
                    work ()
                with failure ->
                    remember failure

            do! attempt (fun () -> this.SettleSessionExecutions sessionId)

            // Algorithm G3/H3: closing a session reaps every parked site owned
            // by that session (Algorithm F, ABANDONED protocol) so no parked
            // program survives its owner silently. The reap action is owned by
            // lane-a-core (JsParkedSites.reapSession); the durable Reaped
            // audit is recorded here, one event per consumed handle. Both
            // steps share the close's attempt/remember isolation per
            // managed-session-lifecycle-027: an audit failure is remembered
            // and rethrown with the close, never error-silently dropped.
            let reapedHandles = JsParkedSites.handlesForSession sessionId

            do! attempt (fun () -> JsParkedSites.reapSession sessionId)

            let recordReapedAudit (durable: IJsLifecyclePersistence) (handle: string) =
                attempt (fun () ->
                    task {
                        let event: JsLifecycleEvent =
                            { EventType = JsLifecycleEventType.Reaped
                              Handle = handle
                              SessionId = sessionId
                              At = System.DateTimeOffset.UtcNow.ToString("o") }

                        match! durable.AppendLifecycleEvent event with
                        | Ok _ -> ()
                        | Error failure ->
                            raise (
                                InvalidOperationException(
                                    sprintf
                                        "session close failed to record the Reaped audit event for handle %s: %A"
                                        handle
                                        failure
                                )
                            )
                    })

            let recordAllReapedAudits (durable: IJsLifecyclePersistence) =
                task {
                    for handle in reapedHandles do
                        do! recordReapedAudit durable handle
                }

            match jsLifecyclePersistence with
            | None -> ()
            | Some durable -> do! recordAllReapedAudits durable

            // managed-session-lifecycle-027: scope close cancels the retained
            // continuation inputs this session owns; the last cancellation
            // completes the delayed exact return of the held credit.
            attemptSync (fun () -> ModelRouting.cancelRetainedInputsForSession (SessionId.create sessionId))

            match this.Companions.TryGetValue sessionId with
            | true, companion ->
                this.Companions.Remove sessionId |> ignore
                attemptSync (fun () -> (companion :> IDisposable).Dispose())
            | false, _ -> ()

            attemptSync (fun () -> this.OwnedSessions.Remove sessionId |> ignore)
            attemptSync (fun () -> this.ModelRoutingSessions.Remove sessionId |> ignore)
            attemptSync (fun () -> this.SessionParents.Remove sessionId |> ignore)
            attemptSync (fun () -> this.SessionDirectories.Remove sessionId |> ignore)
            let sid = SessionId.create sessionId

            attemptSync (fun () -> this.DropSessionIdentity sessionId)

            // HOST-004 Q-10: a deleted session's idle permits die forever.
            attemptSync (fun () -> this.Quiescence.DropSession sid)
            // SessionDeleted: drop join-interrupt waiters + one-shot user-message latch.
            attemptSync (fun () -> this.JoinInterrupts.ClearSession sid)

            firstFailure |> Option.iter raise
        }

    /// Instance disposal releases local leases, not leases owned by a shared coordinator.
    member this.Dispose() =
        for companion in this.Companions.Values |> Seq.toList do
            (companion :> IDisposable).Dispose()

        this.Companions.Clear()

        let routed =
            Seq.append this.ModelRoutingSessions this.OwnedSessions
            |> Seq.distinct
            |> Seq.toArray

        for sessionId in routed do
            releaseInstanceModelLease sessionId

        this.ModelRoutingSessions.Clear()
        this.OwnedSessions.Clear()
