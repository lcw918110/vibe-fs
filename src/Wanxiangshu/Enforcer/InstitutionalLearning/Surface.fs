namespace Wanxiangshu.Enforcer.InstitutionalLearning

open Fable.Core
open Wanxiangshu.Enforcer
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module InstitutionalLearningSurface =

    type private BoxedState(state: InstitutionalLearningProjectionState) =
        member _.State = state

    let private stateOf (value: obj) = (unbox<BoxedState> value).State
    let private boxed state = BoxedState(state) :> obj

    let private dispositionName disposition =
        match disposition with
        | LearningDisposition.Absorb _ -> "ABSORB"
        | LearningDisposition.Birth _ -> "BIRTH"
        | LearningDisposition.Discard _ -> "DISCARD"

    /// String property of a JS-native candidate object; null unless the
    /// property is a real string.
    [<Emit("($0 == null || typeof $0[$1] !== 'string') ? null : $0[$1]")>]
    let private textPropertyOf (owner: obj) (name: string) : string = jsNative

    let private rulesOf (ruleNames: string array) =
        ruleNames
        |> Array.mapi (fun index name ->
            { Name = name
              EnforcerText = "rule"
              MainText = "main"
              RuleId = name
              FieldName = name
              LexicalOrder = index + 1 })
        |> Array.toList

    let private candidateOf (value: obj) : BirthCandidate option =
        if isNull value then
            None
        else
            Some
                { TipName = textPropertyOf value "tipName"
                  EnforcerTextEn = textPropertyOf value "enforcerTextEn"
                  EnforcerTextZh = textPropertyOf value "enforcerTextZh"
                  MainTextEn = textPropertyOf value "mainTextEn"
                  MainTextZh = textPropertyOf value "mainTextZh"
                  Trigger = textPropertyOf value "trigger"
                  Negative = textPropertyOf value "negative" }

    let evaluate (experience: string) (ruleNames: string array) (candidate: obj) : obj =
        let disposition =
            InstitutionalEnhancer.evaluate experience (rulesOf ruleNames) (candidateOf candidate)

        box {| disposition = dispositionName disposition |}

    /// Pure revision-contract decision (WHAT institutional-learning-002):
    /// one evaluation, at most one reevaluation after live-revision drift,
    /// explicit conflict on the second mismatch. Each load consumes the next
    /// rule-name snapshot, mirroring a fresh live-rulebook read.
    let learn (experience: string) (candidate: obj) (ruleSnapshots: string array array) : obj =
        let snapshots = if isNull ruleSnapshots then [||] else ruleSnapshots
        // DSL-MUTABLE: algorithm-scratch — rule-snapshot cursor; each load
        // consumes the next snapshot to mirror a fresh live-rulebook read
        let mutable next = 0

        let load () =
            // An exhausted snapshot sequence repeats the last snapshot: the live
            // rulebook does not drift to an empty book between two reads.
            let names =
                if next < snapshots.Length then
                    snapshots.[next]
                elif snapshots.Length > 0 then
                    snapshots.[snapshots.Length - 1]
                else
                    [||]

            next <- next + 1
            rulesOf names

        match InstitutionalEnhancer.commitDecision experience (candidateOf candidate) load with
        | InstitutionalEnhancer.LearnOutcome.LearnCommitted(disposition, revision, reevaluated) ->
            box
                {| disposition = dispositionName disposition
                   revision = revision
                   reevaluated = reevaluated |}
        | InstitutionalEnhancer.LearnOutcome.LearnRevisionConflict revision ->
            box
                {| conflict = "revision-conflict"
                   revision = revision |}

    let revision (ruleNames: string array) =
        ruleNames |> rulesOf |> InstitutionalEnhancer.rulebookRevision

    let empty () =
        boxed InstitutionalLearningProjection.empty

    let commit session occurrence kind experience revision disposition frozen resurfaced state =
        let kind =
            if kind = "celebrate" then
                ExperienceKind.Celebrate
            else
                ExperienceKind.Regret

        let disposition =
            if disposition = "ABSORB" then
                LearningDisposition.Absorb "existing"
            elif disposition = "BIRTH" then
                LearningDisposition.Birth "candidate"
            else
                LearningDisposition.Discard "discarded"

        let fact =
            InstitutionalLearningFactCases.LearningDispositionCommitted
                {| SessionId = SessionId.create session
                   OccurrenceId = occurrence
                   Kind = kind
                   Experience = experience
                   RulebookRevision = revision
                   Disposition = disposition
                   FrozenResult = frozen
                   ResurfacedDeferredWorkIds = Array.toList resurfaced |}

        match InstitutionalLearningProjection.apply fact (stateOf state) with
        | Ok updated -> boxed updated
        | Error reason -> failwith reason

    /// Fold one InstitutionalRuleBorn fact into the projection. A TipName
    /// collision with a different payload fails closed.
    let born (session: string) (occurrence: string) (candidate: obj) (state: 'state) : obj =
        match candidateOf candidate with
        | None -> failwith "born requires a candidate object"
        | Some candidate ->
            let fact =
                InstitutionalLearningFactCases.InstitutionalRuleBorn
                    {| SessionId = SessionId.create session
                       OccurrenceId = occurrence
                       TipName = candidate.TipName
                       EnforcerTextEn = candidate.EnforcerTextEn
                       EnforcerTextZh = candidate.EnforcerTextZh
                       MainTextEn = candidate.MainTextEn
                       MainTextZh = candidate.MainTextZh
                       Trigger = candidate.Trigger
                       Negative = candidate.Negative
                       LexicalOrder = 0 |}

            match InstitutionalLearningProjection.apply fact (stateOf state) with
            | Ok updated -> boxed updated
            | Error reason -> failwith reason

    /// Live born rules in derived merge order (TipName sort); lexicalOrder is
    /// the derived position, never the durable snapshot value.
    let bornRules (state: 'state) : obj =
        let rules =
            (stateOf state).BornRules
            |> List.sortBy (fun rule -> rule.TipName)
            |> List.mapi (fun index rule ->
                {| tipName = rule.TipName
                   enforcerTextEn = rule.EnforcerTextEn
                   enforcerTextZh = rule.EnforcerTextZh
                   mainTextEn = rule.MainTextEn
                   mainTextZh = rule.MainTextZh
                   trigger = rule.Trigger
                   negative = rule.Negative
                   lexicalOrder = index + 1 |})
            |> Array.ofList

        box rules

    let frozen session occurrence state : obj =
        match InstitutionalLearningProjection.tryFind (SessionId.create session) occurrence (stateOf state) with
        | Some record -> box record.FrozenResult
        | None -> null
