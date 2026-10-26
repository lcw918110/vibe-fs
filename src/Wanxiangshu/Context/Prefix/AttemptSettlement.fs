namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Participant.Provider.Attempt

/// HNI-09: the product decisions of settling one physical provider attempt.
///
/// The X-wire adapter used to hold these decisions inline: which visible tool
/// continuation closes an attempt plan, when a successful attempt clears the
/// provider failure count, and when a retry has to be planned instead of
/// settled. They are the prefix/product's rules, not the host wire's, so they
/// live here and the adapter asks.
[<RequireQualifiedAccess>]
module AttemptSettlement =

    /// What settling one attempt does.
    [<RequireQualifiedAccess>]
    type Settlement =
        /// The attempt plan is consumed. `ClearsFailureCount` says whether the
        /// provider failure count is cleared too.
        | ConsumePlan of ClearsFailureCount: bool
        /// Nothing is settled: the plan is kept and the failure count stands.
        | KeepPlan
        /// A retry has to be planned for this physical request.
        | PlanRetry

    /// Whether a successful attempt of this request kind clears the provider
    /// failure count. Only a work-main request does: an interaction-repair
    /// success is a repair, not the work completing.
    let successClearsFailureCount (kind: ProviderRequestKind) : bool =
        ProviderRequestKind.clearsFailureCountOnSuccess kind

    /// The settlement of one attempt that reached a terminal outcome.
    ///
    /// A terminal outcome always closes the attempt plan: `finish=tool-calls`
    /// closes a successful attempt while the Host tool loop continues, and
    /// only a genuinely provisional snapshot keeps the plan. Only a successful
    /// work-main request clears the failure count; every other terminal outcome
    /// consumes the plan and leaves the failure count standing.
    let settleTerminalOutcome (kind: ProviderRequestKind) (outcome: AttemptOutcome) : Settlement =
        match outcome with
        | AttemptOutcome.Completed -> Settlement.ConsumePlan(successClearsFailureCount kind)
        | AttemptOutcome.CompletedInvalid
        | AttemptOutcome.Failed
        | AttemptOutcome.Aborted -> Settlement.ConsumePlan false

    /// The settlement of an attempt with no observed outcome: the plan is kept.
    let keepProvisionalPlan: Settlement = Settlement.KeepPlan

    /// The settlement of a visible tool continuation. The attempt ended with
    /// tool calls, so it settles like any other completed attempt.
    let settleToolContinuation (kind: ProviderRequestKind) : Settlement =
        Settlement.ConsumePlan(successClearsFailureCount kind)

    /// The settlement of a provider retry attempt: the retry is planned, not
    /// settled, and the failure count stands.
    let planProviderRetry: Settlement = Settlement.PlanRetry

    /// Whether a physical request must be planned as a provider retry.
    ///
    /// The origin label is read from the host wire; the rule that only a
    /// provider-retry attempt is planned is the product's.
    let requiresRetryPlan (originLabel: string option) (providerRetryOriginLabel: string) : bool =
        originLabel = Some providerRetryOriginLabel

    /// The reasons a retry cannot be planned.
    [<RequireQualifiedAccess>]
    type RetryIntentlocker =
        /// The session has no active authority profile.
        | NoAuthority
        /// The session has no provider failure state.
        | NoFailureState

    /// Why this session may not plan a provider retry.
    ///
    /// A retry needs an active authority profile and a recorded provider
    /// failure; without either one the plan would name an identity nobody
    /// recorded.
    let retryIntentlockers (hasAuthority: bool) (hasFailureState: bool) : RetryIntentlocker list =
        [ if not hasAuthority then
              RetryIntentlocker.NoAuthority
          if not hasFailureState then
              RetryIntentlocker.NoFailureState ]

    /// Whether settling this attempt records a confirmed provider success.
    ///
    /// Only a successful work-main attempt does: an interaction-repair
    /// success is a repair, not the work completing.
    let recordsConfirmedSuccess (settlement: Settlement) : bool =
        match settlement with
        | Settlement.ConsumePlan clearsFailureCount -> clearsFailureCount
        | Settlement.KeepPlan
        | Settlement.PlanRetry -> false

    /// Whether settling this attempt consumes the attempt plan.
    let consumesAttemptPlan (settlement: Settlement) : bool =
        match settlement with
        | Settlement.ConsumePlan _ -> true
        | Settlement.KeepPlan
        | Settlement.PlanRetry -> false
