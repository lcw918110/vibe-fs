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
        | Role.Blogger -> "role/blogger"
        | Role.Plan -> "role/plan"
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> invalidArg "role" "Retired roles have no provider prompt."

    let private semanticPaths =
        [ "world/common-law"
          "library/ingress"
          "library/closing"
          "library/kolmogorov"
          "library/scarcity"
          "library/relay/quality-ledger"
          "role/manager"
          "role/engineer"
          "role/devops"
          "role/orchestrator"
          "role/blogger"
          "role/bookkeeper"
          "role/plan" ]

    let private ensureParity () =
        semanticPaths |> List.iter ProviderResources.requireLanguagePair

    let private libraryPaths =
        function
        | Role.Manager -> [ "library/kolmogorov"; "library/scarcity"; "library/relay/quality-ledger" ]
        | Role.Engineer -> [ "library/kolmogorov" ]
        | Role.DevOps -> [ "library/kolmogorov"; "library/scarcity" ]
        | _ -> []

    let private composeInstructions (parts: string list) =
        parts
        |> List.filter (System.String.IsNullOrWhiteSpace >> not)
        |> List.map (fun text -> text.Trim())

    let instructionTextsForRole (lang: ProviderLanguage) (role: Role) =
        ensureParity ()
        let common = ProviderResources.readText lang "world/common-law"
        let law = ProviderResources.readText lang (roleSemanticPath role)
        let inherited = libraryPaths role

        if List.isEmpty inherited then
            composeInstructions [ common; law ]
        else
            let books = inherited |> List.map (ProviderResources.readText lang)

            composeInstructions (
                [ common; law; ProviderResources.readText lang "library/ingress" ]
                @ books
                @ [ ProviderResources.readText lang "library/closing" ]
            )

    let systemForRole (lang: ProviderLanguage) (role: Role) =
        instructionTextsForRole lang role |> LlmFacing.renderInstructions

    /// InternalLeaf Bookkeeper is not a public Role, but it shares the same
    /// Common Law and receives its own Role Law.
    let bookkeeperInstructionTextsFor (lang: ProviderLanguage) : string list =
        ensureParity ()

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
          BloggerSystemPrompt = systemForRole lang Role.Blogger }

    let load () : PromptCatalog =
        loadForLanguage ProviderLanguage.English
