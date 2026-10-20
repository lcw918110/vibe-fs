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
open Wanxiangshu.Mission.Work
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process
open Wanxiangshu.OpenCode.Host


/// The parentless manager execution spawn used by an authorized delivery.
/// Every branch reports what the Host actually proved; an unproven creation or
/// admission stays Unknown and never unlocks a fresh delivery operation.
module private ExecutionSpawn =
    type private Spawn = Wanxiangshu.Mission.Planning.DeliverySpawnOutcome

    let unknown (prefix: string) (reason: string) : Spawn =
        Spawn.Unknown(sprintf "%s: %s" prefix reason)

    let ofSendOutcome (sessionId: SessionId) (outcome: Outcome.SendOutcome) : Spawn =
        match outcome with
        | Outcome.SendOutcome.AdmittedWithReceipt _
        | Outcome.SendOutcome.AdmittedWithPhysicalMessage _ -> Spawn.Started(SessionId.value sessionId)
        | Outcome.SendOutcome.Retryable reason
        | Outcome.SendOutcome.Fatal reason ->
            Spawn.NotStarted(sprintf "Execution session prompt was not admitted: %s" reason)
        | Outcome.SendOutcome.AcceptanceUnknown reason ->
            unknown "Execution session prompt acceptance is unknown" reason

    let prompt (sessions: ISessionHostPort) (sessionId: SessionId) (agent: string) (text: string) =
        task {
            use _listener = sessions.SubscribeTerminal(sessionId, (fun _ _ -> ()))

            let sendOptions: SessionPromptOptions =
                { Model = None
                  Agent = Some agent
                  Directory = None
                  Metadata = None
                  Tools = None
                  DetachedListener = None }

            let! outcome = sessions.SendPrompt(sessionId, text, sendOptions)
            return ofSendOutcome sessionId outcome
        }

    let create (sessions: ISessionHostPort) (operationId: string) (agent: string) : Task<Result<SessionId, string>> =
        task {
            let title = sprintf "wanxiangshu-delivery:%s" operationId

            let options: OpenCodeChildOptions =
                { Title = Some title
                  Agent = Some agent
                  Directory = None }

            match! sessions.CreateSiblingSession(SessionId.create operationId, None, options) with
            // Session creation itself is a Host effect. A generic transport
            // error does not prove that the Host failed before creating it.
            | Error error -> return Error(sprintf "Execution session creation outcome is unknown: %s" error)
            | Ok sessionId -> return Ok sessionId
        }


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
        viewForSession: (string -> Task<string option>) option,
        snapshot: ISessionSnapshotPort option,
        cancelSignals: (SessionId seq -> unit) option,
        ?childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>),
        ?continueManagerLoop: (SessionId -> string -> Task<Result<unit, string>>),
        ?eventPort: IEventObservationPort
    ) =

    let gate = obj ()
    let ownedWorkGate = obj ()
    // DSL-MUTABLE: resource — per-owner fork runtime registry
    let runtimes = Dictionary<string, HostForkRuntime>()
    // DSL-MUTABLE: resource — per-session executor runtime registry
    let executorRuntimes = Dictionary<string, HostForkRuntime>()

    let scopeOwnershipId =
        "tool-runtime-scope:" + Guid.NewGuid().ToString("N").Substring(0, 8)
    // DSL-MUTABLE: resource — wanxiangli/ownership registry for scope-level owned work and tenure drain
    let scopeOwnership =
        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.createOwnership scopeOwnershipId [||]
    // DSL-MUTABLE: resource — monotonic sequence for scope ownership requests
    let mutable scopeOwnershipSeq = 0
    // DSL-MUTABLE: resource — tenure scope index; the Drain fact itself lives in the Ownership registry
    let tenureScopes = Dictionary<string, string * int>()
    // DSL-MUTABLE: resource — per-session orchestrator host registry
    let orchestratorHosts = Dictionary<string, OrchestratorHost>()
    let onCancelSignals = defaultArg cancelSignals ignore
    let onStarted = defaultArg onRunStarted (fun _ _ _ -> ())
    let sessionView = defaultArg viewForSession (fun _ -> Task.FromResult None)

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

    let childViewForRun = defaultArg childViewForRun (fun _ _ -> Task.FromResult None)

    /// change-integration-005/013: the head cursor this process observed for one
    /// session, read from the same journal projection the Manager deliver used for
    /// its retirement cursor. A missing projection is not a guessed cursor, so the
    /// hand-off it feeds fails closed.
    let observedHeadCursor (sessionId: SessionId) : int64 option =
        journal
        |> Option.bind (fun durable ->
            AgentProjection.tryFind sessionId (AgentJournal.snapshot durable).AgentProjections
            |> Option.bind (fun session -> session.XTrace)
            |> Option.map (XTraceProjection.headCursor >> XTraceCursor.sequence))

    let terminalPort = eventPort
    // DSL-MUTABLE: resource — tool runtime dispose latch
    let mutable disposed = false
    // DSL-MUTABLE: single-flight — shared waiter for tool-owned callback drain.
    let mutable ownedWorkDrainWaiter: TaskCompletionSource<unit> option = None
    // DSL-MUTABLE: resource — first tool-owned callback failure for shutdown propagation.
    let mutable ownedWorkFailure: exn option = None
    /// Process-local join admission before join / publish consume.
    // DSL-MUTABLE: resource — current-process join admission callback attachment
    let mutable currentProcessJoin: (SessionId -> Task<FamilyRecovery>) option = None
    let mutable questionNotifier: (string -> Task<Result<unit, string>>) option = None
    let questionRequests = HashSet<SessionId>()
    let questionInterrupts = HashSet<SessionId * ProviderRunIdentity * ToolCallId>()

    let postS3PendingInterrupts =
        HashSet<SessionId * ProviderRunIdentity * ToolCallId>()

    let postS3SettledInterrupts =
        HashSet<SessionId * ProviderRunIdentity * ToolCallId>()

    /// EXEC-017 attempt-scoped join interrupt registry (PluginRuntimeScope or local default).
    // DSL-MUTABLE: resource — join attempt registry attachment
    let mutable joinAttempts: IJoinAttemptRegistry =
        JoinAttemptRegistry() :> IJoinAttemptRegistry

    let ownedWorkScopeId = "tool-owned-work-scope"

    do
        let openRoot =
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                "OpenScope"
                [ "scopeId", box ownedWorkScopeId; "parentId", null ]

        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
            scopeOwnership
            ("open:" + ownedWorkScopeId)
            openRoot
        |> ignore

    let unsettledOwnedWork () =
        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipBlockersInMemory scopeOwnership ownedWorkScopeId

    let startReservedOwnedWork
        (seq: int)
        (resId: string)
        (lease: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.OwnershipLease)
        =
        let inc = sprintf "inc-%d" seq

        let startedMsg =
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                "Started"
                [ "lease", Wanxiangshu.Mission.Lifecycle.DurableWorkHost.leaseObject lease
                  "binding", Wanxiangshu.Mission.Lifecycle.DurableWorkHost.bindingObject resId inc ]

        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
            scopeOwnership
            ("started:" + resId)
            startedMsg
        |> ignore

        lease, resId, inc

    let reserveOwnedWork () =
        lock ownedWorkGate (fun () ->
            scopeOwnershipSeq <- scopeOwnershipSeq + 1
            let seq = scopeOwnershipSeq
            let resId = sprintf "tool-owned-work:%d" seq

            try
                let msg =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                        "Reserve"
                        [ "resourceId", box resId; "scopeId", box ownedWorkScopeId; "kind", box "tool" ]

                let receipt =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                        scopeOwnership
                        ("reserve:" + resId)
                        msg

                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.mayStartResource receipt
                |> Option.map (startReservedOwnedWork seq resId)
            with _ ->
                None)

    let finishOwnedWork
        (lease: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.OwnershipLease)
        (resId: string)
        (inc: string)
        (outcome: string)
        =
        lock ownedWorkGate (fun () ->
            try
                let endedMsg =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                        "Ended"
                        [ "lease", Wanxiangshu.Mission.Lifecycle.DurableWorkHost.leaseObject lease
                          "binding", Wanxiangshu.Mission.Lifecycle.DurableWorkHost.bindingObject resId inc
                          "outcome", box outcome ]

                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                    scopeOwnership
                    ("ended:" + resId)
                    endedMsg
                |> ignore
            with _ ->
                ()

            let phase =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipScopePhaseInMemory
                    scopeOwnership
                    ownedWorkScopeId

            if
                (phase = Some "Draining" || phase = Some "Closed")
                && List.isEmpty (unsettledOwnedWork ())
            then
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
                try
                    scopeOwnershipSeq <- scopeOwnershipSeq + 1

                    let drainMsg =
                        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                            "Drain"
                            [ "scopeId", box ownedWorkScopeId ]

                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                        scopeOwnership
                        (sprintf "drain:%s:%d" ownedWorkScopeId scopeOwnershipSeq)
                        drainMsg
                    |> ignore
                with _ ->
                    ()

                if List.isEmpty (unsettledOwnedWork ()) then
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

    let observeOwnedWork
        (lease: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.OwnershipLease)
        (resId: string)
        (inc: string)
        (start: unit -> Task)
        : Task =
        task {
            let! failure = captureOwnedWorkFailure start
            failure |> Option.iter recordOwnedWorkFailure
            finishOwnedWork lease resId inc (if failure.IsSome then "Failed" else "Completed")
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
            childViewForRun,
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
          HasConfirmation = false
          IsFinalIncumbent = false
          CleanupBlockerDigest = None }

    // Class-body let bindings are always private; an explicit visibility
    // attribute here is a duplicate and the compiler rejects it.
    let inspectWorkspaceWork workspace sessionId =
        task {
            let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace

            if String.IsNullOrWhiteSpace commonDir then
                return None
            else
                return Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir sessionId
        }

    let lifecycleWorkOfDirectory
        (directory: string option)
        (sessionId: string)
        : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
        match directory with
        | None -> Task.FromResult None
        | Some workspace -> inspectWorkspaceWork workspace sessionId

    /// The active unified incumbency id for this session.
    let activeIncumbencyOfSession (directory: string option) (sessionId: string) : Task<string option> =
        task {
            let! work = lifecycleWorkOfDirectory directory sessionId
            return work |> Option.bind (fun work -> work.ActiveId)
        }

    let managerFactsOfSession (directory: string option) (sessionId: string) : Task<ManagerCapabilityFacts> =
        task {
            // The capability gate reads the wanxiangli work snapshot for this
            // session. No phase enum crosses this boundary: an inactive work grants
            // nothing, a recorded cleanup blocker confines to the finish window, a
            // promoted final incumbent keeps the read/cleanup/close-out surface, and
            // every other active fact keeps the full Manager set.
            let! work = lifecycleWorkOfDirectory directory sessionId

            return
                work
                |> Option.map (fun work ->
                    match work.ActiveId, work.Reviewed with
                    | None, _ -> defaultEmptyManagerFacts
                    | Some _, reviewed ->
                        { HasActiveIncumbency = true
                          HasAssessment = reviewed
                          HasConfirmation = work.Confirmed
                          IsFinalIncumbent = work.ReviewOutcome = Some "Promoted"
                          CleanupBlockerDigest = work.BlockerDigest })
                |> Option.defaultValue defaultEmptyManagerFacts
        }

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

    let isManagerWorkSession (sessionId: SessionId) =
        let sidStr = SessionId.value sessionId

        match String.IsNullOrWhiteSpace sidStr with
        | true -> false
        | false -> isManagerProfile sessionId

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
            // runTask never starts, and settle's AwaitCurrentView waits on the
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

    let obtainWorkDevOpsTask (parentSessionId: SessionId) (key: string) =
        lock gate (fun () ->
            match getOrCreateRuntime key with
            | Ok runtime when not (runtime.OwnsAgent "devops") ->
                devopsBindingTasks.Remove key |> ignore
                getOrCreateDevOpsTask parentSessionId key
            | _ -> getOrCreateDevOpsTask parentSessionId key)

    let ensureWorkDevOpsBound (parentSessionId: SessionId) : Task<unit> =
        match isManagerWorkSession parentSessionId with
        | false -> Task.FromResult()
        | true ->
            let key = SessionId.value parentSessionId
            obtainWorkDevOpsTask parentSessionId key

    member _.Sessions = sessions
    member _.WaitObserver = waitObserver
    member _.RootWorkspace = rootWorkspace
    member _.Journal = journal
    member _.Snapshot = snapshot
    member _.EventPort = terminalPort
    member _.WorkspaceDirectory = workspaceDirectory
    member _.ActiveProfileFor(sessionId: SessionId) = activeProfileFor sessionId

    member _.AttachQuestionNotifier(notify: string -> Task<Result<unit, string>>) = questionNotifier <- Some notify

    member _.IsWaitingForUser(sessionId: string) =
        journal
        |> Option.bind (fun durable -> UserQuestionJournal.pending durable (SessionId.create sessionId))
        |> Option.isSome

    member private _.ClearQuestionInterrupts(sid: SessionId) =
        let stale =
            questionInterrupts
            |> Seq.filter (fun (owner, _, _) -> owner = sid)
            |> Seq.toArray

        Array.iter (questionInterrupts.Remove >> ignore) stale

    member private _.RecordShownQuestion
        (durable: AgentJournal, sid: SessionId, sessionIdText: string, request: UserQuestionAskedPayload)
        =
        task {
            let! recorded = UserQuestionJournal.request durable request

            match recorded, currentPhysicalUserMessage sessionIdText with
            | Ok(), Some latest ->
                return! UserQuestionJournal.observeHumanInput durable sid (PhysicalUserMessageId.create latest)
            | _ -> return recorded
        }

    member private this.SendFreshQuestion
        (
            durable: AgentJournal,
            sid: SessionId,
            sessionIdText: string,
            notify: string -> Task<Result<unit, string>>,
            request: UserQuestionAskedPayload
        ) =
        taskResult {
            try
                this.ClearQuestionInterrupts sid
                let! shown = notify request.Question
                return! this.RecordShownQuestion(durable, sid, sessionIdText, request)
            finally
                questionRequests.Remove sid |> ignore
        }

    member private this.DispatchValidatedQuestion
        (
            durable: AgentJournal,
            sid: SessionId,
            sessionIdText: string,
            notify: string -> Task<Result<unit, string>>,
            request: UserQuestionAskedPayload
        ) : Task<Result<unit, string>> =
        task {
            let! work =
                lifecycleWorkOfDirectory (directoryFor sessionIdText |> Option.orElse workspaceDirectory) sessionIdText

            if
                not (
                    work
                    |> Option.exists (fun current -> current.ActiveId.IsSome && not current.Delivered)
                )
            then
                return Error "ask requires an unfinished work with an active incumbency"
            elif not (work |> Option.exists (fun current -> current.Stage = "S1")) then
                // 产品裁决：只有 S1 阶段的 Plan 可以向用户中断提问。
                return Error "ask is allowed only in the S1 planning stage"
            elif not (questionRequests.Add sid) then
                return Error "a question is already being sent for this session"
            else
                return! this.SendFreshQuestion(durable, sid, sessionIdText, notify, request)
        }

    member private this.ResolveOrSendQuestion
        (
            durable: AgentJournal,
            sid: SessionId,
            sessionIdText: string,
            call: ToolCallId,
            notify: string -> Task<Result<unit, string>>,
            request: UserQuestionAskedPayload
        ) =
        match UserQuestionJournal.tryFind durable sid call with
        | Some previous when previous.Request = request -> Task.FromResult(Ok())
        | Some _ -> Task.FromResult(Error "ask call identity conflicts with its recorded question")
        | None when UserQuestionJournal.pending durable sid |> Option.isSome ->
            Task.FromResult(Error "a question is already waiting for the user")
        | None -> this.DispatchValidatedQuestion(durable, sid, sessionIdText, notify, request)

    /// 先确认提问已经呈现，再登记同一逻辑运行的等待。工具只交回占位回执。
    member this.AskUser(ctx: HostToolContext, question: string) : Task<Result<unit, string>> =
        task {
            let sid = SessionId.create ctx.SessionId

            match
                journal,
                activeProfileFor sid,
                ctx.ProviderRunId,
                ctx.ToolCallId,
                currentPhysicalUserMessage ctx.SessionId,
                questionNotifier
            with
            | Some durable, Some profile, Some run, Some call, Some physical, Some notify when
                profile.CanonicalRole = Role.Plan && not (String.IsNullOrWhiteSpace question)
                ->
                let request: UserQuestionAskedPayload =
                    { SessionId = sid
                      LogicalRunId = profile.LogicalRunId
                      AuthorityRootUserMessageId = profile.AuthorityRootUserMessageId
                      PhysicalUserMessageId = PhysicalUserMessageId.create physical
                      ProviderRunId = run
                      ToolCallId = call
                      Question = question }

                return! this.ResolveOrSendQuestion(durable, sid, ctx.SessionId, call, notify, request)
            | _ ->
                return
                    Error "ask requires active Plan authority, exact call evidence, a question, and user notification"
        }

    member private _.PruneFailedQuestionInterrupt
        (key: SessionId * ProviderRunIdentity * ToolCallId, stopped: Result<unit, string>)
        =
        if Result.isError stopped then
            questionInterrupts.Remove key |> ignore

        stopped

    member private this.RunQuestionInterrupt(sessionId: SessionId, key: SessionId * ProviderRunIdentity * ToolCallId) =
        task {
            try
                let! stopped = sessions.InterruptAttempt sessionId
                return this.PruneFailedQuestionInterrupt(key, stopped)
            with ex ->
                questionInterrupts.Remove key |> ignore
                return Error ex.Message
        }

    /// 只在宿主确认 ask 的工具结果后中断本次 attempt；不终结逻辑运行或取消子资源。
    member this.InterruptForUserQuestion
        (sessionId: SessionId, providerRun: ProviderRunIdentity, callId: ToolCallId)
        : Task<Result<unit, string>> =
        task {
            match
                journal
                |> Option.bind (fun durable -> UserQuestionJournal.pending durable sessionId)
            with
            | Some question when
                question.ToolCallId = callId
                && question.ProviderRunId = providerRun
                && currentPhysicalUserMessage (SessionId.value sessionId) = Some(
                    PhysicalUserMessageId.value question.PhysicalUserMessageId
                )
                && questionInterrupts.Add(sessionId, question.ProviderRunId, callId)
                ->
                return! this.RunQuestionInterrupt(sessionId, (sessionId, question.ProviderRunId, callId))
            | _ -> return Ok()
        }

    member private _.RunPostS3InterruptAttempt
        (sessionId: SessionId, key: SessionId * ProviderRunIdentity * ToolCallId)
        : Task<Result<unit, string>> =
        task {
            try
                let! stopped = sessions.InterruptAttempt sessionId

                lock gate (fun () ->
                    let _ =
                        Result.isError stopped && postS3SettledInterrupts.Remove key
                        || postS3PendingInterrupts.Remove key

                    ())

                return stopped
            with ex ->
                lock gate (fun () -> postS3SettledInterrupts.Remove key |> ignore)
                return Error ex.Message
        }

    member _.RegisterPostS3Interrupt(sessionId: SessionId, providerRun: ProviderRunIdentity, callId: ToolCallId) =
        lock gate (fun () -> postS3PendingInterrupts.Add(sessionId, providerRun, callId) |> ignore)

    member _.HasPendingPostS3Interrupt(sessionId: string) =
        let sid = SessionId.create sessionId
        lock gate (fun () -> postS3PendingInterrupts |> Seq.exists (fun (owner, _, _) -> owner = sid))

    member this.InterruptForPostS3Deliver
        (sessionId: SessionId, providerRun: ProviderRunIdentity, callId: ToolCallId)
        : Task<Result<unit, string>> =
        let key = sessionId, providerRun, callId

        let shouldInterrupt =
            lock gate (fun () -> postS3PendingInterrupts.Contains key && postS3SettledInterrupts.Add key)

        if shouldInterrupt then
            this.RunPostS3InterruptAttempt(sessionId, key)
        else
            Task.FromResult(Ok())

    /// Run-started callback wired by plugin bootstrap for Host child reconciliation.
    member _.RunStarted = onStarted

    member _.ContinueManagerLoop(sessionId: SessionId, workspace: string) : Task<Result<unit, string>> =
        continueManagerLoop sessionId workspace

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
    member _.ViewForSession(sessionId) = sessionView sessionId
    member _.DirectoryFor(sessionId) = directoryFor sessionId
    member _.LogicalOwnerFor(sessionId: SessionId) = logicalOwnerFor sessionId

    member _.RegisterPhysicalParent(sessionId: SessionId, parentId: SessionId option) =
        match parentId with
        | Some parent -> sessionParents.[SessionId.value sessionId] <- SessionId.value parent
        | None -> sessionParents.Remove(SessionId.value sessionId) |> ignore


    member _.RegisterDirectory(sessionId, path) = sessionDirectories.[sessionId] <- path

    member _.RoleFor(ctx: HostToolContext) = roleFor ctx
    member _.EnsureRoleFor(ctx: HostToolContext) = ensureRoleFor ctx

    member _.EnsureCommandRoleFor(sessionId: SessionId, resolveAgent: SessionId -> Task<string option>) =
        ensureCommandRoleFor sessionId resolveAgent

    member _.EnsureWorkDevOpsBound(parentSessionId: SessionId) = ensureWorkDevOpsBound parentSessionId

    /// work-039: create the parentless execution container. The returned
    /// SessionId is not permission to send its prompt: PlanTools first commits
    /// DeliverySessionBound for the operation.
    member _.CreateExecutionSession(operationId: string, agent: string) : Task<Result<string, string>> =
        task {
            try
                let! created = ExecutionSpawn.create sessions operationId agent
                return created |> Result.map SessionId.value
            with ex ->
                return Error(sprintf "Execution session creation outcome is unknown: %s" ex.Message)
        }

    /// Send the first prompt only after the exact SessionId is durably bound.
    /// AcceptanceUnknown remains pending; it is never converted to NotStarted.
    member _.PromptExecutionSession
        (execSessionId: string, agent: string, prompt: string)
        : Task<Wanxiangshu.Mission.Planning.DeliverySpawnOutcome> =
        task {
            try
                return! ExecutionSpawn.prompt sessions (SessionId.create execSessionId) agent prompt
            with ex ->
                return ExecutionSpawn.unknown "Execution session prompt outcome is unknown" ex.Message
        }

    /// Cleanup for a container whose first prompt was definitely refused.
    /// A failed cleanup prevents NotStarted settlement so no orphan is hidden.
    member _.DiscardExecutionSession(execSessionId: string) : Task<Result<unit, string>> =
        sessions.AbortSession(SessionId.create execSessionId)

    /// Manager authorization facts for the capability gate, derived purely
    /// from the lifecycle work. The final incumbent is the one whose review
    /// is Promoted: it keeps the read/cleanup/close-out surface and loses
    /// every new-work capability. The cleanup blocker digest is the stored
    /// objective evidence, passed through verbatim.
    static member emptyManagerFacts: ManagerCapabilityFacts =
        { HasActiveIncumbency = false
          HasAssessment = false
          HasConfirmation = false
          IsFinalIncumbent = false
          CleanupBlockerDigest = None }

    member self.ManagerCapabilityFactsFor(sessionId: string) : Task<ManagerCapabilityFacts> =
        managerFactsOfSession self.WorkspaceDirectory sessionId

    member self.LifecycleWorkFor
        (sessionId: string)
        : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
        lifecycleWorkOfDirectory self.WorkspaceDirectory sessionId

    member private _.OpenAndDrainTenureScope (sessionId: string) (incumbentId: string) (gen: int) =
        let rootScope = "session:" + sessionId
        let tenureScope = sprintf "tenure:%s:%s:%d" sessionId incumbentId gen

        try
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                scopeOwnership
                (sprintf "open-root:%s:%d" sessionId gen)
                (Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                    "OpenScope"
                    [ "scopeId", box rootScope; "parentId", null ])
            |> ignore

            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                scopeOwnership
                ("open-tenure:" + tenureScope)
                (Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage
                    "OpenScope"
                    [ "scopeId", box tenureScope; "parentId", box rootScope ])
            |> ignore

            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                scopeOwnership
                ("drain-tenure:" + tenureScope)
                (Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage "Drain" [ "scopeId", box tenureScope ])
            |> ignore
        with _ ->
            ()

    member private _.CloseTenureScope (sessionId: string) (incumbentId: string) (gen: int) =
        let tenureScope = sprintf "tenure:%s:%s:%d" sessionId incumbentId gen

        try
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.executeOwnershipInMemory
                scopeOwnership
                ("close-tenure:" + tenureScope)
                (Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipMessage "Close" [ "scopeId", box tenureScope ])
            |> ignore
        with _ ->
            ()

    member this.TryFreezeRetirement(sessionId: string, incumbentId: string) =
        lock gate (fun () ->
            match tenureScopes.TryGetValue sessionId with
            | true, (current, _) when current = incumbentId -> false
            | _ ->
                scopeOwnershipSeq <- scopeOwnershipSeq + 1
                let gen = scopeOwnershipSeq
                this.OpenAndDrainTenureScope sessionId incumbentId gen
                tenureScopes.[sessionId] <- (incumbentId, gen)
                true)

    member this.UnfreezeRetirement(sessionId: string) =
        lock gate (fun () ->
            match tenureScopes.TryGetValue sessionId with
            | false, _ -> ()
            | true, (incumbentId, gen) ->
                this.CloseTenureScope sessionId incumbentId gen
                tenureScopes.Remove sessionId |> ignore)

    member _.IsRetirementFrozen(sessionId: string) : Task<bool> =
        task {
            let! activeIncumbency = activeIncumbencyOfSession workspaceDirectory sessionId

            return
                lock gate (fun () ->
                    match tenureScopes.TryGetValue sessionId, activeIncumbency with
                    | (false, _), _ -> false
                    | (true, (frozen, _)), Some active when active <> frozen ->
                        tenureScopes.Remove sessionId |> ignore
                        false
                    | (true, (frozen, gen)), _ ->
                        let tenureScope = sprintf "tenure:%s:%s:%d" sessionId frozen gen

                        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipScopePhaseInMemory
                            scopeOwnership
                            tenureScope = Some "Draining")
        }

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
                lock ownedWorkGate (fun () ->
                    unsettledOwnedWork () |> List.map (fun blocker -> blocker.Lease.ResourceId))

            let isNotDevOpsScope (scopeId: string) =
                scopeId <> "devops"
                && (devopsChildKeyOpt |> Option.forall (fun devopsKey -> scopeId <> devopsKey))

            let durableBlockers =
                let commonDir =
                    workspaceDirectory
                    |> Option.bind (fun ws -> Wanxiangshu.Persistence.Journal.RuntimePath.forStateRoot ws |> Some)

                commonDir
                |> Option.map (fun commonDir ->
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.ownershipBlockersDurable
                        commonDir
                        sessionId
                        sessionId
                    |> List.filter (fun b -> isNotDevOpsScope b.ScopeId)
                    |> List.map (fun b -> sprintf "ownership:%s:%s:%s" b.ScopeId b.Kind b.Lease.ResourceId))
                |> Option.defaultValue []

            (forkBlockers @ executorBlockers @ toolOwned @ durableBlockers)
            |> List.distinct
            |> List.sort)

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
                        childViewForRun,
                        CompletionMailboxRuntime.create,
                        NodeTiming.nodeClockPort (),
                        NodeTiming.raceExit,
                        ?journal = journal,
                        onChildCreated = (fun _ role childId -> registerChild ctx.SessionId role childId),
                        // EXEC-014: host executor leaves are Host-owned and
                        // parent-invisible. A DurableParentHandle would leak every
                        // worker into the caller's list/join/guard (EXEC-016) and
                        // block deliver with "join before end" long after `run`
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
                      ObservedHeadCursor = observedHeadCursor
                      SessionSnapshot = snapshot
                      OnChildCreated = fun _ role childId -> registerChild sessionId role childId
                      RegisterChildDirectory = fun childId path -> sessionDirectories.[SessionId.value childId] <- path
                      OnRunStarted = onStarted
                      SendGateContinuation = HostSessionNudge.trySendGateContinuationPhysical sessions rootWorkspace
                      ContinueManagerLoop = continueManagerLoop
                      RepoPath = defaultArg workspaceDirectory "."
                      TargetBranch = "" }

                let host = OrchestratorHost(deps, SessionId.create sessionId)
                orchestratorHosts.[sessionId] <- host
                host)

    member _.RunOwnedWork(start: unit -> Task) : bool =
        match reserveOwnedWork () with
        | None -> false
        | Some(lease, resId, inc) ->
            observeOwnedWork lease resId inc start |> ignore
            true

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
        let questionOwner = SessionId.create sessionId

        for key in
            questionInterrupts
            |> Seq.filter (fun (owner, _, _) -> owner = questionOwner)
            |> Seq.toArray do
            questionInterrupts.Remove key |> ignore

        lock gate (fun () ->
            for key in
                postS3PendingInterrupts
                |> Seq.filter (fun (owner, _, _) -> owner = questionOwner)
                |> Seq.toArray do
                postS3PendingInterrupts.Remove key |> ignore

            for key in
                postS3SettledInterrupts
                |> Seq.filter (fun (owner, _, _) -> owner = questionOwner)
                |> Seq.toArray do
                postS3SettledInterrupts.Remove key |> ignore)

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
        member this.RetirementBlockersFor sessionId = this.RetirementBlockersFor sessionId

    interface IDisposable with
        member this.Dispose() = this.Dispose()
