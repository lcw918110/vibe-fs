namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Foundation

/// Assembly for the domain-owned fact families whose fold now decides on its own
/// slices and returns a change list. The bridges are the only place that turns a
/// family decision into aggregate writes, so no domain fold has to know the
/// session record, the aggregate's write algebra, or the spine's rejection shape.
module PromptAuthorityProjectionBridge =

    let private authorityOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.PromptAuthority)

    let private applyChange (projection: AgentProjectionSet) (change: PromptAuthorityProjectionChange) =
        match change with
        | PromptAuthorityProjectionChange.PromptAuthoritySet(sessionId, authority) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        PromptAuthority = Some authority })
                projection
        | PromptAuthorityProjectionChange.ProviderFailuresSet(sessionId, failures) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        ProviderFailures = Some failures })
                projection

    let fold (projection: AgentProjectionSet) (fact: PromptFactCases) : Result<AgentProjectionSet, FoldRejection> =
        match PromptFactFold.fold (authorityOf projection) projection.RuntimeStartCount fact with
        | Ok changes -> DelegationProjectionBridge.admitAuthority (List.fold applyChange projection changes) fact
        | Error rejection ->
            FoldRejection.reject
                (PromptAuthorityFoldRejection.fact rejection)
                (PromptAuthorityFoldRejection.message rejection)

module ProviderFailureProjectionBridge =

    let private providerFailuresOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.ProviderFailures)

    let private applyChange (projection: AgentProjectionSet) (change: ProviderFailureProjectionChange) =
        match change with
        | ProviderFailureProjectionChange.ProviderFailuresSet(sessionId, failures) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        ProviderFailures = Some failures })
                projection

    let fold
        (projection: AgentProjectionSet)
        (fact: ProviderFailureFactCases)
        : Result<AgentProjectionSet, FoldRejection> =
        match ProviderFailureFactFold.fold (providerFailuresOf projection) fact with
        | Ok changes -> Ok(List.fold applyChange projection changes)
        | Error rejection ->
            FoldRejection.reject
                (ProviderFailureFoldRejection.fact rejection)
                (ProviderFailureFoldRejection.message rejection)

module CompanionProjectionBridge =

    let private companionOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.Companion)

    let private xTraceOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.XTrace)

    let private applyChange (projection: AgentProjectionSet) (change: CompanionProjectionChange) =
        match change with
        | CompanionProjectionChange.AssociationsSet associations ->
            { projection with
                Associations = associations }
        | CompanionProjectionChange.CompanionSet(sessionId, companion) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        Companion = Some companion })
                projection
        | CompanionProjectionChange.XTraceSet(sessionId, xTrace) ->
            ProjectionUpdate.updateSession sessionId (fun session -> { session with XTrace = Some xTrace }) projection

    let fold (projection: AgentProjectionSet) (fact: CompanionFactCases) : Result<AgentProjectionSet, FoldRejection> =
        match CompanionFactFold.fold projection.Associations (companionOf projection) (xTraceOf projection) fact with
        | Ok changes -> Ok(List.fold applyChange projection changes)
        | Error rejection ->
            FoldRejection.reject (CompanionFoldRejection.fact rejection) (CompanionFoldRejection.message rejection)

module ContextProjectionBridge =

    let private cyclesOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.BloggerCycles)

    let private enforcementOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.Enforcement)

    let private blogOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.Blog)

    let private prefixEpochOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        Map.tryFind sessionId projection.Sessions
        |> Option.bind (fun session -> session.PrefixEpoch)

    let private checkpointWindowOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        projection.TodoCheckpoints
        |> Map.tryFind sessionId
        |> Option.defaultValue PhaseWindow.emptyWindow

    let private turnOfCheckpoint (projection: AgentProjectionSet) (sessionId: SessionId) (callId: ToolCallId) =
        AgentProjection.tryFind sessionId projection
        |> Option.bind (fun session -> session.XTrace)
        |> Option.bind (XTraceProjection.tryTurnOfToolCallId callId)

    let private setCheckpointWindow
        (sessionId: SessionId)
        (window: PhaseWindow.PhaseCommitWindow)
        (projection: AgentProjectionSet)
        =
        let checkpoints =
            if List.isEmpty window.Checkpoints then
                Map.remove sessionId projection.TodoCheckpoints
            else
                Map.add sessionId window projection.TodoCheckpoints

        { projection with
            TodoCheckpoints = checkpoints }

    let private appendCheckpoint projection sessionId callId =
        checkpointWindowOf projection sessionId
        |> PhaseWindow.appendCheckpoint callId
        |> fun updated -> setCheckpointWindow sessionId updated projection

    let private pruneCommittedPrefix projection sessionId cutoffExclusive =
        checkpointWindowOf projection sessionId
        |> PhaseWindow.pruneBefore (turnOfCheckpoint projection sessionId) cutoffExclusive
        |> fun updated -> setCheckpointWindow sessionId updated projection

    let private clearCheckpointGeneration projection sessionId =
        { projection with
            TodoCheckpoints = Map.remove sessionId projection.TodoCheckpoints }

    let private applyChange (projection: AgentProjectionSet) (change: ContextProjectionChange) =
        match change with
        | ContextProjectionChange.BloggerCyclesSet(sessionId, cycles) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        BloggerCycles = Some cycles })
                projection
        | ContextProjectionChange.EnforcementSet(sessionId, enforcement) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        Enforcement = Some enforcement })
                projection
        | ContextProjectionChange.BlogSet(sessionId, blog) ->
            ProjectionUpdate.updateSession sessionId (fun session -> { session with Blog = Some blog }) projection
        | ContextProjectionChange.PrefixEpochSet(sessionId, epoch) ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        PrefixEpoch = Some epoch })
                projection
        | ContextProjectionChange.BlogReanchored sessionId ->
            ProjectionUpdate.updateSession
                sessionId
                (fun session ->
                    { session with
                        Blog = session.Blog |> Option.map BlogProjection.applyReanchor })
                projection
        | ContextProjectionChange.AuxiliaryVisibilityRetired sessionId ->
            ProjectionUpdate.updateSession sessionId ProjectionUpdate.retireAuxiliaryInjectionVisibility projection

    let private foldOwned
        (projection: AgentProjectionSet)
        (fact: ContextFactCases)
        : Result<AgentProjectionSet, FoldRejection> =
        match
            ContextFactFold.fold
                (cyclesOf projection)
                (enforcementOf projection)
                (blogOf projection)
                (prefixEpochOf projection)
                fact
        with
        | Ok changes -> Ok(List.fold applyChange projection changes)
        | Error rejection ->
            FoldRejection.reject (ContextFoldRejection.fact rejection) (ContextFoldRejection.message rejection)

    let fold (projection: AgentProjectionSet) (fact: ContextFactCases) : Result<AgentProjectionSet, FoldRejection> =
        match fact with
        | ContextFactCases.TodoCheckpointCommitted payload ->
            Ok(appendCheckpoint projection payload.SessionId payload.ToolCallId)
        | ContextFactCases.PrefixRebaseCommitted payload ->
            foldOwned projection fact
            |> Result.map (fun updated -> pruneCommittedPrefix updated payload.SessionId payload.CutoffExclusive)
        | ContextFactCases.ContextReanchored payload ->
            foldOwned projection fact
            |> Result.map (fun updated -> clearCheckpointGeneration updated payload.SessionId)
        | ContextFactCases.TenureReanchored payload ->
            foldOwned projection fact
            |> Result.map (fun updated -> clearCheckpointGeneration updated payload.SessionId)
        | _ -> foldOwned projection fact
