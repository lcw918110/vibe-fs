namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Execution.Routing
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Confirmed-failure recovery has one strategy.
/// The first confirmed failure of a provider retries that provider as-is.
/// A later confirmed failure of that same provider blacklists it, then retries.
/// Ambiguous acceptance is not a retry and not a success.
module ProviderRecoveryWorkflow =

    [<Import("providerAttemptStopFence", "wanxiangli/failure")>]
    let private providerAttemptStopFence: obj = jsNative

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

    /// Manager, Orchestrator and Plan own open work. When automatic retry gives up on a
    /// transient provider failure (retry budget exhausted, failed redispatch), the turn
    /// ended but the work did not: such a role gets its own goal-directed continuation,
    /// never a terminal stop. Short-lived roles keep the terminal failure.
    let longWorkContinuationPath (role: Role option) (failure: ExecutionFailure) : string option =
        match failure, role with
        | ExecutionFailure.ProviderTransient, Some Role.Manager -> Some "work/manager-continue"
        | ExecutionFailure.ProviderTransient, Some Role.Plan -> Some "work/plan-continue"
        | ExecutionFailure.ProviderTransient, Some Role.Orchestrator -> Some "work/orchestrator-continue"
        | _ -> None

    /// A late failure of a run that has already closed durably is stale: the
    /// work it belongs to is finished. Silence requires the full durable
    /// association — no active run, the session's last authority profile
    /// naming this turn's authority root, and a recorded closure for exactly
    /// that run. A missing active profile alone proves nothing; without the
    /// closure the failure stays real.
    let private sameRunAlreadyClosed (durable: AgentJournal) (turn: ReconciledTurn) : bool =
        let agentProjections = (AgentJournal.snapshot durable).AgentProjections

        match PromptAuthorityProjectionQueries.projectionFor turn.SessionId agentProjections with
        | Some authority when authority.ActiveLogicalRun.IsNone ->
            PromptAuthorityProjectionQueries.lastAuthorityProfile turn.SessionId agentProjections
            |> Option.exists (fun profile ->
                profile.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId
                && (PromptAuthorityProjectionQueries.closedRun turn.SessionId profile.LogicalRunId agentProjections
                    |> Option.isSome))
        | _ -> false

    [<RequireQualifiedAccess>]
    type private LongWorkAfterStop =
        | Continued
        | ReceiptUnknown
        | WaitDenied
        | RunClosed
        | NotSent of reason: string

    /// A context-window overflow is not a gateway-style transient: resending the
    /// same oversized input cannot succeed, so it never earns a continuation nudge.
    let isContextOverflowReason (reason: string) : bool =
        let text = if isNull reason then "" else reason.ToLowerInvariant()

        text.Contains "input_too_large"
        || text.Contains "context_length_exceeded"
        || text.Contains "contextoverflow"
        || text.Contains "context window"
        || text.Contains "prompt is too long"
        || text.Contains "maximum context length"

    /// An external hard stop: resending cannot succeed, so the failure earns neither
    /// a retry nor a continuation nudge. A context-window overflow and an account out
    /// of credits / over its spending limit are the two observed cases.
    let isHardStopReason (reason: string) : bool =
        isContextOverflowReason reason || ProviderCreditExhaustion.isReason reason

    /// A confirmed ProviderPermanent failure is the turn's terminal: the classifier
    /// only names a failure permanent when resending the same request cannot succeed
    /// (credentials, context overflow, exhausted credits). Retrying it, or nudging a
    /// long-running role to continue, is an unbounded loop against a wall.
    let isHardStop (failure: ExecutionFailure) (reason: string) : bool =
        failure = ExecutionFailure.ProviderPermanent || isHardStopReason reason

    let private sendLongWorkContinuation
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (path: string)
        : Task<LongWorkAfterStop> =
        task {
            let! hostStopped =
                providerAttemptStopFence?WaitForStop (
                    SessionId.value turn.SessionId,
                    ProviderRunIdentity.value turn.ProviderRun,
                    30000
                )

            match string hostStopped, HostSessionNudge.tryActiveProfile (Some durable) turn.SessionId with
            | "Stopped", Some profile when profile.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId ->
                let prompt =
                    ProviderProse.documentFor turn.SessionId RuntimeNudge.ProviderRetry Map.empty
                    + "\n\n"
                    + ProviderProse.documentFor turn.SessionId path Map.empty

                match!
                    HostSessionNudge.trySendGateContinuation
                        sessionPort
                        rootWorkspace
                        turn.SessionId
                        prompt
                        PromptAuthority.ContinuationKind.InteractionRepair
                        turn.Directory
                        (Some durable)
                        "unfinished-long-work-after-provider-failure"
                        turn.ProviderRun
                with
                | HostSessionNudge.GateContinuationOutcome.Failed error -> return LongWorkAfterStop.NotSent error
                | HostSessionNudge.GateContinuationOutcome.Sent _
                | HostSessionNudge.GateContinuationOutcome.AlreadyAdmitted
                | HostSessionNudge.GateContinuationOutcome.Retired -> return LongWorkAfterStop.Continued
            | "Stopped", Some _ -> return LongWorkAfterStop.Continued
            | "Stopped", None ->
                if sameRunAlreadyClosed durable turn then
                    return LongWorkAfterStop.RunClosed
                else
                    return LongWorkAfterStop.NotSent "No active authority profile"
            | "Denied", _ -> return LongWorkAfterStop.WaitDenied
            | _, _ -> return LongWorkAfterStop.ReceiptUnknown
        }

    /// The terminal answer of confirmed-failure recovery. A long-running role with open
    /// work is continued instead; anything else is reported as the turn's failure.
    let private stopOrContinueLongWork
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (failure: ExecutionFailure)
        (reason: string)
        : Task =
        task {
            match longWorkContinuationPath turn.Role failure with
            | None -> notifyFailure eventPort turn reason
            | Some _ when isHardStop failure reason -> notifyFailure eventPort turn reason
            | Some path ->
                match! sendLongWorkContinuation sessionPort rootWorkspace durable turn path with
                | LongWorkAfterStop.Continued -> ()
                | LongWorkAfterStop.RunClosed -> ()
                | LongWorkAfterStop.ReceiptUnknown ->
                    notifyFailure eventPort turn (reason + "; stop receipt did not arrive")
                | LongWorkAfterStop.WaitDenied -> notifyFailure eventPort turn (reason + "; stop wait was denied")
                | LongWorkAfterStop.NotSent error ->
                    notifyFailure eventPort turn (reason + "; long-work continuation not sent: " + error)
        }
        :> Task

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

    let internal continueSameProvider
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
            | Some _ -> return RetryVerdict.Superseded
            | None -> return RetryVerdict.Terminal "No active authority profile"
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

            let! hostStopped =
                providerAttemptStopFence?WaitForStop (
                    SessionId.value input.Turn.SessionId,
                    ProviderRunIdentity.value input.Turn.ProviderRun,
                    30000
                )

            match Retry.verdictUnlessContinuing admitted (string hostStopped) with
            | None ->
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
            | Some verdict -> return verdict
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
            | RetryVerdict.ReceiptUnknown -> notifyFailure eventPort turn "stop receipt did not arrive"
            | RetryVerdict.WaitDenied -> notifyFailure eventPort turn "stop wait was denied"
            | RetryVerdict.Terminal reason ->
                do! stopOrContinueLongWork sessionPort rootWorkspace eventPort durable turn failure reason
        }
        :> Task

    let private defaultPromptOptions: SessionPromptOptions =
        { Model = None
          Agent = None
          Directory = None
          Metadata = None
          Tools = None
          DetachedListener = None }

    let private nudgeDirectly (sessionPort: ISessionHostPort) (turn: ReconciledTurn) (prompt: string) : Task<bool> =
        task {
            match! sessionPort.SendPrompt(turn.SessionId, prompt, defaultPromptOptions) with
            | SendOutcome.AdmittedWithReceipt _ -> return true
            | _ -> return false
        }

    let private notifyIfNudgeFailed
        (eventPort: IEventObservationPort)
        (turn: ReconciledTurn)
        (error: string)
        (sent: bool)
        =
        match sent with
        | false -> notifyFailure eventPort turn error
        | true -> ()

    let private verdictOfNudge (error: string) (sent: bool) =
        match sent with
        | true -> RetryVerdict.Dispatched
        | false -> RetryVerdict.Terminal error

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
                || ModelRouting.hasTheoreticalCapacity
                    roleName
                    Wanxiangshu.Execution.Routing.ModelExecutionPurpose.Normal

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
            | _ when sameRunAlreadyClosed durable turn -> ()
            | _ when isHardStop failure error -> notifyFailure eventPort turn error
            | false, _ -> notifyFailure eventPort turn "All candidate providers exhausted (zero capacity)"
            | true, None ->
                let prompt =
                    ProviderProse.documentFor turn.SessionId RuntimeNudge.MissingClosingReport Map.empty

                let! sent = nudgeDirectly sessionPort turn prompt
                notifyIfNudgeFailed eventPort turn error sent
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
                || ModelRouting.hasTheoreticalCapacity
                    roleName
                    Wanxiangshu.Execution.Routing.ModelExecutionPurpose.Normal

            let recoveryContext =
                requestKindFor durable turn.SessionId turn.PhysicalUserMessageId
                |> Option.bind (fun requestKind -> currentFailure projections turn.SessionId requestKind)

            match hasCapacity, recoveryContext with
            | _ when isHardStop failure error -> return RetryVerdict.Terminal error
            | false, _ -> return RetryVerdict.Terminal "All candidate providers exhausted (zero capacity)"
            | true, None ->
                let prompt =
                    ProviderProse.documentFor turn.SessionId RuntimeNudge.MissingClosingReport Map.empty

                let! sent = nudgeDirectly sessionPort turn prompt
                return verdictOfNudge error sent
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
