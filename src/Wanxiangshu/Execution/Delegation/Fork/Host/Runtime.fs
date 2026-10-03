namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
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
open Fable.Core.JsInterop
open Microsoft.FSharp.Control
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.OpenCode
open Wanxiangshu.Host
open Wanxiangshu.Process
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Persistence.Journal


[<RequireQualifiedAccess>]
module private PtyHostHelpers =
    let bindTerminalName (gate: obj) (terminalByName: Dictionary<string, string>) (name: string) (id: PtyId) =
        lock gate (fun () ->
            match terminalByName.TryGetValue(name.Trim()) with
            | true, existing when existing <> id.Value ->
                Error(sprintf "Terminal name '%s' is already in use" (name.Trim()))
            | _ ->
                terminalByName.[name.Trim()] <- id.Value
                Ok())

    let ptyByName
        (gate: obj)
        (terminalByName: Dictionary<string, string>)
        (ptyRuns: HashSet<string>)
        (known: PtyId -> bool)
        (name: string)
        =
        lock gate (fun () ->
            match terminalByName.TryGetValue(name.Trim()) with
            | true, id when ptyRuns.Contains id && known (PtyId.Create id) -> Some(PtyId.Create id)
            | _ -> None)

    let ensureLf (prompt: string) =
        if
            prompt.EndsWith("\n", StringComparison.Ordinal)
            || prompt.EndsWith("\r", StringComparison.Ordinal)
        then
            prompt
        else
            prompt + "\n"

    let emptyRead (id: PtyId) : PtyRead =
        { Id = id; Output = ""; Closed = false }

    let mapRead (id: PtyId) (output: string, closed: bool) : PtyRead =
        { Id = id
          Output = output
          Closed = closed }

    let tryTerminalNameByPtyId (gate: obj) (terminalByName: Dictionary<string, string>) (ptyId: string) =
        lock gate (fun () ->
            terminalByName
            |> Seq.tryPick (fun (KeyValue(name, id)) -> if id = ptyId then Some name else None))

/// Bridges real child sessions to the existing completion mailbox.
/// Fork / Reuse / Pty operations live in extension files (semantic split).
type HostForkRuntime
    (
        parentId: SessionId,
        sessions: ISessionHostPort,
        childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>,
        createMailbox: obj -> ForkCompletionMailbox,
        clock: IClockPort,
        raceExit: Task -> int -> Task<bool>,
        ?journal: AgentJournal,
        ?onChildCreated: string -> Role -> SessionId -> unit,
        ?onChildCreatedDir: string -> SessionId -> string option -> unit,
        ?ptyPort: PtyPort,
        ?directoryFor: string -> string option,
        ?onRunStarted: SessionId -> Role -> string option -> unit,
        ?parentWorkRecordFor: SessionId -> Task<string option>,
        ?childWorkRecordFor: SessionId -> Task<string option>,
        ?handoff: ReusableHandoffPort,
        ?sessionSnapshot: ISessionSnapshotPort,
        ?cancelSignals: SessionId seq -> unit,
        /// Ownership of every handle this runtime forks. Host-owned hidden
        /// children stay outside the parent's list/join/recovery surface.
        ?ownership: HandleOwnership,
        /// Optional capability to drain child PTYs upon run completion (e.g. for DevOps session).
        ?drainChildPtys: SessionId -> Task<unit>
    ) as this =
    let clockPort = clock
    let runtime = ForkRuntimeBackend.create clockPort raceExit createMailbox
    // DSL-MUTABLE: resource — live child session registry by agent id
    let children = Dictionary<string, SessionId>()
    // DSL-MUTABLE: resource — process-owned agent handle set
    let processOwnedAgents = HashSet<string>()
    // DSL-MUTABLE: resource — dormant /continue child registry by agent id
    let dormantChildren = Dictionary<string, SessionId>()
    // DSL-MUTABLE: resource — pending host run registry by agent id
    let pendingRuns = Dictionary<string, PendingHostRun>()
    // DSL-MUTABLE: resource — PTY run id set owned by this runtime
    let ptyRuns = HashSet<string>()
    /// Provider TerminalName → PtyId. Occupied until Join delivers closure.
    // DSL-MUTABLE: resource — terminal name to PtyId map
    let terminalByName = Dictionary<string, string>()
    let ptyCompletionObservers = ResizeArray<PtyJoinItem -> unit>()
    let bufferedJoinItems = Queue<JoinItem>()
    let gate = obj ()
    let cancelGate = obj ()
    let ownedWorkGate = obj ()
    // DSL-MUTABLE: single-flight — duplicate joins fail before waiting
    let mutable joinInFlight = false
    // One terminal runtime teardown owns either logical cancel or process-local detach.
    // DSL-MUTABLE: single-flight — detach/cancel teardown owner under cancelGate
    let mutable teardownTask: Task option = None
    // DSL-MUTABLE: resource — terminal/failure callback admission latch.
    let mutable acceptingOwnedWork = true
    // DSL-MUTABLE: resource — in-flight runtime-owned callback count.
    let mutable ownedWorkCount = 0
    // DSL-MUTABLE: single-flight — shared waiter for callback drain.
    let mutable ownedWorkDrainWaiter: TaskCompletionSource<unit> option = None
    // DSL-MUTABLE: resource — first runtime-owned callback failure for shutdown propagation.
    let mutable ownedWorkFailure: exn option = None
    // DSL-MUTABLE: resource — acknowledgement for observed callbacks without closing admission.
    let mutable observedWorkWaiter: TaskCompletionSource<unit> option = None

    let finishOwnedWork () =
        lock ownedWorkGate (fun () ->
            ownedWorkCount <- ownedWorkCount - 1

            if ownedWorkCount = 0 then
                observedWorkWaiter
                |> Option.iter (fun waiter -> AsyncSupport.trySetResult waiter () |> ignore)

                observedWorkWaiter <- None

            if not acceptingOwnedWork && ownedWorkCount = 0 then
                ownedWorkDrainWaiter
                |> Option.iter (fun waiter -> AsyncSupport.trySetResult waiter () |> ignore))

    let ensureObservedWorkWaiter () =
        match observedWorkWaiter with
        | Some waiter -> waiter.Task
        | None ->
            let waiter =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            observedWorkWaiter <- Some waiter
            waiter.Task

    let recordOwnedWorkFailure (failure: exn) =
        lock ownedWorkGate (fun () -> ownedWorkFailure <- Option.orElse ownedWorkFailure (Some failure))

    let captureOwnedWorkFailure (work: unit -> Task) : Task<exn option> =
        task {
            try
                do! work ()
                return None
            with ex ->
                return Some ex
        }

    let observeOwnedWork (work: unit -> Task) : Task =
        task {
            let! failure = captureOwnedWorkFailure work
            failure |> Option.iter recordOwnedWorkFailure
            finishOwnedWork ()
        }
        :> Task

    let startOwnedWork (work: unit -> Task) : Task =
        let admitted =
            lock ownedWorkGate (fun () ->
                if not acceptingOwnedWork then
                    false
                else
                    ownedWorkCount <- ownedWorkCount + 1
                    true)

        if admitted then
            observeOwnedWork work
        else
            Task.FromResult(()) :> Task

    let ownedWorkDrainTask () : Task =
        match ownedWorkDrainWaiter with
        | Some waiter -> waiter.Task :> Task
        | None ->
            let waiter =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            ownedWorkDrainWaiter <- Some waiter
            waiter.Task :> Task

    let stopOwnedWorkAndDrain () : Task =
        let waiting =
            lock ownedWorkGate (fun () ->
                acceptingOwnedWork <- false

                if ownedWorkCount = 0 then
                    Task.FromResult(()) :> Task
                else
                    ownedWorkDrainTask ())

        task {
            do! waiting

            match lock ownedWorkGate (fun () -> ownedWorkFailure) with
            | Some failure -> return raise failure
            | None -> return ()
        }
        :> Task

    // DSL-MUTABLE: resource — first prompts deferred until the review barrier
    // has durably opened (GLORY-040: barrier before assignment).
    let deferredFirstPrompts =
        Dictionary<
            string,
            {| ChildId: SessionId
               IdentitySeed: PromptAuthority.IdentitySeed
               Prompt: string |}
         >()

    let directoryOf = defaultArg directoryFor (fun _ -> None)
    let childCreated = defaultArg onChildCreated (fun _ _ _ -> ())
    let childCreatedDir = defaultArg onChildCreatedDir (fun _ _ _ -> ())
    let runStarted = defaultArg onRunStarted (fun _ _ _ -> ())

    let parentWorkRecordOf =
        defaultArg parentWorkRecordFor (fun _ -> Task.FromResult None)

    let childWorkRecordOf =
        defaultArg childWorkRecordFor (fun _ -> Task.FromResult None)

    let childWorkRecordOfRun = childWorkRecordForRun

    let handoffPort = handoff

    let xTraceHead (sessionId: SessionId) : XTraceCursor =
        journal
        |> Option.bind (fun durable ->
            AgentJournal.snapshot durable
            |> fun snapshot -> AgentProjection.tryFind sessionId snapshot.AgentProjections
            |> Option.bind (fun session -> session.XTrace))
        |> Option.defaultValue XTraceProjection.empty
        |> XTraceProjection.headCursor

    let workRecordForOutcome (run: PendingHostRun) (outcome: TerminalOutcome) =
        HostForkRunLifecycle.workRecordForOutcome childWorkRecordOfRun xTraceHead run outcome

    let cancelSignals = defaultArg cancelSignals (fun _ -> ())

    let ptyPortInstance = defaultArg ptyPort (PtyBackend.createPort ())
    let parentKey = SessionId.value parentId
    let handleOwnership = defaultArg ownership HandleOwnership.DurableParentHandle

    let sendChildPrompt =
        HostForkRunLifecycle.childPromptSender sessions parentId journal directoryOf

    let sendBusyNudge = HostForkBusyNudge.sender sessions parentId journal directoryOf

    let parentAbortToken = Pty.registerParentAbort parentKey (fun () -> this.Cancel())

    let notifyPtyObserver observer item =
        try
            observer item
        with _ ->
            ()

    let notifyPtyObservers item =
        let observers = lock gate (fun () -> ptyCompletionObservers |> Seq.toList)

        for observer in observers do
            notifyPtyObserver observer item

    let mapExitEvent (event: PtyExitEvent) : PtyJoinItem =
        match event with
        | PtyExitEvent.Exited(id, outcome) ->
            PtyExited
                { PtyId = id.Value
                  Outcome = outcome
                  Closed = true }
        | PtyExitEvent.Failed(id, code, msg) ->
            PtyFailed
                { PtyId = id.Value
                  Outcome = msg
                  Closed = true
                  Code = code
                  Message = msg }
        | PtyExitEvent.Aborted(id, code, msg) ->
            PtyAborted
                { PtyId = id.Value
                  Outcome = msg
                  Closed = true
                  Code = code
                  Message = msg }

    do
        ptyPortInstance.AddExitListener(fun exitEvent ->
            let item = mapExitEvent exitEvent
            let id = PtyJoinItem.ptyId item
            let owned = lock gate (fun () -> ptyRuns.Contains id)

            if owned then
                // A PtyPort can be shared by multiple runtimes. Its sender fan-out
                // must not turn another runtime's exit into this runtime's join.
                runtime.PublishPtyCompletion item
                this.UntrackPtyRun id
                notifyPtyObservers item)
    // Cross-process recovery is not wired into ordinary HostForkRuntime lifecycle.
    // HostForkRestart remains a detached algorithm library for explicit resume flows.

    member internal _.Runtime = runtime
    member internal _.Children = children
    member internal _.DormantChildren = dormantChildren
    member internal _.PendingRuns = pendingRuns
    member internal _.PtyRuns = ptyRuns

    /// Fission admission snapshot: existing external work belongs to the logical
    /// owner and becomes broadcast completion sources. Snapshot identities only;
    /// no completion is consumed here.
    member _.SnapshotOutstandingAgentRuns() =
        lock gate (fun () ->
            pendingRuns.Values
            |> Seq.filter (fun run -> not run.Finished)
            |> Seq.map (fun run -> run.AgentId, run.ChildId)
            |> Seq.toList)

    member _.SnapshotOutstandingPtyRuns() =
        lock gate (fun () -> ptyRuns |> Seq.toList)

    member _.SubscribePtyCompletion(listener: PtyJoinItem -> unit) : IDisposable =
        lock gate (fun () -> ptyCompletionObservers.Add listener)

        { new IDisposable with
            member _.Dispose() =
                lock gate (fun () -> ptyCompletionObservers.Remove listener |> ignore) }

    member internal _.HandleOwnership = handleOwnership
    member internal _.DeferredFirstPrompts = deferredFirstPrompts
    member internal _.Clock = clockPort
    /// Wall-clock read for Session extension modules (avoids raw DateTimeOffset stamps).
    member internal _.Now() = clockPort.UtcNow()

    /// GLORY-045: re-enlist a still-ungraduated historical child into this
    /// runtime before Fork, so Fork's existing-child path reuses the SAME Host
    /// session (X/Y context preserved) instead of creating a second one.
    member internal _.AdoptChild(agentId: string, childId: SessionId) : unit =
        lock gate (fun () ->
            children.[agentId] <- childId
            processOwnedAgents.Add agentId |> ignore)

    /// GLORY-040: deliver a first prompt that was deferred until its review
    /// barrier had durably opened. Idempotent per agent id: a second call with
    /// nothing pending is a no-op success.
    member this.SendDeferredFirstPrompt(agentId: string) : Task<Result<unit, string>> =
        let pendingRunForAgent () =
            lock gate (fun () ->
                match pendingRuns.TryGetValue agentId with
                | true, run -> Some run
                | false, _ -> None)

        let deliverDeferredPrompt
            (pending:
                {| ChildId: SessionId
                   IdentitySeed: PromptAuthority.IdentitySeed
                   Prompt: string |})
            =
            task {
                let! sent =
                    HostForkAgentOwner.sendFirstPromptObserved
                        this.Sessions
                        this.Journal
                        pending.ChildId
                        pending.IdentitySeed
                        (this.DirectoryOf agentId)
                        pending.Prompt
                        (fun _ -> ())

                return
                    match sent with
                    | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Accepted _ ->
                        lock gate (fun () -> deferredFirstPrompts.Remove agentId |> ignore)
                        Ok()
                    | HostForkRunLifecycle.AgentOwnerDispatchOutcome.AcceptanceUncertain _ ->
                        // Never resend an outcome-unknown first prompt. Durable
                        // PromptAuthority recovery owns the pending claim now.
                        lock gate (fun () -> deferredFirstPrompts.Remove agentId |> ignore)
                        Ok()
                    | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Rejected err -> Error err
            }

        task {
            let pendingOpt =
                lock gate (fun () ->
                    match deferredFirstPrompts.TryGetValue agentId with
                    | true, pending -> Some pending
                    | false, _ -> None)

            match pendingOpt with
            | None -> return Ok()
            | Some pending -> return! deliverDeferredPrompt pending
        }

    /// Drop a deferSend preamble so process-review assignment can go out as
    /// AgentOwnerRoot instead of a busy nudge against a session with no profile.
    member _.DiscardDeferredFirstPrompt(agentId: string) : unit =
        lock gate (fun () -> deferredFirstPrompts.Remove agentId |> ignore)

    member internal _.Gate = gate
    member internal _.TerminalByName = terminalByName
    member internal _.Sessions = sessions
    member internal _.Journal = journal
    member internal _.SessionSnapshot = sessionSnapshot
    member internal _.ParentId = parentId
    member internal _.ParentKey = parentKey

    /// EXEC-017/018: one join waiter owns the runtime wake channels at a time.
    /// Join workflow (HostForkJoin) acquires/releases through these, so the
    /// latch stays with the state it guards.
    member internal _.TryAcquireJoin() : bool =
        lock gate (fun () ->
            if joinInFlight then
                false
            else
                joinInFlight <- true
                true)

    member internal _.ReleaseJoin() =
        lock gate (fun () -> joinInFlight <- false)

    member internal _.PtyPort = ptyPortInstance
    member internal _.DirectoryOf = directoryOf
    member internal _.RunStarted = runStarted
    member internal _.ChildCreated = childCreated
    member internal _.ChildCreatedDir = childCreatedDir
    member internal _.ParentWorkRecordOf = parentWorkRecordOf
    member internal _.ChildWorkRecordOf = childWorkRecordOf
    member internal _.ChildWorkRecordOfRun = childWorkRecordOfRun
    member internal _.XTraceHead = xTraceHead
    member internal _.HandoffPort = handoffPort

    member internal _.PrepareHandoff(route: DelegationHandoffRoute) : Task<Result<PreparedDelegationHandoff, string>> =
        match handoffPort with
        | Some port ->
            task {
                let! prepared = port.Prepare parentId route
                return Ok prepared
            }
        | None -> Task.FromResult(Error "reusable delegation handoff capability is unavailable")

    member internal _.TrackOwnedWork(work: unit -> Task) = startOwnedWork work |> ignore
    member internal _.SendChildPrompt = sendChildPrompt
    member internal _.SendBusyNudge = sendBusyNudge
    member internal _.ParentAbortToken = parentAbortToken

    member private _.DetachPendingObservers() =
        let subscriptions =
            lock gate (fun () ->
                let values =
                    pendingRuns.Values
                    |> Seq.choose (fun run ->
                        run.Finished <- true
                        run.Subscription)
                    |> Seq.toList

                pendingRuns.Clear()
                children.Clear()
                dormantChildren.Clear()
                deferredFirstPrompts.Clear()
                ptyRuns.Clear()
                terminalByName.Clear()
                values)

        subscriptions |> List.iter (fun subscription -> subscription.Dispose())

    /// EXEC-009: retired OR abandoned ids must never re-fork under the same handle.
    member _.IsRetiredHandle(agentId: string) =
        journal
        |> Option.map (fun durable ->
            let projection = AgentJournal.handleProjection durable parentId
            let handle = HandleController.agentHandle agentId

            HandleProjection.isRetired handle projection
            || HandleProjection.isAbandoned handle projection)

    member this.Complete(run: PendingHostRun, outcome: TerminalOutcome) =
        // Terminal delivery is a synchronous Host callback. The runtime owns the
        // async tail so shutdown can drain it before releasing the Journal.
        startOwnedWork (fun () ->
            task {
                match run.Role, drainChildPtys with
                | Role.DevOps, Some drain -> do! drain run.ChildId
                | _ -> ()

                let! workRecord = workRecordForOutcome run outcome

                do!
                    HostForkRunLifecycle.complete
                        gate
                        pendingRuns
                        journal
                        parentId
                        sessions
                        handoffPort
                        run
                        outcome
                        workRecord
            }
            :> Task)
        |> ignore

    member this.InstallRun
        (
            agentId: string,
            childId: SessionId,
            role: Role,
            authorityRoot: AuthorityRootUserMessageId,
            ?preparedHandoff: PreparedDelegationHandoff
        ) =
        lock gate (fun () -> processOwnedAgents.Add agentId |> ignore)

        let run =
            HostForkRunLifecycle.installRun
                gate
                pendingRuns
                journal
                parentId
                sessions
                childWorkRecordOfRun
                xTraceHead
                (fun work -> startOwnedWork work |> ignore)
                handoffPort
                preparedHandoff
                agentId
                childId
                role
                authorityRoot

        runtime.BindChildSession(agentId, childId)
        runStarted childId role (directoryOf agentId)
        run

    member this.FailRun(run: PendingHostRun, error: string) : Task =
        startOwnedWork (fun () ->
            HostForkRunLifecycle.failRun gate pendingRuns journal parentId sessions handoffPort run error)

    member this.MarkReady(run: PendingHostRun) =
        // markReady is intentionally a no-op; do not perform an unnecessary WorkRecord read.
        HostForkRunLifecycle.markReady gate pendingRuns journal parentId sessions run None

    member private _.WorkRecordFromCompletion(completion: RunCompletion) : Result<string, string> =
        match completion.Outcome with
        | AgentCompleted payload when not (String.IsNullOrWhiteSpace payload.WorkRecord) -> Ok payload.WorkRecord
        | AgentCompleted _ -> Error "reusable fork completed without bounded delta WorkRecord"
        | AgentFailed payload -> Error payload.Message
        | AgentAbandoned(_, reason) -> Error reason

    member internal this.AwaitCurrentWorkRecord(agentId: string) : Task<Result<string, string>> =
        taskResult {
            let! completion = this.AwaitChild agentId
            return! this.WorkRecordFromCompletion completion
        }

    member this.CancelAndDrain() : Task =
        lock cancelGate (fun () ->
            match teardownTask with
            | Some drain -> drain
            | None ->
                let drain =
                    task {
                        // Close terminal/failure callback admission first and let
                        // callbacks that already observed a terminal settle their
                        // durable completion before parent-cancel claims leftovers.
                        do! stopOwnedWorkAndDrain ()

                        do!
                            HostForkChildDispatch.cancelParent
                                cancelSignals
                                // GREEN-4: no second recovery ownership; cancel does not start restore.
                                (fun () -> Task.FromResult(()))
                                runtime
                                ptyPortInstance
                                parentKey
                                parentAbortToken
                                gate
                                pendingRuns
                                children
                                sessions
                                journal
                                (journal
                                 |> Option.map (fun durable -> AgentJournal.handleProjection durable parentId))
                                parentId
                                (fun run -> HostForkRunLifecycle.settleParentCancelled gate pendingRuns run)
                                (clockPort.UtcNow())
                    }
                    :> Task

                teardownTask <- Some drain
                drain)

    /// managed-session-lifecycle-018: plugin/process lifetime ending is not a logical
    /// parent cancellation. Stop this process's observers and local runtime
    /// resources without writing HandleAbandoned and without aborting live Host
    /// child sessions. Durable Active handles remain the restart authority.
    member this.DetachAndDrain() : Task =
        lock cancelGate (fun () ->
            match teardownTask with
            | Some drain -> drain
            | None ->
                let drain =
                    task {
                        do! stopOwnedWorkAndDrain ()

                        // Cancel only process-local ChildRun/mailbox waiters. This
                        // does not call the Host AbortSession port and does not
                        // write any durable handle terminal.
                        runtime.Cancel()
                        this.DetachPendingObservers()

                        // PTYs are process-owned OS resources and are not durable
                        // agent sessions; close them while preventing exit fan-out
                        // from re-entering the detached runtime.
                        do! ptyPortInstance.CloseAll()
                        Pty.unregisterParentAbort parentKey parentAbortToken
                    }
                    :> Task

                teardownTask <- Some drain
                drain)

    member this.Cancel() : unit = this.CancelAndDrain() |> ignore

    member _.List() = runtime.List()

    member _.TryFindAgent(agentId: string) =
        runtime.List() |> fst |> List.tryFind (fun a -> a.AgentId = agentId)

    member internal _.OwnsAgent(agentId: string) =
        lock gate (fun () -> processOwnedAgents.Contains agentId)

    /// crash-reconciliation-018: explicit /continue may discover a physically surviving child.
    /// It stays dormant: addressable by a later explicit reuse, but excluded from
    /// this process's cancellation/teardown ownership until that reuse begins.
    member _.AdoptExisting(agentId: string, childId: SessionId, role: Role, agent: string) : unit =
        lock gate (fun () -> dormantChildren.[agentId] <- childId)
        runtime.Restore(agentId, role, agent)
        runtime.BindChildSession(agentId, childId)

    /// crash-reconciliation-020: the durable handle projection is the single
    /// source of truth for which children this parent still owns. A restarted
    /// process has empty process tables, so every reuse/await path must be able
    /// to resolve a child from it — the in-process maps are only a cache of what
    /// this process currently drives.
    member internal _.TryChildFromDurable(agentId: string) : (SessionId * Role * string) option =
        journal
        |> Option.bind (fun durable ->
            DurableChildLookup.byHandleId (AgentJournal.handleProjection durable this.ParentId) agentId)

    member private this.AdoptDurableChild(agentId: string) : (SessionId * bool) option =
        match this.TryChildFromDurable agentId with
        | None -> None
        | Some(childId, role, agent) ->
            this.AdoptExisting(agentId, childId, role, agent)
            Some(childId, true)

    /// Resolve a child for reuse: process-local registration first, then the
    /// durable handle, which is adopted on demand so placement/await see it.
    member internal this.ReusableChildOrAdopt(agentId: string) : (SessionId * bool) option =
        match this.TryReusableChild agentId with
        | Some found -> Some found
        | None -> this.AdoptDurableChild agentId

    /// Resolve a child through durable evidence when this process has not met it
    /// yet (restart). The handle is the existence evidence; the in-process maps
    /// are only a cache of what this process currently drives.
    member this.TryFindAgentOrAdopt(agentId: string) : (SessionId * Role * string) option =
        match this.TryReusableChild agentId, this.TryChildFromDurable agentId with
        | Some(childId, _), Some(_, role, agent) -> Some(childId, role, agent)
        | Some _, None -> None
        | None, None -> None
        | None, Some(childId, role, agent) ->
            this.AdoptExisting(agentId, childId, role, agent)
            Some(childId, role, agent)

    /// A parent-visible child exists when this process drives it or when the
    /// durable handle says so — the process tables alone would answer "no" after
    /// every restart.
    member this.HasChild(agentId: string) : bool =
        match this.TryReusableChild agentId, this.TryChildFromDurable agentId with
        | Some _, _ -> true
        | None, Some _ -> true
        | None, None -> false

    member private this.ChildNotReusableOutcome(agentId: string) : Result<RunCompletion, string> =
        match this.TryFindAgentOrAdopt agentId with
        | None -> Error(sprintf "Unknown agent id: %s" agentId)
        | Some _ -> Error(sprintf "No work in flight for %s" agentId)

    /// Await one child's completion. A restarted process has nothing in flight for
    /// a child it only knows from the journal: that answers explicitly instead of
    /// the bare "Unknown agent id" the process tables produced.
    member this.AwaitChild(agentId: string, ?timeoutMs: int) : Task<Result<RunCompletion, string>> =
        task {
            match this.TryReusableChild agentId with
            | Some _ -> return! this.Runtime.AwaitAgent(agentId, ?timeoutMs = timeoutMs)
            | None -> return this.ChildNotReusableOutcome agentId
        }

    member internal _.TryReusableChild(agentId: string) : (SessionId * bool) option =
        lock gate (fun () ->
            match children.TryGetValue agentId, dormantChildren.TryGetValue agentId with
            | (true, childId), _ -> Some(childId, false)
            | _, (true, childId) -> Some(childId, true)
            | _ -> None)

    member internal _.ActivateDormantChild(agentId: string, childId: SessionId, role: Role) : unit =
        lock gate (fun () ->
            dormantChildren.Remove agentId |> ignore
            children.[agentId] <- childId
            processOwnedAgents.Add agentId |> ignore)

        childCreated agentId role childId

    member internal this.ActivateDormantChildIfNeeded
        (wasDormant: bool, agentId: string, childId: SessionId, role: Role)
        =
        if wasDormant then
            this.ActivateDormantChild(agentId, childId, role)

    /// The Host child session a forked agent id drives.
    ///
    /// ORCH-006 needs it right after a fork, to record `ManagerJobCreated`. The map is
    /// the same one restart recovery repopulates from `HandleLinked.ChildSessionId`, so
    /// a resumed job reads the session the Host actually issued rather than one derived
    /// from the agent id.
    member this.TryChildSession(agentId: string) : SessionId option =
        let local =
            lock gate (fun () ->
                match children.TryGetValue agentId with
                | true, childId -> Some childId
                | false, _ -> None)

        match local with
        | Some childId -> Some childId
        | None -> this.TryChildFromDurable agentId |> Option.map (fun (childId, _, _) -> childId)

    member _.PendingRunCount = lock gate (fun () -> pendingRuns.Count)
    member _.PendingCompletionCount = runtime.PendingCompletionCount
    member _.IsCancelled = runtime.IsCancelled

    member this.TrackPtyRun(id: PtyId) =
        lock gate (fun () -> ptyRuns.Add id.Value |> ignore)

    member this.RegisterPtySnapshot (id: PtyId) (command: string) =
        runtime.RegisterPty
            { PtyId = id.Value
              AgentId = id.Value
              Command = command
              StartedAt = this.Now() }

    member this.UntrackPtyRun(id: string) =
        lock gate (fun () ->
            ptyRuns.Remove id |> ignore

            let stale =
                terminalByName
                |> Seq.filter (fun kv -> kv.Value = id)
                |> Seq.map (fun kv -> kv.Key)
                |> Seq.toList

            for name in stale do
                terminalByName.Remove name |> ignore)

        runtime.UnregisterPty id

    member this.OwnsPty(id: PtyId) =
        lock gate (fun () -> ptyRuns.Contains id.Value)

    member this.AwaitObservedWork() : Task<unit> =
        let waiting =
            lock ownedWorkGate (fun () ->
                if ownedWorkCount = 0 then
                    Task.FromResult(())
                else
                    ensureObservedWorkWaiter ())

        task {
            do! waiting

            match lock ownedWorkGate (fun () -> ownedWorkFailure) with
            | Some failure -> return raise failure
            | None -> return ()
        }

    member this.DrainOwnedWork() : Task<unit> =
        task {
            let! _ = stopOwnedWorkAndDrain ()
            return ()
        }

    member this.CloseOwnedPtys(?graceMs: int) : Task<unit> =
        task {
            let ids = this.SnapshotOutstandingPtyRuns() |> List.map PtyId.Create

            for id in ids do
                do! ptyPortInstance.ClosePty(id, ?graceMs = graceMs)
                this.UntrackPtyRun id.Value
        }

    member this.IsPtyCompletion(runId: string) =
        lock gate (fun () -> ptyRuns.Contains runId)

    member this.TryBindTerminalName(name: string, id: PtyId) : Result<unit, string> =
        if String.IsNullOrWhiteSpace name then
            Error "Terminal name is required"
        else
            PtyHostHelpers.bindTerminalName gate terminalByName name id

    member this.TryPtyByName(name: string) : PtyId option =
        if String.IsNullOrWhiteSpace name then
            None
        else
            PtyHostHelpers.ptyByName gate terminalByName ptyRuns ptyPortInstance.Known name

    member this.ForkPty(command: string, agent: ManagedAgent, ?cwd: string) : Task<Result<PtyId, string>> =
        taskResult {
            do!
                if String.IsNullOrWhiteSpace command then
                    Error "PTY command is required"
                else
                    Ok()

            let id = Pty.newId ()
            this.TrackPtyRun id
            this.RegisterPtySnapshot id command

            try
                ptyPortInstance.Fork(command, agent.Name, ptyId = id, ?cwd = cwd) |> ignore
                return id
            with ex ->
                this.UntrackPtyRun id.Value
                return! Error ex.Message
        }

    member this.TryPty(id: string) =
        if String.IsNullOrWhiteSpace id then
            None
        elif this.OwnsPty(PtyId.Create id) && ptyPortInstance.Known(PtyId.Create id) then
            Some(PtyId.Create id)
        else
            None

    member this.SendPty(id: PtyId, prompt: string, signal: PtySignal option) : Task<Result<PtyRead, string>> =
        taskResult {
            do!
                if not (this.OwnsPty id) then
                    Error(sprintf "Unknown PTY id: %s" id.Value)
                elif not (ptyPortInstance.Exists id) then
                    Error(sprintf "Unknown PTY id: %s" id.Value)
                else
                    Ok()

            match signal with
            | Some value ->
                do! ptyPortInstance.Send(id, PtyCommand.Signal value)
                return PtyHostHelpers.emptyRead id
            | None when String.IsNullOrEmpty prompt ->
                let! output, closed = ptyPortInstance.Read id
                return PtyHostHelpers.mapRead id (output, closed)
            | None ->
                do! ptyPortInstance.Send(id, PtyCommand.Write(Pty.bytes (PtyHostHelpers.ensureLf prompt)))
                return PtyHostHelpers.emptyRead id
        }

    member this.TryTerminalNameByPtyId(ptyId: string) : string option =
        PtyHostHelpers.tryTerminalNameByPtyId gate terminalByName ptyId

    member this.PtyCapability: DelegationPtyCapability =
        { TryPtyByName = this.TryPtyByName
          ForkPty = fun (cmd, agent, cwd) -> this.ForkPty(cmd, agent, ?cwd = cwd)
          TryBindTerminalName = fun (name, id) -> this.TryBindTerminalName(name, id)
          UntrackPtyRun = this.UntrackPtyRun
          SendPty = fun (id, prompt, sigOpt) -> this.SendPty(id, prompt, sigOpt)
          OwnsPty = this.OwnsPty
          TryPty = this.TryPty
          TryTerminalNameByPtyId = this.TryTerminalNameByPtyId }


    member _.EnqueueBufferedJoinItems(items: JoinItem seq) =
        lock gate (fun () ->
            for item in items do
                bufferedJoinItems.Enqueue item)

    member _.DrainBufferedJoinItems(maxCount: int) : JoinItem list =
        lock gate (fun () ->
            let count = min maxCount bufferedJoinItems.Count
            [ for _ in 1..count -> bufferedJoinItems.Dequeue() ])

    member _.HasBufferedJoinItems = lock gate (fun () -> bufferedJoinItems.Count > 0)
