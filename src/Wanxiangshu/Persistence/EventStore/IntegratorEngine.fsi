namespace Wanxiangshu.Persistence.EventStore

[<RequireQualifiedAccess>]
module IntegratorEngine =
    val create: rules: IntegrationRule list -> isEventTypeKnown: (string -> bool) -> ICanonicalIntegrator
