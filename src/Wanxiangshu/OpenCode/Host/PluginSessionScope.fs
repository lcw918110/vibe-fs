namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
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
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Guidance
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

/// Per-instance session registry state for one plugin instance (HOST-012):
/// owned sessions, companions, verdicts, nudges,
/// quiescence permits and join interrupts. Shared cross-worktree state stays
/// in SharedState; everything here is per-instance and dies with the scope.
type PluginSessionScope
    (journal: Wanxiangshu.Persistence.Journal.AgentJournal option, isModelLeaseExternallyOwned: SessionId -> bool) =
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

    /// C6 item 27: waiters are keyed by BloggerSessionId. When the MAIN is
    /// deleted, the linked Blogger's parked waiter + request slots must be
    /// cancelled too. Returns the keys to cancel (including sessionId itself
    /// when it is not a Main with a linked Blogger).
    member this.LinkedBloggerKeys(sessionId: string) : string list =
        let bloggerKeys (companion: CompanionHost) =
            match companion.BloggerSession with
            | Some bloggerId -> [ SessionId.value bloggerId ]
            | None -> []

        match this.Companions.TryGetValue sessionId with
        | true, companion -> bloggerKeys companion
        | false, _ ->
            // sessionId may itself be a Blogger child being deleted.
            [ sessionId ]

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

    /// managed-session-lifecycle-026: the linked Attached InternalLeaf (Blogger)
    /// sessions of a Main, proven by the durable CompanionBloggerLinked
    /// projection. The process-local Companion registry is a cache; the durable
    /// projection is the existence truth (crash-reconciliation-021).
    member private _.LinkedLeafSessions(sessionId: string) : SessionId list =
        let sid = SessionId.create sessionId

        journal
        |> Option.bind (fun durable ->
            (AgentJournal.snapshot durable).AgentProjections.Sessions
            |> Map.tryFind sid
            |> Option.bind (fun session -> session.Companion)
            |> Option.bind (fun companion -> companion.BloggerSessionId))
        |> Option.filter (fun leaf -> leaf <> sid)
        |> Option.toList

    /// managed-session-lifecycle-026: return a linked leaf's remaining exact
    /// custody. SettleSessionExecutions already returned what it just settled;
    /// an unheld key answers AlreadyApplied, so a repeated close stays a no-op.
    /// HeldForInput is the documented delayed return for a retained
    /// continuation credit; a Conflict is a real ownership violation and is
    /// exposed, never swallowed.
    member private _.ReleaseLeafCustody(leaf: SessionId) : Task =
        let executions =
            journal
            |> Option.map (fun durable ->
                AgentJournal.snapshot durable
                |> fun projection -> projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.current
                |> List.filter (fun execution -> execution.key.SessionId = leaf))
            |> Option.defaultValue []

        task {
            for execution in executions do
                match
                    ModelRouting.releasePhysicalExecution execution.key.SessionId execution.key.PhysicalUserMessageId
                with
                | PhysicalExecutionReleaseOutcome.HeldForInput
                | PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.Applied
                | PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.AlreadyApplied
                | PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.StaleFence -> ()
                | PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.Conflict ->
                    invalidOp (
                        sprintf
                            "managed-session-lifecycle-026: linked leaf exact capacity release was rejected (%s/%s)"
                            (SessionId.value execution.key.SessionId)
                            (PhysicalUserMessageId.value execution.key.PhysicalUserMessageId)
                    )
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

            // managed-session-lifecycle-027: scope close cancels the retained
            // continuation inputs this session owns; the last cancellation
            // completes the delayed exact return of the held credit.
            attemptSync (fun () -> ModelRouting.cancelRetainedInputsForSession (SessionId.create sessionId))

            // managed-session-lifecycle-026: a Main close also settles every
            // execution of its durable linked Attached InternalLeaf before the
            // leaf's registry entries are dropped.
            let leaves =
                try
                    this.LinkedLeafSessions sessionId
                with failure ->
                    remember failure
                    []

            for leaf in leaves do
                do! attempt (fun () -> this.SettleSessionExecutions(SessionId.value leaf))
                attemptSync (fun () -> ModelRouting.cancelRetainedInputsForSession leaf)
                do! attempt (fun () -> this.ReleaseLeafCustody leaf)

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
