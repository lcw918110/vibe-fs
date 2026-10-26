namespace Wanxiangshu.Resources

open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider

module PromptResources =

    let internal roleSemanticPath =
        function
        | Role.Manager -> "role/manager"
        | Role.Orchestrator -> "role/orchestrator"
        | Role.Engineer -> "role/engineer"
        | Role.DevOps -> "role/devops"
        // retired roles are not prompted
        | Role.Plan -> "role/plan"
        | Role.Advisor -> "role/advisor"
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> invalidArg "role" "Retired roles have no provider prompt."

    let private libraryPaths =
        function
        | Role.Manager -> [ "library/kolmogorov"; "library/scarcity"; "library/work/quality-ledger" ]
        | Role.Engineer -> [ "library/kolmogorov" ]
        | Role.DevOps -> [ "library/kolmogorov"; "library/scarcity" ]
        | _ -> []

    let private composeInstructions (parts: string list) =
        parts
        |> List.filter (System.String.IsNullOrWhiteSpace >> not)
        |> List.map (fun text -> text.Trim())

    let instructionTextsForRole (lang: ProviderLanguage) (role: Role) =
        let law = ProviderResources.readText lang (roleSemanticPath role)

        match role, libraryPaths role with
        | Role.Advisor, _ -> composeInstructions [ law ]
        | _, [] -> composeInstructions [ ProviderResources.readText lang "world/common-law"; law ]
        | _, inherited ->
            let books = inherited |> List.map (ProviderResources.readText lang)

            composeInstructions (
                [ ProviderResources.readText lang "world/common-law"
                  law
                  ProviderResources.readText lang "library/ingress" ]
                @ books
                @ [ ProviderResources.readText lang "library/closing" ]
            )

    let systemForRole (lang: ProviderLanguage) (role: Role) =
        instructionTextsForRole lang role |> LlmFacing.renderInstructions

    /// InternalLeaf Bookkeeper is not a public Role, but it shares the same
    /// Common Law and receives its own Role Law.
    let bookkeeperInstructionTextsFor (lang: ProviderLanguage) : string list =
        composeInstructions
            [ ProviderResources.readText lang "world/common-law"
              ProviderResources.readText lang "role/bookkeeper" ]

    let loadBookkeeperSystemFor (lang: ProviderLanguage) : string =
        bookkeeperInstructionTextsFor lang |> LlmFacing.renderInstructions

    let loadBookkeeperSystem () : string =
        loadBookkeeperSystemFor ProviderLanguage.English

    let loadForLanguage (lang: ProviderLanguage) : PromptCatalog =
        { ManagerSystemPrompt = systemForRole lang Role.Manager
          EngineerSystemPrompt = systemForRole lang Role.Engineer
          DevopsSystemPrompt = systemForRole lang Role.DevOps
          OrchestratorSystemPrompt = systemForRole lang Role.Orchestrator

          PlanSystemPrompt = systemForRole lang Role.Plan
          AdvisorSystemPrompt = systemForRole lang Role.Advisor }

    let load () : PromptCatalog =
        loadForLanguage ProviderLanguage.English
