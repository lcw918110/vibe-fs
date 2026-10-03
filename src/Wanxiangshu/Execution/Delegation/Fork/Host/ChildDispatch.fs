namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Guidance
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
        =
        let pending = lock gate (fun () -> pendingRuns.Values |> Seq.toList)

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

    let private settleExemptedDevOpsWork
        (journalPort: AgentJournalPort option)
        (parentId: SessionId)
        (run: PendingHostRun)
        : Task<Result<unit, string>> =
        match run.Work with
        | Some admitted ->
            HandleController.settleExemptedWork journalPort parentId admitted HandleAbandonReason.ParentCancelled
        | None -> Task.FromResult(Ok())

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

            pendingRuns.Clear())

    let private nudgeBusyChild
        (sendBusyNudge: string -> SessionId -> Role -> string -> string -> Task<Result<unit, string>>)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (agent: string)
        (prompt: string)
        : Task<Result<ForkResult, string>> =
        taskResult {
            do! sendBusyNudge agentId childId role agent prompt
            return ForkResult.Nudged agentId
        }

    let private completeIdleExistingSend
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>)
        (xTraceHead: SessionId -> XTraceCursor)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (onRunStarted: SessionId -> Role -> unit)
        (handoffPort: ReusableHandoffPort option)
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
        (preparedHandoff: PreparedDelegationHandoff option)
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
                        childWorkRecordForRun
                        xTraceHead
                        trackOwnedWork
                        handoffPort
                        preparedHandoff
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
        (childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>)
        (xTraceHead: SessionId -> XTraceCursor)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (handoffPort: ReusableHandoffPort option)
        (sendChildPrompt:
            string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>)
        (onRunStarted: SessionId -> Role -> unit)
        (preparedHandoff: PreparedDelegationHandoff option)
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
                        childWorkRecordForRun
                        xTraceHead
                        trackOwnedWork
                        runtime
                        onRunStarted
                        handoffPort
                        sendChildPrompt
                        agentId
                        childId
                        role
                        identitySeed
                        preparedHandoff
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
        (childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>)
        (xTraceHead: SessionId -> XTraceCursor)
        (trackOwnedWork: (unit -> Task) -> unit)
        (runtime: ForkRuntime)
        (handoffPort: ReusableHandoffPort option)
        (sendChildPrompt:
            string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>)
        (sendBusyNudge: string -> SessionId -> Role -> string -> string -> Task<Result<unit, string>>)
        (onRunStarted: SessionId -> Role -> unit)
        (preparedHandoff: PreparedDelegationHandoff option)
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
            | Some _, false when preparedHandoff.IsSome ->
                return! Error(sprintf "Agent already has an active assignment: %s" agentId)
            | Some _, false ->
                // Active run: BusyAgentNudge continuation (same LogicalRun).
                return! nudgeBusyChild sendBusyNudge agentId childId role agent prompt
            | None, _ ->
                return!
                    dispatchIdleExistingChild
                        gate
                        pendingRuns
                        journal
                        parentId
                        sessions
                        childWorkRecordForRun
                        xTraceHead
                        trackOwnedWork
                        runtime
                        handoffPort
                        sendChildPrompt
                        onRunStarted
                        preparedHandoff
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

    /// Cancel parent: fail pending runs, abandon child handles, clear maps.
    ///
    /// `cancelSignals` is invoked with parentId :: childIds so the signal router
    /// ignores further idle/retry events for the torn-down sessions. Unregistering
    /// the routing is the whole cancellation: a torn-down session simply stops
    /// producing turns to reconcile.
    ///
    /// Side effects that must be visible before the call returns (ForkRuntime
    /// cancellation, signal unrouting, handle retirement) run synchronously before
    /// the async block starts.
    let cancelParent
        (cancelSignals: SessionId seq -> unit)
        (awaitRecovery: unit -> Task<unit>)
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
        // Synchronous: make sure observers (runtime.Join, tests, parent abort
        // callbacks) see cancellation immediately.
        runtime.Cancel()

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

        let childIdsToCancel = ownedToCancel |> List.map snd |> List.distinct
        cancelSignals (parentId :: childIdsToCancel)

        // EXEC-009: durable abandon before aborting. A crash mid-Cancel must not
        // leave a session aborted but still Active/joinable. A leaked abort is
        // recoverable; a leaked live handle is not.
        task {
            let journalPort = journal |> Option.map AgentJournalPortAdapter.fromAgentJournal

            let! cancelResult =
                HandleController.cancelChildren journalPort parentId (ownedToCancel |> List.map fst) abandonedAt

            requireOk "Parent handle abandon failed" cancelResult

            do! ptyPort.CloseAll()
            Pty.unregisterParentAbort parentKey parentAbortToken

            // managed-session-lifecycle-024 / delegation-027: the fixed DevOps
            // handle is exempt from the handle-level abandon above, but its
            // in-flight work unit still settles durably here — otherwise the
            // next resume's admitWork is refused with WorkStillActive and a
            // physically accepted dispatch degrades to DispatchUncertain.
            // Durable terminal first, then the in-memory waiter settle.
            let devopsPending =
                lock gate (fun () -> pendingRuns.Values |> Seq.toList)
                |> List.filter (fun run -> isFixedDevOpsHandle durableHandles run.AgentId)

            for run in devopsPending do
                let! devopsSettled = settleExemptedDevOpsWork journalPort parentId run
                requireOk "DevOps exempted work settlement failed" devopsSettled

            settlePendingAbandoned gate pendingRuns settleAbandoned
            do! awaitRecovery ()

            let! teardown = teardownChildren sessions (childIdsToCancel |> List.distinct)
            requireOk "Parent teardown failed" teardown

            let durableDevOpsChild =
                durableHandles
                |> Option.bind (fun handles -> DurableChildLookup.byByname handles "devops")
                |> Option.map (fun (childId, _, _) -> childId)

            clearChildrenAndRuns gate children pendingRuns durableDevOpsChild
        }
