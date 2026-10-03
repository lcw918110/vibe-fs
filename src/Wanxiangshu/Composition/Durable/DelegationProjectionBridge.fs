namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Foundation

module DelegationProjectionBridge =

    let private sessionState (projection: AgentProjectionSet) (sessionId: SessionId) : DelegationSessionState option =
        Map.tryFind sessionId projection.Sessions
        |> Option.map (fun s ->
            { Handles = s.Handles
              ToolEstimate = s.DelegatedToolEstimate })

    let private handoffFrontier (projection: AgentProjectionSet) (key: string) : int64 option =
        Map.tryFind key projection.DelegationCompletedHandoffs

    let private applyChange (projection: AgentProjectionSet) (change: DelegationProjectionChange) : AgentProjectionSet =
        match change with
        | ReplaceSessionState(sessionId, state) ->
            let current =
                Map.tryFind sessionId projection.Sessions
                |> Option.defaultValue AgentProjection.emptySession

            let updated =
                { current with
                    Handles = state.Handles
                    DelegatedToolEstimate = state.ToolEstimate }

            { projection with
                Sessions = Map.add sessionId updated projection.Sessions }

        | IndexChildHandle(childSessionId, record) ->
            { projection with
                HandleByChildSession = Map.add childSessionId record projection.HandleByChildSession }

        | MoveHandoffFrontier(key, endExclusive) ->
            { projection with
                DelegationCompletedHandoffs = Map.add key endExclusive projection.DelegationCompletedHandoffs }

        | TerminatedChildWork(work, logicalRunId) ->
            let session =
                Map.tryFind work.ChildSessionId projection.Sessions
                |> Option.defaultValue AgentProjection.emptySession

            let authority =
                session.PromptAuthority
                |> Option.bind (fun current ->
                    PromptAuthorityRun.closeCompletedAgentOwnerChildWork logicalRunId work.AuthorityRoot current
                    |> Result.toOption)
                |> Option.orElse session.PromptAuthority

            { projection with
                Sessions =
                    Map.add
                        work.ChildSessionId
                        { session with
                            PromptAuthority = authority }
                        projection.Sessions }
        | TerminatedChildHandle(parentId, childSessionId) ->
            let session =
                Map.tryFind childSessionId projection.Sessions
                |> Option.defaultValue AgentProjection.emptySession

            let scopedChild =
                sessionState projection parentId
                |> Option.bind _.Handles
                |> Option.exists (fun handles ->
                    handles.Works |> Map.exists (fun key _ -> key.ChildSessionId = childSessionId))

            let updatedAuthority =
                (if scopedChild then None else session.PromptAuthority)
                |> Option.bind (fun current ->
                    current.ActiveLogicalRun
                    |> Option.bind (fun active ->
                        PromptAuthorityRun.closeCompletedAgentOwnerChildWork
                            active.LogicalRunId
                            active.AuthorityRootUserMessageId
                            current
                        |> Result.toOption))
                |> Option.orElse session.PromptAuthority

            { projection with
                Sessions =
                    Map.add
                        childSessionId
                        { session with
                            PromptAuthority = updatedAuthority }
                        projection.Sessions }

    let private admitActiveAuthority parentId handle childId projection state handles authority =
        match HandleProjection.admitWork parentId handle authority handles with
        // Exact landing/owner evidence is not yet durable — keep the fold and retry
        // when a later fact supplies the missing PhysicalLanding / identity seed.
        | Error WorkNotAdmitted -> Ok projection
        | Error reason -> FoldRejection.reject "HandleWorkAdmission" (sprintf "%A" reason)
        | Ok updated ->
            let next =
                applyChange projection (ReplaceSessionState(parentId, { state with Handles = Some updated }))

            Ok(applyChange next (IndexChildHandle(childId, HandleProjection.tryFind handle updated |> Option.get)))

    let private admitBinding parentId handle childId (projection: AgentProjectionSet) =
        let authority =
            Map.tryFind childId projection.Sessions |> Option.bind _.PromptAuthority

        let state =
            sessionState projection parentId
            |> Option.defaultValue DelegationSessionState.empty

        let handles = state.Handles |> Option.defaultValue HandleProjection.empty

        match authority with
        | None -> Ok projection
        | Some authority when authority.ActiveLogicalRun.IsNone -> Ok projection
        | Some authority -> admitActiveAuthority parentId handle childId projection state handles authority

    let admitAuthority (projection: AgentProjectionSet) (fact: PromptFactCases) =
        match fact with
        | PromptFactCases.AuthorityRootAccepted payload when payload.AuthorityKind = "AgentOwnerRoot" ->
            match
                PromptAuthority.identitySeedOwner payload.IdentitySeed,
                Map.tryFind payload.SessionId projection.HandleByChildSession
            with
            | Some(parentId, _, _), Some binding -> admitBinding parentId binding.Handle payload.SessionId projection
            | _, None -> Ok projection
            | _ -> FoldRejection.reject "HandleWorkAdmission" "exact owner authority is missing"
        | _ -> Ok projection

    let private admitAfterExecutionFold projection fact updated =
        match fact with
        | ExecutionFactCases.HandleLinked payload ->
            admitBinding payload.ParentSessionId payload.Handle payload.ChildSessionId updated
        | _ -> Ok updated

    let foldExecution
        (projection: AgentProjectionSet)
        (fact: ExecutionFactCases)
        : Result<AgentProjectionSet, FoldRejection> =
        match ExecutionFactFold.fold (sessionState projection) fact with
        | Ok changes ->
            let updated = List.fold applyChange projection changes
            admitAfterExecutionFold projection fact updated
        | Error rejection ->
            FoldRejection.reject (DelegationFoldRejection.fact rejection) (DelegationFoldRejection.message rejection)

    let foldDelegation
        (projection: AgentProjectionSet)
        (fact: DelegationFactCases)
        : Result<AgentProjectionSet, FoldRejection> =
        match DelegationFactFold.fold (sessionState projection) (handoffFrontier projection) fact with
        | Ok changes -> Ok(List.fold applyChange projection changes)
        | Error rejection ->
            FoldRejection.reject (DelegationFoldRejection.fact rejection) (DelegationFoldRejection.message rejection)
