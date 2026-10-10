namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Change.Host
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process
open Wanxiangshu.OpenCode.Host


/// Owns every per-session tool runtime.
///
/// AGENT-007: Role comes from the Authority Root's CanonicalRole and nothing
/// else. The previous version consulted three sources in order — authority, a
/// per-session role cache, then the Host context's `agent` field — so a session
/// whose authority said Coder could still be gated as DevOps because a cache
/// entry or a message field said so. Two of the three are gone.
type ToolRuntimeScope
    (
        sessions: ISessionHostPort,
        waitObserver: IWaitObserver,
        rootWorkspace: IRootWorkspaceReader,
        journal: AgentJournal option,
        workspaceDirectory: string option,
        sessionParents: Dictionary<string, string>,
        currentPhysicalUserMessage: string -> string option,
        sessionDirectories: Dictionary<string, string>,
        onRunStarted: (SessionId -> Role -> string option -> unit) option,
        parentWorkRecordFor: (string -> Task<string option>) option,
        childWorkRecordFor: (string -> Task<string option>) option,
        snapshot: ISessionSnapshotPort option,
        cancelSignals: (SessionId seq -> unit) option,
        ?childWorkRecordForRun: (SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>),
        ?workRecordCapability: DelegationWorkRecordCapability,
        ?continueManagerLoop: (SessionId -> string -> Task<Result<unit, string>>),
        ?captureWorktreeSnapshot: (WorktreePath -> Result<WorkspaceSnapshotId, string>),
        ?eventPort: IEventObservationPort
    ) =

    let gate = obj ()
    let ownedWorkGate = obj ()
    // DSL-MUTABLE: resource — per-owner fork runtime registry
    let runtimes = Dictionary<string, HostForkRuntime>()
    // DSL-MUTABLE: resource — per-session executor runtime registry
    let executorRuntimes = Dictionary<string, HostForkRuntime>()
    // DSL-MUTABLE: retirement admission fence — exact logical incumbency, never the reusable physical session.
    let retirementFrozen = Dictionary<string, IncumbencyId>()
    // DSL-MUTABLE: resource — per-session orchestrator host registry
    let orchestratorHosts = Dictionary<string, OrchestratorHost>()
    let onCancelSignals = defaultArg cancelSignals ignore
    let onStarted = defaultArg onRunStarted (fun _ _ _ -> ())
    // COMPANION-003: parent→child keeps Opening; child→parent omits it (includeOpening=false).
    let parentRecord = defaultArg parentWorkRecordFor (fun _ -> Task.FromResult None)
    let childRecord = defaultArg childWorkRecordFor (fun _ -> Task.FromResult None)

    // Manager-loop delivery is an injected capability: the Change/Host
    // OrchestratorHostDeps model already owns this edge, and the
    // composition root supplies the real workflow. A missing capability
    // fails closed: a silent no-op success would advance the loop without
    // authority.
    let continueManagerLoop =
        defaultArg continueManagerLoop (fun _ _ ->
            Task.FromResult(Error "continue manager loop unavailable") :> Task<Result<unit, string>>)

    // Workspace-snapshot capture is an injected capability: Relay certificate
    // binding owns the capture vocabulary, the composition root supplies it.
    let captureWorktreeSnapshot =
        defaultArg captureWorktreeSnapshot (fun _ -> Error "workspace snapshot capture unavailable")

    let childRecordForRun =
        defaultArg childWorkRecordForRun (fun _ _ _ -> Task.FromResult None)

    let reusableHandoff =
        workRecordCapability
        |> Option.bind (fun cap -> journal |> Option.map (DelegationHandoffLedger.port cap))

    let terminalPort = eventPort
    // DSL-MUTABLE: resource — tool runtime dispose latch
    let mutable disposed = false
    // DSL-MUTABLE: resource — async tool-callback admission latch.
    let mutable acceptingOwnedWork = true
    // DSL-MUTABLE: resource — in-flight tool-owned callback count.
    let mutable ownedWorkCount = 0
    // DSL-MUTABLE: single-flight — shared waiter for tool-owned callback drain.
    let mutable ownedWorkDrainWaiter: TaskCompletionSource<unit> option = None
    // DSL-MUTABLE: resource — first tool-owned callback failure for shutdown propagation.
    let mutable ownedWorkFailure: exn option = None
    /// Process-local join admission before join / publish consume.
    // DSL-MUTABLE: resource — current-process join admission callback attachment
    let mutable currentProcessJoin: (SessionId -> Task<FamilyRecovery>) option = None

    /// EXEC-017 attempt-scoped join interrupt registry (PluginRuntimeScope or local default).
    // DSL-MUTABLE: resource — join attempt registry attachment
    let mutable joinAttempts: IJoinAttemptRegistry =
        JoinAttemptRegistry() :> IJoinAttemptRegistry

    let finishOwnedWork () =
        lock ownedWorkGate (fun () ->
            ownedWorkCount <- ownedWorkCount - 1

            if not acceptingOwnedWork && ownedWorkCount = 0 then
                ownedWorkDrainWaiter
                |> Option.iter (fun waiter -> AsyncSupport.trySetResult waiter () |> ignore))

    let ownedWorkDrainTask () : Task =
        match ownedWorkDrainWaiter with
        | Some waiter -> waiter.Task :> Task
        | None ->
            let waiter =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            ownedWorkDrainWaiter <- Some waiter
            waiter.Task :> Task

    let stopOwnedWorkAndDrain () : Task<exn option> =
        let waiting =
            lock ownedWorkGate (fun () ->
                acceptingOwnedWork <- false

                if ownedWorkCount = 0 then
                    Task.FromResult(()) :> Task
                else
                    ownedWorkDrainTask ())

        task {
            do! waiting
            return lock ownedWorkGate (fun () -> ownedWorkFailure)
        }

    let recordOwnedWorkFailure (failure: exn) =
        lock ownedWorkGate (fun () -> ownedWorkFailure <- Option.orElse ownedWorkFailure (Some failure))

    let captureOwnedWorkFailure (start: unit -> Task) : Task<exn option> =
        task {
            try
                do! start ()
                return None
            with ex ->
                return Some ex
        }

    let observeOwnedWork (start: unit -> Task) : Task =
        task {
            let! failure = captureOwnedWorkFailure start
            failure |> Option.iter recordOwnedWorkFailure
            finishOwnedWork ()
        }
        :> Task

    let addUniqueForkRuntime (owned: ResizeArray<HostForkRuntime>) (runtime: HostForkRuntime) =
        if not (owned |> Seq.exists (fun current -> obj.ReferenceEquals(current, runtime))) then
            owned.Add runtime

    let registerChild parentSid (_role: Role) childId =
        sessionParents.[SessionId.value childId] <- parentSid

    let directoryFor sid =
        match sessionDirectories.TryGetValue sid with
        // ORCH-006 defence: a manager-family directory is the worktree, which is
        // removed at publish. A request that races the release would otherwise keep
        // pointing at the deleted path and lose the AGENTS.md instruction block
        // (ARCH-004 seal break, measured on orchestrator-publish under concurrency).
        // Verify the path still exists; fall back to None (root workspace) when the
        // worktree is gone — the residual guard-round request has no worktree work
        // left to do.
        | true, path when System.IO.Directory.Exists path -> Some path
        | _ -> None

    let drainChildPtysFor (childId: SessionId) : Task<unit> =
        task {
            let childRuntimeOpt =
                lock gate (fun () ->
                    match runtimes.TryGetValue(SessionId.value childId) with
                    | true, r -> Some r
                    | false, _ -> None)

            match childRuntimeOpt with
            | Some r -> do! r.CloseOwnedPtys()
            | None -> ()
        }

    let createRuntime sid =
        HostForkRuntime(
            SessionId.create sid,
            sessions,
            childRecordForRun,
            CompletionMailboxRuntime.create,
            NodeTiming.nodeClockPort (),
            NodeTiming.raceExit,
            ?journal = journal,
            onChildCreated = (fun _ role childId -> registerChild sid role childId),
            onChildCreatedDir =
                (fun _ childId directory ->
                    directory
                    |> Option.iter (fun path -> sessionDirectories.[SessionId.value childId] <- path)),
            directoryFor = (fun _ -> directoryFor sid),
            onRunStarted = onStarted,
            parentWorkRecordFor = (fun parentId -> parentRecord (SessionId.value parentId)),
            childWorkRecordFor = (fun childId -> childRecord (SessionId.value childId)),
            ?handoff = reusableHandoff,
            ?sessionSnapshot = snapshot,
            cancelSignals = onCancelSignals,
            drainChildPtys = drainChildPtysFor
        )

    let establishedAgentsOf ownerKey =
        match runtimes.TryGetValue ownerKey with
        | true, previous -> previous.EstablishedAgentIds
        | false, _ -> []

    let getOrCreateRuntime ownerKey =
        lock gate (fun () ->
            match disposed, runtimes.TryGetValue ownerKey with
            | true, _ -> Error "Tool runtime scope is disposed"
            | false, (true, runtime) when not runtime.IsCancelled -> Ok runtime
            | false, _ ->
                // participant-horizon-011 / WP-025: a replaced (cancelled) runtime must
                // inherit the agents this process established; otherwise a cancelled child
                // would vanish from its own process' horizon/join.
                let inheritedEstablished = establishedAgentsOf ownerKey

                let runtime = createRuntime ownerKey
                runtime.RestoreEstablishedAgents inheritedEstablished
                runtimes.[ownerKey] <- runtime
                Ok runtime)

    let resumableAgentId (record: HandleRecord) =
        match record.Ownership, HandleId.tryAgent record.Handle with
        | HandleOwnership.HostOwnedHidden, _ -> Error "host-owned hidden child is not resumable by the user session"
        | HandleOwnership.DurableParentHandle, None -> Error "non-agent durable handle is not resumable"
        | HandleOwnership.DurableParentHandle, Some handleId -> Ok(AgentHandleId.value handleId)

    let adoptExistingIntoRuntime (parentSessionId: SessionId) (record: HandleRecord) agentId =
        getOrCreateRuntime (SessionId.value parentSessionId)
        |> Result.map (fun runtime -> runtime.AdoptExisting(agentId, record.ChildSessionId))

    let sessionIdOf (ctx: HostToolContext) =
        if String.IsNullOrWhiteSpace ctx.SessionId then
            None
        else
            Some(SessionId.create ctx.SessionId)

    let logicalOwnerFor (sessionId: SessionId) =
        FissionRuntime.tryOwner sessionId
        |> Option.orElseWith (fun () ->
            journal
            |> Option.bind (fun durable ->
                FissionProjection.tryOwnerOfLane sessionId (AgentJournal.snapshot durable).AgentProjections.Fission))
        |> Option.defaultValue sessionId

    let activeProfileFor sessionId =
        match journal with
        | Some durable ->
            let projections = (AgentJournal.snapshot durable).AgentProjections

            PromptAuthorityProjectionQueries.activeProfile sessionId projections
            |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId projections)
        | None -> None

    /// AGENT-007: the single Role source, or `None`.
    ///
    /// `None` means the tool set is empty — the clause says so explicitly, and
    /// names the "allow inspector while the role is unresolved" exemption the old
    /// code had as the thing to delete. A read-only tool executed under an
    /// unknown role is still an unauthorised execution.
    let roleFor (ctx: HostToolContext) =
        sessionIdOf ctx
        |> Option.bind activeProfileFor
        |> Option.map (fun profile -> profile.CanonicalRole)

    let humanRootIdentitySeedAdmission (durable: AgentJournal) sessionId agent =
        PromptAuthorityProjectionQueries.activeProfile sessionId (AgentJournal.snapshot durable).AgentProjections
        |> Option.map (fun profile -> Ok profile.IdentitySeed)
        |> Option.defaultWith (fun () ->
            ParticipantIdentity.resolveAtRoot agent
            |> Result.map PromptAuthority.IdentitySeed.RootSelection)
        |> Result.mapError (fun _ -> ())

    let acceptNamedHumanRoot
        (durable: AgentJournal)
        (sessionId: SessionId)
        (user: SessionMessage)
        (agent: string)
        : Task<Role option> =
        task {
            let runtime = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            let! admission =
                taskResult {
                    let! seed = humanRootIdentitySeedAdmission durable sessionId agent

                    let! attempt =
                        runtime.AcceptHumanRoot
                            sessionId
                            (Wanxiangshu.Foundation.Identity.PhysicalUserMessageId.create user.Id)
                            (Some seed)
                        |> TaskResultCE.ofTask

                    let! profile = attempt |> Result.mapError (fun _ -> ())
                    return profile.CanonicalRole
                }

            return Result.toOption admission
        }

    let acceptHumanRootFromUser
        (durable: AgentJournal)
        (sessionId: SessionId)
        (user: SessionMessage)
        : Task<Role option> =
        match user.Agent with
        | None -> Task.FromResult None
        | Some agent -> acceptNamedHumanRoot durable sessionId user agent

    let acceptUnkeyedParentUser
        (durable: AgentJournal)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        (messages: SessionMessage list)
        : Task<Role option> =
        let providerRunId = ProviderRunIdentity.value providerRun

        let parentUser =
            messages
            |> List.tryFind (fun message -> message.Id = providerRunId)
            |> Option.bind (fun assistant -> assistant.ParentId)
            |> Option.bind (fun parentId ->
                messages
                |> List.tryFind (fun message -> message.Id = parentId && message.Role = "user"))

        match parentUser with
        | Some user when user.PromptKey.IsNone -> acceptHumanRootFromUser durable sessionId user
        | _ -> Task.FromResult None

    let recoverFromSnapshotMessages
        (durable: AgentJournal)
        (snapshotPort: ISessionSnapshotPort)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : Task<Role option> =
        task {
            match! snapshotPort.GetMessages sessionId with
            | Error _ -> return None
            | Ok messages -> return! acceptUnkeyedParentUser durable sessionId providerRun messages
        }

    let recoverWithPorts
        (durable: AgentJournal)
        (snapshotPort: ISessionSnapshotPort)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : Task<Role option> =
        task {
            match activeProfileFor sessionId with
            | Some profile -> return Some profile.CanonicalRole
            | None -> return! recoverFromSnapshotMessages durable snapshotPort sessionId providerRun
        }

    let recoverHumanRootFromSnapshot (ctx: HostToolContext) =
        task {
            match journal, snapshot, sessionIdOf ctx, ctx.ProviderRunId with
            | Some durable, Some snapshotPort, Some sessionId, Some providerRun ->
                return! recoverWithPorts durable snapshotPort sessionId providerRun
            | _ -> return None
        }

    let ensureRoleFor ctx =
        task {
            match roleFor ctx with
            | Some role -> return Some role
            | None -> return! recoverHumanRootFromSnapshot ctx
        }

    let commandRootPhysicalId (sessionId: SessionId) =
        Wanxiangshu.Foundation.Identity.PhysicalUserMessageId.create (
            "wanxiangshu:command-root:" + SessionId.value sessionId
        )

    let acceptHumanRootFor
        (durable: AgentJournal)
        (sessionId: SessionId)
        (physicalMessageId: Wanxiangshu.Foundation.Identity.PhysicalUserMessageId)
        (agent: string)
        : Task<Role option> =
        task {
            let runtime = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            let! admission =
                taskResult {
                    let! seed = humanRootIdentitySeedAdmission durable sessionId agent

                    let! attempt =
                        runtime.AcceptHumanRoot sessionId physicalMessageId (Some seed)
                        |> TaskResultCE.ofTask

                    let! profile = attempt |> Result.mapError (fun _ -> ())
                    return profile.CanonicalRole
                }

            return Result.toOption admission
        }

    /// The role the Authority Root recorded, when one exists.
    let recordedRole (durable) (sessionId: SessionId) : string option =
        PromptAuthorityProjectionQueries.activeProfile sessionId (AgentJournal.snapshot durable).AgentProjections
        |> Option.map (fun profile -> string profile.CanonicalRole)

    let ensureCommandRoleFor (sessionId: SessionId) (resolveAgent: SessionId -> Task<string option>) =
        task {
            let recorded =
                journal |> Option.bind (fun durable -> recordedRole durable sessionId)

            match Option.isSome recorded with
            | true -> return recorded
            | false -> return! resolveAgent sessionId
        }

    /// The managed agent the Authority Root selected for this session.
    ///
    /// A PTY belongs to the Logical Run and uses the fixed participant identity
    /// from SelectedAgent for its entire duration.
    let managedAgentFor (ctx: HostToolContext) =
        sessionIdOf ctx
        |> Option.bind activeProfileFor
        |> Option.bind (fun profile -> ManagedAgent.tryParse profile.SelectedAgent)

    let parseProcessHardLimit (value: string) =
        match Double.TryParse value with
        | true, seconds when seconds > 0.0 && not (Double.IsInfinity seconds) -> TimeSpan.FromSeconds seconds
        | _ -> TimeSpan.FromHours 1.0

    let defaultEmptyManagerFacts: ManagerCapabilityFacts =
        { HasActiveIncumbency = false
          HasAssessment = false
          IsFinalIncumbent = false
          CleanupBlockerDigest = None }

    let roadViewOfSession (sessionId: string) : RoadView option =
        journal
        |> Option.bind (fun durable ->
            AgentProjection.tryFind (SessionId.create sessionId) (AgentJournal.snapshot durable).AgentProjections)
        |> Option.bind (fun session -> session.Relay)
        |> Option.bind (fun relay -> Fold.view relay (RoadId.create sessionId))

    let activeIncumbencyOfSession (sessionId: string) : IncumbencyId option =
        roadViewOfSession sessionId |> Option.bind (fun road -> road.ActiveIncumbency)

    let managerFactsOfSession (sessionId: string) : ManagerCapabilityFacts =
        roadViewOfSession sessionId
        |> Option.bind (fun road ->
            match road.ActiveIncumbency with
            | None -> None
            | Some _ ->

                let facts: ManagerCapabilityFacts =
                    { HasActiveIncumbency = true
                      HasAssessment = road.AcceptedAssessmentTransport.IsSome
                      IsFinalIncumbent = road.AcceptedAssessmentFindings |> Option.exists AssessmentFindings.isEmpty
                      CleanupBlockerDigest = road.ActiveCleanupBlockerDigest }

                Some facts)
        |> Option.defaultValue defaultEmptyManagerFacts

    let devopsBindingTasks = Dictionary<string, Task<unit>>()

    let isManagerInJournal (journal: AgentJournal option) (sessionId: SessionId) =
        match journal with
        | Some durable ->
            let snapshot = AgentJournal.snapshot durable

            PromptAuthorityProjectionQueries.activeProfile sessionId snapshot.AgentProjections
            |> Option.orElseWith (fun () ->
                PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId snapshot.AgentProjections)
            |> Option.map (fun p -> p.CanonicalRole = Role.Manager)
            |> Option.defaultValue false
        | None -> false

    let isManagerProfile (sessionId: SessionId) =
        match activeProfileFor sessionId with
        | Some profile -> profile.CanonicalRole = Role.Manager
        | None -> isManagerInJournal journal sessionId

    let isManagerRoadSession (sessionId: SessionId) =
        let sidStr = SessionId.value sessionId

        match String.IsNullOrWhiteSpace sidStr with
        | true -> false
        | false -> Option.isSome (roadViewOfSession sidStr) || isManagerProfile sessionId

    let adoptIfNotOwned (runtime: HostForkRuntime) (childSessionId: SessionId) (handleId: AgentHandleId) =
        let agentId = AgentHandleId.value handleId

        match runtime.OwnsAgent agentId with
        | false -> runtime.AdoptChild(agentId, childSessionId)
        | true -> ()

    let adoptDevOpsIfUnowned
        (runtime: HostForkRuntime)
        (parentKey: string)
        (existingHandle: HandleRecord)
        (agentId: string)
        =
        match runtime.OwnsAgent agentId with
        | false ->
            // delegation-026: a reopened road's idle companion handle registers its
            // identity only (AdoptChild). Restore would plant an active ChildRun with no
            // work behind it, so the next dispatch's backend Fork answers Nudged, its
            // runTask never starts, and settle's AwaitCurrentWorkRecord waits on the
            // restored cell forever.
            runtime.AdoptChild(agentId, existingHandle.ChildSessionId)
            runtime.ChildCreated agentId existingHandle.CanonicalRole existingHandle.ChildSessionId
            runtime.ChildCreatedDir agentId existingHandle.ChildSessionId (runtime.DirectoryOf agentId)
            registerChild parentKey existingHandle.CanonicalRole existingHandle.ChildSessionId
        | true -> ()

    let syncAdoptDevOps (runtime: HostForkRuntime) (parentKey: string) (existingHandle: HandleRecord) =
        match HandleId.tryAgent existingHandle.Handle with
        | Some handleId ->
            let agentId = AgentHandleId.value handleId
            adoptDevOpsIfUnowned runtime parentKey existingHandle agentId
        | None -> ()

    let registerDevOpsChild
        (runtime: HostForkRuntime)
        (devopsAgentId: string)
        (role: Role)
        (childSessionId: SessionId)
        (linkageResult: Result<unit, string>)
        =
        match linkageResult with
        | Error err -> Error err
        | Ok() ->
            runtime.AdoptChild(devopsAgentId, childSessionId)
            runtime.ChildCreated devopsAgentId role childSessionId
            runtime.ChildCreatedDir devopsAgentId childSessionId (runtime.DirectoryOf devopsAgentId)
            Ok()

    let linkDevOpsChild
        (durable: AgentJournal)
        (runtime: HostForkRuntime)
        (parentSessionId: SessionId)
        (devopsAgentId: string)
        (devopsName: string)
        (role: Role)
        (childSessionId: SessionId)
        =
        task {
            let journalPort = AgentJournalPortAdapter.fromAgentJournal durable

            let! linkageResult =
                HandleController.linkNamed
                    (Some journalPort)
                    parentSessionId
                    devopsAgentId
                    childSessionId
                    devopsName
                    "devops"
                    role
                    HandleOwnership.DurableParentHandle

            return registerDevOpsChild runtime devopsAgentId role childSessionId linkageResult
        }

    let createAndLinkDevOps
        (sessions: ISessionHostPort)
        (durable: AgentJournal)
        (runtime: HostForkRuntime)
        (parentSessionId: SessionId)
        key
        =
        task {
            let devopsAgentId = "devops"
            let devopsName = "devops"
            let role = Role.DevOps

            let! childResult =
                sessions.CreateChildSession(
                    parentSessionId,
                    { Title = Some "devops"
                      Agent = Some devopsName
                      Directory = directoryFor key }
                )

            match childResult with
            | Error _ -> return Error "create child failed"
            | Ok childSessionId ->
                return! linkDevOpsChild durable runtime parentSessionId devopsAgentId devopsName role childSessionId
        }

    let handleDevOpsCreationResult key (created: Result<unit, string>) =
        match created with
        | Error _ -> lock gate (fun () -> devopsBindingTasks.Remove key |> ignore)
        | Ok() -> ()

    let needsReplacementDevOps (devopsHandleOpt: HandleRecord option) =
        let lifecycleOpt = devopsHandleOpt |> Option.map (fun h -> h.Lifecycle)

        match lifecycleOpt with
        | None
        | Some HandleLifecycle.Retired
        | Some(HandleLifecycle.Abandoned _) -> true
        | Some _ -> false

    let ensureDevOpsInRuntime
        (sessions: ISessionHostPort)
        (durable: AgentJournal)
        (runtime: HostForkRuntime)
        (parentSessionId: SessionId)
        key
        (devopsHandleOpt: HandleRecord option)
        =
        task {
            match needsReplacementDevOps devopsHandleOpt, devopsHandleOpt with
            | true, _ ->
                let! created = createAndLinkDevOps sessions durable runtime parentSessionId key
                handleDevOpsCreationResult key created
            | false, Some existingHandle -> syncAdoptDevOps runtime key existingHandle
            | false, None -> ()
        }

    let performEnsureWithRuntime
        (sessions: ISessionHostPort)
        (durable: AgentJournal)
        (parentSessionId: SessionId)
        key
        (runtimeResult: Result<HostForkRuntime, string>)
        (devopsHandleOpt: HandleRecord option)
        =
        task {
            match runtimeResult with
            | Error _ -> return ()
            | Ok runtime -> return! ensureDevOpsInRuntime sessions durable runtime parentSessionId key devopsHandleOpt
        }

    let performEnsureWithJournal (parentSessionId: SessionId) key (durable: AgentJournal) =
        task {
            let snapshot = AgentJournal.snapshot durable

            let handlesOpt =
                AgentProjection.tryFind parentSessionId snapshot.AgentProjections
                |> Option.bind (fun s -> s.Handles)

            let devopsHandleOpt =
                handlesOpt |> Option.bind (HandleProjection.tryFindBindingByByname "devops")


            let runtimeResult = getOrCreateRuntime key
            return! performEnsureWithRuntime sessions durable parentSessionId key runtimeResult devopsHandleOpt
        }

    let performEnsureDevOpsBound (parentSessionId: SessionId) key =
        task {
            match journal with
            | None -> return ()
            | Some durable -> return! performEnsureWithJournal parentSessionId key durable
        }

    let getOrCreateDevOpsTask (parentSessionId: SessionId) key =
        lock gate (fun () ->
            match devopsBindingTasks.TryGetValue key with
            | true, t -> t
            | false, _ ->
                let t = performEnsureDevOpsBound parentSessionId key
                devopsBindingTasks.[key] <- t
                t)

    let obtainRoadDevOpsTask (parentSessionId: SessionId) (key: string) =
        lock gate (fun () ->
            match getOrCreateRuntime key with
            | Ok runtime when not (runtime.OwnsAgent "devops") ->
                devopsBindingTasks.Remove key |> ignore
                getOrCreateDevOpsTask parentSessionId key
            | _ -> getOrCreateDevOpsTask parentSessionId key)

    let ensureRoadDevOpsBound (parentSessionId: SessionId) : Task<unit> =
        match isManagerRoadSession parentSessionId with
        | false -> Task.FromResult()
        | true ->
            let key = SessionId.value parentSessionId
            obtainRoadDevOpsTask parentSessionId key

    member _.Sessions = sessions
    member _.WaitObserver = waitObserver
    member _.RootWorkspace = rootWorkspace
    member _.Journal = journal
    member _.Snapshot = snapshot
    member _.EventPort = terminalPort
    member _.WorkspaceDirectory = workspaceDirectory
    member _.ActiveProfileFor(sessionId: SessionId) = activeProfileFor sessionId
    /// Run-started callback wired by plugin bootstrap for Host child reconciliation.
    member _.RunStarted = onStarted

    /// EXEC-011: the administrator's ceiling on any single process.
    ///
    /// Resolved once per scope so every executor call in a session shares one
    /// ceiling. A non-positive or unparseable setting falls back to the default
    /// rather than being treated as "no limit": the clause requires the hard limit
    /// to be finite, so an unreadable configuration must not widen it.
    // DSL-MUTABLE: resource — resolved process hard limit ceiling
    member val ProcessHardLimit =
        match Environment.GetEnvironmentVariable "WANXIANGSHU_PROCESS_HARD_LIMIT_SECS" with
        | null
        | "" -> TimeSpan.FromHours 1.0
        | value -> parseProcessHardLimit value

    member _.SessionParents = sessionParents
    member _.CurrentPhysicalUserMessage(sessionId) = currentPhysicalUserMessage sessionId
    member _.DirectoryFor(sessionId) = directoryFor sessionId
    member _.LogicalOwnerFor(sessionId: SessionId) = logicalOwnerFor sessionId

    member _.RegisterPhysicalParent(sessionId: SessionId, parentId: SessionId option) =
        match parentId with
        | Some parent -> sessionParents.[SessionId.value sessionId] <- SessionId.value parent
        | None -> sessionParents.Remove(SessionId.value sessionId) |> ignore

    member _.ParentWorkRecordFor(sessionId) = parentRecord sessionId
    member _.ChildWorkRecordFor(sessionId) = childRecord sessionId

    member _.RegisterDirectory(sessionId, path) = sessionDirectories.[sessionId] <- path

    member _.RoleFor(ctx: HostToolContext) = roleFor ctx
    member _.EnsureRoleFor(ctx: HostToolContext) = ensureRoleFor ctx

    member _.EnsureCommandRoleFor(sessionId: SessionId, resolveAgent: SessionId -> Task<string option>) =
        ensureCommandRoleFor sessionId resolveAgent

    member _.EnsureRoadDevOpsBound(parentSessionId: SessionId) = ensureRoadDevOpsBound parentSessionId

    /// Manager authorization facts for the capability gate, derived purely
    /// from the objective RoadView. The final incumbent is the one whose
    /// accepted assessment has empty findings: it keeps the read/cleanup/
    /// close-out surface and loses every new-work capability. The cleanup
    /// blocker digest is the stored objective evidence, passed through verbatim.
    static member emptyManagerFacts: ManagerCapabilityFacts =
        { HasActiveIncumbency = false
          HasAssessment = false
          IsFinalIncumbent = false
          CleanupBlockerDigest = None }

    member _.ManagerCapabilityFactsFor(sessionId: string) : ManagerCapabilityFacts = managerFactsOfSession sessionId

    member _.TryFreezeRetirement(sessionId: string, incumbentId: IncumbencyId) =
        lock gate (fun () ->
            match retirementFrozen.TryGetValue sessionId with
            | true, current when current = incumbentId -> false
            | _ ->
                retirementFrozen.[sessionId] <- incumbentId
                true)

    member _.UnfreezeRetirement(sessionId: string) =
        lock gate (fun () -> retirementFrozen.Remove sessionId |> ignore)

    member _.IsRetirementFrozen(sessionId: string) =
        let activeIncumbency = activeIncumbencyOfSession sessionId

        lock gate (fun () ->
            match retirementFrozen.TryGetValue sessionId, activeIncumbency with
            | (false, _), _ -> false
            | (true, frozen), Some active when active <> frozen ->
                retirementFrozen.Remove sessionId |> ignore
                false
            | (true, _), _ -> true)

    member _.RetirementBlockersFor(sessionId: string) =
        let parentOf candidate =
            match sessionParents.TryGetValue candidate with
            | true, parent -> Some parent
            | _ -> None

        let rec belongsToRoot (candidate: string) visited =
            if candidate = sessionId then
                true
            elif Set.contains candidate visited then
                false
            else
                parentOf candidate
                |> Option.exists (fun parent -> belongsToRoot parent (Set.add candidate visited))

        lock gate (fun () ->
            let devopsChildFromRuntime =
                match runtimes.TryGetValue sessionId with
                | true, runtime -> runtime.TryChildSession "devops"
                | false, _ -> None

            let devopsChildFromJournal =
                journal
                |> Option.bind (fun durable ->
                    let snapshot = AgentJournal.snapshot durable

                    AgentProjection.tryFind (SessionId.create sessionId) snapshot.AgentProjections
                    |> Option.bind (fun s -> s.Handles)
                    |> Option.bind (HandleProjection.tryFindByByname "devops")
                    |> Option.map (fun h -> h.ChildSessionId))

            let devopsChildSessionIdOpt =
                devopsChildFromRuntime |> Option.orElse devopsChildFromJournal

            let devopsChildKeyOpt = devopsChildSessionIdOpt |> Option.map SessionId.value

            let rec belongsToDevOps (candidate: string) visited =
                match devopsChildKeyOpt with
                | Some devopsChildKey when candidate = devopsChildKey -> true
                | Some _ when Set.contains candidate visited -> false
                | Some _ ->
                    parentOf candidate
                    |> Option.exists (fun parent -> belongsToDevOps parent (Set.add candidate visited))
                | None -> false

            let isDevOpsAgent (handle: string) (child: SessionId) =
                handle = "devops"
                || (match devopsChildKeyOpt with
                    | Some devopsChildKey -> SessionId.value child = devopsChildKey
                    | None -> false)

            let runtimeBlockers prefix ownerKey (runtime: HostForkRuntime) =
                let rawAgents = runtime.SnapshotOutstandingAgentRuns()

                let relevantAgents =
                    rawAgents
                    |> List.filter (fun (handle, child) ->
                        if ownerKey = sessionId then
                            not (isDevOpsAgent handle child)
                        else
                            true)

                let agents =
                    relevantAgents
                    |> List.map (fun (handle, child) ->
                        sprintf "%s:%s:agent:%s:%s" prefix ownerKey handle (SessionId.value child))

                if List.isEmpty agents then
                    ignore (runtime.Runtime.DrainAgentWakes 32)

                let ptys =
                    runtime.SnapshotOutstandingPtyRuns()
                    |> List.map (fun pty -> sprintf "%s:%s:pty:%s" prefix ownerKey pty)

                let pending =
                    [ if relevantAgents.Length > 0 then
                          yield sprintf "%s:%s:pending-runs:%d" prefix ownerKey relevantAgents.Length

                      if runtime.PendingCompletionCount > 0 && not (List.isEmpty agents) then
                          yield sprintf "%s:%s:pending-completions:%d" prefix ownerKey runtime.PendingCompletionCount ]

                agents @ ptys @ pending

            let isRelevantSession (candidate: string) =
                belongsToRoot candidate Set.empty && not (belongsToDevOps candidate Set.empty)

            let forkBlockers =
                runtimes
                |> Seq.collect (fun pair ->
                    if isRelevantSession pair.Key then
                        runtimeBlockers "fork" pair.Key pair.Value
                    else
                        [])
                |> Seq.toList

            let executorBlockers =
                executorRuntimes
                |> Seq.collect (fun pair ->
                    if isRelevantSession pair.Key then
                        runtimeBlockers "executor" pair.Key pair.Value
                    else
                        [])
                |> Seq.toList

            let toolOwned =
                if ownedWorkCount > 0 then
                    [ sprintf "tool-owned-work:%d" ownedWorkCount ]
                else
                    []

            (forkBlockers @ executorBlockers @ toolOwned) |> List.distinct |> List.sort)

    /// AGENT-013 + PROMPT-008: the managed agent a PTY is opened for.
    member _.ManagedAgentFor(ctx: HostToolContext) = managedAgentFor ctx

    member this.IsRole(ctx: HostToolContext, expected: Role) = this.RoleFor ctx = Some expected

    /// Wire PluginRuntimeScope.RequireCurrentProcessJoin (or test double).
    member _.AttachCurrentProcessJoin(fn: SessionId -> Task<FamilyRecovery>) = currentProcessJoin <- Some fn

    /// Wire current-process join recovery mode directly by string label ("ready", "waiting", or "blocked").
    member this.AttachCurrentProcessJoinMode(mode: string) =
        this.AttachCurrentProcessJoin(fun root ->
            let recovery =
                match mode with
                | "ready" -> FamilyRecovery.FamilyReady(FamilyRecoveryPermit.currentProcess root 0L)
                | "waiting" ->
                    FamilyRecovery.FamilyWaiting(NonEmpty.one (RecoveryBlock.RecoveryCoordinatorUnavailable root))
                | _ -> FamilyRecovery.FamilyBlocked(NonEmpty.one (RecoveryBlock.RecoveryCoordinatorUnavailable root))

            Task.FromResult recovery)

    /// EXEC-017: share PluginRuntimeScope.JoinAttempts with JoinTool.
    member _.AttachJoinAttempts(registry: IJoinAttemptRegistry) = joinAttempts <- registry

    member _.JoinAttempts = joinAttempts

    /// Process-local join admission: join / JoinPublishedAvailable require FamilyReady. Missing attach → FamilyBlocked.
    member _.RequireCurrentProcessJoin(root: SessionId) : Task<FamilyRecovery> =
        task {
            match currentProcessJoin with
            | None ->
                return FamilyRecovery.FamilyBlocked(NonEmpty.one (RecoveryBlock.RecoveryCoordinatorUnavailable root))
            | Some fn -> return! fn root
        }

    member _.RuntimeFor(ctx: HostToolContext) =
        if String.IsNullOrWhiteSpace ctx.SessionId then
            Error "Missing sessionID"
        else
            SessionId.create ctx.SessionId
            |> logicalOwnerFor
            |> SessionId.value
            |> getOrCreateRuntime

    member this.PtyCapabilityFor(ctx: HostToolContext) : Result<DelegationPtyCapability, string> =
        this.RuntimeFor ctx |> Result.map (fun r -> r.PtyCapability)

    /// crash-reconciliation-018: process-local adoption for explicit /continue. The durable
    /// handle stays byte-for-byte as it was at the crash boundary; a later LLM
    /// fork reuse is the first action allowed to reopen it durably.
    member _.AdoptExistingChild(parentSessionId: SessionId, record: HandleRecord) : Result<unit, string> =
        resumableAgentId record
        |> Result.bind (adoptExistingIntoRuntime parentSessionId record)

    member _.ExecutorRuntimeFor(ctx: HostToolContext) =
        lock gate (fun () ->
            match executorRuntimes.TryGetValue ctx.SessionId with
            | true, runtime when not runtime.IsCancelled -> runtime
            | _ ->
                let runtime =
                    HostForkRuntime(
                        SessionId.create ctx.SessionId,
                        sessions,
                        childRecordForRun,
                        CompletionMailboxRuntime.create,
                        NodeTiming.nodeClockPort (),
                        NodeTiming.raceExit,
                        ?journal = journal,
                        onChildCreated = (fun _ role childId -> registerChild ctx.SessionId role childId),
                        // EXEC-014: host executor leaves are Host-owned and
                        // parent-invisible. A DurableParentHandle would leak every
                        // worker into the caller's list/join/guard (EXEC-016) and
                        // block suicide with "join before end" long after `run`
                        // returned its bounded result.
                        ownership = HandleOwnership.HostOwnedHidden
                    )

                executorRuntimes.[ctx.SessionId] <- runtime
                runtime)

    member _.OrchestratorHostFor(sessionId: string) : OrchestratorHost =
        lock gate (fun () ->
            match orchestratorHosts.TryGetValue sessionId with
            | true, host -> host
            | false, _ ->
                let deps: OrchestratorHostDeps =
                    { Sessions = sessions
                      RootWorkspace = rootWorkspace
                      WaitObserver = waitObserver
                      Journal = journal
                      SessionSnapshot = snapshot
                      OnChildCreated = fun _ role childId -> registerChild sessionId role childId
                      RegisterChildDirectory = fun childId path -> sessionDirectories.[SessionId.value childId] <- path
                      OnRunStarted = onStarted
                      SendGateContinuation = HostSessionNudge.trySendGateContinuationPhysical sessions rootWorkspace
                      ContinueManagerLoop = continueManagerLoop
                      CaptureWorktreeSnapshot = captureWorktreeSnapshot
                      RepoPath = defaultArg workspaceDirectory "."
                      TargetBranch = ""
                      ParentWorkRecordFor = fun sid -> parentRecord (SessionId.value sid)
                      ChildWorkRecordFor = fun sid -> childRecord (SessionId.value sid)
                      ChildWorkRecordForRun = childRecordForRun }

                let host = OrchestratorHost(deps, SessionId.create sessionId)
                orchestratorHosts.[sessionId] <- host
                host)

    member _.RunOwnedWork(start: unit -> Task) : bool =
        let admitted =
            lock ownedWorkGate (fun () ->
                if not acceptingOwnedWork then
                    false
                else
                    ownedWorkCount <- ownedWorkCount + 1
                    true)

        if admitted then
            observeOwnedWork start |> ignore

        admitted

    member _.DisposeExecutorRuntime(sessionId: string) : Task =
        let runtime =
            lock gate (fun () ->
                match executorRuntimes.TryGetValue sessionId with
                | true, active ->
                    executorRuntimes.Remove sessionId |> ignore
                    Some active
                | false, _ -> None)

        match runtime with
        | Some active -> active.CancelAndDrain()
        | None -> Task.FromResult(()) :> Task

    /// EXEC-016: live PTY on the parent fork runtime (not Executor runtime).
    member _.HasLivePty(sessionId: string) : bool =
        lock gate (fun () ->
            match runtimes.TryGetValue sessionId with
            | true, runtime when not runtime.IsCancelled ->
                let _, ptys = runtime.List()
                not (List.isEmpty ptys)
            | _ -> false)

    member _.CancelSessionChildren(sessionId: string) : Task =
        let forkRuntimes, orchestrator =
            lock gate (fun () ->
                let owned = ResizeArray<HostForkRuntime>()

                match runtimes.TryGetValue sessionId with
                | true, runtime -> owned.Add runtime
                | false, _ -> ()

                match executorRuntimes.TryGetValue sessionId with
                | true, runtime ->
                    executorRuntimes.Remove sessionId |> ignore
                    addUniqueForkRuntime owned runtime
                | false, _ -> ()

                let host =
                    match orchestratorHosts.TryGetValue sessionId with
                    | true, current ->
                        orchestratorHosts.Remove sessionId |> ignore
                        Some current
                    | false, _ -> None

                owned |> Seq.toList, host)

        task {
            for runtime in forkRuntimes do
                do! runtime.CancelAndDrain()

            match orchestrator with
            | Some host -> do! host.CancelAndDrain()
            | None -> ()
        }
        :> Task

    /// managed-session-lifecycle-017: an internal stop is a synchronous
    /// logical termination CE. Failed delivery is what completes the durable
    /// fork handle and wakes the parent; no future TurnAborted callback carries
    /// workflow continuation state.
    member this.TerminateSession(sessionId: string, reason: string) : Task<Result<unit, string>> =
        let authorityRoot =
            currentPhysicalUserMessage sessionId
            |> Option.map (
                Wanxiangshu.Foundation.Identity.PhysicalUserMessageId.create
                >> Wanxiangshu.Foundation.Identity.PhysicalUserMessageId.promoteToAuthorityRoot
            )

        match terminalPort, authorityRoot with
        | None, _ -> Task.FromResult(Error "MANAGED-SESSION-017: terminal event port unavailable")
        | _, None -> Task.FromResult(Error "MANAGED-SESSION-017: current authority root unavailable")
        | Some eventPort, Some root ->
            ManagedSessionTermination.terminate
                (fun sid -> this.CancelSessionChildren(SessionId.value sid))
                sessions
                eventPort
                (SessionId.create sessionId)
                root
                reason

    member _.DisposeSession(sessionId: string) : Task =
        let forkRuntimes, orchestrator =
            lock gate (fun () ->
                // DSL-MUTABLE: algorithm-scratch — owned runtime accumulator for dispose
                let owned = ResizeArray<HostForkRuntime>()

                match runtimes.TryGetValue sessionId with
                | true, runtime ->
                    runtimes.Remove sessionId |> ignore
                    owned.Add runtime
                | false, _ -> ()

                match executorRuntimes.TryGetValue sessionId with
                | true, runtime ->
                    executorRuntimes.Remove sessionId |> ignore
                    addUniqueForkRuntime owned runtime
                | false, _ -> ()

                let host =
                    match orchestratorHosts.TryGetValue sessionId with
                    | true, current ->
                        orchestratorHosts.Remove sessionId |> ignore
                        Some current
                    | false, _ -> None

                owned |> Seq.toList, host)

        task {
            for runtime in forkRuntimes do
                do! runtime.CancelAndDrain()

            match orchestrator with
            | Some host -> do! host.CancelAndDrain()
            | None -> ()
        }
        :> Task

    /// managed-session-lifecycle-024: Replace crashed DevOps physical session under single logical authority
    member private this.FinishReplaceChildSession
        durable
        runtime
        parentSessionId
        devopsAgentId
        devopsName
        role
        newChildSessionId
        =
        task {
            let! linkResult =
                linkDevOpsChild durable runtime parentSessionId devopsAgentId devopsName role newChildSessionId

            match linkResult with
            | Error err -> return Error err
            | Ok() -> return Ok newChildSessionId
        }

    member private this.ContinueWithRuntime
        durable
        parentSessionId
        devopsAgentId
        devopsName
        role
        newChildSessionId
        runtimeResult
        =
        match runtimeResult with
        | Error err -> Task.FromResult(Error err)
        | Ok runtime ->
            this.FinishReplaceChildSession
                durable
                runtime
                parentSessionId
                devopsAgentId
                devopsName
                role
                newChildSessionId

    member private this.ContinueReplacePhysicalSession durable parentSessionId devopsAgentId devopsName role key =
        function
        | Error err -> Task.FromResult(Error(sprintf "create replacement child failed: %s" err))
        | Ok newChildSessionId ->
            this.ContinueWithRuntime
                durable
                parentSessionId
                devopsAgentId
                devopsName
                role
                newChildSessionId
                (getOrCreateRuntime key)

    member private this.ContinueAfterRetire
        durable
        parentSessionId
        devopsAgentId
        oldChildSessionId
        devopsName
        role
        key
        =
        task {
            do! this.DisposeSession(SessionId.value oldChildSessionId)

            let! childResult =
                sessions.CreateChildSession(
                    parentSessionId,
                    { Title = Some "devops"
                      Agent = Some devopsName
                      Directory = directoryFor key }
                )

            return!
                this.ContinueReplacePhysicalSession
                    durable
                    parentSessionId
                    devopsAgentId
                    devopsName
                    role
                    key
                    childResult
        }

    member private this.ProceedReplacePhysicalSession durable parentSessionId devopsAgentId oldChildSessionId =
        task {
            let journalPort = AgentJournalPortAdapter.fromAgentJournal durable
            let handle = HandleController.agentHandle devopsAgentId
            let projection = journalPort.HandleProjection parentSessionId

            // managed-session-lifecycle-024 / 024.test.mjs: if active at crash, record terminal completion before retirement
            let! prepareResult =
                match HandleProjection.tryFind handle projection with
                | Some { Lifecycle = HandleLifecycle.Active } ->
                    journalPort.AppendExecutionFact
                        parentSessionId
                        (ExecutionFactCases.HandleCompleted
                            {| ParentSessionId = parentSessionId
                               Handle = handle
                               Kind = HandleCompletionKind.Terminal
                               CompletionRef = None
                               CompletionDigest = None |})
                | _ -> Task.FromResult(Ok())

            match prepareResult with
            | Error err -> return Error(sprintf "prepare old handle failed: %s" err)
            | Ok() ->
                return!
                    this.RetireAndContinuePhysicalSession
                        journalPort
                        parentSessionId
                        devopsAgentId
                        oldChildSessionId
                        durable
        }

    member private this.RetireAndContinuePhysicalSession
        (journalPort: AgentJournalPort)
        (parentSessionId: SessionId)
        (devopsAgentId: string)
        (oldChildSessionId: SessionId)
        (durable: AgentJournal)
        =
        task {
            let! retireResult = HandleController.retire (Some journalPort) parentSessionId devopsAgentId
            let devopsName = "devops"
            let role = Role.DevOps
            let key = SessionId.value parentSessionId

            match retireResult with
            | Error err -> return Error(sprintf "retire old handle failed: %s" err)
            | Ok() ->
                return!
                    this.ContinueAfterRetire durable parentSessionId devopsAgentId oldChildSessionId devopsName role key
        }

    member this.ReplacePhysicalSession
        (parentSessionId: SessionId, devopsAgentId: string, oldChildSessionId: SessionId)
        : Task<Result<SessionId, string>> =
        match journal with
        | None -> Task.FromResult(Error "journal unavailable")
        | Some durable -> this.ProceedReplacePhysicalSession durable parentSessionId devopsAgentId oldChildSessionId

    member _.DisposeAsync() : Task =
        let forkRuntimes, orchestrators =
            lock gate (fun () ->
                if disposed then
                    [], []
                else
                    disposed <- true

                    let ownedForkRuntimes =
                        Seq.append runtimes.Values executorRuntimes.Values |> Seq.distinct |> Seq.toList

                    let ownedOrchestrators = orchestratorHosts.Values |> Seq.toList

                    runtimes.Clear()
                    executorRuntimes.Clear()
                    orchestratorHosts.Clear()

                    ownedForkRuntimes, ownedOrchestrators)

        task {
            let! ownedFailure = stopOwnedWorkAndDrain ()
            // DSL-MUTABLE: algorithm-scratch — failures from all owned runtime shutdowns.
            let failures = ResizeArray<exn>()
            ownedFailure |> Option.iter failures.Add

            for runtime in forkRuntimes do
                let! failure = captureOwnedWorkFailure runtime.DetachAndDrain
                failure |> Option.iter failures.Add

            for host in orchestrators do
                let! failure = captureOwnedWorkFailure host.DetachAndDrain
                failure |> Option.iter failures.Add

            match Seq.toList failures with
            | [] -> return ()
            | [ failure ] -> return raise failure
            | failures ->
                return
                    raise (
                        emitJsExpr
                            (List.toArray failures)
                            "new AggregateError($0, 'tool runtime scope detach failed', { cause: $0[0] })"
                    )
        }
        :> Task

    member this.Dispose() = this.DisposeAsync() |> ignore

    interface ISessionRuntimeOwner with
        member this.CancelSessionChildren sessionId = this.CancelSessionChildren sessionId
        member this.DisposeSession sessionId = this.DisposeSession sessionId
        member this.DisposeExecutorRuntime sessionId = this.DisposeExecutorRuntime sessionId
        member this.DisposeAsync() = this.DisposeAsync()
        member this.HasLivePty sessionId = this.HasLivePty sessionId

    interface IDisposable with
        member this.Dispose() = this.Dispose()
