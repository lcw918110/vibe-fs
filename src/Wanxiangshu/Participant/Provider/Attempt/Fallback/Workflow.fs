namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Confirmed-failure recovery has one strategy.
/// The first confirmed failure of a provider retries that provider as-is.
/// A later confirmed failure of that same provider blacklists it, then retries.
/// Ambiguous acceptance is not a retry and not a success.
module ProviderRecoveryWorkflow =

    /// The request kind a confirmed failure continues with.
    ///
    /// The durable ProviderStarted fact is the evidence. An execution that is
    /// durably Accepted without it still names its ordinary kind through the
    /// accepted origin. A satellite session without that fact does not get a
    /// guessed kind.
    let requestKindFor
        (durable: AgentJournal)
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        : ProviderRequestKind option =
        let projection = AgentJournal.snapshot durable

        projection.AgentProjections.ChatExecutions
        |> ChatExecutionProjection.byKey
            { SessionId = sessionId
              PhysicalUserMessageId = physicalUserMessageId }
        |> Option.bind (fun execution ->
            match execution.startedEvidence with
            | Some started -> Some started.RequestKind
            | None when SessionAssociationProjection.isSatellite sessionId projection.AgentProjections.Associations ->
                None
            | None -> Some(AttemptPlanner.ordinaryRequestKind execution.origin))

    let private notifyFailure (eventPort: IEventObservationPort) (turn: ReconciledTurn) (reason: string) =
        eventPort.NotifyTerminal
            turn.SessionId
            (TerminalOutcome.Failed(TerminalStop.forAuthority turn.AuthorityRootUserMessageId reason))
        |> ignore

    let private recoveryGateKind (authorization: ProviderRecoveryAuthorization) =
        "provider-recovery:" + authorization.DecisionId.Value

    let private recoveryAlreadyAdmitted
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (authorization: ProviderRecoveryAuthorization)
        =
        match HostSessionNudge.tryActiveProfile (Some durable) turn.SessionId with
        | None -> false
        | Some profile ->
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create durable))
                .GateNudgeAlreadyAdmitted
                profile
                PromptAuthority.ContinuationKind.ProviderRetryAttempt
                (recoveryGateKind authorization)
                authorization.ProviderRun

    let private observeRecoveryAcceptance
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (authorization: ProviderRecoveryAuthorization)
        (observer: ContinuationAcceptanceObserver option)
        =
        match HostSessionNudge.tryActiveProfile (Some durable) turn.SessionId, observer with
        | Some profile, Some observer when profile.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId ->
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create durable))
                .ObserveGateNudgeAcceptance
                profile
                PromptAuthority.ContinuationKind.ProviderRetryAttempt
                (recoveryGateKind authorization)
                authorization.ProviderRun
                observer
        | _ -> ()

    let private observeWorkContinuation
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (authorization: ProviderRecoveryAuthorization)
        (observer: ContinuationAcceptanceObserver option)
        (error: string)
        =
        function
        | HostSessionNudge.GateContinuationOutcome.Sent _
        | HostSessionNudge.GateContinuationOutcome.AlreadyAdmitted ->
            observeRecoveryAcceptance durable turn authorization observer
            RetryVerdict.Dispatched
        | HostSessionNudge.GateContinuationOutcome.Retired -> RetryVerdict.Superseded
        | HostSessionNudge.GateContinuationOutcome.Failed _ -> RetryVerdict.Terminal error

    /// True only when this failed attempt is itself the verbatim retry of the same provider.
    /// The durable facts are the continuation kind and the provider run that established
    /// ProviderStarted. A failure ordinal and a clock are not evidence.
    let failureIsSameProviderRetry
        (durable: AgentJournal)
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        : bool =
        let kind =
            (AgentJournalPortAdapter.forTurnObservation durable).TryContinuationKind sessionId physicalUserMessageId

        let establishedRun =
            (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
            |> ChatExecutionProjection.byKey
                { SessionId = sessionId
                  PhysicalUserMessageId = physicalUserMessageId }
            |> Option.bind _.startedEvidence
            |> Option.map (fun started -> started.ProviderRun)

        kind = Some PromptAuthority.ContinuationKind.ProviderRetryAttempt
        && establishedRun = Some providerRun

    /// Apply the only recovery step to the failed provider.
    /// First confirmed failure keeps the provider for a verbatim retry.
    /// A confirmed failure of that retry blacklists the provider.
    let private settleFailedAttemptTarget (durable: AgentJournal) (turn: ReconciledTurn) =
        match
            ProviderAttemptRecovery.next (
                failureIsSameProviderRetry durable turn.SessionId turn.PhysicalUserMessageId turn.ProviderRun
            )
        with
        | ProviderRecoveryStep.RetrySameProvider ->
            ModelRouting.retainFailedTargetForRetry turn.SessionId turn.ProviderRun
            |> ignore
        | ProviderRecoveryStep.BlacklistProviderThenRetry -> ModelRouting.condemnFailedTarget turn.ProviderRun |> ignore

    let private continueSameProvider
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (observer: ContinuationAcceptanceObserver option)
        (authorization: ProviderRecoveryAuthorization)
        (continuationPrompt: string)
        (error: string)
        : Task<RetryVerdict> =
        task {
            match HostSessionNudge.tryActiveProfile (Some durable) turn.SessionId with
            | Some profile when profile.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId ->
                let! continuation =
                    HostSessionNudge.trySendGateContinuationObserved
                        sessionPort
                        rootWorkspace
                        turn.SessionId
                        continuationPrompt
                        PromptAuthority.ContinuationKind.ProviderRetryAttempt
                        turn.Directory
                        (Some durable)
                        (recoveryGateKind authorization)
                        authorization.ProviderRun
                        observer

                return observeWorkContinuation durable turn authorization observer error continuation
            | _ -> return RetryVerdict.Superseded
        }

    let private redispatchAfterFailure
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (continuationPrompt: string)
        (failureReason: string)
        (observer: ContinuationAcceptanceObserver option)
        (authorization: ProviderRecoveryAuthorization)
        (input: RetryAttempt)
        : Task<RetryVerdict> =
        task {
            let admitted = recoveryAlreadyAdmitted durable input.Turn authorization

            if admitted then
                observeRecoveryAcceptance durable input.Turn authorization observer

            if not admitted then
                settleFailedAttemptTarget durable input.Turn

            let! hostStopped = ProviderAttemptStopFence.shared.AwaitStop(input.Turn.SessionId, input.Turn.ProviderRun)

            match admitted, hostStopped with
            | true, _
            | false, false -> return RetryVerdict.Superseded
            | false, true ->
                return!
                    continueSameProvider
                        sessionPort
                        rootWorkspace
                        durable
                        input.Turn
                        observer
                        authorization
                        continuationPrompt
                        failureReason
        }

    let private admitAuthorizedFailure
        (durable: AgentJournal)
        (ownerSessionId: SessionId)
        (authorization: ProviderRecoveryAuthorization)
        (error: string)
        =
        let port =
            Wanxiangshu.Composition.Durable.AgentJournalPortAdapter.forProviderFailure durable

        ProviderFailureLedger.recordAuthorizedFailure port ownerSessionId authorization error

    let private currentFailure
        (projections: ProjectionSet)
        (ownerSessionId: SessionId)
        (requestKind: ProviderRequestKind)
        =
        let failureState =
            AgentProjection.tryFind ownerSessionId projections.AgentProjections
            |> Option.bind _.ProviderFailures

        ProviderFailureEvidence.currentState failureState
        |> Option.map (fun current -> ownerSessionId, requestKind, current)

    let retryConfirmedFailure
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (failure: ExecutionFailure)
        (continuationPrompt: string)
        (error: string)
        (ownerSessionId: SessionId)
        (requestKind: ProviderRequestKind)
        (current: ProviderFailureProjection)
        : Task =
        task {
            let! verdict =
                Retry.attempt
                    { Admit = fun authorization -> admitAuthorizedFailure durable ownerSessionId authorization error
                      Redispatch =
                        redispatchAfterFailure sessionPort rootWorkspace durable continuationPrompt error None }
                    { Turn = turn
                      Failure = failure
                      OwnerSession = ownerSessionId
                      RequestKind = requestKind
                      Current = current
                      Error = error }

            match verdict with
            | RetryVerdict.Dispatched
            | RetryVerdict.Superseded -> ()
            | RetryVerdict.Terminal reason -> notifyFailure eventPort turn reason
        }
        :> Task

    let private continueDurableFailure
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (failure: ExecutionFailure)
        (continuationPrompt: string)
        (error: string)
        : Task =
        task {
            let projections = AgentJournal.snapshot durable

            let activeProfileOpt =
                PromptAuthorityProjectionQueries.activeProfile turn.SessionId projections.AgentProjections

            let roleName =
                activeProfileOpt
                |> Option.map (fun profile -> Roles.roleLabel profile.CanonicalRole)
                |> Option.defaultValue ""

            let hasCapacity =
                roleName = ""
                || ModelRouting.hasTheoreticalCapacity roleName Wanxiangshu.OpenCode.ModelExecutionPurpose.Normal

            let refusedSatellite =
                SessionAssociationProjection.isSatellite turn.SessionId projections.AgentProjections.Associations

            let recoveryContext =
                requestKindFor durable turn.SessionId turn.PhysicalUserMessageId
                |> Option.bind (fun requestKind -> currentFailure projections turn.SessionId requestKind)
                |> Option.orElseWith (fun () ->
                    if refusedSatellite then
                        None
                    else
                        activeProfileOpt
                        |> Option.bind (fun _ ->
                            currentFailure projections turn.SessionId ProviderRequestKind.WorkMain))

            match hasCapacity, recoveryContext with
            | false, _ -> notifyFailure eventPort turn "All candidate providers exhausted (zero capacity)"
            | true, None -> notifyFailure eventPort turn error
            | true, Some(ownerSessionId, requestKind, current) ->
                return!
                    retryConfirmedFailure
                        sessionPort
                        rootWorkspace
                        eventPort
                        durable
                        turn
                        failure
                        continuationPrompt
                        error
                        ownerSessionId
                        requestKind
                        current
        }

    /// Confirmed provider failure handling.
    ///
    /// The reconciled snapshot proves the attempt failed, so this is where the
    /// failure budget is recorded. The recovery step is the only continuation.
    let continueAfterConfirmedFailure
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (turn: ReconciledTurn)
        (failure: ExecutionFailure)
        (error: string)
        (continuationPrompt: string)
        : Task =
        task {
            match journal with
            | None -> notifyFailure eventPort turn error
            | Some durable ->
                return!
                    continueDurableFailure
                        sessionPort
                        rootWorkspace
                        eventPort
                        durable
                        turn
                        failure
                        continuationPrompt
                        error
        }
        :> Task

    /// Retry plug for one dedicated delegate child.
    /// A single confirmed attempt failure never reaches the caller as itself.
    let continueDelegateCallAfterConfirmedFailure
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (observer: ContinuationAcceptanceObserver option)
        (failure: ExecutionFailure)
        (error: string)
        : Task<RetryVerdict> =
        task {
            let projections = AgentJournal.snapshot durable

            let roleName =
                PromptAuthorityProjectionQueries.activeProfile turn.SessionId projections.AgentProjections
                |> Option.map (fun profile -> Roles.roleLabel profile.CanonicalRole)
                |> Option.defaultValue ""

            let hasCapacity =
                roleName = ""
                || ModelRouting.hasTheoreticalCapacity roleName Wanxiangshu.OpenCode.ModelExecutionPurpose.Normal

            let recoveryContext =
                requestKindFor durable turn.SessionId turn.PhysicalUserMessageId
                |> Option.bind (fun requestKind -> currentFailure projections turn.SessionId requestKind)

            match hasCapacity, recoveryContext with
            | false, _ -> return RetryVerdict.Terminal "All candidate providers exhausted (zero capacity)"
            | true, None -> return RetryVerdict.Terminal error
            | true, Some(ownerSessionId, requestKind, current) ->
                return!
                    Retry.attempt
                        { Admit = fun authorization -> admitAuthorizedFailure durable ownerSessionId authorization error
                          Redispatch =
                            redispatchAfterFailure
                                sessionPort
                                rootWorkspace
                                durable
                                (ProviderProse.documentFor turn.SessionId RuntimeNudge.ProviderRetry Map.empty)
                                error
                                observer }
                        { Turn = turn
                          Failure = failure
                          OwnerSession = ownerSessionId
                          RequestKind = requestKind
                          Current = current
                          Error = error }
        }
