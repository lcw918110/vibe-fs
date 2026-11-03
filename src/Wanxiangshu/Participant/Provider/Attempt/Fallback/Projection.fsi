namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt

type ProviderFailureProjection =
    { LogicalRunId: LogicalRunId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      Budget: ProviderFailureBudget.FailureBudget
      RecentFailureKeys: string list
      Exhausted: bool
      LastTransitionWasSuccess: bool }

type ProviderFailureAdvanceRejection =
    | AlreadyObserved
    | AlreadyExhausted
    | DifferentRun
    | NoActiveBudget
    | InvalidTransition

[<RequireQualifiedAccess>]
type ProviderFailureReplay =
    | LatestMayRetry
    | LatestExhausted
    | Superseded

module ProviderFailureProjection =
    val forAuthority:
        logicalRunId: LogicalRunId -> authorityRoot: AuthorityRootUserMessageId -> ProviderFailureProjection

    /// The single host→library state mapping; the durable budget adapter reuses it.
    val toLibraryState: current: ProviderFailureProjection -> obj

    val applyFailure:
        identity: FailedProviderAttemptIdentity ->
        consecutiveFailureCount: int ->
        current: ProviderFailureProjection ->
            Result<ProviderFailureProjection, ProviderFailureAdvanceRejection>

    val applyExhausted: current: ProviderFailureProjection -> ProviderFailureProjection
    val recordSuccess: current: ProviderFailureProjection -> ProviderFailureProjection
    val mayRetry: budgetLimit: int -> current: ProviderFailureProjection -> bool

    val replay:
        budgetLimit: int ->
        identity: FailedProviderAttemptIdentity ->
        current: ProviderFailureProjection ->
            ProviderFailureReplay
