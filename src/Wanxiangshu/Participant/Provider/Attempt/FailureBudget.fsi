namespace Wanxiangshu.Participant.Provider.Attempt

open Wanxiangshu.Foundation.Identity

/// Exact identity of one physical provider attempt (used for failure deduplication).
type FailedProviderAttemptIdentity =
    { SessionId: SessionId
      LogicalRunId: LogicalRunId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      ProviderRun: ProviderRunIdentity }

module FailedProviderAttemptIdentity =
    val view: identity: FailedProviderAttemptIdentity -> obj
    val dedupeKey: identity: FailedProviderAttemptIdentity -> string

[<RequireQualifiedAccess>]
module ProviderFailureBudget =
    type FailureBudget = { ConsecutiveFailureCount: int }

    [<Literal>]
    val DefaultBudget: int = 10

    type Verdict =
        | MayRetry of FailureBudget
        | Exhausted of FailureBudget

    val initial: FailureBudget
    val recordFailure: budget: FailureBudget -> FailureBudget
    val recordSuccess: budget: FailureBudget -> FailureBudget
    val verdict: budgetLimit: int -> budget: FailureBudget -> Verdict
    val forNewAuthorityRoot: FailureBudget

    val isValidRecord: previousCount: int -> nextCount: int -> bool

    val attemptIdentity:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        authorityRoot: AuthorityRootUserMessageId ->
        providerRun: ProviderRunIdentity ->
            FailedProviderAttemptIdentity
