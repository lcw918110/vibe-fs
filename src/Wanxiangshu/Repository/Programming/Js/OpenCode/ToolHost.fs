namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Programming.Js


/// PROMPT-019: load already-localized js-program prose. Domain assembles;
/// this module binds language.
module JsDescriptionAssets =

    let private text (lang: ProviderLanguage) (path: string) =
        ProviderProse.render lang path Map.empty

    let private template (lang: ProviderLanguage) (path: string) =
        ProviderResources.requireLanguagePair path
        ProviderResources.readText lang path

    let load (lang: ProviderLanguage) : JsCanonicalDescription.Prose =
        { Header = text lang JsCanonicalDescription.Path.Header
          EditPrelude = template lang JsCanonicalDescription.Path.EditPrelude
          EditStructuralGuidance = template lang JsCanonicalDescription.Path.EditStructuralGuidance
          Footer = text lang JsCanonicalDescription.Path.Footer
          Contract = template lang JsCanonicalDescription.Path.Contract
          ContractEditRecommendation = template lang JsCanonicalDescription.Path.ContractEditRecommendation
          ContractParallelEdits = template lang JsCanonicalDescription.Path.ContractParallelEdits
          ContractParallelReads = template lang JsCanonicalDescription.Path.ContractParallelReads
          VerbRead = text lang JsCanonicalDescription.Path.VerbRead
          VerbSearch = text lang JsCanonicalDescription.Path.VerbSearch
          VerbTransform = text lang JsCanonicalDescription.Path.VerbTransform
          VerbRewrite = text lang JsCanonicalDescription.Path.VerbRewrite
          VerbCreate = text lang JsCanonicalDescription.Path.VerbCreate
          ReadRules = template lang JsCanonicalDescription.Path.ReadRules
          Recipes = text lang JsCanonicalDescription.Path.Recipes
          EditRules = template lang JsCanonicalDescription.Path.EditRules
          WriteRules = text lang JsCanonicalDescription.Path.WriteRules
          MutationRules = text lang JsCanonicalDescription.Path.MutationRules
          Rm = text lang JsCanonicalDescription.Path.Rm
          Mv = text lang JsCanonicalDescription.Path.Mv
          RunRules = text lang JsCanonicalDescription.Path.RunRules
          Deliver = text lang JsCanonicalDescription.Path.Deliver
          DelegationRules = text lang JsCanonicalDescription.Path.DelegationRules
          UltraFraming = text lang JsCanonicalDescription.Path.UltraFraming
          UltraUnavailable = text lang JsCanonicalDescription.Path.UltraUnavailable
          UltraEngineering = text lang JsCanonicalDescription.Path.UltraEngineering
          UltraInvestigation = text lang JsCanonicalDescription.Path.UltraInvestigation
          UltraDevOps = text lang JsCanonicalDescription.Path.UltraDevOps
          UltraManagerInvestigation = text lang JsCanonicalDescription.Path.UltraManagerInvestigation
          UltraPlan = text lang JsCanonicalDescription.Path.UltraPlan
          MechanicalSemantic = text lang JsCanonicalDescription.Path.MechanicalSemantic
          CommentAnchorOwnSearch = text lang JsCanonicalDescription.Path.CommentAnchorOwnSearch
          CommentIgnoreGy = text lang JsCanonicalDescription.Path.CommentIgnoreGy
          CommentHostCapability = text lang JsCanonicalDescription.Path.CommentHostCapability
          ReasonEmptyStringPattern = text lang JsCanonicalDescription.Path.ReasonEmptyStringPattern
          ReasonInvalidRegexp = text lang JsCanonicalDescription.Path.ReasonInvalidRegexp
          ReasonPatternType = text lang JsCanonicalDescription.Path.ReasonPatternType
          ReasonAnchorEmptyNames = text lang JsCanonicalDescription.Path.ReasonAnchorEmptyNames
          ReasonAnchorReserved = text lang JsCanonicalDescription.Path.ReasonAnchorReserved
          ReasonAnchorNamesDiffer = text lang JsCanonicalDescription.Path.ReasonAnchorNamesDiffer
          ReasonAnchorNamesUnique = text lang JsCanonicalDescription.Path.ReasonAnchorNamesUnique
          ReasonAnchorNotFound = text lang JsCanonicalDescription.Path.ReasonAnchorNotFound
          ReasonUnknownAnchor = text lang JsCanonicalDescription.Path.ReasonUnknownAnchor
          ReasonInvalidSlice = text lang JsCanonicalDescription.Path.ReasonInvalidSlice
          ReasonFileReadFailed = text lang JsCanonicalDescription.Path.ReasonFileReadFailed
          ReasonRunUnimplemented = text lang JsCanonicalDescription.Path.ReasonRunUnimplemented
          ReasonEditInvalid = text lang JsCanonicalDescription.Path.ReasonEditInvalid
          ReasonEditNotFound = text lang JsCanonicalDescription.Path.ReasonEditNotFound
          ReasonEditAmbiguous = text lang JsCanonicalDescription.Path.ReasonEditAmbiguous
          ReasonEditOverlap = text lang JsCanonicalDescription.Path.ReasonEditOverlap
          ReasonEditAtomicity = text lang JsCanonicalDescription.Path.ReasonEditAtomicity
          ReasonEditPreview = text lang JsCanonicalDescription.Path.ReasonEditPreview
          ReasonEditCopyReady = text lang JsCanonicalDescription.Path.ReasonEditCopyReady
          ReasonEditAttempted = text lang JsCanonicalDescription.Path.ReasonEditAttempted
          ReasonEditUnknownFields = text lang JsCanonicalDescription.Path.ReasonEditUnknownFields
          ReasonEditCandidate = text lang JsCanonicalDescription.Path.ReasonEditCandidate
          ReasonEditLine = text lang JsCanonicalDescription.Path.ReasonEditLine
          ReasonEditOverlaps = text lang JsCanonicalDescription.Path.ReasonEditOverlaps
          Park = template lang JsCanonicalDescription.Path.Park
          ParkedPayload = template lang JsCanonicalDescription.Path.ParkedPayload }

    let argProgram (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.ArgProgram

    let missingProgram (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.MissingProgram

    let viewDeclarationExtra (lang: ProviderLanguage) =
        text lang JsCanonicalDescription.Path.ViewDeclarationExtra

/// JS-073/JS-074: a generated js-* tool spec — the dynamic counterpart of the
/// static baseSpecs. Built from a generated surface (JS-002); execution goes
/// through JsToolWorkflow (sandbox → staging → preflight → durable facts →
/// commit) and the result renders through JsToolsResult (JS-016).
module JsToolSpec =

    let admissionFor (roleName: string) : ToolAdmission =
        let fsPermissions =
            set
                [ ToolPermission.Read
                  ToolPermission.Write
                  ToolPermission.Edit
                  ToolPermission.Glob
                  ToolPermission.Grep
                  ToolPermission.Move
                  ToolPermission.Remove
                  ToolPermission.Fork
                  ToolPermission.Join ]

        ToolAdmission.OfficeRole(fun _ r ->
            (string r).ToLowerInvariant() = roleName.ToLowerInvariant()
            && not (Set.isEmpty (Set.intersect (OfficeCapability.permissions r) fsPermissions)))

    [<Emit("$0===undefined||$0===null")>]
    let private isUndefined (value: obj) : bool = jsNative

    /// The directory one session's js-* surface executes in.
    ///
    /// The Host creates a Manager and her forked children rooted at the directory
    /// their owner registered — for a Change road that is the job's own worktree
    /// (`OrchestratorHost.directoryFor` → `HostForkRuntime.CreateChildSession`).
    /// The surface must execute where its session lives: resolving the plugin's
    /// project directory instead writes every road mutation into the target's own
    /// working tree, leaves the job worktree empty, and turns the candidate into a
    /// no-op commit whose publish rebase cannot conflict.
    ///
    /// A registered path is used as given, never existence-checked into the
    /// fallback: a worktree released under a live session must fail the write
    /// closed, not silently redirect it onto the target's checkout.
    let rootForSession
        (sessionDirectories: System.Collections.Generic.Dictionary<string, string>)
        (fallback: string)
        (sessionId: string)
        : string =
        match System.String.IsNullOrWhiteSpace sessionId, sessionDirectories.TryGetValue sessionId with
        | false, (true, path) when not (System.String.IsNullOrWhiteSpace path) -> path
        | _ -> fallback

    /// Build a ToolSpec for one generated surface.
    ///
    /// `workspaceRoot` is where the tool executes; `persistence` enables the
    /// durable transaction facts (JS-012) when the caller has an EventStore.
    /// `modelSourceProvider` supplies the model program (the tool arguments);
    /// the first version reads it from the Host arguments payload.
    let create
        (factory: HostToolFactory)
        (surface: JsSurface)
        (workspaceRoot: string)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option)
        (rootOf: string -> string)
        : ToolSpec =
        let readProgram (args: HostToolArguments) : string option = args.OptionalText "program"

        /// Build the three-state input object from the Host arguments:
        /// `{ program } | { unpark: { handle, value } } | { abandon: { handle } }`.
        let inputOf (args: HostToolArguments) : obj =
            let program = readProgram args

            let unpark = if isNull (args.Raw?unpark) then null else args.Raw?unpark

            let abandon = if isNull (args.Raw?abandon) then null else args.Raw?abandon

            match program, unpark, abandon with
            | Some source, _, _ -> createObj [ "program" ==> source ]
            | None, unparkValue, _ when not (isNull unparkValue) ->
                createObj
                    [ "unpark"
                      ==> createObj [ "handle" ==> string (unparkValue?handle); "value" ==> unparkValue?value ] ]
            | None, _, abandonValue when not (isNull abandonValue) ->
                createObj [ "abandon" ==> createObj [ "handle" ==> string (abandonValue?handle) ] ]
            | _ -> createObj []

        // The unpark/abandon arguments are nested objects; the Host schema
        // describes them as plain objects with string handle fields. The value
        // field of unpark is free-form (the injected resume value).
        let unparkSchema =
            ToolHostCodec.objectSchemaOf
                [ "handle", ToolHostCodec.stringSchema factory
                  "value", ToolHostCodec.anySchema factory ]
                factory

        let abandonSchema =
            ToolHostCodec.objectSchemaOf [ "handle", ToolHostCodec.stringSchema factory ] factory

        // ANCHOR view declaration (PLAN phase 3): prefixUntil protects the
        // cache prefix and thenRemove replaces anchors with the model's own
        // summaries for good. Optional; consumed once by the next transform.
        // The bilingual resource states the whole contract.
        let viewSchema =
            ToolHostCodec.objectSchemaOf
                [ "prefixUntil", ToolHostCodec.numberSchema factory
                  "thenRemove",
                  ToolHostCodec.recordSchemaOf
                      (ToolHostCodec.stringSchema factory)
                      (ToolHostCodec.stringSchema factory)
                      (JsDescriptionAssets.viewDeclarationExtra (ProviderLanguageBinding.readGlobalPreference ()))
                      factory ]
                factory

        { Name = surface.ToolName
          Description = surface.Description
          Arguments =
            [ "program",
              ToolHostCodec.stringSchemaDescribed
                  (JsDescriptionAssets.argProgram (ProviderLanguageBinding.readGlobalPreference ()))
                  factory
              "unpark", unparkSchema
              "abandon", abandonSchema
              "view", viewSchema ]
          Admission = admissionFor surface.RoleName
          Execute =
            fun args ctx ->
                task {
                    let input = inputOf args

                    let hasProgram = not (isNull (input?program))

                    let viewFailure =
                        match
                            Wanxiangshu.Repository.Programming.Js.Anchor.AnchorSurface.setView
                                ctx.SessionId
                                (args.Raw?view)
                        with
                        | Ok() -> None
                        | Error failure -> Some failure

                    match viewFailure with
                    | Some failure ->
                        return JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(failure, [], [], [], []))
                    | None when not hasProgram && isNull (input?unpark) && isNull (input?abandon) ->
                        return
                            ToolHostCodec.tomlObject
                                [ "error",
                                  ToolHostCodec.TString(
                                      JsDescriptionAssets.missingProgram (
                                          ProviderLanguageBinding.readGlobalPreference ()
                                      )
                                  ) ]
                    | None ->
                        // 10 s sandbox deadline; 1 MiB output bound (JS-054).
                        let deadlineEpochMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000L

                        // The session's own directory wins over the plugin workspace:
                        // a road's deliverable belongs in the worktree that session was
                        // created in, not in the target's checkout.
                        let root = rootOf ctx.SessionId

                        let! outcome =
                            JsToolWorkflow.execute
                                ctx.SessionId
                                surface.Capabilities
                                root
                                surface.BaseClassSource
                                input
                                10000
                                deadlineEpochMs
                                (1 <<< 20)
                                persistence
                                lifecyclePersistence
                                (fileAccessObservation |> Option.map (fun observe -> observe ctx))

                        return JsToolsResult.render outcome
                } }

    /// Build a ToolSpec with dedicated ToolAdmission.
    let createWithAdmission
        (factory: HostToolFactory)
        (surface: JsSurface)
        (admission: ToolAdmission)
        (workspaceRoot: string)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option)
        (rootOf: string -> string)
        : ToolSpec =
        let spec =
            create factory surface workspaceRoot persistence lifecyclePersistence fileAccessObservation rootOf

        { spec with Admission = admission }
