namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt

[<RequireQualifiedAccess>]
type FailureAdmissionOutcome =
    | RetryAuthorized
    | RetryExhausted
    | EpisodeSuperseded
    | NoActiveRun

module ProviderFailureLedger =

    [<Import("DurableProviderBudget", "wanxiangli/failure-budget")>]
    let private budgetClass: obj = jsNative

    let private stateView (state: ProviderFailureProjection) =
        createObj
            [ "logicalRun" ==> LogicalRunId.value state.LogicalRunId
              "authorityRoot"
              ==> AuthorityRootUserMessageId.value state.AuthorityRootUserMessageId
              "consecutiveFailureCount" ==> state.Budget.ConsecutiveFailureCount
              "recentFailureKeys" ==> List.toArray state.RecentFailureKeys
              "exhausted" ==> state.Exhausted
              "lastTransitionWasSuccess" ==> state.LastTransitionWasSuccess ]

    let private factOf (value: obj) =
        let session = SessionId.create (unbox value?sessionId)
        let run = LogicalRunId.create (unbox value?logicalRun)
        let root = AuthorityRootUserMessageId.create (unbox value?authorityRoot)
        let provider = ProviderRunIdentity.create (unbox value?providerRun)

        let fact =
            match string value?kind with
            | "FailureRecorded" ->
                ProviderFailureFactCases.FailureRecorded
                    {| SessionId = session
                       LogicalRunId = run
                       AuthorityRootUserMessageId = root
                       ProviderRun = provider
                       ConsecutiveFailureCount = unbox value?count
                       Reason = unbox value?reason |}
            | "RetryExhausted" ->
                ProviderFailureFactCases.RetryExhausted
                    {| SessionId = session
                       LogicalRunId = run
                       AuthorityRootUserMessageId = root
                       FinalConsecutiveFailureCount = unbox value?count |}
            | _ ->
                ProviderFailureFactCases.SuccessRecorded
                    {| SessionId = session
                       LogicalRunId = run
                       AuthorityRootUserMessageId = root
                       ProviderRun = provider |}

        session, provider, fact

    let private create (port: ProviderFailureJournalPort) =
        let append value =
            task {
                let session, provider, fact = factOf value
                let! answer = port.Append session provider fact

                return
                    match answer with
                    | Ok() -> createObj [ "ok" ==> true ]
                    | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
            }

        let native =
            createObj
                [ "current"
                  ==> (fun session ->
                      port.CurrentState(SessionId.create session)
                      |> Option.map stateView
                      |> Option.toObj)
                  "append" ==> append ]

        createNew budgetClass native

    let private admissionOutcome (value: obj) =
        match string value with
        | "RetryAuthorized" -> FailureAdmissionOutcome.RetryAuthorized
        | "RetryExhausted" -> FailureAdmissionOutcome.RetryExhausted
        | "EpisodeSuperseded" -> FailureAdmissionOutcome.EpisodeSuperseded
        | _ -> FailureAdmissionOutcome.NoActiveRun

    let recordAuthorizedFailure
        (port: ProviderFailureJournalPort)
        (sessionId: SessionId)
        (authorization: ProviderRecoveryAuthorization)
        (reason: string)
        : Task<Result<FailureAdmissionOutcome, string>> =
        task {
            let owner = create port

            let input =
                createObj
                    [ "logicalRun" ==> LogicalRunId.value authorization.LogicalRun
                      "providerRun" ==> ProviderRunIdentity.value authorization.ProviderRun ]

            let! (answer: obj) = owner?recordAuthorizedFailure (SessionId.value sessionId, input, reason)

            return
                if not (unbox<bool> answer?ok) then
                    Error(unbox answer?error)
                else
                    Ok(admissionOutcome answer?value)
        }

    let recordConfirmedSuccess
        (port: ProviderFailureJournalPort)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : Task<Result<unit, string>> =
        task {
            let owner = create port

            let! (answer: obj) =
                owner?recordConfirmedSuccess (SessionId.value sessionId, ProviderRunIdentity.value providerRun)

            return
                if unbox<bool> answer?ok then
                    Ok()
                else
                    Error(unbox answer?error)
        }
