namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.InstitutionalLearning
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Resources

[<RequireQualifiedAccess>]
module InstitutionalLearningTools =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let CelebrateDescription = "institutional-learning/celebrate-description"

        [<Literal>]
        let RegretDescription = "institutional-learning/regret-description"

        [<Literal>]
        let ExperienceArgument = "institutional-learning/experience-argument"

        [<Literal>]
        let CandidateArgument = "institutional-learning/candidate-argument"

        [<Literal>]
        let Absorbed = "institutional-learning/absorbed"

        [<Literal>]
        let Born = "institutional-learning/born"

        [<Literal>]
        let Discarded = "institutional-learning/discarded"

        [<Literal>]
        let RevisionConflict = "institutional-learning/revision-conflict"

        [<Literal>]
        let ResurfacedHeading = "institutional-learning/resurfaced-heading"

        [<Literal>]
        let ResurfacedItem = "institutional-learning/resurfaced-item"

        [<Literal>]
        let Invalid = "institutional-learning/invalid"

        [<Literal>]
        let DurableUnavailable = "institutional-learning/durable-unavailable"

    let private languageOf (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private trim (value: string) =
        if isNull value then "" else value.Trim()

    /// Optional BIRTH candidate decoded from the tool argument object. A
    /// candidate with any missing field is absent: admission falls back to
    /// ABSORB/DISCARD rather than birthing an incomplete rule.
    let private candidateOf (args: HostToolArguments) : BirthCandidate option =
        let field name = args.OptionalTextIn("candidate", name)

        match
            field "tipName",
            field "enforcerTextEn",
            field "enforcerTextZh",
            field "mainTextEn",
            field "mainTextZh",
            field "trigger",
            field "negative"
        with
        | Some tipName, Some en, Some zh, Some mainEn, Some mainZh, Some trigger, Some negative ->
            Some
                { TipName = tipName
                  EnforcerTextEn = en
                  EnforcerTextZh = zh
                  MainTextEn = mainEn
                  MainTextZh = mainZh
                  Trigger = trigger
                  Negative = negative }
        | _ -> None

    let private dispositionInstructions language disposition =
        match disposition with
        | LearningDisposition.Absorb rule ->
            ProviderProse.instructionLines language Path.Absorbed (Map [ "rule", rule ])
        | LearningDisposition.Birth tip -> ProviderProse.instructionLines language Path.Born (Map [ "rule", tip ])
        | LearningDisposition.Discard _ -> ProviderProse.instructionLines language Path.Discarded Map.empty

    /// Live union (behavior-diagnosis 001): shipped built-in catalog plus
    /// born institutional rules, projected into the current language with no
    /// cross-language fallback (behavior-diagnosis 005). Born rules follow the
    /// built-in block in TipName-derived order.
    let private liveRules language (state: InstitutionalLearningProjectionState) =
        let builtIn = EnforcerCatalogResource.loadFor language
        let offset = List.length builtIn

        let born =
            state.BornRules
            |> List.sortBy (fun rule -> rule.TipName)
            |> List.mapi (fun index rule ->
                let enforcerText, mainText =
                    match language with
                    | ProviderLanguage.English -> rule.EnforcerTextEn, rule.MainTextEn
                    | ProviderLanguage.SimplifiedChinese -> rule.EnforcerTextZh, rule.MainTextZh

                { Name = rule.TipName
                  EnforcerText = enforcerText
                  MainText = mainText
                  RuleId = rule.TipName
                  FieldName = rule.TipName
                  LexicalOrder = offset + index + 1 })

        builtIn @ born

    let private resurfacedInstructions language (items: (string * string) list) =
        match items with
        | [] -> []
        | values ->
            ProviderProse.instructionLines language Path.ResurfacedHeading Map.empty
            @ (values
               |> List.collect (fun (_, text) ->
                   ProviderProse.instructionLines language Path.ResurfacedItem (Map [ "work", text ])))

    let private instructionResult language path subs =
        ProviderProse.instructionLines language path subs
        |> LlmFacing.renderInstructions

    let private pendingFor kind (durable: InstitutionalLearningJournalPort) sessionId =
        match kind with
        | ExperienceKind.Celebrate -> durable.PendingAttentionWorkPairs sessionId
        | ExperienceKind.Regret -> []

    type private LearningCommitFailure =
        | RevisionConflict
        | AppendUnavailable of InstitutionalLearningAppendFailure

    let private commitDispositionAppend
        (append: InstitutionalLearningFactCases -> Task<Result<unit, InstitutionalLearningAppendFailure>>)
        (appendCommitted:
            LearningDisposition -> string -> string -> (string * string) list -> InstitutionalLearningFactCases)
        disposition
        revision
        frozen
        pending
        =
        task {
            let! committedAppend = append (appendCommitted disposition revision frozen pending)

            match committedAppend with
            | Error failure -> return Error(LearningCommitFailure.AppendUnavailable failure)
            | Ok() -> return Ok frozen
        }

    let private commitBirthDisposition
        (append: InstitutionalLearningFactCases -> Task<Result<unit, InstitutionalLearningAppendFailure>>)
        (appendCommitted:
            LearningDisposition -> string -> string -> (string * string) list -> InstitutionalLearningFactCases)
        (durable: InstitutionalLearningJournalPort)
        (candidate: BirthCandidate option)
        language
        sessionId
        occurrence
        disposition
        revision
        frozen
        pending
        tipName
        =
        task {
            // Unreachable with a None candidate: evaluate concludes
            // BIRTH only for an admissible candidate.
            let candidate =
                Option.defaultWith (fun () -> invalidOp "BIRTH disposition requires an admissible candidate") candidate

            let state = durable.ReadState sessionId

            let lexicalOrder =
                List.length state.BornRules
                + List.length (EnforcerCatalogResource.loadFor language)
                + 1

            let bornFact =
                InstitutionalLearningFactCases.InstitutionalRuleBorn
                    {| SessionId = sessionId
                       OccurrenceId = occurrence
                       TipName = tipName
                       EnforcerTextEn = candidate.EnforcerTextEn.Trim()
                       EnforcerTextZh = candidate.EnforcerTextZh.Trim()
                       MainTextEn = candidate.MainTextEn.Trim()
                       MainTextZh = candidate.MainTextZh.Trim()
                       Trigger = candidate.Trigger.Trim()
                       Negative = candidate.Negative.Trim()
                       LexicalOrder = lexicalOrder |}

            let! bornAppend = append bornFact

            match bornAppend with
            | Error failure -> return Error(LearningCommitFailure.AppendUnavailable failure)
            | Ok() -> return! commitDispositionAppend append appendCommitted disposition revision frozen pending
        }

    let private commitDispositionOutcome
        kind
        (durable: InstitutionalLearningJournalPort)
        (candidate: BirthCandidate option)
        language
        sessionId
        occurrence
        providerRun
        experience
        disposition
        revision
        =
        task {
            let pending = pendingFor kind durable sessionId

            let frozen =
                LlmFacing.renderInstructions (
                    dispositionInstructions language disposition
                    @ resurfacedInstructions language pending
                )

            let append fact =
                durable.Append sessionId providerRun fact

            let appendCommitted disposition revision frozen pending =
                InstitutionalLearningFactCases.LearningDispositionCommitted
                    {| SessionId = sessionId
                       OccurrenceId = occurrence
                       Kind = kind
                       Experience = experience
                       RulebookRevision = revision
                       Disposition = disposition
                       FrozenResult = frozen
                       ResurfacedDeferredWorkIds = pending |> List.map fst |}

            match disposition with
            | LearningDisposition.Birth tipName ->
                return!
                    commitBirthDisposition
                        append
                        appendCommitted
                        durable
                        candidate
                        language
                        sessionId
                        occurrence
                        disposition
                        revision
                        frozen
                        pending
                        tipName
            | _ -> return! commitDispositionAppend append appendCommitted disposition revision frozen pending
        }

    let private commitFreshLearning
        kind
        (durable: InstitutionalLearningJournalPort)
        experience
        (candidate: BirthCandidate option)
        language
        sessionId
        occurrence
        providerRun
        =
        let load () =
            liveRules language (durable.ReadState sessionId)

        match InstitutionalEnhancer.commitDecision experience candidate load with
        | InstitutionalEnhancer.LearnOutcome.LearnRevisionConflict _ ->
            // Zero commits, explicit failure, no intermediate state.
            Task.FromResult(Error LearningCommitFailure.RevisionConflict)
        | InstitutionalEnhancer.LearnOutcome.LearnCommitted(disposition, revision, _) ->
            commitDispositionOutcome
                kind
                durable
                candidate
                language
                sessionId
                occurrence
                providerRun
                experience
                disposition
                revision

    let private commitLearning
        kind
        (durable: InstitutionalLearningJournalPort)
        experience
        (candidate: BirthCandidate option)
        language
        sessionId
        occurrence
        providerRun
        =
        task {
            match InstitutionalLearningProjection.tryFind sessionId occurrence (durable.ReadState sessionId) with
            | Some record -> return Ok record.FrozenResult
            | None ->
                return! commitFreshLearning kind durable experience candidate language sessionId occurrence providerRun
        }

    let private executeDurable
        kind
        durable
        experience
        (candidate: BirthCandidate option)
        language
        callId
        (ctx: HostToolContext)
        =
        task {
            let sessionId = SessionId.create ctx.SessionId
            let occurrence = ToolCallId.value callId

            let! result =
                commitLearning kind durable experience candidate language sessionId occurrence ctx.ProviderRunId

            match result with
            | Ok frozen -> return frozen
            | Error LearningCommitFailure.RevisionConflict ->
                return instructionResult language Path.RevisionConflict Map.empty
            | Error(LearningCommitFailure.AppendUnavailable _) ->
                return instructionResult language Path.DurableUnavailable Map.empty
        }

    let private execute
        kind
        (journal: InstitutionalLearningJournalPort option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        =
        task {
            let experience = args.Text "experience" |> trim
            let language = languageOf ctx
            let candidate = candidateOf args

            match journal, ctx.ToolCallId with
            | _, _ when experience.Length = 0 -> return instructionResult language Path.Invalid Map.empty
            | Some durable, Some callId when not (String.IsNullOrWhiteSpace ctx.SessionId) ->
                return! executeDurable kind durable experience candidate language callId ctx
            | _ -> return instructionResult language Path.DurableUnavailable Map.empty
        }

    let admission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ (r: Role) -> r <> Role.Blogger && r <> Role.Distiller)

    let specs factory (journal: InstitutionalLearningJournalPort option) =
        let language = ProviderLanguageBinding.readGlobalPreference ()

        let argument =
            ToolHostCodec.stringSchemaDescribed
                (ProviderProse.render language Path.ExperienceArgument Map.empty)
                factory

        let candidateArgument =
            ToolHostCodec.birthCandidateSchemaDescribed
                (ProviderProse.render language Path.CandidateArgument Map.empty)
                factory

        [ { Name = "celebrate"
            Description = ProviderProse.render language Path.CelebrateDescription Map.empty
            Arguments = [ "experience", argument; "candidate", candidateArgument ]
            Admission = admission
            Execute = execute ExperienceKind.Celebrate journal }
          { Name = "regret"
            Description = ProviderProse.render language Path.RegretDescription Map.empty
            Arguments = [ "experience", argument; "candidate", candidateArgument ]
            Admission = admission
            Execute = execute ExperienceKind.Regret journal } ]
