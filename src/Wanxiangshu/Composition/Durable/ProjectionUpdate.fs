namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Interaction.Attention
open Wanxiangshu.Interaction.Concern
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Foundation

/// Shared session-scoped projection-update algebra for the fold families
/// (formerly private helpers of `Fold`). `prefixOutcome` is shared by the
/// Context family and the Cognition envelope branch; the `apply*` appliers at
/// the end assemble the single-field fact families whose decision lives in the
/// owning domain fold.
module ProjectionUpdate =

    let private reject = FoldRejection.reject

    /// PERSIST-010 prefix-epoch refusals. Absorption policy is defined by
    /// `PrefixEpochProjection.describe` (Context/Prefix/Epoch.fs).
    let prefixOutcome factName projection result =
        match result with
        | Ok updated -> Ok updated
        | Error rejection ->
            PrefixEpochProjection.describe rejection
            |> Option.map (reject factName)
            |> Option.defaultValue (Ok projection)

    // ── session-scoped helpers ──────────────────────────────────────────────

    let updateSession sessionId apply projection =
        AgentProjection.update sessionId apply projection

    let tryUpdatePrefix sessionId apply projection =
        AgentProjection.tryUpdate
            sessionId
            (fun session ->
                apply (Option.defaultValue PrefixEpochProjection.empty session.PrefixEpoch)
                |> Result.map (fun updated ->
                    { session with
                        PrefixEpoch = Some updated }))
            projection

    let retireAuxiliaryInjectionVisibility (session: SessionAgentProjection) =
        { session with
            TipDelivery = session.TipDelivery |> Option.map TipDeliveryProjection.applyReanchor
            Guidelines = session.Guidelines |> Option.map GuidelineProjection.applyReanchor
            RequirementGrounding =
                session.RequirementGrounding
                |> Option.map RequirementGroundingProjection.applyReanchor }

    // ── single-field fact families ──────────────────────────────────────────

    let applyFission (projection: AgentProjectionSet) (fact: FissionFactCases) =
        FissionProjection.fold projection.Fission fact
        |> Result.map (fun updated -> { projection with Fission = updated })
        |> Result.mapError (fun reason ->
            { Fact = "Fission"
              Reason = sprintf "%A" reason })

    let applyConcern (projection: AgentProjectionSet) (fact: ConcernFactCases) =
        ConcernProjection.applyFact fact projection.Concern
        |> Result.map (fun updated -> { projection with Concern = updated })
        |> Result.mapError (fun reason -> { Fact = "Concern"; Reason = reason })

    let applyAttention
        (projection: AgentProjectionSet)
        (fact: AttentionFactCases)
        : Result<AgentProjectionSet, FoldRejection> =
        match fact with
        | AttentionFactCases.DeferredWorkRecorded payload ->
            Ok
                { projection with
                    Attention =
                        projection.Attention
                        |> AttentionProjection.record payload.SessionId payload.OccurrenceId payload.Text }
        | AttentionFactCases.DeferredWorkConsumed payload ->
            Ok
                { projection with
                    Attention =
                        projection.Attention
                        |> AttentionProjection.consume payload.SessionId payload.OccurrenceIds }
