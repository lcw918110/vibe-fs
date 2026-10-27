namespace Wanxiangshu.OpenCode

open Wanxiangshu.Execution.Routing

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
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

module private JoinAttemptLibrary =
    [<Import("joinAttemptRegistry", "wanxiangli/handle-lifecycle")>]
    let joinAttemptRegistry: obj = jsNative

open JoinAttemptLibrary

[<AttachMembers>]
type private HostJoinAttempt(interrupt: JoinInterrupt, unregister: unit -> unit) =
    member _.Wait: Task<JoinInterruptReason> = interrupt.Wait
    member _.SignalOperatorAbort() = interrupt.Signal JoinInterruptReason.OperatorAbort
    member _.SignalUserMessage() = interrupt.Signal JoinInterruptReason.UserMessageArrived
    member _.SignalDeadline() = interrupt.Signal JoinInterruptReason.DeadlineExpired
    interface IDisposable with
        member _.Dispose() = unregister ()

[<AttachMembers>]
type private HostJoinAttempts() =
    let activeInterrupts = Dictionary<string, JoinInterrupt>()

    member _.Begin(sessionId: SessionId, toolCallId: ToolCallId option) : obj =
        let sid = SessionId.value sessionId
        let aid =
            match toolCallId with
            | Some tid -> ToolCallId.value tid
            | None -> Guid.NewGuid().ToString("N")
        joinAttemptRegistry?acquire(sid, aid) |> ignore
        let completion = TaskCompletionSource<JoinInterruptReason>()
        let interrupt =
            { Wait = completion.Task
              Signal = fun reason -> AsyncSupport.trySetResult completion reason |> ignore }
        activeInterrupts.[sid] <- interrupt
        box (new HostJoinAttempt(interrupt, (fun () ->
            joinAttemptRegistry?release(sid, aid) |> ignore
            activeInterrupts.Remove sid |> ignore)))

    member _.SignalVisibleUserMessage(sessionId: SessionId, messageId: PhysicalUserMessageId) =
        let sid = SessionId.value sessionId
        let mid = PhysicalUserMessageId.value messageId
        let verdict: obj = joinAttemptRegistry?signalVisibleUserMessage(sid, mid)
        if not (isNull verdict) && unbox<bool> verdict?woken then
            match activeInterrupts.TryGetValue sid with
            | true, interrupt -> interrupt.Signal JoinInterruptReason.UserMessageArrived
            | false, _ -> ()

    member _.ClearSession(sessionId: SessionId) =
        let sid = SessionId.value sessionId
        joinAttemptRegistry?clearSession(sid) |> ignore
        activeInterrupts.Remove sid |> ignore

module HostJoinAttemptFactory =
    let create () : obj = box (new HostJoinAttempts())

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
    // DSL-MUTABLE: single-flight — per-instance nudge sent set.
    member val NudgeSent = HashSet<string>()
    // DSL-MUTABLE: single-flight — per-instance join guard nudge set.
    member val JoinGuardNudges = HashSet<string>()
    // DSL-MUTABLE: resource — per-instance aborted session set.
    // HOST-004: process-local idle-derived continuation admission. Per plugin
    // instance like NudgeSent; never journalled (HOST-007). A
    // worktree owner transfer starts a fresh gate — no old permit survives.
    // DSL-MUTABLE: resource — per-instance quiescence gate instance.
    member val Quiescence = SessionQuiescenceGate()
    /// EXEC-017: process-local attempt-scoped join registry. External user messages
    /// signal only the CURRENT active JoinAttempt (UserMessageArrived), without
    /// cancelling mailbox/runtime and without a future latch (not journaled).
    // DSL-MUTABLE: resource — per-instance join interrupt registry.
    member val JoinInterrupts: obj = box (new HostJoinAttempts()) with get, set

    /// EXEC-017: a visible user message wakes the CURRENT active join attempt.
    /// The adapter-facing scope owns the call so the host boundary never opens
    /// the delegation handle namespace itself.
    member this.ObserveAcceptedMessage(evidence: AcceptedChatExecutionEvidence) =
        match evidence.Origin with
        | PromptOrigin.AuthorityRoot _
        | PromptOrigin.Continuation _ ->
            this.JoinInterrupts?SignalVisibleUserMessage(evidence.SessionId, evidence.PhysicalUserMessageId) |> ignore
        | PromptOrigin.HostInternal
        | PromptOrigin.UnknownOrigin -> ()

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

    /// Drops the provider-language identity and purges the bound DevOps model
    /// target entry from the model routing acceleration layer for this session.
    member _.DropSessionIdentity(sessionId: string) =
        ModelRouting.dropSession (SessionId.create sessionId)

    member private _.RecordProgramReaped(durable: IJsLifecyclePersistence, sessionId: string, name: string) : Task =
        task {
            let event: JsLifecycleEvent =
                { EventType = JsLifecycleEventType.Reaped
                  Handle = name
                  SessionId = sessionId
                  At = DateTimeOffset.UtcNow.ToString("o") }

            match! durable.AppendLifecycleEvent event with
            | Ok _ -> ()
            | Error failure ->
                invalidOp (
                    sprintf "session close failed to record the Reaped audit event for program %s: %A" name failure
                )
        }

    member private this.RecordProgramsReaped
        (durable: IJsLifecyclePersistence, sessionId: string, names: string list)
        : Task =
        task {
            // DSL-MUTABLE: algorithm-scratch — retain the first failure while auditing every confirmed close.
            let mutable firstFailure: exn option = None

            let record name =
                task {
                    try
                        do! this.RecordProgramReaped(durable, sessionId, name)
                    with failure ->
                        firstFailure <- Option.orElse firstFailure (Some failure)
                }

            for name in names do
                do! record name

            firstFailure |> Option.iter raise
        }

    member private this.CloseProgramsForSession(sessionId: string) : Task =
        task {
            let! names = JsPrograms.closeSession sessionId

            match jsLifecyclePersistence with
            | None -> ()
            | Some durable -> do! this.RecordProgramsReaped(durable, sessionId, names)
        }

    /// Close only this plugin instance's programs before releasing its journal.
    member this.ClosePrograms() : Task =
        task {
            let owned =
                Seq.append this.OwnedSessions this.ModelRoutingSessions
                |> Seq.distinct
                |> Seq.toArray

            // DSL-MUTABLE: algorithm-scratch — retain failure while draining independent sessions.
            let mutable firstFailure: exn option = None

            let close sessionId =
                task {
                    try
                        do! this.CloseProgramsForSession sessionId
                    with failure ->
                        firstFailure <- Option.orElse firstFailure (Some failure)
                }

            for sessionId in owned do
                do! close sessionId

            firstFailure |> Option.iter raise
        }

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

            do! attempt (fun () -> this.CloseProgramsForSession sessionId)

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
            attemptSync (fun () -> this.JoinInterrupts?ClearSession sid |> ignore)

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
