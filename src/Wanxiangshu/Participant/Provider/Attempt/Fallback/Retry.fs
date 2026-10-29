namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
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

    let verdictUnlessContinuing (alreadyAdmitted: bool) (stopWait: string) : RetryVerdict option =
        match stopWait with
        | "Stopped" ->
            if alreadyAdmitted then Some RetryVerdict.Superseded else None
        | "Denied" -> Some RetryVerdict.WaitDenied
        | _ -> Some RetryVerdict.ReceiptUnknown

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

    let private admitAndRedispatch
        (ports: RetryPorts)
        (input: RetryAttempt)
        (authorization: ProviderRecoveryAuthorization)
        : Task<RetryVerdict> =
        task {
            match! ports.Admit authorization with
            | Ok FailureAdmissionOutcome.RetryAuthorized -> return! ports.Redispatch authorization input
            | Ok FailureAdmissionOutcome.RetryExhausted -> return RetryVerdict.Terminal input.Error
            | Ok FailureAdmissionOutcome.EpisodeSuperseded -> return RetryVerdict.Superseded
            | Ok FailureAdmissionOutcome.NoActiveRun ->
                return RetryVerdict.Terminal "Confirmed provider failure has no active provider run"
            | Error reason -> return RetryVerdict.Terminal reason
        }

    let attempt (ports: RetryPorts) (input: RetryAttempt) : Task<RetryVerdict> =
        match (decision input).Resolution with
        | ExecutionFailureResolution.RetryFreshAttempt authorization -> admitAndRedispatch ports input authorization
        | ExecutionFailureResolution.PreserveCurrentFact -> Task.FromResult RetryVerdict.Superseded
        | ExecutionFailureResolution.AwaitAcceptanceReconciliation _ ->
            Task.FromResult(RetryVerdict.Terminal input.Error)
        | ExecutionFailureResolution.TerminalizeAcceptedPreProvider _
        | ExecutionFailureResolution.TerminalizeProviderStarted _ -> Task.FromResult(RetryVerdict.Terminal input.Error)
