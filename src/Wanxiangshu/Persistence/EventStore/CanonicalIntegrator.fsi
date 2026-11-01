namespace Wanxiangshu.Persistence.EventStore

/// 组装领域规则；Current 与结构 heads 由库内 Integrator 持有。
[<RequireQualifiedAccess>]
module CanonicalIntegrator =
    /// 所有实际 store 共用 Journal 规则。
    val baseRules: IntegrationRule list

    /// rules 保留注册次序，并包含 Journal 规则。
    val createWithRules: rules: IntegrationRule list -> isEventTypeKnown: (string -> bool) -> ICanonicalIntegrator
