namespace Wanxiangshu.Persistence.EventStore

open Wanxiangshu.Composition.Durable

[<RequireQualifiedAccess>]
module CanonicalIntegrator =

    /// Journal-only spine: the structural frontier plus the journal fold.
    /// Every domain rule (Sphinx, JsTransaction, Casebook) lives in
    /// its owning module and is injected explicitly by composition owners
    /// through `createWithRules`.
    let baseRules: IntegrationRule list =
        [ StructuralIntegration.rule; JournalIntegration.rule ]

    /// Fail-closed seam precondition: structural heads and the journal fold
    /// are load-bearing for every store (`TryHeads`, journal resume).
    let private requireBaseRules (program: IntegrationRule list) =
        [ StructuralIntegration.rule.Name; JournalIntegration.rule.Name ]
        |> List.iter (fun name ->
            if program |> List.exists (fun rule -> rule.Name = name) |> not then
                invalidArg "rules" (sprintf "CanonicalIntegrator program is missing required rule '%s'" name))

    /// Explicit construction seam. `rules` is the complete history program
    /// in registration order; domain rules arrive from their owning modules.
    let createWithRules (rules: IntegrationRule list) (isEventTypeKnown: string -> bool) : ICanonicalIntegrator =
        requireBaseRules rules
        IntegratorEngine.create rules isEventTypeKnown
