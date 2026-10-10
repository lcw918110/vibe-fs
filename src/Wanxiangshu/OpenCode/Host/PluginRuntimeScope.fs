namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Context.Companion.Blogger.OpenCode
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Explicit lifetime root for one plugin instance. Collections here are either
/// physical resources, display caches, or bounded per-call deduplication.
type PluginRuntimeScope(journal: AgentJournal option, isModelLeaseExternallyOwned: SessionId -> bool) =
    let blogger = PluginBloggerScope()
    let sessions = PluginSessionScope(journal, isModelLeaseExternallyOwned)
    let recovery = PluginRecoveryScope(journal)
    // DSL-MUTABLE: resource — session cleanup hook list registered by composition
    let mutable sessionCleanups: (string -> unit) list = []
    // DSL-MUTABLE: resource — scope dispose hook list registered by composition
    let mutable scopeDisposers: (unit -> unit) list = []

    let toolRuntimeGate = obj ()
    // DSL-MUTABLE: resource — session tool runtime owner handle
    let mutable toolRuntime: ISessionRuntimeOwner option = None
    // DSL-MUTABLE: resource
    let mutable subscription: IDisposable option = None
    // DSL-MUTABLE: resource — shared terminal bus key
    let mutable sharedTerminalKey: string option = None
    // DSL-MUTABLE: resource — shared terminal bus port handle
    let mutable sharedTerminalPort: Events.HostEventPort option = None
    let disposeGate = obj ()
    let ownedWorkGate = obj ()
    // DSL-MUTABLE: resource — scope dispose latch
    let mutable disposed = false
    // DSL-MUTABLE: resource — one shutdown Task owns the Journal/store release sequence.
    let mutable disposeTask: Task option = None
    // DSL-MUTABLE: resource — scheduler shutdown hook owned by this scope.
    let mutable reconcileShutdown: (unit -> Task) option = None
    // DSL-MUTABLE: resource — Host-work admission latch shared by foreground hooks and detached work.
    let mutable acceptingOwnedWork = true
    // DSL-MUTABLE: resource — in-flight Host-work count.
    let mutable ownedWorkCount = 0
    // DSL-MUTABLE: single-flight — shared waiter for Host-work drain.
    let mutable ownedWorkDrainWaiter: TaskCompletionSource<unit> option = None
    // DSL-MUTABLE: resource — first detached Host-work failure for shutdown propagation.
    let mutable backgroundFailure: exn option = None
    let durabilityActivationGate = obj ()
    // DSL-MUTABLE: resource — one-shot callbacks that may force deferred durable Current.
    let mutable durabilityActivators: (unit -> unit) list = []
    // DSL-MUTABLE: resource — first real durable admission owns activation exactly once.
    let mutable durabilityActivated = false
    // DSL-MUTABLE: resource — crash-reconciliation-018: one restart status guidance
    // is owed to the next real user instruction after this process's load-phase
    // normalization; consumed once, never persisted (the delivered bytes are
    // frozen by the pair-guideline anchor).
    // DSL-MUTABLE: single-flight — one-shot restart guidance latch
    let mutable restartGuidancePending = false

    /// HOST-006: the first compaction setting the config hook could not establish.
    ///
    /// Recorded rather than thrown, because HOST-006's verdict needs both halves — the
    /// settings and the first turn's observation. Throwing at config time would report
    /// the symptom before the probe could say whether anything actually compacted.
    // DSL-MUTABLE: resource — HOST-006 compaction setting gap observation
    let mutable compactionSettingGap: Wanxiangshu.Host.CompactionSetting option = None

    /// HOST-006 startup probe latch, with its own gate.
    ///
    /// Not sharing `toolRuntimeGate`: two unrelated invariants behind one lock read as
    /// if they were related, and the next person to touch either has to prove they are
    /// not.
    let startupProbeGate = obj ()
    // DSL-MUTABLE: single-flight — HOST-006 startup probe one-shot latch
    let mutable startupProbeDone = false

    /// degeneration-guard-008: process-local armed anomaly lives inside the sensor.
    /// Optional until HostSignalBootstrap wires abort + ownership.
    // DSL-MUTABLE: resource — loop sensor attachment slot
    let mutable loopSensor: ILoopSensor option = None
    // DSL-MUTABLE: resource — message-visibility hub attachment slot
    let mutable messageVisibility: MessageVisibilityHub option = None
    // DSL-MUTABLE: resource — the ONE attachment lease registry shared by every kind
    let mutable attachmentRegistry: AttachmentLeaseRegistry option = None
    // DSL-MUTABLE: resource — Companion kind adapter over that registry
    let mutable companionLeases: CompanionLeaseRuntime option = None
    // DSL-MUTABLE: resource — sync-delegate runtime attachment slot
    let mutable syncDelegateRuntime: SyncDelegateRuntime option = None
    // DSL-MUTABLE: resource — event-driven managed chat recovery owner
    let mutable chatRecoveryRuntime: SessionRecoveryHost option = None

    let disposeRuntimeOwner (owner: ISessionRuntimeOwner option) =
        task {
            match owner with
            | Some active -> do! active.DisposeAsync()
            | None -> ()
        }

    let captureTaskFailure (work: Task) : Task<exn option> =
        task {
            try
                do! work
                return None
            with ex ->
                return Some ex
        }

    let captureSyncFailure (work: unit -> unit) : exn option =
        try
            work ()
            None
        with ex ->
            Some ex

    member _.Journal = journal

    member _.AttachDurabilityActivation(activate: unit -> unit) =
        let runNow =
            lock durabilityActivationGate (fun () ->
                if durabilityActivated then
                    true
                else
                    durabilityActivators <- activate :: durabilityActivators
                    false)

        if runNow then
            activate ()

    member _.ActivateDurability() =
        let activators =
            lock durabilityActivationGate (fun () ->
                if durabilityActivated then
                    []
                else
                    durabilityActivated <- true
                    let pending = List.rev durabilityActivators
                    durabilityActivators <- []
                    pending)

        for activate in activators do
            activate ()

    /// crash-reconciliation-018: load-phase normalization just happened; the next
    /// real user instruction in this process must carry one restart status guidance.
    member _.MarkRestartGuidancePending() = restartGuidancePending <- true

    member _.RestartGuidancePending = restartGuidancePending

    member _.MarkRestartGuidanceDelivered() = restartGuidancePending <- false

    member _.AttachSessionCleanup(cleanup: string -> unit) =
        sessionCleanups <- cleanup :: sessionCleanups

    member _.AttachScopeDispose(dispose: unit -> unit) =
        scopeDisposers <- dispose :: scopeDisposers

    /// Composition-of-owners: Blogger parking/flight/drain state lives in its own scope.
    member _.Blogger = blogger
    member _.BloggerRuntimeHost: IBloggerRuntimeHost = blogger :> IBloggerRuntimeHost

    /// Composition-of-owners: per-instance session registries live in their own scope.
    member _.Sessions = sessions

    /// Composition-of-owners: family recovery + attempt planning live in their own scope.
    member _.Recovery = recovery

    /// The single lifecycle owner's registry: one instance per plugin, attached by
    /// Host composition and shared by every AttachmentKind adapter.
    member _.AttachAttachmentRegistry(registry: AttachmentLeaseRegistry) = attachmentRegistry <- Some registry

    member _.AttachmentRegistry =
        match attachmentRegistry with
        | Some registry -> registry
        | None -> invalidOp "AttachmentLeaseRegistry has not been attached"

    member _.AttachCompanionLeases(runtime: CompanionLeaseRuntime) = companionLeases <- Some runtime

    member _.CompanionLeases =
        match companionLeases with
        | Some runtime -> runtime
        | None -> invalidOp "CompanionLeaseRuntime has not been attached"

    member _.AttachSyncDelegateRuntime(runtime: SyncDelegateRuntime) = syncDelegateRuntime <- Some runtime

    member _.AttachChatRecoveryRuntime(runtime: SessionRecoveryHost) = chatRecoveryRuntime <- Some runtime

    member _.SignalChatRecovery(event: ChatExecutionRecoveryLifecycleEvent) : Task =
        match chatRecoveryRuntime with
        | Some runtime -> runtime.Signal event
        | None -> Task.FromResult(()) :> Task

    member _.SignalChatRecoverySession
        (sessionId: SessionId)
        (eventOf: ChatExecutionKey -> ChatExecutionRecoveryLifecycleEvent)
        : Task =
        match chatRecoveryRuntime with
        | Some runtime -> runtime.SignalSession(sessionId, eventOf)
        | None -> Task.FromResult(()) :> Task

    member _.DrainChatRecovery(sessionId: SessionId) : Task =
        match chatRecoveryRuntime with
        | Some runtime -> runtime.Drain sessionId
        | None -> Task.FromResult(()) :> Task

    member _.SyncDelegateRuntime = syncDelegateRuntime

    member _.AttachLoopSensor(sensor: ILoopSensor) = loopSensor <- Some sensor

    member _.AttachMessageVisibility(hub: MessageVisibilityHub) = messageVisibility <- Some hub

    /// None until the signal stack wires the hub; the catch-up re-read then
    /// falls back to its bounded immediate form.
    member _.MessageVisibility = messageVisibility

    member _.LoopSensor: ILoopSensor =
        match loopSensor with
        | Some sensor -> sensor
        | None -> invalidOp "LoopSensor must be attached by Host composition before use"

    /// Current-process join admission only; no cross-process tool recovery.
    member this.RequireCurrentProcessJoin(root: SessionId) : Task<FamilyRecovery> =
        recovery.RequireCurrentProcessJoin root

    member _.PublishManualChatIntervention(request: ManualInterventionRequest) =
        recovery.PublishManualChatIntervention request

    member _.ManualChatInterventions() : ManualInterventionRequest[] = recovery.ManualChatInterventions()

    member _.RevokeManualIntervention(key: ChatExecutionKey) : unit = recovery.RevokeManualIntervention key

    member this.RecordPendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (plan: PendingAttemptPlan)
        =
        recovery.RecordPendingAttemptPlan sessionId physicalUserMessageId plan

    member this.TryBindAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        =
        recovery.TryBindAttemptPlan sessionId physicalUserMessageId providerRun

    member this.RecordAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) (plan: AttemptPlan) =
        recovery.RecordAttemptPlan sessionId providerRun plan

    member this.TryAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) =
        recovery.TryAttemptPlan sessionId providerRun

    member this.ConsumeAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) =
        recovery.ConsumeAttemptPlan sessionId providerRun

    /// HOST-006 prevention layer: the config hook's finding.
    ///
    /// Written once at config time, read once by the startup probe. Not a collection
    /// because there is one verdict per plugin instance — the settings are
    /// instance-global (`config/config.ts:607`), not per session.
    member _.RecordCompactionSettingGap(gap: Wanxiangshu.Host.CompactionSetting option) = compactionSettingGap <- gap

    member _.CompactionSettingGap = compactionSettingGap

    /// HOST-006 startup probe: has it already run.
    ///
    /// One probe per plugin instance, not per session. The claim it tests is about the
    /// Host build, and the first managed session's first turn is the cheapest place to
    /// observe it — running it again on every later session would keep asking a
    /// question already answered while risking a false refusal from a legitimate
    /// `/compact`.
    ///
    /// `TryClaimStartupProbe` returns true exactly once, so the caller cannot
    /// accidentally judge twice from concurrent reconcile passes.
    member _.TryClaimStartupProbe() : bool =
        lock startupProbeGate (fun () ->
            if startupProbeDone then
                false
            else
                startupProbeDone <- true
                true)

    /// Cheap read for the common case: after the probe has run, every later reconcile
    /// pass skips the judgement entirely rather than building a verdict and discarding
    /// it.
    member _.IsStartupProbeOpen = lock startupProbeGate (fun () -> not startupProbeDone)

    member _.AttachToolRuntime(owner: ISessionRuntimeOwner) =
        lock toolRuntimeGate (fun () -> toolRuntime <- Some owner)

    member _.TrackSubscription(value: IDisposable option) = subscription <- value

    member _.TrackReconcileShutdown(stopAndDrain: unit -> Task) = reconcileShutdown <- Some stopAndDrain

    member private _.AdmitOwnedWork() : bool =
        lock ownedWorkGate (fun () ->
            if not acceptingOwnedWork then
                false
            else
                ownedWorkCount <- ownedWorkCount + 1
                true)

    member private _.RecordBackgroundFailure(failure: exn) =
        lock ownedWorkGate (fun () -> backgroundFailure <- Option.orElse backgroundFailure (Some failure))

    member private _.FinishOwnedWork() =
        lock ownedWorkGate (fun () ->
            ownedWorkCount <- ownedWorkCount - 1

            if not acceptingOwnedWork && ownedWorkCount = 0 then
                ownedWorkDrainWaiter
                |> Option.iter (fun waiter -> AsyncSupport.trySetResult waiter () |> ignore))

    member private _.CaptureBackgroundFailure(start: unit -> Task) : Task<exn option> =
        task {
            try
                do! start ()
                return None
            with ex ->
                return Some ex
        }

    member private this.ObserveBackgroundWork(start: unit -> Task) : Task =
        task {
            let! failure = this.CaptureBackgroundFailure start
            failure |> Option.iter this.RecordBackgroundFailure
            this.FinishOwnedWork()
        }
        :> Task

    member this.RunBackground(start: unit -> Task) : unit =
        if this.AdmitOwnedWork() then
            this.ObserveBackgroundWork(start) |> ignore

    member private this.RunAdmittedOwnedWork(start: unit -> Task) : Task =
        task {
            try
                do! start ()
            finally
                this.FinishOwnedWork()
        }
        :> Task

    member this.RunOwnedWork(start: unit -> Task) : Task =
        if this.AdmitOwnedWork() then
            this.RunAdmittedOwnedWork start
        else
            Task.FromResult(()) :> Task

    member private _.OwnedWorkDrainTask() : Task =
        match ownedWorkDrainWaiter with
        | Some waiter -> waiter.Task :> Task
        | None ->
            let waiter =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            ownedWorkDrainWaiter <- Some waiter
            waiter.Task :> Task

    member private this.StopOwnedWorkAndDrain() : Task<exn option> =
        let waiting =
            lock ownedWorkGate (fun () ->
                acceptingOwnedWork <- false

                if ownedWorkCount = 0 then
                    Task.FromResult(()) :> Task
                else
                    this.OwnedWorkDrainTask())

        task {
            do! waiting
            return lock ownedWorkGate (fun () -> backgroundFailure)
        }

    member _.AttachSharedTerminal(key: string option, port: Events.HostEventPort option) =
        sharedTerminalKey <- key
        sharedTerminalPort <- port

    member _.DisposeExecutorRuntime(sessionId: string) : Task =
        let owner = lock toolRuntimeGate (fun () -> toolRuntime)

        match owner with
        | Some active -> active.DisposeExecutorRuntime sessionId
        | None -> Task.FromResult(()) :> Task

    /// EXEC-016: live PTY probe for DevOps join guard.
    member _.HasLivePty(sessionId: string) : bool =
        lock toolRuntimeGate (fun () ->
            match toolRuntime with
            | Some owner -> owner.HasLivePty sessionId
            | None -> false)

    member _.CancelSessionChildren(sessionId: string) : Task =
        let owner = lock toolRuntimeGate (fun () -> toolRuntime)

        match owner with
        | Some active -> active.CancelSessionChildren sessionId
        | None -> Task.FromResult(()) :> Task

    member this.DisposeSession(sessionId: string) : Task =
        task {
            // managed-session-lifecycle-027: per-session teardown is
            // best-effort-complete but never error-silent — the first real
            // failure is remembered, every independent cleanup group below
            // still runs, and the first failure is rethrown last.
            // DSL-MUTABLE: algorithm-scratch — first per-session teardown failure accumulator.
            let mutable firstFailure: exn option = None

            let remember (failure: exn) =
                firstFailure <- Option.orElse firstFailure (Some failure)

            let attempt (work: unit -> Task) =
                task {
                    try
                        do! work ()
                    with failure ->
                        remember failure
                }

            let attemptSync (work: unit -> unit) =
                try
                    work ()
                with failure ->
                    remember failure

            let owner = lock toolRuntimeGate (fun () -> toolRuntime)

            match owner with
            | Some active -> do! attempt (fun () -> active.DisposeSession sessionId)
            | None -> ()

            // C6 item 27: waiters are keyed by BloggerSessionId. When the MAIN is
            // deleted, cancel the linked Blogger's parked waiter + request slots too.
            let linkedBloggerKeys =
                try
                    sessions.LinkedBloggerKeys sessionId
                with failure ->
                    remember failure
                    []

            // managed-chat-execution-010: a session delete may not be declared
            // drained until this scope's unfinished executions carry a durable
            // terminal and their exact capacity is back, so ClearSession is awaited.
            do! attempt (fun () -> sessions.ClearSession sessionId)

            attemptSync (fun () -> recovery.ClearSession sessionId)

            for cleanup in List.rev sessionCleanups do
                attemptSync (fun () -> cleanup sessionId)

            attemptSync (fun () -> this.LoopSensor.DropSession(SessionId.create sessionId))

            // Always cancel the deleted id; also cancel linked Blogger keys.
            let cancelKeys = (sessionId :: linkedBloggerKeys) |> List.distinct

            for key in cancelKeys do
                attemptSync (fun () -> (blogger :> IBloggerRuntimeHost).CancelParked key)
                attemptSync (fun () -> blogger.CancelEpisodesForSession key)

                attemptSync (fun () ->
                    lock SharedState.BloggerFlightGate (fun () -> SharedState.BloggerFlights.Remove key |> ignore))

                attemptSync (fun () -> recovery.ClearAttemptPlansFor key)

            this.RethrowFirstFailure firstFailure
        }
        :> Task

    member _.DropSessionIdentity(sessionId: string) = sessions.DropSessionIdentity sessionId

    member private _.TakeReconcileDrain() : Task =
        let shutdown = reconcileShutdown
        reconcileShutdown <- None

        match shutdown with
        | Some stopAndDrain -> stopAndDrain ()
        | None -> Task.FromResult(()) :> Task

    member private _.TakeRuntimeOwner() : ISessionRuntimeOwner option =
        lock toolRuntimeGate (fun () ->
            let owner = toolRuntime
            toolRuntime <- None
            owner)

    member private _.RethrowFirstFailure(firstFailure: exn option) =
        match firstFailure with
        | Some failure -> raise failure
        | None -> ()

    member private this.StartDisposeAsync() : Task =
        disposed <- true

        task {
            // Teardown is best-effort-complete but never error-silent: remember
            // the first real failure, continue safe independent cleanup, rethrow last.
            // DSL-MUTABLE: algorithm-scratch — first teardown failure accumulator.
            let mutable firstFailure: exn option = None

            let remember failure =
                firstFailure <- Option.orElse firstFailure failure

            // Close external admission first. Close both internal admissions before
            // awaiting either drain, so no durable work can enter during shutdown.
            remember (captureSyncFailure (fun () -> subscription |> Option.iter (fun active -> active.Dispose())))
            subscription <- None

            blogger.BeginShutdown()

            let reconcileDrain = this.TakeReconcileDrain()
            let ownedWorkDrain = this.StopOwnedWorkAndDrain()

            let! reconcileFailure = captureTaskFailure reconcileDrain
            remember reconcileFailure

            let! backgroundFailure = ownedWorkDrain
            remember backgroundFailure

            remember (captureSyncFailure (fun () -> blogger.Dispose()))

            let! runtimeFailure = captureTaskFailure (disposeRuntimeOwner (this.TakeRuntimeOwner()))
            remember runtimeFailure

            remember (captureSyncFailure (fun () -> sessions.Dispose()))
            remember (captureSyncFailure (fun () -> syncDelegateRuntime |> Option.iter (fun sd -> sd.Dispose())))
            syncDelegateRuntime <- None

            for dispose in List.rev scopeDisposers do
                remember (captureSyncFailure dispose)

            let! repairDrainFailure = captureTaskFailure (blogger.DrainRepairEpisodes())
            remember repairDrainFailure

            // managed-session-lifecycle-018: the shared durable substrate is the last owner
            // released, after scheduler/background/process-local detach drains.
            let! journalFailure = captureTaskFailure (SharedAgentJournal.releaseAsync journal)
            remember journalFailure
            remember (captureSyncFailure (fun () -> SharedTerminalBus.release sharedTerminalKey sharedTerminalPort))
            sharedTerminalKey <- None
            sharedTerminalPort <- None

            this.RethrowFirstFailure firstFailure
        }
        :> Task

    member this.DisposeAsync() : Task =
        lock disposeGate (fun () ->
            match disposeTask with
            | Some running -> running
            | None ->
                let running = this.StartDisposeAsync()
                disposeTask <- Some running
                running)

    member this.Dispose() = this.DisposeAsync() |> ignore

    interface IDisposable with
        member this.Dispose() = this.Dispose()
