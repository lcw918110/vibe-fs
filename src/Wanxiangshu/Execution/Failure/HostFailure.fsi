namespace Wanxiangshu.Execution.Failure

/// Exhausted credits / spending limit / subscription required: an external hard stop.
module ProviderCreditExhaustion =

    val isReason: reason: string -> bool

/// Product-side classification of a host-neutral failure fact.
/// The host port reports raw facts; the product owns retry / budget / terminal decisions.
module HostFailureClassifier =

    val classify: failure: Wanxiangshu.Host.Ports.HostFailure -> ExecutionFailure
