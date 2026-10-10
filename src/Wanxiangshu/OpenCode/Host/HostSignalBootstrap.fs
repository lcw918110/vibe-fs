namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Handle.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git.Hook
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Concern
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

module HostSignalBootstrap =

    /// Neutral Strength ports built by plugin composition (`PluginStrengthPorts`)
    /// from the already-held `PluginStrengthScope` and durability handle.
    /// The Host boundary never names Strength types; it only invokes these.
    /// `None` means that Strength aspect is absent (same as before: no scope,
    /// no durability, or no replica runtime attached at wire time).
    type StrengthHostPorts =
        { HandlePreTurn: (ReconciledTurn -> Task<bool>) option
          ObservePrimaryTurn: (ReconciledTurn -> Task<unit>) option
          CancelStrengthOwner: (SessionId -> unit) option
          OnSessionDeleted: (SessionId -> unit) option }

    type internal ChatAdmissionHookFailure =
        | IntentRejected of ChatAdmissionIntent.Rejection
        | TransactionFailed of ChatAdmissionTransactionError
        | TransactionStopped of ChatAdmissionTransactionOutcome

    type internal ChatAdmissionHookException(failure: ChatAdmissionHookFailure, executionKey: ChatExecutionKey option) =
        inherit Exception(sprintf "Managed chat admission failed: %A" failure)
        member _.Failure = failure
        member _.ExecutionKey = executionKey

    [<RequireQualifiedAccess>]
    type private ChatAdmissionFlightPhase =
        | Running
        | Finished

    type private ChatAdmissionPurpose =
        | PublishInput
        | EnterProviderStep

    type private ChatAdmissionFlight =
        { Work: Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>
          Outputs: ResizeArray<obj>
          Phase: ChatAdmissionFlightPhase ref }

    /// What the composition root needs back from `wire`.
    ///
    /// Exactly the members `SpikePlugin` calls. Six more used to hang here —
    /// `Reconciler`, `SignalRouter`, `Subscription`, `UnregisterOwned`,
    /// `RegisterSource`, `BindUserMessage` — with no consumer anywhere: the
    /// subscription is already tracked by the scope inside `wire`, and the three
    /// functions are called internally by the binding helpers. Handing them out as
    /// well made the signal stack look like it had six more entry points than it does.
    type WiredSignals =
        { RegisterOwned: string -> unit
          CancelSignals: SessionId seq -> unit
          BindActiveRun: SessionId -> Role -> string option -> unit
          CurrentPhysicalUserMessage: string -> string option
          ConfirmProviderStarted: ExactProviderStartObservation -> Task
          ChatMessageHook: obj
          ObserveEvent: obj -> Task<unit>
          EnsureVisibleInputAdmission: obj -> Task<unit> }

    let wire
        (observeTurnWorkflow: AbortCause -> ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (snapshotOpt: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (strengthPorts: StrengthHostPorts)
        (scope: PluginRuntimeScope)
        (rootWorkspace: IRootWorkspaceReader)
        (input: obj)
        /// Exact process-local private-agent attachment; it cannot establish a public authority profile.
        (tryConsumeHostInternalPrompt: SessionId -> string option -> string option -> bool)
        /// Exact assistant terminal evidence for private agents outside public PromptAuthority.
        (observeHostInternalTerminal: ExactProviderTerminalObservation -> unit)
        /// Workspace root for graceful Casebook finalize (SpikePlugin → CasebookLifecycle).
        (workspaceDirectory: string option)
        /// Owner-scope graceful close: finalize inspector draft once (root → delegateSessionId).
        /// Returns a Task so SessionDeleted can await CaseFinalize before CancelSession.
        (tryFinalizeDraft: (string -> string -> Task<CaseFinalizeSettlement>) option)
        /// Unexpected / residual draft cleanup (delegateSessionId).
        (cleanupDraft: (string -> unit) option)
        : Task<WiredSignals> =
        task {
            let finalizeDelegate =
                defaultArg tryFinalizeDraft (fun _ delegateSessionId ->
                    Task.FromResult(CaseFinalizeSettlement.nothingToFinalize delegateSessionId))

            let cleanupDelegateDraft = defaultArg cleanupDraft (fun _ -> ())

            let snapshot =
                match snapshotOpt with
                | Some port -> port
                | None ->
                    { new ISessionSnapshotPort with
                        member _.GetMessages _ =
                            Task.FromResult(Ok([]: SessionMessage list)) }

            journal
            |> Option.iter (fun durable ->
                let recovery = SessionRecoveryHost(durable, snapshot, scope.Recovery, None)
                scope.AttachChatRecoveryRuntime recovery

                scope.AttachDurabilityActivation(fun () ->
                    scope.RunBackground(fun () ->
                        scope.SignalChatRecovery ChatExecutionRecoveryLifecycleEvent.DurabilityActivated)))

            // Host visibility catch-up still owns a Node timer backstop. Provider
            // recovery itself is causal and no longer uses a wall-clock deadline.
            let recoveryTimerPort = NodeTiming.nodeTimerPort ()

            // host-boundary-008: projection catch-up wakes on the session's
            // message.updated signal; recoveryTimerPort supplies the backstop.
            let messageVisibility = MessageVisibilityHub(recoveryTimerPort)
            scope.AttachMessageVisibility messageVisibility


            let resolveProjection (sessionId: SessionId) : AgentProjectionSet option =
                match journal with
                | None -> None
                | Some j -> Some((AgentJournal.snapshot j).AgentProjections)

            let binding = TurnBinding.Store()

            let onTurn =
                HostTurnObserver.observe
                    observeTurnWorkflow
                    sessionPort
                    rootWorkspace
                    eventPort
                    journal
                    strengthPorts.HandlePreTurn
                    strengthPorts.ObservePrimaryTurn
                    scope

            let compactionProbe: CompactionProbe =
                { TryClaimStartupProbe = (fun () -> scope.TryClaimStartupProbe())
                  ReadCompactionSettingGap = (fun () -> scope.CompactionSettingGap)
                  IsStartupProbeOpen = (fun () -> scope.IsStartupProbeOpen) }

            let onSnapshot = HostCompactionObserver.observe compactionProbe journal

            let reconciler =
                Reconciler.Scheduler(
                    snapshot,
                    binding,
                    onTurn,
                    ?projection = Some resolveProjection,
                    ?onSnapshot = Some onSnapshot,
                    ?durableUnavailable = Some(fun () -> journal |> Option.exists AgentJournal.isPoisoned)
                )

            do scope.TrackReconcileShutdown(fun () -> reconciler.StopAndDrain())

            /// provider-attempt-recovery-003: no Host signal may name the failed ProviderRun.
            ///
            /// `ProviderFailure` and `ProviderRetry` used to run their own writers here
            /// — a second and third writer of the durable failure budget, each deciding from
            /// event fields whether an attempt had failed. Both are gone: the
            /// reconciled snapshot supplies exact run identity, and ProviderFailureLedger
            /// performs the admission. ProviderFailure contributes only failure finality;
            /// Scheduler freezes the current physical identity at signal admission and
            /// reconciliation must match it to the snapshot assistant before publishing.
            let tryObserveCurrentIdle key attempt =
                if ModelRouting.wasExecutionSuperseded key then
                    None
                else
                    scope.Sessions.Quiescence.ObserveIdleFor attempt

            let observeQuiescedAssistant sessionId attempt (assistant: SessionMessage) =
                let key: ChatExecutionKey =
                    { SessionId = sessionId
                      PhysicalUserMessageId = PhysicalUserMessageId.create assistant.ParentId.Value }

                task {
                    match tryObserveCurrentIdle key attempt with
                    | None -> ()
                    | Some permit ->
                        scope.LoopSensor.ResetDetector sessionId
                        reconciler.SignalIdle(sessionId, permit)
                        do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.PhysicalExecutionQuiesced key)
                }

            let observeIdleSnapshot sessionId attempt =
                task {
                    match! snapshot.GetMessages sessionId with
                    | Error _ -> ()
                    | Ok messages ->
                        let observed =
                            ProviderRunBinding.quiescedRun messages
                            |> Result.toOption
                            |> Option.map (observeQuiescedAssistant sessionId attempt)
                            |> Option.defaultWith (fun () -> Task.FromResult(()))

                        do! observed
                }

            let onSignal (signal: HostSignal) =
                match signal with
                | SessionIdle sessionId ->
                    reconciler.Signal signal
                    let attempt = scope.Sessions.Quiescence.CaptureCurrentAttempt sessionId

                    scope.RunBackground(fun () -> observeIdleSnapshot sessionId attempt)
                | ProviderRetry _
                | ProviderFailure _ -> reconciler.Signal signal
                // Session-only abort is a wake. Only an exact current cancelled
                // assistant below may revoke resources.
                | AttemptAborted _ -> reconciler.Signal signal
                | SessionDeleted(sessionId, parentSessionIdOpt) ->
                    let deletion = HostSessionDeletion.prepare scope sessionId parentSessionIdOpt

                    scope.RunBackground(fun () ->
                        task {
                            do!
                                HostSessionDeletion.finalizePreparedDelegate
                                    scope
                                    workspaceDirectory
                                    finalizeDelegate
                                    deletion

                            ProviderAttemptStopFence.shared.Revoke sessionId

                            do!
                                scope.SignalChatRecoverySession
                                    sessionId
                                    ChatExecutionRecoveryLifecycleEvent.SessionDeleted

                            do! scope.DrainChatRecovery sessionId

                            do!
                                HostSessionDeletion.handle
                                    scope
                                    cleanupDelegateDraft
                                    reconciler.Signal
                                    sessionId
                                    strengthPorts.OnSessionDeleted
                                    deletion
                        })


            // LOOP-002/006 and HOST-027 share one raw Host subscription but own
            // disjoint stream fields. Both abort physically; only their typed armed
            // marks decide the later reconciled-turn meaning.
            HostTurnObserver.attachLoopSensor sessionPort rootWorkspace journal scope Diagnostic.emit

            let exactStarted (key: ChatExecutionKey) : ProviderStartedEvidence option =
                journal
                |> Option.bind (fun durable ->
                    AgentJournal.snapshot durable
                    |> fun projection -> projection.AgentProjections.ChatExecutions
                    |> ChatExecutionProjection.byKey key
                    |> Option.bind _.startedEvidence)

            let rejectProviderTerminal (observation: ExactProviderTerminalObservation) =
                Diagnostic.emit
                    "provider-terminal-evidence-mismatch"
                    [ "session_id", SessionId.value observation.SessionId
                      "physical_user_message_id", PhysicalUserMessageId.value observation.PhysicalUserMessageId ]

            let applyObservedTerminal
                (observation: ExactProviderTerminalObservation)
                (evidence: ProviderStartedEvidence)
                (disposition: ChatExecutionTerminalDisposition)
                =
                task {
                    do!
                        scope.SignalChatRecovery(
                            ChatExecutionRecoveryLifecycleEvent.ExactAssistantTerminal(evidence, disposition)
                        )

                    reconciler.NotifyProjectionChanged(observation.SessionId, observation.PhysicalUserMessageId)

                    FissionHost.observePhysicalExecutionEnd
                        reconciler.TryPhysicalUserMessage
                        journal
                        (fun sid -> reconciler.Kick(sid, ReconcileProgram.ReconcileWake.RetryWake))
                        observation.SessionId
                        observation.PhysicalUserMessageId
                }

            let startedEvidenceForTerminal (observation: ExactProviderTerminalObservation) =
                let key =
                    { SessionId = observation.SessionId
                      PhysicalUserMessageId = observation.PhysicalUserMessageId }

                exactStarted key

            let settleExactTerminal (observation: ExactProviderTerminalObservation) =
                let key: ChatExecutionKey =
                    { SessionId = observation.SessionId
                      PhysicalUserMessageId = observation.PhysicalUserMessageId }

                match observation.Outcome, ModelRouting.tryReadExecution key with
                | HostProviderTerminalOutcome.Cancelled _, Some _ ->
                    ProviderAttemptStopFence.shared.Observe(observation.SessionId, observation.ProviderRun)
                    scope.Sessions.Quiescence.RevokeCurrentAttempt observation.SessionId

                    FissionHost.routeAttemptAborted observation.SessionId ignore (fun () ->
                        strengthPorts.CancelStrengthOwner
                        |> Option.iter (fun cancel -> cancel observation.SessionId))
                | _ -> ()

                match observation.Outcome, observation.Disposition, startedEvidenceForTerminal observation with
                | HostProviderTerminalOutcome.ProviderFailure failure, None, _ ->
                    // provider-attempt-recovery-022: the exact terminal projection IS the Host's
                    // attempt-stop observation. A missing durable start fact must not
                    // suppress the fence or the failure wake, or the recovery
                    // dispatch would wait forever on a fence nothing observes.
                    ProviderAttemptStopFence.shared.Observe(observation.SessionId, observation.ProviderRun)

                    reconciler.Kick(
                        observation.SessionId,
                        ReconcileProgram.ReconcileWake.FailureWake(
                            Some observation.PhysicalUserMessageId,
                            failure,
                            "exact-provider-terminal",
                            ReconcileProgram.FailureWakeSource.ExactAssistantProjection
                        )
                    )

                    Task.FromResult()
                | _, Some disposition, Some evidence -> applyObservedTerminal observation evidence disposition
                | HostProviderTerminalOutcome.Cancelled _, Some ChatExecutionTerminalDisposition.Cancelled, None ->
                    task { do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.SessionCancelled key) }
                | _ ->
                    rejectProviderTerminal observation
                    Task.FromResult()

            let settleObservedTerminal (terminal: ExactProviderTerminalObservation option) =
                terminal
                |> Option.map settleExactTerminal
                |> Option.defaultValue (Task.FromResult())

            let continueStartedLifecycle
                (started: ExactProviderStartObservation)
                (providerStepEnded: bool)
                (terminal: ExactProviderTerminalObservation option)
                =
                task {
                    if providerStepEnded then
                        ModelRouting.endProviderStep started.SessionId started.PhysicalUserMessageId started.ProviderRun

                    do! settleObservedTerminal terminal
                }

            let rejectProviderStart
                (started: ExactProviderStartObservation)
                (error: ProviderLifecycle.ProviderStartObservationError<unit>)
                =
                Diagnostic.emit
                    "provider-start-observation-rejected"
                    [ "session_id", SessionId.value started.SessionId
                      "physical_user_message_id", PhysicalUserMessageId.value started.PhysicalUserMessageId
                      "provider_run", ProviderRunIdentity.value started.ProviderRun
                      "reason", ProviderLifecycle.providerStartObservationErrorCode error ]

            let signalProviderStarted (started: ExactProviderStartObservation) =
                let key =
                    { SessionId = started.SessionId
                      PhysicalUserMessageId = started.PhysicalUserMessageId }

                exactStarted key
                |> Option.map (
                    ChatExecutionRecoveryLifecycleEvent.ExactAssistantStarted
                    >> scope.SignalChatRecovery
                )
                |> Option.defaultValue (Task.FromResult() :> Task)

            let signalNewProviderStart started providerStarted =
                if providerStarted then
                    signalProviderStarted started
                else
                    Task.FromResult() :> Task

            let confirmProviderStarted (started: ExactProviderStartObservation) =
                let key: ChatExecutionKey =
                    { SessionId = started.SessionId
                      PhysicalUserMessageId = started.PhysicalUserMessageId }

                if ModelRouting.wasExecutionSuperseded key then
                    Task.FromResult() :> Task
                else
                    ModelRouting.rememberProviderStepIdentity
                        started.SessionId
                        started.PhysicalUserMessageId
                        started.ProviderRun

                    reconciler.BindPhysicalUserMaterial(started.SessionId, started.PhysicalUserMessageId)
                    signalProviderStarted started

            let continueProviderStart
                (started: ExactProviderStartObservation)
                (providerStepEnded: bool)
                (terminal: ExactProviderTerminalObservation option)
                (persistence: Result<bool, ProviderLifecycle.ProviderStartObservationError<unit>>)
                =
                match persistence with
                | Error error ->
                    // provider-attempt-recovery-022: the durable start fact only owns the plan
                    // binding. Its rejection must not suppress the Host's exact
                    // attempt-stop observation below, otherwise the recovery dispatch
                    // waits forever on a fence nothing observes.
                    rejectProviderStart started error
                    (continueStartedLifecycle started providerStepEnded terminal :> Task)
                | Ok providerStarted ->
                    task {
                        reconciler.BindPhysicalUserMaterial(started.SessionId, started.PhysicalUserMessageId)
                        do! signalNewProviderStart started providerStarted
                        do! continueStartedLifecycle started providerStepEnded terminal
                    }
                    :> Task

            let onLoopEvent raw = scope.LoopSensor.Observe raw

            let signalRouter =
                HostSignalRouter(
                    scope.Sessions.OwnedSessions,
                    onSignal,
                    onLoopEvent = onLoopEvent,
                    onExactAssistantObservation =
                        (fun
                            (started: ExactProviderStartObservation)
                            (providerStepEnded: bool)
                            (terminal: ExactProviderTerminalObservation option) ->
                            task {
                                // The Host itself stated this assistant run's parentID,
                                // so this is the one authoritative moment the exact
                                // run→physical relation becomes knowable. Every later
                                // consumer (tool boundary, step end, diagnostics) reads
                                // it from here instead of guessing a session-current
                                // binding, and a durable-fact rejection below cannot
                                // make the Host's relation untrue.
                                ModelRouting.rememberProviderStepIdentity
                                    started.SessionId
                                    started.PhysicalUserMessageId
                                    started.ProviderRun

                                let! providerStarted =
                                    ProviderLifecycle.persistProviderStartedFromObservation
                                        journal
                                        scope.TryBindAttemptPlan
                                        started

                                do! continueProviderStart started providerStepEnded terminal providerStarted
                            })
                )

            let! subscriptionResult = HostSignalSubscribe.trySubscribe input signalRouter.Observe

            let subscription: IDisposable option =
                match subscriptionResult with
                | Error error ->
                    let evidence = sprintf "%A" error
                    Diagnostic.fatal "signal-subscribe-failed" [ "result", evidence ]
                    raise (InvalidOperationException evidence)

                | Ok HostSignalSubscribe.HostSignalSubscriptionMode.LocalEventHook -> None
                | Ok(HostSignalSubscribe.HostSignalSubscriptionMode.EventsListen active) -> Some(active :> IDisposable)

            do scope.TrackSubscription subscription

            let registerOwned (sessionId: string) =
                if not (String.IsNullOrWhiteSpace sessionId) then
                    scope.Sessions.OwnedSessions.Add sessionId |> ignore
                    let sid = SessionId.create sessionId
                    signalRouter.RegisterOwned sid

            let bindUserMessage (sessionId: string) (messageId: string) =
                if
                    not (String.IsNullOrWhiteSpace sessionId)
                    && not (String.IsNullOrWhiteSpace messageId)
                then
                    let sid = SessionId.create sessionId
                    let physical = PhysicalUserMessageId.create messageId

                    let agentRole =
                        HostSessionNudge.tryActiveProfile journal sid
                        |> Option.map (fun profile -> profile.CanonicalRole)

                    reconciler.BindUserMessage(sid, physical, ?agentRole = agentRole)
                    registerOwned sessionId

            let bindContinuationMessage (sessionId: string) (messageId: string) =
                if
                    not (String.IsNullOrWhiteSpace sessionId)
                    && not (String.IsNullOrWhiteSpace messageId)
                then
                    reconciler.BindContinuationUserMessage(
                        SessionId.create sessionId,
                        PhysicalUserMessageId.create messageId
                    )

            let bindHumanContinuationMessage (sessionId: string) (messageId: string) =
                if
                    not (String.IsNullOrWhiteSpace sessionId)
                    && not (String.IsNullOrWhiteSpace messageId)
                then
                    bindContinuationMessage sessionId messageId

            let bindActiveRun (sessionId: SessionId) (role: Role) (directory: string option) =
                let key = SessionId.value sessionId
                registerOwned key

                // A host-registered run knows its physical opening message; the
                // Authority Root is derived from it by PROMPT-002 promotion rather than
                // read out of a second binding table.
                let physical = reconciler.TryPhysicalUserMessage sessionId

                reconciler.BindActiveRun
                    { SessionId = sessionId
                      RunId = None
                      AuthorityRootUserMessageId = physical |> Option.map PhysicalUserMessageId.promoteToAuthorityRoot
                      PhysicalUserMessageId = physical
                      ContinuationMessageIds = Set.empty
                      Role = Some role
                      Directory = directory }

            let admissionTransaction =
                journal
                |> Option.map (fun durable ->
                    let runtime = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

                    ChatAdmissionTransaction.production durable (fun managed ->
                        runtime.AcceptManagedChatIntent(ChatAdmissionIntent.ofManaged managed)))

            let durabilityActivation =
                lazy
                    (match workspaceDirectory with
                     | None -> Ok()
                     | Some workspace -> HookDispatcher.ensure workspace)

            let requireDurabilityActivation () =
                match durabilityActivation.Value with
                | Ok() -> scope.ActivateDurability()
                | Error error -> Diagnostic.fatal "durability-activation-failed" [ "result", error ]

            let observePhysicalAdmission output sessionId physicalId =
                scope.Sessions.Quiescence.ObservePhysicalUserMessage(sessionId, physicalId)

                reconciler.BindPhysicalUserMaterial(sessionId, physicalId)

            /// Durable topology evidence for this session's parent edge: a
            /// parent-visible handle or a Companion association. It answers "is
            /// this a parented session" across a restart, where the Host query
            /// may be unavailable, without keeping a second cache of the answer.
            let durableParentOf (sessionId: SessionId) : string option =
                journal
                |> Option.bind (fun durable ->
                    let projections = (AgentJournal.snapshot durable).AgentProjections

                    let fromHandle =
                        projections.HandleByChildSession
                        |> Map.tryFind sessionId
                        |> Option.filter (fun record -> record.Ownership = HandleOwnership.DurableParentHandle)
                        |> Option.bind (fun record ->
                            projections.Sessions
                            |> Map.toList
                            |> List.tryPick (fun (parentSessionId, session) ->
                                match session.Handles with
                                | Some handles when HandleProjection.tryFind record.Handle handles |> Option.isSome ->
                                    Some(SessionId.value parentSessionId)
                                | _ -> None))

                    let fromCompanion =
                        SessionAssociationProjection.tryMainSessionOf sessionId projections.Associations
                        |> Option.map SessionId.value

                    fromHandle |> Option.orElse fromCompanion)
                |> Option.filter (String.IsNullOrWhiteSpace >> not)

            let rememberParent key parentId =
                scope.Sessions.SessionParents.[key] <- parentId

            let discoverHostParent (sessionId: SessionId) (key: string) : Task =
                task {
                    match! sessionPort.TryGetParentSession sessionId with
                    | Ok(Some parentId) -> rememberParent key (SessionId.value parentId)
                    | _ -> ()
                }

            let rememberDurableParent (sessionId: SessionId) (key: string) =
                durableParentOf sessionId
                |> Option.iter (fun parent -> rememberParent key parent)

            let discoverMissingParent (sessionId: SessionId) (key: string) : Task =
                if scope.Sessions.SessionParents.ContainsKey key then
                    Task.FromResult()
                else
                    discoverHostParent sessionId key

            let ensurePhysicalParentDiscovered (sessionId: SessionId) =
                task {
                    let key = SessionId.value sessionId

                    if not (scope.Sessions.SessionParents.ContainsKey key) then
                        rememberDurableParent sessionId key
                        do! discoverMissingParent sessionId key
                }

            let hasPhysicalParent sessionId =
                scope.Sessions.SessionParents.ContainsKey(SessionId.value sessionId)
                || (durableParentOf sessionId).IsSome

            let continueUnmanagedChatMessage () = requireDurabilityActivation ()


            let continueManagedChatMessage intent output =
                match intent with
                | ChatAdmissionIntent.Decision.ExternalRootIntent evidence ->
                    let sid = SessionId.value evidence.Key.SessionId
                    let pid = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId

                    scope.Sessions.ModelRoutingSessions.Add sid |> ignore
                    bindUserMessage sid pid
                    registerOwned sid
                | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent evidence ->
                    let sessionId = SessionId.value evidence.Key.SessionId
                    let physicalId = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId

                    scope.Sessions.ModelRoutingSessions.Add sessionId |> ignore
                    bindHumanContinuationMessage sessionId physicalId
                    registerOwned sessionId
                | ChatAdmissionIntent.Decision.PendingPromptIntent evidence ->
                    let sessionId = SessionId.value evidence.Key.SessionId
                    let physicalId = PhysicalUserMessageId.value evidence.Key.PhysicalUserMessageId

                    scope.Sessions.ModelRoutingSessions.Add sessionId |> ignore
                    bindContinuationMessage sessionId physicalId
                    registerOwned sessionId
                | ChatAdmissionIntent.Decision.AcceptedInputIntent evidence ->
                    let sessionId = SessionId.value evidence.SessionId
                    let physicalId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId

                    scope.Sessions.ModelRoutingSessions.Add sessionId |> ignore
                    bindContinuationMessage sessionId physicalId
                    registerOwned sessionId
                | _ -> ()

                FissionHostRequestProjection.projectPendingManaged hasPhysicalParent intent output
                requireDurabilityActivation ()

            let observeVisibleChatMessage raw =
                match HostEventEnvelope.tryVisibleUserMessage raw with
                | None -> ()
                | Some(sessionId, physicalId) ->
                    let key =
                        { SessionId = sessionId
                          PhysicalUserMessageId = physicalId }

                    resolveProjection sessionId
                    |> Option.bind (fun projection -> ChatExecutionProjection.byKey key projection.ChatExecutions)
                    |> Option.filter (fun state ->
                        state.terminalDisposition.IsNone
                        && (ModelRouting.ownsExecutionAdmission key
                            || (journal
                                |> Option.bind (fun durable ->
                                    SessionExecutionBinding.tryContinuationAdmission durable state.acceptedEvidence)
                                |> Option.isSome)))
                    |> Option.iter (fun state ->
                        JoinWake.observeAcceptedMessage scope.Sessions.JoinInterrupts state.acceptedEvidence)

            let executionKey intent =
                match intent with
                | ChatAdmissionIntent.Decision.ExternalRootIntent evidence ->
                    Some
                        { SessionId = evidence.Key.SessionId
                          PhysicalUserMessageId = evidence.Key.PhysicalUserMessageId }
                | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent evidence ->
                    Some
                        { SessionId = evidence.Key.SessionId
                          PhysicalUserMessageId = evidence.Key.PhysicalUserMessageId }
                | ChatAdmissionIntent.Decision.PendingPromptIntent evidence ->
                    Some
                        { SessionId = evidence.Key.SessionId
                          PhysicalUserMessageId = evidence.Key.PhysicalUserMessageId }
                | ChatAdmissionIntent.Decision.AcceptedInputIntent evidence ->
                    Some
                        { SessionId = evidence.SessionId
                          PhysicalUserMessageId = evidence.PhysicalUserMessageId }
                | ChatAdmissionIntent.Decision.NoManagedExecution _
                | ChatAdmissionIntent.Decision.HostInternal _
                | ChatAdmissionIntent.Decision.Reject _ -> None

            // managed-chat-execution-004 / P4: same exact key concurrent
            // admissions merge into one in-process flight. Durable accept
            // idempotence and the ModelRouting lock/queue deduplication stay the
            // underlying guarantee; this table only coalesces the orchestration
            // layer, where two parallel transactions would race Host projection
            // and one failure's compensation could release the lease the other
            // still uses. It holds no identity, persists nothing, never keeps a
            // lock across capacity waiting, and no same-key serial work waits on
            // it.
            let admissionInFlight = Dictionary<ChatExecutionKey, ChatAdmissionFlight>()

            let admissionSequence = Dictionary<SessionId, Task>()

            let removeAdmissionFlight key flight =
                lock admissionInFlight (fun () ->
                    match admissionInFlight.TryGetValue key with
                    | true, registered when obj.ReferenceEquals(registered.Work, flight) ->
                        admissionInFlight.Remove(key) |> ignore
                    | _ -> ())

            let projectCommittedAdmission key output =
                let lease =
                    ModelRouting.tryReadExecution key
                    |> Option.defaultWith (fun () -> invalidOp "managed admission has no exact committed lease")

                ModelRouting.projectHostModel output (ModelRouting.toOpenCodeModel lease.Identity.Target)
                |> Result.defaultWith raise

            let completeAdmission intent output result =
                match result with
                | Ok(ChatAdmissionTransactionOutcome.Settled witness) ->
                    let evidence = ManagedChatAcceptanceWitness.evidence witness

                    let key =
                        { SessionId = evidence.SessionId
                          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

                    projectCommittedAdmission key output
                    continueManagedChatMessage intent output
                | Ok(ChatAdmissionTransactionOutcome.DeferredInput(_, target)) ->
                    ModelRouting.projectHostModel output (ModelRouting.toOpenCodeModel target)
                    |> Result.defaultWith raise

                    continueManagedChatMessage intent output
                | Ok outcome -> raise (ChatAdmissionHookException(TransactionStopped outcome, executionKey intent))
                | Error error ->
                    executionKey intent |> Option.iter ModelRouting.cancelContinuationInput
                    raise (ChatAdmissionHookException(TransactionFailed error, executionKey intent))

            let priorExecutions durable (key: ChatExecutionKey) =
                (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                |> ChatExecutionProjection.current
                |> List.filter (fun state -> state.key.SessionId = key.SessionId && state.key <> key)

            let settleSupersededAdmissions durable key =
                let superseded =
                    priorExecutions durable key
                    |> List.filter (fun state ->
                        state.terminalDisposition.IsNone
                        && ModelRouting.wasExecutionSuperseded state.key)

                task {
                    for state in superseded do
                        do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.SessionSuperseded state.key)
                }

            let beginAdmissionTurn sessionId =
                lock admissionInFlight (fun () ->
                    let preceding =
                        match admissionSequence.TryGetValue sessionId with
                        | true, previous -> previous
                        | _ -> Task.FromResult() :> Task

                    let completion =
                        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                    admissionSequence.[sessionId] <- completion.Task
                    preceding, completion)

            let releaseAdmissionTurn sessionId (completion: TaskCompletionSource<unit>) =
                AsyncSupport.trySetResult completion () |> ignore

                lock admissionInFlight (fun () ->
                    match admissionSequence.TryGetValue sessionId with
                    | true, current when obj.ReferenceEquals(current, completion.Task) ->
                        admissionSequence.Remove sessionId |> ignore
                    | _ -> ())

            let runProjectionTurn sessionId (operation: unit -> Task<'value>) =
                let preceding, completion = beginAdmissionTurn sessionId

                task {
                    try
                        do! preceding
                        return! operation ()
                    finally
                        releaseAdmissionTurn sessionId completion
                }

            let projectAdmissionOutput key result extra =
                match result with
                | Ok(ChatAdmissionTransactionOutcome.DeferredInput(_, target)) ->
                    ModelRouting.projectHostModel extra (ModelRouting.toOpenCodeModel target)
                    |> Result.defaultWith raise
                | _ -> projectCommittedAdmission key extra

            let finishProjection
                (phase: ChatAdmissionFlightPhase ref)
                intent
                output
                key
                (outputs: ResizeArray<obj>)
                result
                =
                try
                    completeAdmission intent output result

                    outputs
                    |> Seq.filter (fun requestOutput -> not (obj.ReferenceEquals(requestOutput, output)))
                    |> Seq.iter (projectAdmissionOutput key result)
                finally
                    phase.Value <- ChatAdmissionFlightPhase.Finished

            let projectCurrentAdmission durable key witness finish operation =
                task {
                    if ModelRouting.wasExecutionSuperseded key then
                        let! settled = ManagedChatSupersession.settle durable key

                        return
                            settled
                            |> Result.map (fun () -> ChatAdmissionTransactionOutcome.Superseded witness)
                            |> Result.mapError ChatAdmissionTransactionError.SupersessionSettlementFailed
                    else
                        let! result = operation ()
                        finish result
                        return result
                }

            let handoffAcquisition durable key admission =
                task {
                    try
                        do! settleSupersededAdmissions durable key
                    with error ->
                        raise (ChatAdmissionLeaseHandoffException(error, admission))
                }

            let observeQueuedAcquisition onQueued =
                function
                | ExecutionAdmissionAcquisition.Queued _ -> onQueued ()
                | _ -> ()

            let acquireAndHandoff
                durable
                (key: ChatExecutionKey)
                purpose
                (ports: ChatAdmissionTransactionPorts)
                onQueued
                witness
                =
                task {
                    let! acquired =
                        match
                            purpose,
                            SessionExecutionBinding.tryContinuationAdmission
                                durable
                                (ManagedChatAcceptanceWitness.evidence witness)
                        with
                        | ChatAdmissionPurpose.EnterProviderStep, Some previous ->
                            task {
                                try
                                    let! acquired =
                                        ModelRouting.continueExecutionAdmission previous key.PhysicalUserMessageId

                                    return Ok acquired
                                with error ->
                                    return Error error
                            }
                        | _ -> ports.Acquire witness

                    match acquired with
                    | Ok(ExecutionAdmissionAcquisition.Admitted _ as admission)
                    | Ok(ExecutionAdmissionAcquisition.Queued _ as admission) ->
                        do! handoffAcquisition durable key admission
                        observeQueuedAcquisition onQueued admission
                    | _ -> ()

                    return acquired
                }

            let finishUnsettledAdmission intent output =
                function
                | Ok(ChatAdmissionTransactionOutcome.Settled _) -> ()
                | result -> completeAdmission intent output result

            let startAdmissionFlight
                durable
                createTransaction
                purpose
                intent
                managed
                (key: ChatExecutionKey)
                output
                (outputs: ResizeArray<obj>)
                (phase: ChatAdmissionFlightPhase ref)
                =
                let preceding, completion = beginAdmissionTurn key.SessionId
                // DSL-MUTABLE: resource — this admission released ingress while awaiting its capacity grant.
                let mutable waitingForCapacity = false

                let onQueued () =
                    waitingForCapacity <- true
                    releaseAdmissionTurn key.SessionId completion

                let operation () =
                    task {
                        do! preceding

                        let ports: ChatAdmissionTransactionPorts =
                            createTransaction (ModelRouting.projectHostModel output)

                        let finish = finishProjection phase intent output key outputs

                        let withLeaseOwner witness projection =
                            let project () =
                                projectCurrentAdmission durable key witness finish projection

                            if waitingForCapacity then
                                runProjectionTurn key.SessionId project
                            else
                                project ()

                        let tryInputTarget witness =
                            match purpose with
                            | ChatAdmissionPurpose.EnterProviderStep -> None
                            | ChatAdmissionPurpose.PublishInput ->
                                SessionExecutionBinding.tryContinuationAdmission
                                    durable
                                    (ManagedChatAcceptanceWitness.evidence witness)
                                |> Option.map (fun lease ->
                                    ModelRouting.retainContinuationInput
                                        lease
                                        (ManagedChatAcceptanceWitness.key witness))

                        let! result =
                            ChatAdmissionTransaction.executeWithLeaseOwner
                                ignore
                                withLeaseOwner
                                tryInputTarget
                                { ports with
                                    Acquire = acquireAndHandoff durable key purpose ports onQueued }
                                managed

                        match result with
                        | Ok(ChatAdmissionTransactionOutcome.DeferredInput _) -> finish result
                        | _ -> finishUnsettledAdmission intent output result

                        return result
                    }

                task {
                    try
                        return! operation ()
                    finally
                        phase.Value <- ChatAdmissionFlightPhase.Finished
                        releaseAdmissionTurn key.SessionId completion
                }

            let admissionFlight durable createTransaction purpose intent managed (key: ChatExecutionKey) output =
                lock admissionInFlight (fun () ->
                    match admissionInFlight.TryGetValue key with
                    | true, existing when existing.Phase.Value = ChatAdmissionFlightPhase.Running ->
                        existing.Outputs.Add output
                        existing.Work
                    | _ ->
                        let outputs = ResizeArray<obj>()
                        outputs.Add output
                        // DSL-MUTABLE: resource — registration remains open until this flight finishes Host projection.
                        let phase = ref ChatAdmissionFlightPhase.Running

                        let started =
                            startAdmissionFlight
                                durable
                                createTransaction
                                purpose
                                intent
                                managed
                                key
                                output
                                outputs
                                phase

                        admissionInFlight.[key] <-
                            { Work = started
                              Outputs = outputs
                              Phase = phase }

                        started)

            let admitManagedChatMessage durable createTransaction purpose intent output =
                let managed =
                    ChatAdmissionIntent.tryManaged intent
                    |> Option.defaultWith (fun () ->
                        invalidArg "intent" "managed chat transaction requires a managed intent")

                let key = ChatAdmissionIntent.managedKey managed

                let flight =
                    admissionFlight durable createTransaction purpose intent managed key output

                task {
                    try
                        let! _ = flight
                        ()
                    finally
                        removeAdmissionFlight key flight
                }

            let rejectedChatMessage failure =
                let completion =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                completion.SetException(ChatAdmissionHookException(failure, None))
                completion.Task

            let continueClassifiedChatMessage intent output =
                match intent, journal, admissionTransaction with
                | ChatAdmissionIntent.Decision.NoManagedExecution _, _, _
                | ChatAdmissionIntent.Decision.HostInternal _, _, _ ->
                    continueUnmanagedChatMessage ()
                    Task.FromResult()
                | ChatAdmissionIntent.Decision.Reject rejection, _, _ -> rejectedChatMessage (IntentRejected rejection)
                | ChatAdmissionIntent.Decision.ExternalRootIntent _, Some durable, Some createTransaction ->
                    admitManagedChatMessage durable createTransaction ChatAdmissionPurpose.PublishInput intent output
                | ChatAdmissionIntent.Decision.PendingPromptIntent _, Some durable, Some createTransaction ->
                    admitManagedChatMessage durable createTransaction ChatAdmissionPurpose.PublishInput intent output
                | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent _, Some durable, Some createTransaction ->
                    admitManagedChatMessage durable createTransaction ChatAdmissionPurpose.PublishInput intent output
                | ChatAdmissionIntent.Decision.AcceptedInputIntent _, Some durable, Some createTransaction ->
                    admitManagedChatMessage
                        durable
                        createTransaction
                        ChatAdmissionPurpose.EnterProviderStep
                        intent
                        output
                | ChatAdmissionIntent.Decision.ExternalRootIntent _, _, _
                | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent _, _, _
                | ChatAdmissionIntent.Decision.PendingPromptIntent _, _, _ ->
                    rejectedChatMessage (IntentRejected ChatAdmissionIntent.Rejection.DurableAuthorityUnavailable)
                | ChatAdmissionIntent.Decision.AcceptedInputIntent _, _, _ ->
                    rejectedChatMessage (IntentRejected ChatAdmissionIntent.Rejection.DurableAuthorityUnavailable)

            let includedInput durable (selected: AcceptedChatExecutionEvidence) message =
                let info = ProviderWireDecode.infoObject message

                ProviderWireDecode.hostMessageId message
                |> Option.filter (fun _ ->
                    ProviderWireDecode.firstString info [ "role" ] = Some "user"
                    && ProviderWireDecode.firstString info [ "sessionID"; "sessionId" ] = Some(
                        SessionId.value selected.SessionId
                    ))
                |> Option.map (fun physical ->
                    { SessionId = selected.SessionId
                      PhysicalUserMessageId = PhysicalUserMessageId.create physical })
                |> Option.bind (fun key ->
                    (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                    |> ChatExecutionProjection.byKey key)
                |> Option.bind (function
                    | ChatExecutionState.Accepted earlier when
                        earlier.PhysicalUserMessageId <> selected.PhysicalUserMessageId
                        && earlier.LogicalRunId = selected.LogicalRunId
                        && earlier.AuthorityRootUserMessageId = selected.AuthorityRootUserMessageId
                        && earlier.IdentitySeed = selected.IdentitySeed
                        && (earlier.Origin = PromptOrigin.Continuation PromptContinuationKind.HumanMessage
                            || earlier.Origin = PromptOrigin.Continuation PromptContinuationKind.BusyAgentNudge)
                        ->
                        let key =
                            { SessionId = earlier.SessionId
                              PhysicalUserMessageId = earlier.PhysicalUserMessageId }

                        if ModelRouting.ownsExecutionAdmission key then
                            None
                        else
                            Some key
                    | _ -> None)

            let settleIncludedInput durable (selected: AcceptedChatExecutionEvidence) key =
                task {
                    match! ManagedChatSupersession.settle durable key with
                    | Ok() -> ModelRouting.cancelContinuationInput key
                    | Error error ->
                        raise (
                            ChatAdmissionHookException(
                                TransactionFailed(ChatAdmissionTransactionError.SupersessionSettlementFailed error),
                                Some
                                    { SessionId = selected.SessionId
                                      PhysicalUserMessageId = selected.PhysicalUserMessageId }
                            )
                        )
                }

            let settleIncludedInputs durable selected messages =
                task {
                    for key in messages |> List.choose (includedInput durable selected) do
                        do! settleIncludedInput durable selected key
                }

            let admitContinuationInput durable createTransaction physicalId projection messages sessionId =
                task {
                    let key: ChatExecutionKey =
                        { SessionId = sessionId
                          PhysicalUserMessageId = physicalId }

                    let state =
                        (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                        |> ChatExecutionProjection.byKey key

                    match state with
                    | Some state when
                        state.terminalDisposition.IsNone
                        && (ModelRouting.tryReadExecution key |> Option.isNone)
                        && (SessionExecutionBinding.tryContinuationAdmission durable state.acceptedEvidence
                            |> Option.isSome)
                        ->
                        do!
                            admitManagedChatMessage
                                durable
                                createTransaction
                                ChatAdmissionPurpose.EnterProviderStep
                                (ChatAdmissionIntent.Decision.AcceptedInputIntent state.acceptedEvidence)
                                projection

                        do! settleIncludedInputs durable state.acceptedEvidence messages
                    | _ -> ()
                }

            let admitVisibleMessage durable createTransaction physicalId messages message =
                task {
                    let info = ProviderWireDecode.infoObject message
                    let projection = createObj [ "message" ==> info; "parts" ==> message?parts ]
                    let decoded = PromptIngressCodec.decodeWith info projection

                    match decoded.InvalidIdentityCarrier, decoded.SessionId with
                    | None, Some sessionId ->
                        do! admitContinuationInput durable createTransaction physicalId projection messages sessionId
                    | _ -> ()
                }

            let ensureVisibleInputAdmission (output: obj) =
                task {
                    let messages = ProviderWireDecode.messagesFromTransformOutput output

                    match journal, admissionTransaction, ProviderWireCapture.lastUserMessageId messages with
                    | Some durable, Some createTransaction, Some physicalId ->
                        return!
                            messages
                            |> List.tryFind (fun message ->
                                ProviderWireDecode.hostMessageId message = Some(PhysicalUserMessageId.value physicalId))
                            |> Option.map (admitVisibleMessage durable createTransaction physicalId messages)
                            |> Option.defaultWith (fun () -> Task.FromResult())
                    | _ -> ()
                }

            // concern-routing-001: a session owns a mailbox named by its
            // stable Byname (or the reserved `root` when it has none). The
            // reserved `user` address belongs to the human and is never a
            // session's own name. The subscription is idempotent, so a repeated
            // physical admission is a no-op once the live mailbox exists; a
            // name already owned by another session is silently skipped.
            let selfMailboxName (sessionId: SessionId) projections =
                let raw =
                    Map.tryFind sessionId projections.HandleByChildSession
                    |> Option.map (fun record -> record.Byname)
                    |> Option.defaultValue ""

                let candidate = if isNull raw then "" else raw.Trim()

                if candidate.Length = 0 || candidate = ReservedAddress.User then
                    ReservedAddress.Root
                else
                    candidate

            let subscribeSelfMailbox (durable: AgentJournal) (sessionId: SessionId) name state =
                task {
                    match ConcernProjection.subscribe sessionId (SessionId.value sessionId) name name state with
                    | Ok(Some fact) ->
                        let port = AgentJournalPortAdapter.forConcern durable
                        let! _ = port.Append sessionId None fact
                        return ()
                    | _ -> return ()
                }

            let ensureSelfMailboxFor (durable: AgentJournal) (sessionId: SessionId) =
                task {
                    let projections = (AgentJournal.snapshot durable).AgentProjections
                    let name = selfMailboxName sessionId projections
                    let state = projections.Concern

                    if (ConcernProjection.activeMailbox name state).IsNone then
                        do! subscribeSelfMailbox durable sessionId name state
                }

            let ensureSelfMailbox (sessionId: SessionId) =
                match journal with
                | None -> Task.FromResult(())
                | Some durable -> ensureSelfMailboxFor durable sessionId

            let chatMessageHook =
                fun (input: obj) (output: obj) ->
                    task {
                        requireDurabilityActivation ()

                        // Decode once; routing and physical authority consume
                        // the same frozen claim and identity evidence.
                        let decoded = PromptIngressCodec.decodeWith input output

                        let intent =
                            match decoded.SessionId with
                            | Some sessionId when
                                tryConsumeHostInternalPrompt sessionId decoded.ExplicitAgent decoded.Text
                                ->
                                ChatAdmissionIntent.Decision.HostInternal
                                    { SessionId = decoded.SessionId
                                      PhysicalUserMessageId = decoded.PhysicalUserMessageId
                                      Origin = PromptAuthority.PromptOrigin.HostInternal }
                            | _ -> PromptIngress.resolveDecision journal decoded

                        match decoded.SessionId with
                        | Some sessionId -> do! ensurePhysicalParentDiscovered sessionId
                        | None -> ()

                        match decoded.SessionId with
                        | Some sessionId -> do! ensureSelfMailbox sessionId
                        | None -> ()

                        // intra-participant-parallelism-013: request-local origin
                        // narrowing is independent of business-root admission. In
                        // particular, crash-reconciliation-018 explicit /continue still performs a
                        // provider turn even though it deliberately skips managed admission.
                        FissionHostRequestProjection.projectExternalManaged hasPhysicalParent intent output

                        match decoded.SessionId, decoded.PhysicalUserMessageId with
                        | Some sessionId, Some physicalId ->
                            // HOST-004 / crash-reconciliation-006: physical admission itself closes
                            // the previous terminal's idle-send window. Waiting until
                            // messages.transform leaves a race where an old idle repair
                            // can enqueue after this message is accepted and supersede
                            // its model-routing lease before chat.params.
                            observePhysicalAdmission output sessionId physicalId
                        | _ -> ()

                        do! continueClassifiedChatMessage intent output
                    }

            let cancelSignals (ids: SessionId seq) =
                ids
                |> Seq.iter (fun id ->
                    scope.LoopSensor.DropSession id
                    signalRouter.UnregisterOwned id)

            return
                { RegisterOwned = registerOwned
                  CancelSignals = cancelSignals
                  BindActiveRun = bindActiveRun
                  CurrentPhysicalUserMessage =
                    (fun sessionId ->
                        reconciler.TryPhysicalUserMessage(SessionId.create sessionId)
                        |> Option.map PhysicalUserMessageId.value)
                  ChatMessageHook = chatMessageHook
                  EnsureVisibleInputAdmission = ensureVisibleInputAdmission
                  ConfirmProviderStarted = confirmProviderStarted
                  ObserveEvent =
                    (fun raw ->
                        task {
                            observeVisibleChatMessage raw

                            HostEventCodec.tryDecodeExactProviderTerminal raw
                            |> Option.iter observeHostInternalTerminal

                            do! signalRouter.ObserveLocal raw
                            SyncDelegateHostObservation.observe scope.SyncDelegateRuntime raw
                            MessageVisibilitySignal.observeEvent messageVisibility raw
                        }) }
        }
