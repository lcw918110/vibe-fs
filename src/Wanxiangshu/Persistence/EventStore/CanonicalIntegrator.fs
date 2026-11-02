namespace Wanxiangshu.Persistence.EventStore

open Wanxiangshu.Composition.Durable

[<RequireQualifiedAccess>]
module CanonicalIntegrator =

    /// 领域规则由组装层提供；结构 heads 归库内 Integrator。
    let baseRules: IntegrationRule list = [ JournalIntegration.rule ]

    let private requireBaseRules (program: IntegrationRule list) =
        [ JournalIntegration.rule.Name ]
        |> List.iter (fun name ->
            if program |> List.exists (fun rule -> rule.Name = name) |> not then
                invalidArg "rules" (sprintf "CanonicalIntegrator program is missing required rule '%s'" name))

    /// Explicit construction seam. `rules` is the complete history program
    /// in registration order; domain rules arrive from their owning modules.
    let createWithRules (rules: IntegrationRule list) (isEventTypeKnown: string -> bool) : ICanonicalIntegrator =
        requireBaseRules rules
        IntegratorEngine.create rules isEventTypeKnown
