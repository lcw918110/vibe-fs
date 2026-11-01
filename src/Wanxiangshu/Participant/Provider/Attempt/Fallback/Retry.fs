namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt

[<RequireQualifiedAccess>]
type RetryVerdict =
    | Dispatched
    | Superseded
    | ReceiptUnknown
    | WaitDenied
    | Terminal of reason: string

type RetryAttempt =
    { Turn: ReconciledTurn
      Failure: ExecutionFailure
      OwnerSession: SessionId
      RequestKind: ProviderRequestKind
      Current: ProviderFailureProjection
      Error: string }

type RetryPorts =
    { Admit: ProviderRecoveryAuthorization -> Task<Result<FailureAdmissionOutcome, string>>
      Redispatch: ProviderRecoveryAuthorization -> RetryAttempt -> Task<RetryVerdict> }

/// Single retry engine shared by the ordinary turn path and
/// dedicated delegate children (delegation-023 / execution-failure-policy-003 / provider-attempt-recovery-019).
module Retry =

    [<Import("ProviderRecovery", "wanxiangli/failure")>]
    let private recoveryClass: obj = jsNative

    let private recovery: obj = createNew recoveryClass ()

    let internal fromNativeVerdict (value: obj) =
        match string value?kind with
        | "Dispatched" -> RetryVerdict.Dispatched
        | "Superseded" -> RetryVerdict.Superseded
        | "ReceiptUnknown" -> RetryVerdict.ReceiptUnknown
        | "WaitDenied" -> RetryVerdict.WaitDenied
        | _ -> RetryVerdict.Terminal(unbox value?reason)

    let internal toNativeVerdict verdict =
        match verdict with
        | RetryVerdict.Dispatched -> createObj [ "kind" ==> "Dispatched" ]
        | RetryVerdict.Superseded -> createObj [ "kind" ==> "Superseded" ]
        | RetryVerdict.ReceiptUnknown -> createObj [ "kind" ==> "ReceiptUnknown" ]
        | RetryVerdict.WaitDenied -> createObj [ "kind" ==> "WaitDenied" ]
        | RetryVerdict.Terminal reason -> createObj [ "kind" ==> "Terminal"; "reason" ==> reason ]

    let verdictUnlessContinuing (alreadyAdmitted: bool) (stopWait: string) : RetryVerdict option =
        let answer: obj = recovery?verdictUnlessContinuing (alreadyAdmitted, stopWait)
        if isNull answer then None else Some(fromNativeVerdict answer)

    let observeDelegateRetry (verdict: RetryVerdict) : Result<unit, string> =
        match verdict with
        | RetryVerdict.Dispatched
        | RetryVerdict.Superseded -> Ok()
        | RetryVerdict.ReceiptUnknown -> Error "stop receipt did not arrive"
        | RetryVerdict.WaitDenied -> Error "stop wait was denied"
        | RetryVerdict.Terminal reason -> Error reason

    let private decision (input: RetryAttempt) =
        let budget =
            if ProviderFailureProjection.mayRetry ProviderFailureBudget.DefaultBudget input.Current then
                ProviderRecoveryBudget.Available
            else
                ProviderRecoveryBudget.Exhausted

        ExecutionFailurePolicy.decide
            { Failure = input.Failure
              Lifecycle = DurableExecutionLifecycle.ProviderStarted
              ExecutionKey =
                { SessionId = input.Turn.SessionId
                  PhysicalUserMessageId = input.Turn.PhysicalUserMessageId }
              Capacity = CapacityOwnership.NoCapacityFence
              Provider =
                { LogicalRun = input.Current.LogicalRunId
                  ProviderRun = input.Turn.ProviderRun
                  RequestKind = input.RequestKind
                  RetryBudget = budget
                  Breaker = ProviderBreakerState.Closed } }

    let attempt (ports: RetryPorts) (input: RetryAttempt) : Task<RetryVerdict> =
        let selected =
            match (decision input).Resolution with
            | ExecutionFailureResolution.RetryFreshAttempt authorization ->
                createObj [ "resolution" ==> "RetryFreshAttempt"; "authorization" ==> authorization ]
            | ExecutionFailureResolution.PreserveCurrentFact -> createObj [ "resolution" ==> "PreserveCurrentFact" ]
            | ExecutionFailureResolution.AwaitAcceptanceReconciliation _ -> createObj [ "resolution" ==> "AwaitAcceptanceReconciliation" ]
            | ExecutionFailureResolution.TerminalizeAcceptedPreProvider _ -> createObj [ "resolution" ==> "TerminalizeAcceptedPreProvider" ]
            | ExecutionFailureResolution.TerminalizeProviderStarted _ -> createObj [ "resolution" ==> "TerminalizeProviderStarted" ]
        let effects =
            createObj
                [ "admit" ==> (fun authorization -> task {
                      let! answer = ports.Admit authorization
                      return
                          match answer with
                          | Ok value -> createObj [ "ok" ==> true; "value" ==> string value ]
                          | Error error -> createObj [ "ok" ==> false; "error" ==> error ] })
                  "redispatch" ==> (fun authorization -> task {
                      let! answer = ports.Redispatch authorization input
                      return toNativeVerdict answer }) ]
        task {
            let! answer: obj = recovery?attempt (selected, effects, input.Error)
            return fromNativeVerdict answer
        }
