namespace Wanxiangshu.Enforcer.InstitutionalLearning

open Wanxiangshu.Foundation.Identity

type LearningRecord =
    { OccurrenceId: string
      Kind: ExperienceKind
      Experience: string
      RulebookRevision: string
      Disposition: LearningDisposition
      FrozenResult: string
      ResurfacedDeferredWorkIds: string list }

/// Live-union view of one institutional born rule (bilingual leaf contract).
/// Merge order is derived from TipName, never from the durable fact's
/// LexicalOrder snapshot (behavior-diagnosis 003: derived order, not a
/// competing sequence carried by events).
type BornRule =
    { TipName: string
      EnforcerTextEn: string
      EnforcerTextZh: string
      MainTextEn: string
      MainTextZh: string
      Trigger: string
      Negative: string }

type InstitutionalLearningProjectionState =
    { BySession: Map<SessionId, Map<string, LearningRecord>>
      BornRules: BornRule list }

[<RequireQualifiedAccess>]
module InstitutionalLearningProjection =

    let empty =
        { BySession = Map.empty
          BornRules = [] }

    let tryFind sessionId occurrenceId state =
        state.BySession
        |> Map.tryFind sessionId
        |> Option.bind (Map.tryFind occurrenceId)

    let private applyCommitted
        sessionId
        occurrenceId
        kind
        experience
        rulebookRevision
        disposition
        frozenResult
        resurfacedDeferredWorkIds
        state
        =
        let current = Map.tryFind sessionId state.BySession |> Option.defaultValue Map.empty

        if Map.containsKey occurrenceId current then
            state
        else
            let record =
                { OccurrenceId = occurrenceId
                  Kind = kind
                  Experience = experience
                  RulebookRevision = rulebookRevision
                  Disposition = disposition
                  FrozenResult = frozenResult
                  ResurfacedDeferredWorkIds = resurfacedDeferredWorkIds }

            { state with
                BySession = Map.add sessionId (Map.add occurrenceId record current) state.BySession }

    let private applyBorn
        (payload:
            {| SessionId: SessionId
               OccurrenceId: string
               TipName: string
               EnforcerTextEn: string
               EnforcerTextZh: string
               MainTextEn: string
               MainTextZh: string
               Trigger: string
               Negative: string
               LexicalOrder: int |})
        state
        =
        let born =
            { TipName = payload.TipName
              EnforcerTextEn = payload.EnforcerTextEn
              EnforcerTextZh = payload.EnforcerTextZh
              MainTextEn = payload.MainTextEn
              MainTextZh = payload.MainTextZh
              Trigger = payload.Trigger
              Negative = payload.Negative }

        match state.BornRules |> List.tryFind (fun rule -> rule.TipName = born.TipName) with
        // Idempotent replay of the same birth receipt.
        | Some existing when existing = born -> Ok state
        // Shared TipName namespace across the live union fails closed
        // (behavior-diagnosis 001).
        | Some _ -> Error(sprintf "institutional rule tip name conflict: %s" born.TipName)
        | None ->
            Ok
                { state with
                    BornRules = state.BornRules @ [ born ] }

    /// Fold one institutional-learning fact. A TipName collision between born
    /// rules is a semantic rejection, not a silent overwrite.
    let apply fact state =
        match fact with
        | InstitutionalLearningFactCases.LearningDispositionCommitted payload ->
            applyCommitted
                payload.SessionId
                payload.OccurrenceId
                payload.Kind
                payload.Experience
                payload.RulebookRevision
                payload.Disposition
                payload.FrozenResult
                payload.ResurfacedDeferredWorkIds
                state
            |> Ok
        | InstitutionalLearningFactCases.InstitutionalRuleBorn payload -> applyBorn payload state
