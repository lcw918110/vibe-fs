namespace Wanxiangshu.Execution.Session

open System
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation

/// Pure RecoveryClosure discovery from durable projections (RECOVERY-FAMILY-001).
/// Child-first order: dependents before ancestors; siblings by SessionId.
module RecoveryClosureProjection =

    let private sortKey =
        function
        | RecoveryNode.Companion(_, id)
        | RecoveryNode.AgentChild(_, id, _)
        | RecoveryNode.ManagerJob(_, id)
        | RecoveryNode.WorkSession id -> SessionId.value id

    let private digestOf (root: SessionId) (nodes: RecoveryNode list) =
        nodes
        |> List.map RecoveryNode.token
        |> fun parts -> String.Join("|", SessionId.value root :: parts)

    let private rootHandles (root: SessionId) (projection: AgentProjectionSet) =
        AgentProjection.tryFind root projection
        |> Option.bind (fun session -> session.Handles)
        |> Option.defaultValue HandleProjection.empty


    let private addLinkedChild
        (root: SessionId)
        (projection: AgentProjectionSet)
        (add: RecoveryNode -> unit)
        (linkedChildIds: System.Collections.Generic.HashSet<string>)
        (record: HandleRecord)
        =
        ignore (linkedChildIds.Add(SessionId.value record.ChildSessionId))

        match record.Ownership, record.Lifecycle, HandleId.tryAgent record.Handle with
        | HandleOwnership.HostOwnedHidden, _, _ -> ()
        | _, HandleLifecycle.Retired, _
        | _, HandleLifecycle.Abandoned _, _
        | _, _, None -> ()
        | _, HandleLifecycle.Active, Some agentHandle
        | _, HandleLifecycle.CompletedAwaitingJoin _, Some agentHandle ->
            add (RecoveryNode.AgentChild(root, record.ChildSessionId, agentHandle))

    let private addManagerJob
        (root: SessionId)
        (projection: AgentProjectionSet)
        (add: RecoveryNode -> unit)
        (linkedChildIds: System.Collections.Generic.HashSet<string>)
        (jobId: ManagerJobId, managerSessionId: SessionId)
        =
        let related =
            managerSessionId = root
            || linkedChildIds.Contains(SessionId.value managerSessionId)

        if not related then
            ()
        elif managerSessionId = root then
            add (RecoveryNode.ManagerJob(jobId, managerSessionId))
        else
            add (RecoveryNode.ManagerJob(jobId, managerSessionId))

            add (RecoveryNode.AgentChild(root, managerSessionId, AgentHandleId.create (ManagerJobId.value jobId)))


    let private sessionNeedsRecovery session =
        let pending =
            session.PromptAuthority
            |> Option.map (fun authority -> not (Map.isEmpty authority.PendingClaims))
            |> Option.defaultValue false

        pending

    let private addPendingSession
        (root: SessionId)
        (projection: AgentProjectionSet)
        (add: RecoveryNode -> unit)
        (linkedChildIds: System.Collections.Generic.HashSet<string>)
        (sessionId: SessionId)
        session
        =
        if not (sessionNeedsRecovery session) then
            ()
        elif
            sessionId = root
            || SessionAssociationProjection.tryMainSessionOf sessionId projection.Associations = Some root
            || linkedChildIds.Contains(SessionId.value sessionId)
        then
            add (RecoveryNode.WorkSession sessionId)

    /// Discover durable recovery dependency closure for a parent session.
    let discover (root: SessionId) (projection: AgentProjectionSet) (journalSequence: int64) : RecoveryClosure =
        // DSL-MUTABLE: algorithm-scratch — recovery node accumulator
        let nodes = ResizeArray<RecoveryNode>()
        // DSL-MUTABLE: algorithm-scratch — local walk visited set
        let seen = System.Collections.Generic.HashSet<string>()
        // DSL-MUTABLE: algorithm-scratch — local walk linked child id accumulator
        let linkedChildIds = System.Collections.Generic.HashSet<string>()

        let add (node: RecoveryNode) =
            let key = sortKey node

            if seen.Add key then
                nodes.Add node

        add (RecoveryNode.WorkSession root)

        for record in HandleProjection.linkedChildren (rootHandles root projection) do
            addLinkedChild root projection add linkedChildIds record


        for job in AgentProjection.activeOrchestratorJobPairs projection do
            addManagerJob root projection add linkedChildIds job

        for sessionId, session in Map.toList projection.Sessions do
            addPendingSession root projection add linkedChildIds sessionId session

        let rank =
            function
            | RecoveryNode.Companion _ -> 1
            | RecoveryNode.AgentChild _ -> 2
            | RecoveryNode.ManagerJob _ -> 3
            | RecoveryNode.WorkSession _ -> 4

        let ordered =
            nodes |> Seq.toList |> List.sortBy (fun node -> rank node, sortKey node)

        { Root = root
          Nodes = ordered
          Digest = digestOf root ordered
          JournalSequence = journalSequence }
