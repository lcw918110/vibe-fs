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
///
/// Note: the neutral vocabulary has no "superseded" kind. A superseded attempt
/// therefore arrives as Provider/Unknown with the error name preserved in `Message`,
/// and is classified as ProviderTransient like any other unclassified provider failure.
module HostFailureClassifier =

    let classify (failure: Wanxiangshu.Host.Ports.HostFailure) : ExecutionFailure =
        match failure.Kind with
        | Wanxiangshu.Host.Ports.HostFailureKind.Aborted -> ExecutionFailure.UserCancelled
        | Wanxiangshu.Host.Ports.HostFailureKind.Authentication
        | Wanxiangshu.Host.Ports.HostFailureKind.ContextOverflow -> ExecutionFailure.ProviderPermanent
        | Wanxiangshu.Host.Ports.HostFailureKind.RateLimited
        | Wanxiangshu.Host.Ports.HostFailureKind.Network
        | Wanxiangshu.Host.Ports.HostFailureKind.Provider
        | Wanxiangshu.Host.Ports.HostFailureKind.Unknown -> ExecutionFailure.ProviderTransient
