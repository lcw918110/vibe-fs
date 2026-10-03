namespace Wanxiangshu.Enforcer.InstitutionalLearning

open System
open Wanxiangshu.Enforcer
open Wanxiangshu.Host

[<RequireQualifiedAccess>]
module InstitutionalEnhancer =

    let rulebookRevision (rules: EnforcerRule list) =
        rules
        |> List.sortBy _.LexicalOrder
        |> List.map (fun rule -> rule.Name + "\u001f" + rule.EnforcerText + "\u001f" + rule.MainText)
        |> String.concat "\u001e"
        |> HostDigest.sha256Hex

    let private nonEmpty (value: string) =
        not (isNull value) && value.Trim().Length > 0

    /// Mechanical admission boundary for a BIRTH candidate: unique TipName in
    /// the live rule set and complete bilingual leaf text with trigger and
    /// negative/distinction. Semantic abstraction (novelty, long-term value)
    /// stays with the caller; this check only refuses mechanically
    /// inadmissible candidates.
    let candidateAdmissible (candidate: BirthCandidate) (rules: EnforcerRule list) =
        nonEmpty candidate.TipName
        && nonEmpty candidate.EnforcerTextEn
        && nonEmpty candidate.EnforcerTextZh
        && nonEmpty candidate.MainTextEn
        && nonEmpty candidate.MainTextZh
        && nonEmpty candidate.Trigger
        && nonEmpty candidate.Negative
        && not (rules |> List.exists (fun rule -> rule.Name = candidate.TipName.Trim()))

    /// One bounded evaluation. An admissible candidate concludes BIRTH; a
    /// missing or inadmissible candidate falls back to the existing
    /// absorb-by-explicit-rule-name / discard logic. Pure: no IO, no provider.
    let evaluate
        (experience: string)
        (rules: EnforcerRule list)
        (candidate: BirthCandidate option)
        : LearningDisposition =
        match candidate with
        | Some candidate when candidateAdmissible candidate rules -> LearningDisposition.Birth(candidate.TipName.Trim())
        | _ ->
            let lower = experience.ToLowerInvariant()

            rules
            |> List.tryFind (fun rule -> lower.Contains(rule.Name.ToLowerInvariant(), StringComparison.Ordinal))
            |> Option.map (fun rule -> LearningDisposition.Absorb rule.Name)
            |> Option.defaultValue (LearningDisposition.Discard "no-reusable-mechanism")

    /// WHAT institutional-learning-002 revision contract around one
    /// evaluation. Before committing a BIRTH the live revision is re-read; on
    /// drift the evaluation runs once more against the latest live rulebook;
    /// a second conflict fails explicitly with zero commits.
    type LearnOutcome =
        | LearnCommitted of disposition: LearningDisposition * revision: string * reevaluated: bool
        | LearnRevisionConflict of revision: string

    let private settleReevaluatedBirth
        (load: unit -> EnforcerRule list)
        (freshRevision: string)
        (reevaluatedTip: string)
        : LearnOutcome =
        let final = load ()
        let finalRevision = rulebookRevision final

        if finalRevision = freshRevision then
            LearnCommitted(LearningDisposition.Birth reevaluatedTip, finalRevision, true)
        else
            LearnRevisionConflict finalRevision

    let private reevaluateBirthOnDrift
        (experience: string)
        (candidate: BirthCandidate option)
        (load: unit -> EnforcerRule list)
        (fresh: EnforcerRule list)
        : LearnOutcome =
        let freshRevision = rulebookRevision fresh

        match evaluate experience fresh candidate with
        | LearningDisposition.Birth reevaluatedTip -> settleReevaluatedBirth load freshRevision reevaluatedTip
        | other -> LearnCommitted(other, freshRevision, true)

    let private commitBirth
        (experience: string)
        (candidate: BirthCandidate option)
        (load: unit -> EnforcerRule list)
        (tip: string)
        (revision: string)
        : LearnOutcome =
        let fresh = load ()
        let freshRevision = rulebookRevision fresh

        if freshRevision = revision then
            LearnCommitted(LearningDisposition.Birth tip, revision, false)
        else
            reevaluateBirthOnDrift experience candidate load fresh

    let commitDecision
        (experience: string)
        (candidate: BirthCandidate option)
        (load: unit -> EnforcerRule list)
        : LearnOutcome =
        let rules = load ()
        let revision = rulebookRevision rules

        match evaluate experience rules candidate with
        | LearningDisposition.Birth tip -> commitBirth experience candidate load tip revision
        | other -> LearnCommitted(other, revision, false)
