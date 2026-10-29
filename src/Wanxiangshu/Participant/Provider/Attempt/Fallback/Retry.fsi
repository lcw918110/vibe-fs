namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt

/// One pluggable retry decorator over a confirmed provider failure. The policy
/// decision, the durable budget admission and the path-specific physical
/// re-entry live behind injected ports, so every execution path composes the
/// same engine instead of deciding retry locally.
[<RequireQualifiedAccess>]
type RetryVerdict =
    /// A fresh physical attempt was admitted and sent; the caller keeps waiting.
    | Dispatched
    /// A duplicate or superseded episode: idempotent stop, no terminal.
    | Superseded
    /// Stop receipt did not arrive. Not success, not supersession, and not a claim that the host did not stop.
    | ReceiptUnknown
    /// Stop wait was refused. A known rejection, not a missing receipt and not a superseded recovery.
    | WaitDenied
    /// Recovery is exhausted or not licensed; the caller terminalizes with this reason.
    | Terminal of reason: string

/// Read-only input of one confirmed failure awaiting a retry decision.
type RetryAttempt =
    { Turn: ReconciledTurn
      Failure: ExecutionFailure
      OwnerSession: SessionId
      RequestKind: ProviderRequestKind
      Current: ProviderFailureProjection
      Error: string }

/// Injected effects: the ledger owner admits the authorization; the path plug
/// performs the path-specific physical re-entry and reports the verdict.
type RetryPorts =
    { Admit: ProviderRecoveryAuthorization -> Task<Result<FailureAdmissionOutcome, string>>
      Redispatch: ProviderRecoveryAuthorization -> RetryAttempt -> Task<RetryVerdict> }

module Retry =
    /// The single retry decision: pure policy, then admitted redispatch or a typed terminal.
    val attempt: ports: RetryPorts -> input: RetryAttempt -> Task<RetryVerdict>

    /// None: the attempt stopped and recovery is not already admitted, so redispatch may continue.
    /// Some: already admitted, receipt missing, or wait refused. Timeout is never Superseded.
    val verdictUnlessContinuing: alreadyAdmitted: bool -> stopWait: string -> RetryVerdict option

    /// Ok only for a real dispatch or a real supersession.
    val observeDelegateRetry: verdict: RetryVerdict -> Result<unit, string>
