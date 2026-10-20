namespace Wanxiangshu.Resources

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

/// JS-native owner boundary for the canonical provider prompt catalog and the
/// package runtime-resource bundle. Prompt composition remains owned by
/// PromptResources; this module only translates its records and localized
/// language values into plain JavaScript data (js-semantic-surface-003/005).
[<RequireQualifiedAccess>]
module PromptSurface =

    let private languageOf (raw: string) : ProviderLanguage = ProviderLanguage.parse raw

    let private catalogToJs (catalog: PromptCatalog) : obj =
        box
            {| ManagerSystemPrompt = catalog.ManagerSystemPrompt
               EngineerSystemPrompt = catalog.EngineerSystemPrompt
               DevopsSystemPrompt = catalog.DevopsSystemPrompt
               OrchestratorSystemPrompt = catalog.OrchestratorSystemPrompt
               PlanSystemPrompt = catalog.PlanSystemPrompt |}

    let private catalogValues (catalog: PromptCatalog) : string array =
        [| catalog.ManagerSystemPrompt
           catalog.EngineerSystemPrompt
           catalog.DevopsSystemPrompt
           catalog.OrchestratorSystemPrompt
           catalog.PlanSystemPrompt |]

    let private ruleToJs (rule: obj) : obj =
        emitJsExpr
            rule
            """
        (() => {
            if (!$0) return null;
            return {
                name: $0.Name || $0.name || '',
                lessonText: $0.LessonText || $0.lessonText || '',
                ruleId: $0.RuleId || $0.ruleId || '',
                fieldName: $0.FieldName || $0.fieldName || '',
                lexicalOrder: $0.LexicalOrder || $0.lexicalOrder || 0
            };
        })()
        """

    let private runtimeToJs (resources: RuntimeResources) : obj =
        box
            {| Prompts = catalogToJs resources.Prompts
               ProviderLanguageRootsReady = resources.ProviderLanguageRootsReady |}

    /// Canonical English public-role prompt catalog.
    let load () : obj = PromptResources.load () |> catalogToJs

    /// Localized public-role prompt catalog. Language crosses as a stable string.
    let loadForLanguage (language: string) : obj =
        PromptResources.loadForLanguage (languageOf language) |> catalogToJs

    /// Prompt bodies in the same public-role order as the catalog.
    let allForLanguage (language: string) : string array =
        PromptResources.loadForLanguage (languageOf language) |> catalogValues

    let loadBookkeeperSystem () : string = PromptResources.loadBookkeeperSystem ()

    let loadBookkeeperSystemFor (language: string) : string =
        PromptResources.loadBookkeeperSystemFor (languageOf language)

    /// Load the complete runtime package bundle as JS-native data.
    let runtimeLoad () : obj =
        RuntimeResourceAssembly.load () |> runtimeToJs

    let runtimeLoadForLanguage (language: string) : obj =
        RuntimeResourceAssembly.loadFor (languageOf language) |> runtimeToJs

    /// Match plugin initialization: install the package-owned bundle before
    let runtimeInstallFromPackage () : unit =
        RuntimeResources.install (RuntimeResourceAssembly.load ())

    let private roleSemanticPath role =
        match role with
        | Role.Manager -> "role/manager"
        | Role.Orchestrator -> "role/orchestrator"
        | Role.Engineer -> "role/engineer"
        | Role.DevOps -> "role/devops"
        // retired roles are not prompted
        | Role.Plan -> "role/plan"
        | _ -> "role/engineer"

    // provider-language-008: role law is Class A text the model reads. The
    // language is supplied by the caller — the Host boundary owns preference
    // observation and session binding, and this layer owns rendering only.
    let instructionTextsForRole (language: string) (roleLabel: string) : obj =
        match Roles.tryParseRole roleLabel with
        | None -> null
        | Some role ->
            let lang = languageOf language
            let common = ProviderResources.readText lang "world/common-law"
            let law = ProviderResources.readText lang (roleSemanticPath role)
            box {| roleLaw = law; commonLaw = common |}

    let systemForRole (language: string) (roleLabel: string) : string =
        match Roles.tryParseRole roleLabel with
        | None -> ""
        | Some role -> PromptResources.systemForRole (languageOf language) role

    let runtimeCurrent () : obj =
        RuntimeResources.current () |> runtimeToJs
