namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

/// Optional Host capability bound to exact already-accepted physical material.
/// Absence is not permission to create a replacement PromptClaim or resend text.
type ExactAcceptedMessageRecoveryPort =
    { ResumeAccepted: PreProviderResumeRequest -> Task<bool> }

type SessionRecoveryHost
    (
        journal: AgentJournal,
        snapshot: ISessionSnapshotPort,
        scope: PluginRecoveryScope,
        acceptedMessageRecovery: ExactAcceptedMessageRecoveryPort option
    ) =

    let drainGate = obj ()
    let drainWaiters = Dictionary<string, TaskCompletionSource<unit>>()

    let keyOfStarted (started: ProviderStartedEvidence) : ChatExecutionKey =
        { SessionId = started.Accepted.SessionId
          PhysicalUserMessageId = started.Accepted.PhysicalUserMessageId }

    let eventKey (event: ChatExecutionRecoveryLifecycleEvent) =
        match event with
        | ChatExecutionRecoveryLifecycleEvent.ExactAssistantStarted started -> Some(keyOfStarted started)
        | ChatExecutionRecoveryLifecycleEvent.ExactAssistantTerminal(started, _) -> Some(keyOfStarted started)
        | ChatExecutionRecoveryLifecycleEvent.SessionAborted key -> Some key
        | ChatExecutionRecoveryLifecycleEvent.SessionDeleted key
        | ChatExecutionRecoveryLifecycleEvent.SessionCancelled key -> Some key
        | _ -> None

    let statesFor (event: ChatExecutionRecoveryLifecycleEvent) =
        let projection = (AgentJournal.snapshot journal).AgentProjections.ChatExecutions

        match event with
        | ChatExecutionRecoveryLifecycleEvent.SessionQuiesced sessionId ->
            // provider-attempt-recovery-023: the obligation is exactly `Accepted ∧ ¬ProviderStarted` for
            // that session. Executions that already reached the provider belong to
            // the provider recovery owner (or their own terminal projection), and an
            // idle observation must never re-judge them.
            ChatExecutionProjection.current projection
            |> List.filter (fun state ->
                state.key.SessionId = sessionId
                && state.startedEvidence.IsNone
                && state.terminalDisposition.IsNone)
        | _ ->
            eventKey event
            |> Option.map (fun key -> ChatExecutionProjection.byKey key projection |> Option.toList)
            |> Option.defaultWith (fun () -> ChatExecutionProjection.current projection)

    let classifyProviderSnapshot (state: ChatExecutionState) (messages: SessionMessage list) =
        let matches =
            messages
            |> List.filter (fun message ->
                message.Role = "assistant"
                && message.ParentId = Some(PhysicalUserMessageId.value state.key.PhysicalUserMessageId))

        match matches, state.startedEvidence with
        | [], _ -> ProviderPhysicalObservation.ProviderAbsent state.key
        | [ message ], Some started when
            message.Id = ProviderRunIdentity.value started.ProviderRun
            && not message.Completed
            ->
            ProviderPhysicalObservation.ProviderAlive started
        // ProviderPhysicalObservation.ReceiptAmbiguous -> fail closed to manual intervention
        | _ -> ProviderPhysicalObservation.ReceiptAmbiguous

    let providerFromSnapshot (state: ChatExecutionState) =
        task {
            match! snapshot.GetMessages state.key.SessionId with
            | Error _ -> return ProviderPhysicalObservation.ReceiptMissing
            | Ok messages -> return classifyProviderSnapshot state messages
        }

    let providerObservation (event: ChatExecutionRecoveryLifecycleEvent) (state: ChatExecutionState) =
        match event with
        | ChatExecutionRecoveryLifecycleEvent.ExactAssistantStarted started ->
            Task.FromResult(ProviderPhysicalObservation.ProviderAlive started)
        | ChatExecutionRecoveryLifecycleEvent.ExactAssistantTerminal(started, disposition) ->
            Task.FromResult(ProviderPhysicalObservation.ProviderTerminal(started, disposition))
        | ChatExecutionRecoveryLifecycleEvent.SessionDeleted _ ->
            Task.FromResult(ProviderPhysicalObservation.ProviderAbsent state.key)
        | _ -> providerFromSnapshot state

    let lifecycleCancellation (event: ChatExecutionRecoveryLifecycleEvent) =
        match event with
        | ChatExecutionRecoveryLifecycleEvent.SessionDeleted _
        | ChatExecutionRecoveryLifecycleEvent.SessionCancelled _ -> true
        | _ -> false

    let cancelledProviderDecision (state: ChatExecutionState) (started: ProviderStartedEvidence) =
        ExecutionFailurePolicy.decide
            { Failure = ExecutionFailure.UserCancelled
              Lifecycle = DurableExecutionLifecycle.ProviderStarted
              ExecutionKey = state.key
              Capacity = CapacityOwnership.NoCapacityFence
              Provider =
                { LogicalRun = started.Accepted.LogicalRunId
                  ProviderRun = started.ProviderRun
                  RequestKind = started.RequestKind
                  RetryBudget = ProviderRecoveryBudget.Exhausted
                  Breaker = ProviderBreakerState.Closed } }
        |> RecoveryPolicyEvidence.FailureDecision

    let cancellationFailureEvidence (state: ChatExecutionState) =
        state.startedEvidence
        |> Option.map (cancelledProviderDecision state)
        |> Option.defaultValue RecoveryPolicyEvidence.NoFailureDecision

    let failureEvidence (event: ChatExecutionRecoveryLifecycleEvent) (state: ChatExecutionState) =
        match event with
        | _ when lifecycleCancellation event -> cancellationFailureEvidence state
        | _ -> RecoveryPolicyEvidence.NoFailureDecision

    let eventIsIdleSweep (event: ChatExecutionRecoveryLifecycleEvent) =
        match event with
        | ChatExecutionRecoveryLifecycleEvent.SessionQuiesced _ -> true
        | _ -> false

    let currentState (state: ChatExecutionState) =
        (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
        |> ChatExecutionProjection.byKey state.key
        |> Option.defaultValue state

    let completedLifecycleSettlement
        (state: ChatExecutionState)
        (settlement: Result<PreProviderTerminalWitness, PreProviderSettlementError>)
        =
        match settlement with
        | Ok _ -> currentState state
        | Error error -> raise (InvalidOperationException($"managed chat lifecycle settlement failed: {error}"))

    let settleAcceptedCancellation (event: ChatExecutionRecoveryLifecycleEvent) (state: ChatExecutionState) =
        task {
            match lifecycleCancellation event, state with
            | true, ChatExecutionState.Accepted accepted ->
                let! settled =
                    PreProviderSettlement.settle journal state.key accepted ChatExecutionTerminalDisposition.Cancelled

                let completed = completedLifecycleSettlement state settled
                scope.RevokeManualIntervention state.key
                return completed
            | _ -> return state
        }

    let persistenceCommitment () =
        if AgentJournal.isPoisoned journal then
            PersistenceCommitment.Unknown
        else
            PersistenceCommitment.NotCommitted

    let release (key: ChatExecutionKey) =
        match ModelRouting.releasePhysicalExecution key.SessionId key.PhysicalUserMessageId with
        | CapacityTransitionOutcome.Applied
        | CapacityTransitionOutcome.AlreadyApplied
        | CapacityTransitionOutcome.StaleFence -> Task.FromResult(()) :> Task
        | CapacityTransitionOutcome.Conflict ->
            raise (InvalidOperationException "managed chat recovery exact capacity release was rejected")

    let requirePersistence (label: string) (result: Result<'witness, ManagedChatProviderLifecycleError>) =
        match result with
        | Ok _ -> ()
        | Error error -> raise (InvalidOperationException($"managed chat recovery {label} persistence failed: {error}"))

    let persistStarted (started: ProviderStartedEvidence) =
        let key = keyOfStarted started

        task {
            let! result =
                ManagedChatProviderLifecycle.providerStarted
                    journal
                    key
                    started.Accepted
                    started.ProviderRun
                    started.RequestKind
                    started.ProjectionChoice

            return requirePersistence "start" result
        }
        :> Task

    let persistTerminal
        (key: ChatExecutionKey)
        (terminalEvidence: ChatExecutionTerminalEvidence)
        (disposition: ChatExecutionTerminalDisposition)
        =
        match terminalEvidence with
        | ChatExecutionTerminalEvidence.PreProvider _ ->
            invalidOp "managed chat recovery Finalize requires provider-started terminal evidence"
        | ChatExecutionTerminalEvidence.AfterProviderStart started ->
            task {
                let! result = ManagedChatProviderLifecycle.terminal journal key started disposition

                requirePersistence "terminal" result
                do! release key
                scope.RevokeManualIntervention key
            }
            :> Task

    let reconcile (request: PhysicalReconciliationRequest) =
        match request with
        | PhysicalReconciliationRequest.PersistProviderStarted started -> persistStarted started
        | PhysicalReconciliationRequest.PersistProviderStartedAndTerminal(started, disposition) ->
            task {
                do! persistStarted started

                do!
                    persistTerminal
                        (keyOfStarted started)
                        (ChatExecutionTerminalEvidence.AfterProviderStart started)
                        disposition
            }
            :> Task
        | PhysicalReconciliationRequest.ReleaseTerminalResource(key, _, _) ->
            task {
                do! release key
                scope.RevokeManualIntervention key
            }
            :> Task

    let publishNoAuthorizedDisposition (request: PreProviderResumeRequest) =
        let state = currentState (ChatExecutionState.Accepted request.AcceptedEvidence)

        scope.PublishManualChatIntervention
            { ExecutionState = state
              ProviderObservation = ProviderPhysicalObservation.ProviderAbsent request.ExecutionKey
              ResourceObservation = ModelRouting.observePhysicalResource request.ExecutionKey
              InterventionReason = ManualInterventionReason.NoAuthorizedProviderDisposition }

    let tryResumeAccepted (request: PreProviderResumeRequest) : Task<bool> =
        match acceptedMessageRecovery with
        | Some port -> port.ResumeAccepted request
        | None -> Task.FromResult false

    /// provider-attempt-recovery-023: the idle-sweep decision must be final. When
    /// no typed resume capability takes over the exact accepted material, the
    /// execution settles as a pre-provider terminal failure — it never silently
    /// dangles as Accepted. The manual-intervention fact stays published as the
    /// observable reason the turn failed; the terminal is the decision.
    let settleUnresolvedAsFailed (request: PreProviderResumeRequest) =
        task {
            let current =
                (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey request.ExecutionKey

            match current with
            | Some(ChatExecutionState.Accepted accepted) ->
                let! settled =
                    PreProviderSettlement.settle
                        journal
                        request.ExecutionKey
                        accepted
                        ChatExecutionTerminalDisposition.Failed

                (completedLifecycleSettlement (ChatExecutionState.Accepted accepted) settled)
                |> ignore

                publishNoAuthorizedDisposition request
            | Some _
            | None -> ()
        }

    let handleUnresumedEvent (event: ChatExecutionRecoveryLifecycleEvent) (request: PreProviderResumeRequest) =
        task {
            publishNoAuthorizedDisposition request

            if eventIsIdleSweep event then
                do! settleUnresolvedAsFailed request
        }

    let resumeFor (event: ChatExecutionRecoveryLifecycleEvent) (request: PreProviderResumeRequest) =
        task {
            let! resumed = tryResumeAccepted request

            if not resumed then
                do! handleUnresumedEvent event request
        }
        :> Task

    let finalize (request: TerminalFinalizationRequest) =
        persistTerminal request.ExecutionKey request.TerminalEvidence request.TerminalDisposition

    let resume (request: PreProviderResumeRequest) =
        // Non-sweep callers keep the original semantics: publish the manual
        // intervention fact and leave further settlement to the owner events.
        task {
            let! resumed = tryResumeAccepted request

            if not resumed then
                publishNoAuthorizedDisposition request
        }
        :> Task

    let actions =
        { ReconcilePhysical = reconcile
          ResumePreProvider = resume
          Finalize = finalize
          MarkManualIntervention =
            fun request ->
                scope.PublishManualChatIntervention request
                Task.FromResult(()) :> Task }

    let sessionDrained (sessionId: SessionId) =
        (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
        |> ChatExecutionProjection.current
        |> List.filter (fun state -> state.key.SessionId = sessionId)
        |> List.forall (fun state ->
            state.terminalDisposition.IsSome
            && (match ModelRouting.observePhysicalResource state.key with
                | PhysicalResourceObservation.ResourceAbsent _
                | PhysicalResourceObservation.ResourceReleased _ -> true
                | PhysicalResourceObservation.ResourceHeld _
                | PhysicalResourceObservation.ResourceUnknown _ -> false))

    let takeDrainWaiter (sessionId: SessionId) =
        lock drainGate (fun () ->
            let key = SessionId.value sessionId

            match drainWaiters.TryGetValue key with
            | true, completion ->
                drainWaiters.Remove key |> ignore
                Some completion
            | false, _ -> None)

    let pulseDrain (sessionId: SessionId) =
        if sessionDrained sessionId then
            takeDrainWaiter sessionId
            |> Option.iter (fun completion -> AsyncSupport.trySetResult completion () |> ignore)

    let sessionsToPulse (event: ChatExecutionRecoveryLifecycleEvent) =
        match eventKey event with
        | Some key -> [ key.SessionId ]
        | None -> statesFor event |> List.map (fun state -> state.key.SessionId) |> List.distinct

    let recoverState (event: ChatExecutionRecoveryLifecycleEvent) (state: ChatExecutionState) =
        task {
            let! current = settleAcceptedCancellation event state
            let! provider = providerObservation event current

            let sweepActions =
                { actions with
                    ResumePreProvider = resumeFor event }

            let evidence =
                { ExecutionState = current
                  ProviderObservation = provider
                  ResourceObservation = ModelRouting.observePhysicalResource current.key
                  PersistenceCommitment = persistenceCommitment ()
                  FailureDecisionEvidence = failureEvidence event current }

            // provider-attempt-recovery-023: the idle sweep must decide finally.
            // `resumeFor event` settles an unresumed Accepted execution as a
            // pre-provider terminal failure instead of leaving a silent
            // dangling obligation; other events keep the plain resume port.
            let! _ =
                ChatExecutionRecoveryRuntime.recover (if eventIsIdleSweep event then sweepActions else actions) evidence

            ()
        }

    let waitForDrain (sessionId: SessionId) =
        lock drainGate (fun () ->
            let key = SessionId.value sessionId

            match drainWaiters.TryGetValue key with
            | true, completion -> completion.Task :> Task
            | false, _ ->
                let completion =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                drainWaiters.[key] <- completion
                completion.Task :> Task)

    member _.Signal(event: ChatExecutionRecoveryLifecycleEvent) : Task =
        task {
            for state in statesFor event do
                do! recoverState event state

            sessionsToPulse event |> List.iter pulseDrain
        }
        :> Task

    member this.SignalSession
        (sessionId: SessionId, eventOf: ChatExecutionKey -> ChatExecutionRecoveryLifecycleEvent)
        : Task =
        task {
            let keys =
                (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
                |> ChatExecutionProjection.current
                |> List.choose (fun state ->
                    if state.key.SessionId = sessionId then
                        Some state.key
                    else
                        None)

            for key in keys do
                do! this.Signal(eventOf key)
        }
        :> Task

    member _.Drain(sessionId: SessionId) : Task =
        if sessionDrained sessionId then
            Task.FromResult(()) :> Task
        else
            waitForDrain sessionId

    /// Typed single-resume entry for the JS semantic boundary. Delegates to
    /// the same recovery action ports as Signal-driven recovery; the port
    /// answers remain test inputs owned by the caller.
    member _.ResumePreProvider(request: PreProviderResumeRequest) : Task = actions.ResumePreProvider request

    /// Typed terminal finalization entry for the JS semantic boundary.
    /// Persists the terminal, releases the physical resource and revokes the
    /// manual intervention through the shared recovery ports.
    member _.Finalize(request: TerminalFinalizationRequest) : Task = actions.Finalize request
