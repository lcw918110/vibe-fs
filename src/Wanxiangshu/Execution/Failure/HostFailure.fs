namespace Wanxiangshu.Execution.Failure

/// Product-side classification of a host-neutral failure fact.
///
/// The host port reports raw facts (`Wanxiangshu.Host.Ports.HostFailure`); turning
/// them into a retry / budget / terminal decision is product logic and lives here,
/// never inside an adapter. Adapters translate host payloads into the neutral shape
/// and call this module at the boundary.
///
/// Mapping rationale:
///   - Aborted            -> UserCancelled. An operator abort is a typed wake, not a
///                           provider failure; it must never advance the failure budget.
///   - RateLimited / Network / Provider -> ProviderTransient. The provider recovery
///                           path owns retry, budget and context replacement.
///   - Authentication     -> ProviderPermanent. Credentials do not recover by retrying.
///   - ContextOverflow    -> ProviderPermanent. Resending the same oversized input
///                           cannot succeed; an overflow is not a gateway-style transient
///                           and must never feed an unbounded retry / continue loop.
///   - Unknown            -> ProviderTransient. An unobservable failure keeps the
///                           recovery path in charge instead of guessing a terminal.
///   - Provider / Unknown whose message reports exhausted credits or spending limit
///                        -> ProviderPermanent. The account cannot pay for any further
///                           request; resending is a guaranteed failure (see
///                           `ProviderCreditExhaustion`).
///
/// Note: the neutral vocabulary has no "superseded" kind. A superseded attempt
/// therefore arrives as Provider/Unknown with the error name preserved in `Message`,
/// and is classified as ProviderTransient like any other unclassified provider failure.
/// A provider that refuses every request because the account ran out of credits,
/// hit its spending limit or needs a subscription is an external hard stop, not a
/// transient: no retry, budget or continuation can make the next request succeed
/// until a human tops the account up. Matched on the provider's own words
/// (xAI: `personal-team-blocked:spending-limit: You have run out of credits or need
/// a Grok subscription`; OpenAI: `insufficient_quota`).
module ProviderCreditExhaustion =

    let private markers =
        [ "spending-limit"
          "spending_limit"
          "spending limit"
          "personal-team-blocked"
          "run out of credits"
          "out of credits"
          "need a grok subscription"
          "insufficient_quota"
          "exceeded your current quota"
          "credit balance is too low"
          "insufficient credits" ]

    let isReason (reason: string) : bool =
        if System.String.IsNullOrWhiteSpace reason then
            false
        else
            let text = reason.ToLowerInvariant()
            markers |> List.exists (fun marker -> text.Contains marker)

module HostFailureClassifier =

    let classify (failure: Wanxiangshu.Host.Ports.HostFailure) : ExecutionFailure =
        match failure.Kind with
        | Wanxiangshu.Host.Ports.HostFailureKind.Aborted -> ExecutionFailure.UserCancelled
        | Wanxiangshu.Host.Ports.HostFailureKind.Authentication
        | Wanxiangshu.Host.Ports.HostFailureKind.ContextOverflow -> ExecutionFailure.ProviderPermanent
        | Wanxiangshu.Host.Ports.HostFailureKind.Provider
        | Wanxiangshu.Host.Ports.HostFailureKind.Unknown when ProviderCreditExhaustion.isReason failure.Message ->
            ExecutionFailure.ProviderPermanent
        | Wanxiangshu.Host.Ports.HostFailureKind.RateLimited
        | Wanxiangshu.Host.Ports.HostFailureKind.Network
        | Wanxiangshu.Host.Ports.HostFailureKind.Provider
        | Wanxiangshu.Host.Ports.HostFailureKind.Unknown -> ExecutionFailure.ProviderTransient
