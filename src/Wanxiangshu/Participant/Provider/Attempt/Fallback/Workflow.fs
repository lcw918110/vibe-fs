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
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

module ProviderRecoveryWorkflow =

    [<Import("providerAttemptStopFence", "wanxiangli/failure")>]
    let private providerAttemptStopFence: obj = jsNative

    [<Import("ProviderRecovery", "wanxiangli/failure")>]
    let private recoveryClass: obj = jsNative

    let private recovery: obj = createNew recoveryClass ()

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

    let longWorkContinuationPath (role: Role option) (failure: ExecutionFailure) : string option =
        match failure, role with
        | ExecutionFailure.ProviderTransient, Some Role.Manager -> Some "work/manager-continue"
        | ExecutionFailure.ProviderTransient, Some Role.Plan -> Some "work/plan-continue"
        | ExecutionFailure.ProviderTransient, Some Role.Orchestrator -> Some "work/orchestrator-continue"
        | _ -> None

    let isContextOverflowReason (reason: string) : bool =
        let text = if isNull reason then "" else reason.ToLowerInvariant()

        text.Contains "input_too_large"
        || text.Contains "context_length_exceeded"
        || text.Contains "contextoverflow"
        || text.Contains "context window"
        || text.Contains "prompt is too long"
        || text.Contains "maximum context length"

    let isHardStopReason (reason: string) : bool =
        isContextOverflowReason reason || ProviderCreditExhaustion.isReason reason

    let isHardStop (failure: ExecutionFailure) (reason: string) : bool =
        failure = ExecutionFailure.ProviderPermanent || isHardStopReason reason

    let private currentAuthority (durable: AgentJournal) (turn: ReconciledTurn) () =
        HostSessionNudge.tryActiveProfile (Some durable) turn.SessionId
        |> Option.map (fun profile ->
            createObj
                [ "root" ==> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId
                  "value" ==> profile ])
        |> Option.toObj

    let private sameRunAlreadyClosed (durable: AgentJournal) (turn: ReconciledTurn) () =
        let projections = (AgentJournal.snapshot durable).AgentProjections

        match PromptAuthorityProjectionQueries.projectionFor turn.SessionId projections with
        | Some authority when authority.ActiveLogicalRun.IsNone ->
            PromptAuthorityProjectionQueries.lastAuthorityProfile turn.SessionId projections
            |> Option.exists (fun profile ->
                profile.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId
                && (PromptAuthorityProjectionQueries.closedRun turn.SessionId profile.LogicalRunId projections
                    |> Option.isSome))
        | _ -> false

    let private waitForStop (turn: ReconciledTurn) () : Task<string> =
        providerAttemptStopFence?WaitForStop (
            SessionId.value turn.SessionId,
            ProviderRunIdentity.value turn.ProviderRun,
            30000
        )

    let private sentView =
        function
        | HostSessionNudge.GateContinuationOutcome.Sent _ -> createObj [ "kind" ==> "Sent" ]
        | HostSessionNudge.GateContinuationOutcome.AlreadyAdmitted -> createObj [ "kind" ==> "AlreadyAdmitted" ]
        | HostSessionNudge.GateContinuationOutcome.Retired -> createObj [ "kind" ==> "Retired" ]
        | HostSessionNudge.GateContinuationOutcome.Failed error -> createObj [ "kind" ==> "Failed"; "error" ==> error ]

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
        (observer: ContinuationAcceptanceObserver option)
        (authorization: ProviderRecoveryAuthorization)
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

    let private retryEffects
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        (continuationPrompt: string)
        (observer: ContinuationAcceptanceObserver option)
        =
        // A two-argument F# function passed through `==>` is emitted as `curry2`,
        // so the caller's tuple-style invocation would receive the second stage
        // instead of the Task. A delegate keeps the JS arity intact.
        let sendRetry =
            System.Func<obj, ProviderRecoveryAuthorization, Task<obj>>(fun _profile license ->
                task {
                    let! sent =
                        HostSessionNudge.trySendGateContinuationObserved
                            sessionPort
                            rootWorkspace
                            turn.SessionId
                            continuationPrompt
                            PromptAuthority.ContinuationKind.ProviderRetryAttempt
                            turn.Directory
                            (Some durable)
                            (recoveryGateKind license)
                            license.ProviderRun
                            observer

                    return sentView sent
                })

        createObj
            [ "alreadyAdmitted" ==> recoveryAlreadyAdmitted durable turn
              "observeAcceptance" ==> observeRecoveryAcceptance durable turn observer
              "waitForStop" ==> waitForStop turn
              "currentAuthority" ==> currentAuthority durable turn
              "sendRetry" ==> sendRetry ]

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
            let effects =
                retryEffects sessionPort rootWorkspace durable turn continuationPrompt observer

            let! (answer: obj) =
                recovery?continueSameProvider (
                    AuthorityRootUserMessageId.value turn.AuthorityRootUserMessageId,
                    authorization,
                    effects,
                    error
                )

            return Retry.fromNativeVerdict answer
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
        let effects =
            retryEffects sessionPort rootWorkspace durable input.Turn continuationPrompt observer

        task {
            let! (answer: obj) =
                recovery?redispatch (
                    AuthorityRootUserMessageId.value input.Turn.AuthorityRootUserMessageId,
                    authorization,
                    effects,
                    failureReason
                )

            return Retry.fromNativeVerdict answer
        }

    let private currentFailure (projections: ProjectionSet) (ownerSessionId: SessionId) requestKind =
        AgentProjection.tryFind ownerSessionId projections.AgentProjections
        |> Option.bind _.ProviderFailures
        |> ProviderFailureEvidence.currentState
        |> Option.map (fun current -> ownerSessionId, requestKind, current)

    let private capacityFor (role: Role option) =
        let roleName = role |> Option.map Roles.roleLabel |> Option.defaultValue ""

        roleName = ""
        || ModelRouting.hasTheoreticalCapacity roleName ModelExecutionPurpose.Normal

    let private retry
        sessionPort
        rootWorkspace
        (durable: AgentJournal)
        turn
        failure
        prompt
        error
        observer
        (ownerSessionId, requestKind, current)
        =
        Retry.attempt
            { Admit =
                fun authorization ->
                    ProviderFailureLedger.recordAuthorizedFailure
                        (AgentJournalPortAdapter.forProviderFailure durable)
                        ownerSessionId
                        authorization
                        error
              Redispatch = redispatchAfterFailure sessionPort rootWorkspace durable prompt error observer }
            { Turn = turn
              Failure = failure
              OwnerSession = ownerSessionId
              RequestKind = requestKind
              Current = current
              Error = error }

    let private defaultPromptOptions: SessionPromptOptions =
        { Model = None
          Agent = None
          Directory = None
          Metadata = None
          Delivery = None
          Tools = None
          DetachedListener = None }

    let private sendMissing (sessionPort: ISessionHostPort) (turn: ReconciledTurn) () =
        task {
            let prompt =
                ProviderProse.documentFor turn.SessionId RuntimeNudge.MissingClosingReport Map.empty

            let! sent = sessionPort.SendPrompt(turn.SessionId, prompt, defaultPromptOptions)

            return
                match sent with
                | SendOutcome.AdmittedWithReceipt _ -> true
                | _ -> false
        }

    let private recoveryInput failure error hasCapacity context continueLongWork (turn: ReconciledTurn) =
        createObj
            [ "reason" ==> error
              "hardStop" ==> isHardStop failure error
              "hasCapacity" ==> hasCapacity
              "hasRecovery" ==> Option.isSome context
              "continueLongWork" ==> continueLongWork
              "expectedRoot"
              ==> AuthorityRootUserMessageId.value turn.AuthorityRootUserMessageId ]

    let private mainFailure satellite active projections session =
        if satellite then
            None
        else
            active
            |> Option.bind (fun _ -> currentFailure projections session ProviderRequestKind.WorkMain)

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
                let projections = AgentJournal.snapshot durable

                let active =
                    PromptAuthorityProjectionQueries.activeProfile turn.SessionId projections.AgentProjections

                let hasCapacity = capacityFor (active |> Option.map _.CanonicalRole)

                let satellite =
                    SessionAssociationProjection.isSatellite turn.SessionId projections.AgentProjections.Associations

                let context =
                    requestKindFor durable turn.SessionId turn.PhysicalUserMessageId
                    |> Option.bind (currentFailure projections turn.SessionId)
                    |> Option.orElseWith (fun () -> mainFailure satellite active projections turn.SessionId)

                let longPath = longWorkContinuationPath turn.Role failure

                let retryCurrent () =
                    task {
                        let! answer =
                            retry
                                sessionPort
                                rootWorkspace
                                durable
                                turn
                                failure
                                continuationPrompt
                                error
                                None
                                (Option.get context)

                        return Retry.toNativeVerdict answer
                    }

                let sendLong (_profile: obj) =
                    task {
                        let prompt =
                            ProviderProse.documentFor turn.SessionId RuntimeNudge.ProviderRetry Map.empty
                            + "\n\n"
                            + ProviderProse.documentFor turn.SessionId (Option.get longPath) Map.empty

                        let! sent =
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

                        return sentView sent
                    }

                let effects =
                    createObj
                        [ "terminal" ==> notifyFailure eventPort turn
                          "isRunClosed" ==> sameRunAlreadyClosed durable turn
                          "sendMissing" ==> sendMissing sessionPort turn
                          "retry" ==> retryCurrent
                          "hardStop" ==> isHardStop failure
                          "waitForStop" ==> waitForStop turn
                          "currentAuthority" ==> currentAuthority durable turn
                          "sendLong" ==> sendLong ]

                do!
                    recovery?recoverOrdinary (
                        recoveryInput failure error hasCapacity context (Option.isSome longPath) turn,
                        effects
                    )
        }
        :> Task

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

            let hasCapacity =
                PromptAuthorityProjectionQueries.activeProfile turn.SessionId projections.AgentProjections
                |> Option.map _.CanonicalRole
                |> capacityFor

            let context =
                requestKindFor durable turn.SessionId turn.PhysicalUserMessageId
                |> Option.bind (currentFailure projections turn.SessionId)

            let retryCurrent () =
                task {
                    let prompt =
                        ProviderProse.documentFor turn.SessionId RuntimeNudge.ProviderRetry Map.empty

                    let! answer =
                        retry sessionPort rootWorkspace durable turn failure prompt error observer (Option.get context)

                    return Retry.toNativeVerdict answer
                }

            let effects =
                createObj [ "sendMissing" ==> sendMissing sessionPort turn; "retry" ==> retryCurrent ]

            let! (answer: obj) =
                recovery?recoverDelegate (recoveryInput failure error hasCapacity context false turn, effects)

            return Retry.fromNativeVerdict answer
        }
