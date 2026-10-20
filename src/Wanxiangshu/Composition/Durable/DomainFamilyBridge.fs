namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Context.Companion
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

    let private checkpointWindowOf (projection: AgentProjectionSet) (sessionId: SessionId) =
        projection.ViewCheckpoints
        |> Map.tryFind sessionId
        |> Option.defaultValue PhaseWindow.emptyWindow

    let private setCheckpointWindow
        (sessionId: SessionId)
        (window: PhaseWindow.PhaseCommitWindow)
        (projection: AgentProjectionSet)
        =
        let checkpoints =
            if List.isEmpty window.Checkpoints then
                Map.remove sessionId projection.ViewCheckpoints
            else
                Map.add sessionId window projection.ViewCheckpoints

        { projection with
            ViewCheckpoints = checkpoints }

    let private appendCheckpoint projection sessionId callId =
        checkpointWindowOf projection sessionId
        |> PhaseWindow.appendCheckpoint callId
        |> fun updated -> setCheckpointWindow sessionId updated projection

    let private clearCheckpointGeneration projection sessionId =
        { projection with
            ViewCheckpoints = Map.remove sessionId projection.ViewCheckpoints }

    /// context-compression-019: a reanchor retires the auxiliary injection
    /// visibility of the old provider horizon. The durable facts stay as audit
    /// history; only the visible floor moves.
    let private retireAuxiliaryVisibility projection sessionId =
        ProjectionUpdate.updateSession sessionId ProjectionUpdate.retireAuxiliaryInjectionVisibility projection

    let fold (projection: AgentProjectionSet) (fact: ContextFactCases) : Result<AgentProjectionSet, FoldRejection> =
        match fact with
        | ContextFactCases.ViewCheckpointCommitted payload ->
            Ok(appendCheckpoint projection payload.SessionId payload.ToolCallId)
        | ContextFactCases.ContextReanchored payload ->
            Ok(clearCheckpointGeneration (retireAuxiliaryVisibility projection payload.SessionId) payload.SessionId)
        | ContextFactCases.TenureReanchored payload ->
            Ok(clearCheckpointGeneration (retireAuxiliaryVisibility projection payload.SessionId) payload.SessionId)
