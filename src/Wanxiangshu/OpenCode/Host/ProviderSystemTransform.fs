namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Resources

/// HOST-026 / PROMPT-017: project the session-bound ProviderLanguage onto the
/// Wanxiangshu-owned system-prompt segment without disturbing Host/AGENTS text.
module ProviderSystemTransform =

    let private canonical (text: string) = if isNull text then "" else text.Trim()

    let private updateMatchingConstraintLine
        (currentSystem: string array)
        (isConstraintLine: string -> bool)
        (nextConstraint: string)
        =
        let replaceIfMatching index text =
            match isConstraintLine text with
            | true -> currentSystem.[index] <- nextConstraint
            | false -> ()

        currentSystem |> Array.iteri replaceIfMatching

    /// work-031: the three stage-method resources. The stage slot is one
    /// line whose content is decided by the current incumbency's stage and the
    /// current language; recognition stays byte-only across all six candidates
    /// (3 stages × 2 languages).

    let private planStagePaths = [ "planning/s1"; "planning/s2"; "planning/s3" ]

    let private planStageGuidanceFor (lang: ProviderLanguage) (stagePath: string) =
        ProviderProse.render lang stagePath Map.empty

    let private applyPlanStageGuidance (lang: ProviderLanguage) (currentSystem: string array) (stagePath: string) =
        let expected =
            [ for path in planStagePaths do
                  for stageLang in [ ProviderLanguage.English; ProviderLanguage.SimplifiedChinese ] do
                      canonical (planStageGuidanceFor stageLang path) ]

        let isStageLine (text: string) =
            expected |> List.contains (canonical text)

        let nextGuidance = planStageGuidanceFor lang stagePath
        let hasStageLine = currentSystem |> Array.exists isStageLine

        // Same stage + same language replays the same bytes in place
        // (idempotent); a stage change replaces the slot on the next request.
        match hasStageLine with
        | true -> updateMatchingConstraintLine currentSystem isStageLine nextGuidance
        | false -> emitJsExpr (currentSystem, nextGuidance) "$0.push($1)" |> ignore

    /// Only active roles own a projected segment; retired identities are
    /// decoded for history but never rewrite a live system prompt.
    let private catalogPrompt (catalog: PromptCatalog) =
        function
        | Role.Manager -> Some catalog.ManagerSystemPrompt
        | Role.Orchestrator -> Some catalog.OrchestratorSystemPrompt
        | Role.Engineer -> Some catalog.EngineerSystemPrompt
        | Role.DevOps -> Some catalog.DevopsSystemPrompt
        | Role.Plan -> Some catalog.PlanSystemPrompt
        | Role.Advisor -> Some catalog.AdvisorSystemPrompt
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> None

    let private localizedRolePrompt lang role = PromptResources.systemForRole lang role

    let private replaceOwnedSegment (oldPrompt: string) (nextPrompt: string) (system: string array) =
        let expected = canonical oldPrompt

        system
        |> Array.map (fun text -> if canonical text = expected then nextPrompt else text)

    let private sessionTransformInput (input: obj) (output: obj) =
        if
            not (isNull input)
            && not (isNull output)
            && not (isNull input?sessionID)
            && not (String.IsNullOrWhiteSpace(string input?sessionID))
            && not (isNull output?system)
        then
            Some(string input?sessionID, unbox<string array> output?system)
        else
            None

    let private activeRoles =
        [ Role.Manager; Role.Orchestrator; Role.Engineer; Role.DevOps; Role.Plan ]

    let private chooseBookkeeperPrompt expectedEn expectedZh nextPrompt (text: string) =
        let c = canonical text

        if c = expectedEn || c = expectedZh then
            nextPrompt
        else
            text

    /// Wanxiangshu-owned role prompts may arrive as the canonical English or
    /// Chinese Role Law, or as either language's installed-bundle view. All four
    /// are the same semantic prompt, so all four repair to the bound language.
    let private chooseRolePrompt expectedEn expectedZh expectedCur expectedInstalled nextPrompt (text: string) =
        let c = canonical text

        if c = expectedEn || c = expectedZh || c = expectedCur || c = expectedInstalled then
            nextPrompt
        else
            text

    let private roleMatchesSystem (r: Role) (system: string array) : bool =
        let expectedEn =
            canonical (PromptResources.systemForRole ProviderLanguage.English r)

        let expectedZh =
            canonical (PromptResources.systemForRole ProviderLanguage.SimplifiedChinese r)

        system
        |> Array.exists (fun text ->
            let c = canonical text
            c = expectedEn || c = expectedZh)

    let private tryDeduceRole (role: SessionId -> Role option) (sid: SessionId) (system: string array) : Role option =
        match role sid with
        | Some r -> Some r
        | None -> activeRoles |> List.tryFind (fun r -> roleMatchesSystem r system)

    let private replaceBookkeeperSystem lang sessionText (system: string array) =
        let oldPromptEn = PromptResources.loadBookkeeperSystemFor ProviderLanguage.English

        let oldPromptZh =
            PromptResources.loadBookkeeperSystemFor ProviderLanguage.SimplifiedChinese

        let expectedEn = canonical oldPromptEn
        let expectedZh = canonical oldPromptZh

        let matchesBookkeeper =
            system
            |> Array.exists (fun text ->
                let c = canonical text
                c = expectedEn || c = expectedZh)

        if BookkeeperRuntime.isAttached sessionText || matchesBookkeeper then
            let nextPrompt = PromptResources.loadBookkeeperSystemFor lang

            system
            |> Array.iteri (fun index text ->
                system.[index] <- chooseBookkeeperPrompt expectedEn expectedZh nextPrompt text)

            true
        else
            false

    let private replaceRoleSystem (role: SessionId -> Role option) sid lang (system: string array) =
        match tryDeduceRole role sid system with
        | None -> ()
        | Some r ->
            let oldPromptEn = PromptResources.systemForRole ProviderLanguage.English r
            let oldPromptZh = PromptResources.systemForRole ProviderLanguage.SimplifiedChinese r

            let currentPrompt =
                catalogPrompt (RuntimeResources.current().Prompts) r
                |> Option.defaultValue oldPromptEn

            // provider-language-008: the installed bundle's own view is also a
            // legitimate seed, so a config written under either language still
            // repairs. Byte comparison stays the only recognition rule — the
            // transform never invents or translates prose (provider-language-009).
            let installedPrompt =
                catalogPrompt (RuntimeResources.promptsFor lang) r
                |> Option.defaultValue oldPromptEn

            let nextPrompt = localizedRolePrompt lang r
            let expectedEn = canonical oldPromptEn
            let expectedZh = canonical oldPromptZh
            let expectedCur = canonical currentPrompt
            let expectedInstalled = canonical installedPrompt

            system
            |> Array.iteri (fun index text ->
                system.[index] <- chooseRolePrompt expectedEn expectedZh expectedCur expectedInstalled nextPrompt text)

    let private transformSystem
        (role: SessionId -> Role option)
        (planStage: SessionId -> Task<string option>)
        sessionText
        system
        : Task<unit> =
        task {
            let sid = SessionId.create sessionText
            let lang = GlobalProviderLanguage.current ()

            if replaceBookkeeperSystem lang sessionText system then
                ()
            else
                replaceRoleSystem role sid lang system

            match! planStage sid with
            | Some stagePath -> applyPlanStageGuidance lang system stagePath
            | None -> ()
        }

    let createWith
        (role: SessionId -> Role option)
        (planStage: SessionId -> Task<string option>)
        : obj -> obj -> Task<unit> =
        fun input output ->
            task {
                match sessionTransformInput input output with
                | None -> ()
                | Some(sessionText, system) -> do! transformSystem role planStage sessionText system
            }
