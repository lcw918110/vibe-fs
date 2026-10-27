namespace Wanxiangshu.Participant.Provider.Attempt

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// Exact identity of one physical provider attempt (used for failure deduplication).
type FailedProviderAttemptIdentity =
    { SessionId: SessionId
      LogicalRunId: LogicalRunId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      ProviderRun: ProviderRunIdentity }

module FailedProviderAttemptIdentity =
    [<Import("ProviderFailureBudget", "wanxiangli/failure-budget")>]
    let private budgetLibraryClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private library = construct budgetLibraryClass

    /// The plain identity `wanxiangli/failure-budget` keys an exact attempt by.
    let view (identity: FailedProviderAttemptIdentity) : obj =
        createObj
            [ "sessionId" ==> SessionId.value identity.SessionId
              "logicalRun" ==> LogicalRunId.value identity.LogicalRunId
              "authorityRoot"
              ==> AuthorityRootUserMessageId.value identity.AuthorityRootUserMessageId
              "providerRun" ==> ProviderRunIdentity.value identity.ProviderRun ]

    /// Stable string form for set membership in a projection (owned by wanxiangli/failure-budget).
    let dedupeKey (identity: FailedProviderAttemptIdentity) : string = library?dedupeKey (view identity)

/// Pure provider failure budget. No Host, Journal or Fable dependency.
///
/// Bounded consecutive failure budget tracking.
/// Exact failure deduplication + consecutive failure budget increment + exhaustion check.
[<RequireQualifiedAccess>]
module ProviderFailureBudget =

    type FailureBudget = { ConsecutiveFailureCount: int }

    [<Literal>]
    let DefaultBudget = 10 // = wanxiangli/failure-budget defaultBudget (asserted by execution-failure-policy-002)

    /// What the controller may do after a failure has been recorded.
    type Verdict =
        /// Budget remains; the next automatic attempt may be issued.
        | MayRetry of FailureBudget
        /// Budget consumed. Issue no further automatic physical request.
        | Exhausted of FailureBudget

    let initial: FailureBudget = { ConsecutiveFailureCount = 0 }

    /// On failure: budget is consumed by one.
    let recordFailure (budget: FailureBudget) : FailureBudget =
        { ConsecutiveFailureCount = budget.ConsecutiveFailureCount + 1 }

    /// On business-main success: reset the failure budget.
    let recordSuccess (budget: FailureBudget) : FailureBudget =
        ignore budget
        { ConsecutiveFailureCount = 0 }

    [<Import("ProviderFailureBudget", "wanxiangli/failure-budget")>]
    let private budgetLibraryClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private library = construct budgetLibraryClass

    /// Judgement happens after the failure is recorded (owned by wanxiangli/failure-budget).
    let verdict (budgetLimit: int) (budget: FailureBudget) : Verdict =
        match (library?verdict (budgetLimit, budget.ConsecutiveFailureCount): string) with
        | "Exhausted" -> Exhausted budget
        | _ -> MayRetry budget

    let forNewAuthorityRoot: FailureBudget = initial

    /// Validation: consecutive failure count advances exactly once from the folded state.
    /// A prior success is already represented by a folded count of zero.
    let isValidRecord (previousCount: int) (nextCount: int) : bool = nextCount = previousCount + 1

    /// The identity used to deduplicate one failed attempt.
    let attemptIdentity
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (authorityRoot: AuthorityRootUserMessageId)
        (providerRun: ProviderRunIdentity)
        : FailedProviderAttemptIdentity =
        { SessionId = sessionId
          LogicalRunId = logicalRunId
          AuthorityRootUserMessageId = authorityRoot
          ProviderRun = providerRun }
