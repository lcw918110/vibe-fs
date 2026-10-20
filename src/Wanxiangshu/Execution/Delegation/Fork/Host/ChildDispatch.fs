namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.FSharp.Control
open Wanxiangshu.OpenCode
open Wanxiangshu.Process
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Persistence.Journal
open FsToolkit.ErrorHandling

/// Existing-child dispatch + parent teardown helpers.
module HostForkChildDispatch =
    let private mergeAbortError (errOpt: string option) (abortResult: Result<unit, string>) =
        match errOpt, abortResult with
        | None, Error err -> Some err
        | _ -> errOpt

    let private abortSessionResult (sessions: ISessionHostPort) (childId: SessionId) =
        task {
            try
                return! sessions.AbortSession childId
            with ex ->
                return Error ex.Message
        }

    let private abortOne (sessions: ISessionHostPort) (childId: SessionId) (errOpt: string option) =
        task {
            let! abortResult = abortSessionResult sessions childId
            return mergeAbortError errOpt abortResult
        }

    let private isActiveOwnedHandle (childId: SessionId) (record: HandleRecord) =
        record.ChildSessionId = childId
        && match record.Lifecycle with
           | HandleLifecycle.Active -> true
           | HandleLifecycle.CompletedAwaitingJoin _
           | HandleLifecycle.Abandoned _
           | HandleLifecycle.Retired -> false

    let private isProcessOwnedActiveHandle (handles: AgentLinkageProjection) (agentId: string, childId: SessionId) =
        match HandleProjection.tryFind (HandleController.agentHandle agentId) handles with
        | Some record -> isActiveOwnedHandle childId record
        | None -> false

    let private requireOk (context: string) (result: Result<unit, string>) =
        match result with
        | Ok() -> ()
        | Error err -> raise (InvalidOperationException(sprintf "%s: %s" context err))

    let private awaitUnit (work: Task) : Task<unit> =
        task {
            do! work
            return ()
        }

    let private settlePendingAbandoned
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (settleAbandoned: PendingHostRun -> unit)
        (agentIds: string list)
        =
        let owned = Set.ofList agentIds

        let pending =
            lock gate (fun () ->
                pendingRuns.Values
                |> Seq.filter (fun run -> Set.contains run.AgentId owned)
                |> Seq.toList)

        for run in pending do
            settleAbandoned run

    let private isFixedDevOps (agentId: string) =
        String.Equals(agentId.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

    let private isFixedDevOpsHandle (handles: AgentLinkageProjection option) (agentId: string) =
        let handleRecordOpt =
            handles
            |> Option.bind (fun h -> HandleProjection.tryFind (HandleController.agentHandle agentId) h)

        match handleRecordOpt with
        | Some r -> r.CanonicalRole = Role.DevOps || r.Byname = "devops"
        | None -> isFixedDevOps agentId

    let private retainedFixedDevOps handles (agentId, childId) =
        handles
        |> Option.bind (HandleProjection.tryBinding (HandleController.agentHandle agentId))
        |> Option.exists (fun binding ->
            binding.ChildSessionId = childId
            && binding.CanonicalRole = Role.DevOps
            && binding.Byname = "devops"
            && binding.TargetAgent = "devops"
            && binding.Ownership = HandleOwnership.DurableParentHandle
            && binding.Lifecycle = HandleLifecycle.Active)

    let private clearChildrenAndRuns
        (gate: obj)
        (children: Dictionary<string, SessionId>)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (durableDevOpsChild: SessionId option)
        =
        lock gate (fun () ->
            // The fixed DevOps binding survives a teardown. After a restart the map
            // is empty, so the durable handle answers instead of losing the binding.
            let devopsChildOpt =
                match children.TryGetValue "devops" with
                | true, cid -> Some cid
                | false, _ -> durableDevOpsChild

            children.Clear()

            match devopsChildOpt with
            | Some cid -> children.["devops"] <- cid
            | None -> ()

            let cancelled = pendingRuns.Keys |> Seq.filter (isFixedDevOps >> not) |> Seq.toList

            for agentId in cancelled do
                pendingRuns.Remove agentId |> ignore)

    let private nudgeBusyChild
        (sendBusyNudge:
            string -> SessionId -> PromptAuthority.AuthorityExecutionProfile -> string -> Task<Result<unit, string>>)
        journal
        (run: PendingHostRun)
        (prompt: string)
        : Task<Result<ForkResult, string>> =
        taskResult {
            let! profile = HostForkBusyNudge.profileForRun journal run
            do! sendBusyNudge run.AgentId run.ChildId profile prompt
            return ForkResult.Nudged run.AgentId
        }

    let private completeIdleExistingSend
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (childViewForRun: SessionId -> ProviderRunIdentity -> Task<string option>)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (onRunStarted: SessionId -> Role -> unit)
        (sendChildPrompt:
            string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (identitySeed: PromptAuthority.IdentitySeed)
        (prompt: string)
        (agent: string)
        (enrichedPrompt: string option)
        : Task<Result<ForkResult, string>> =
        taskResult {
            let payload = Option.defaultValue prompt enrichedPrompt

            let! sent =
                sendChildPrompt agentId childId role identitySeed payload (fun _ -> ())
                |> TaskResultCE.ofTask

            match sent with
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Accepted(_, authorityRoot) ->
                let run =
                    HostForkRunLifecycle.installRun
                        gate
                        pendingRuns
                        journal
                        parentId
                        sessions
                        childViewForRun
                        trackOwnedWork
                        agentId
                        childId
                        role
                        authorityRoot

                onRunStarted childId role

                let result =
                    runtime.Fork(agentId, role, agent, runWork = (fun () -> run.Source.Task))

                return result
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.AcceptanceUncertain _ ->
                return ForkResult.DispatchUncertain agentId
            | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Rejected err -> return! Error err
        }

    let private dispatchIdleExistingChild
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (childViewForRun: SessionId -> ProviderRunIdentity -> Task<string option>)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (sendChildPrompt:
            string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>)
        (onRunStarted: SessionId -> Role -> unit)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (prompt: string)
        (agent: string)
        (enrichedPrompt: string option)
        : Task<Result<ForkResult, string>> =
        taskResult {
            let! identitySeed = HostForkRunLifecycle.issueCurrentOwnerIdentitySeed journal parentId agent

            if runtime.IsCancelled then
                return! Error "Fork runtime is cancelled"
            else
                return!
                    completeIdleExistingSend
                        gate
                        pendingRuns
                        journal
                        parentId
                        sessions
                        childViewForRun
                        trackOwnedWork
                        runtime
                        onRunStarted
                        sendChildPrompt
                        agentId
                        childId
                        role
                        identitySeed
                        prompt
                        agent
                        enrichedPrompt
        }

    /// Sends a prompt to an already-linked child: if a run is active for this
    /// agent, nudge (fire-and-forget send, carrying role explicitly — after a
    /// host restart OpenCode would otherwise resolve an agent-less child prompt
    /// to the default build agent, not the session's original role); otherwise
    /// install a fresh run and fork it. Shared by HostForkRuntime.Fork's
    /// existing-child path and Reuse, which differ only in how they obtain
    /// `role` before reaching this point.
    let sendToExistingChild
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (childViewForRun: SessionId -> ProviderRunIdentity -> Task<string option>)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (sendChildPrompt:
            string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>)
        (sendBusyNudge:
            string -> SessionId -> PromptAuthority.AuthorityExecutionProfile -> string -> Task<Result<unit, string>>)
        (onRunStarted: SessionId -> Role -> unit)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (prompt: string)
        (agent: string)
        (enrichedPrompt: string option)
        : Task<Result<ForkResult, string>> =
        taskResult {
            let activeRun =
                lock gate (fun () ->
                    match pendingRuns.TryGetValue agentId with
                    | true, run -> Some run
                    | false, _ -> None)

            match activeRun, runtime.IsCancelled with
            | Some _, true -> return! Error "Fork runtime is cancelled"
            | Some run, false ->
                // Active run: BusyAgentNudge continuation (same LogicalRun).
                return! nudgeBusyChild sendBusyNudge journal run prompt
            | None, _ ->
                return!
                    dispatchIdleExistingChild
                        gate
                        pendingRuns
                        journal
                        parentId
                        sessions
                        childViewForRun
                        trackOwnedWork
                        runtime
                        sendChildPrompt
                        onRunStarted
                        agentId
                        childId
                        role
                        prompt
                        agent
                        enrichedPrompt
        }

    /// Abort linked child sessions. Handle retirement has already been written
    /// synchronously by the caller before the async cleanup begins.
    let teardownChildren (sessions: ISessionHostPort) (childIds: SessionId list) : Task<Result<unit, string>> =
        let rec loop remaining firstError =
            task {
                match remaining, firstError with
                | [], Some err -> return Error err
                | [], None -> return Ok()
                | childId :: rest, errOpt ->
                    let! next = abortOne sessions childId errOpt
                    return! loop rest next
            }

        loop childIds None

    /// Cancel ordinary owned work after its observed callbacks drain. The fixed
    /// road companion retains its physical runtime and completion subscription.
    let cancelParent
        (cancelSignals: SessionId seq -> unit)
        (drainCancelledCallbacks: string list -> string list -> Task<unit>)
        (runtime: ForkRuntime)
        (ptyPort: PtyPort)
        (parentKey: string)
        (parentAbortToken: int)
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (children: Dictionary<string, SessionId>)
        (sessions: ISessionHostPort)
        (journal: AgentJournal option)
        (durableHandles: AgentLinkageProjection option)
        (parentId: SessionId)
        (settleAbandoned: PendingHostRun -> unit)
        (abandonedAt: DateTimeOffset)
        : Task<unit> =
        // Teardown ownership is process-local. Durable Active handles from a
        // previous process are broken historical tools, not resources this
        // runtime may abandon/abort merely because the same parent runtime exists.
        // Explicit /continue discoveries remain dormant outside `children` until
        // a new reuse charge activates them.
        let processOwned =
            lock gate (fun () -> children |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.toList)

        let owned =
            match durableHandles with
            | None -> processOwned
            | Some handles -> processOwned |> List.filter (isProcessOwnedActiveHandle handles)

        // managed-session-lifecycle-024 / Common Law: fixed road companion DevOps must never be abandoned or torn down on parent cancellation
        let ownedToCancel =
            owned
            |> List.filter (fun (agentId, _) -> not (isFixedDevOpsHandle durableHandles agentId))

        let retainedAgents =
            processOwned |> List.filter (retainedFixedDevOps durableHandles) |> List.map fst

        let cancelledAgents = ownedToCancel |> List.map fst

        let childIdsToCancel = ownedToCancel |> List.map snd |> List.distinct
        cancelSignals (parentId :: childIdsToCancel)

        // EXEC-009: durable abandon before aborting. A crash mid-Cancel must not
        // leave a session aborted but still Active/joinable. A leaked abort is
        // recoverable; a leaked live handle is not.
        task {
            do! drainCancelledCallbacks cancelledAgents retainedAgents

            if List.isEmpty retainedAgents then
                runtime.Cancel()
            else
                cancelledAgents |> List.iter runtime.CancelAgent

            let journalPort = journal |> Option.map AgentJournalPortAdapter.fromAgentJournal

            let activeToAbandon =
                match journal with
                | None -> ownedToCancel
                | Some durable ->
                    let current = AgentJournal.handleProjection durable parentId
                    ownedToCancel |> List.filter (isProcessOwnedActiveHandle current)

            let! cancelResult =
                HandleController.cancelChildren journalPort parentId (activeToAbandon |> List.map fst) abandonedAt

            requireOk "Parent handle abandon failed" cancelResult

            do! ptyPort.CloseAll()
            Pty.unregisterParentAbort parentKey parentAbortToken
            settlePendingAbandoned gate pendingRuns settleAbandoned (activeToAbandon |> List.map fst)

            let! teardown = teardownChildren sessions (childIdsToCancel |> List.distinct)
            requireOk "Parent teardown failed" teardown

            let durableDevOpsChild =
                durableHandles
                |> Option.bind (fun handles -> DurableChildLookup.byByname handles "devops")
                |> Option.map (fun (childId, _, _) -> childId)

            clearChildrenAndRuns gate children pendingRuns durableDevOpsChild
        }
