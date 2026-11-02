namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Execution.Routing
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Host.Ports

module PluginRecoveryWiring =

    let private hostErrorReason (error: HostError) =
        match error with
        | HostError.Unsupported capability -> sprintf "Host capability %A is unsupported" capability
        | HostError.NotFound what -> what
        | HostError.Refused reason -> reason
        | HostError.Transport reason -> reason
        | HostError.Unknown reason -> reason

    let private inspectRecoveryWork gitCommonDir workId =
        try
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir workId
        with error ->
            Wanxiangshu.Foundation.Diagnostic.emit
                "lifecycle-work-recovery-refused"
                [ "work_id", workId; "reason", error.Message ]

            None

    let private chooseRecoveredWork
        (workId: string, result: Result<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option, string>)
        =
        match result with
        | Ok(Some work) -> Some(SessionId.create workId, work)
        | Ok None -> None
        | Error reason ->
            Wanxiangshu.Foundation.Diagnostic.emit
                "lifecycle-work-recovery-refused"
                [ "work_id", workId; "reason", reason ]

            None

    let private queryInspectedWorks stateRoot workIds =
        try
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspectedMany stateRoot workIds
            |> Array.choose chooseRecoveredWork
        with error ->
            Wanxiangshu.Foundation.Diagnostic.emit
                "lifecycle-work-recovery-refused"
                [ "workspace", stateRoot; "reason", error.Message ]

            [||]

    let private inspectRecoveryWorks (journal: AgentJournal) (workspace: string option) =
        task {
            let! stateRoot =
                match workspace with
                | Some ws -> RuntimePath.stateRoot ws
                | None -> Task.FromResult ""

            if String.IsNullOrWhiteSpace stateRoot then
                return [||]
            else
                let workIds =
                    (AgentJournal.snapshot journal).AgentProjections.Sessions
                    |> Seq.map (fun (KeyValue(sessionId, _)) -> SessionId.value sessionId)
                    |> Seq.toArray

                return queryInspectedWorks stateRoot workIds
        }

    let private firstUserPrompt (messages: SessionMessage list) : string option =
        messages
        |> List.tryFind (fun message -> message.Role = "user")
        |> Option.map (fun message -> Wanxiangshu.Interaction.Repair.CompletedTurnClassifier.partsText message.Parts)
        |> Option.filter (fun text -> not (String.IsNullOrEmpty text))

    let private observeBoundDelivery
        (gitCommonDir: string)
        (workId: string)
        (execSessionId: string)
        (snapshots: ISessionSnapshotPort)
        (retirementBlockers: string -> string array)
        : Task =
        task {
            let! observed = snapshots.GetMessages(SessionId.create execSessionId)

            ignore observed
            ignore retirementBlockers

            Wanxiangshu.Foundation.Diagnostic.emit
                "plan-delivery-recovery-retired"
                [ "work_id", workId
                  "exec_session_id", execSessionId
                  "reason", "review is the only lifecycle entry" ]
        }

    let private observeAvailableDelivery gitCommonDir workId execSessionId snapshots retirementBlockers : Task =
        match snapshots with
        | Some snapshot -> observeBoundDelivery gitCommonDir workId execSessionId snapshot retirementBlockers
        | None ->
            Wanxiangshu.Foundation.Diagnostic.emit
                "plan-delivery-recovery-no-snapshot"
                [ "work_id", workId; "exec_session_id", execSessionId ]

            Task.FromResult(()) :> Task

    let private bindDiscoveredDelivery
        gitCommonDir
        workId
        operationId
        execSessionId
        snapshots
        retirementBlockers
        : Task =
        ignore gitCommonDir
        ignore snapshots
        ignore retirementBlockers

        Wanxiangshu.Foundation.Diagnostic.emit
            "plan-delivery-recovery-retired"
            [ "work_id", workId
              "operation_id", operationId
              "exec_session_id", execSessionId
              "reason", "review is the only lifecycle entry" ]

        Task.FromResult(()) :> Task

    let private discoverUnboundDelivery
        (gitCommonDir: string)
        (workId: string)
        (operationId: string)
        (snapshots: ISessionSnapshotPort option)
        (discovery: ISessionPort)
        (retirementBlockers: string -> string array)
        : Task =
        task {
            let title = "wanxiangshu-delivery:" + operationId

            match! discovery.FindByTitle title with
            | Error error ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-discovery-failed"
                    [ "work_id", workId
                      "operation_id", operationId
                      "reason", hostErrorReason error ]
            | Ok [] ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-unbound"
                    [ "work_id", workId
                      "operation_id", operationId
                      "reason", "no exact Host session is currently observable" ]
            | Ok [ sessionId ] ->
                let execSessionId = SessionId.value sessionId
                do! bindDiscoveredDelivery gitCommonDir workId operationId execSessionId snapshots retirementBlockers
            | Ok sessions ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-ambiguous"
                    [ "work_id", workId
                      "operation_id", operationId
                      "matches", string sessions.Length ]
        }

    let private reconcilePendingWork
        gitCommonDir
        workId
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        snapshotPort
        discoveryPort
        retirementBlockers
        : Task =
        task {
            match work.PendingExecSessionId, work.PendingOperationId, discoveryPort with
            | Some execSessionId, _, _ ->
                do! observeAvailableDelivery gitCommonDir workId execSessionId snapshotPort retirementBlockers
            | None, Some operationId, Some discovery ->
                do! discoverUnboundDelivery gitCommonDir workId operationId snapshotPort discovery retirementBlockers
            | None, Some operationId, None ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-no-discovery"
                    [ "work_id", workId; "operation_id", operationId ]
            | None, None, _ ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-unbound"
                    [ "work_id", workId; "reason", "pending delivery has no operation identity" ]
        }

    let private reconcileDeliveryWork gitCommonDir workId snapshotPort discoveryPort retirementBlockers : Task =
        match inspectRecoveryWork gitCommonDir workId with
        | Some work when work.PendingDelivery ->
            reconcilePendingWork gitCommonDir workId work snapshotPort discoveryPort retirementBlockers
        | _ -> Task.FromResult(()) :> Task

    let private recoverDeliveryWork gitCommonDir workId snapshotPort discoveryPort retirementBlockers : Task =
        task {
            try
                do! reconcileDeliveryWork gitCommonDir workId snapshotPort discoveryPort retirementBlockers
            with error ->
                Wanxiangshu.Foundation.Diagnostic.emit
                    "plan-delivery-recovery-failed"
                    [ "work_id", workId; "reason", error.Message ]
        }

    /// A delivery session is bound durably before its first prompt is sent.
    /// After restart we never resend that prompt: OpenCode 1.18.29 accepts a
    /// caller message id but does not make repeated parts idempotent. Instead
    /// we read the exact bound session and settle only when the real first user
    /// message proves the reserved plan bytes reached that container.
    let private reconcilePendingDeliveries
        (works: (SessionId * Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) array)
        (workspace: string option)
        (snapshotPort: ISessionSnapshotPort option)
        (discoveryPort: ISessionPort option)
        (retirementBlockers: string -> string array)
        : Task =
        task {
            let! gitCommonDir =
                match workspace with
                | Some ws -> Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot ws
                | None -> Task.FromResult ""

            let sessions =
                works
                |> Array.choose (fun (sessionId, work) ->
                    if work.PendingDelivery then
                        Some(SessionId.value sessionId)
                    else
                        None)

            for workId in sessions do
                do! recoverDeliveryWork gitCommonDir workId snapshotPort discoveryPort retirementBlockers
        }

    let private readExistingFile (fullPath: string) : string option =
        if System.IO.File.Exists fullPath then
            Some(System.IO.File.ReadAllText fullPath)
        else
            None

    let private tryReadPlanFile (root: string) (relPath: string) : string option =
        try
            readExistingFile (System.IO.Path.Combine(root, relPath))
        with _ ->
            None

    let attach (boot: PluginBoot.Boot) : unit =
        let scope = boot.Scope
        let snapshotPort = boot.Transport.Snapshot

        let discoveryPort = boot.Transport.SessionCatalog

        let retirementBlockers sessionId =
            scope.RetirementBlockersFor sessionId |> List.toArray

        // Restart dropped the process-local execution bindings; the durable
        // handle records are the evidence for which parented children a road
        // still owns, so its fixed DevOps can be dispatched to again
        // (crash-reconciliation-020).
        match boot.Journal with
        | Some journal -> SessionBindingRecovery.install journal
        | None -> ()

        /// crash-reconciliation-020: the load-phase void settlement is journal-only
        /// and fast, and every handoff's identity judgment must read its facts, so
        /// it is the entire journal recovery barrier. Host round-trips (delivery
        /// observation, per-session reload sweeps) never sit inside it: they would
        /// put every unrelated session's slow Host answer between a handoff and
        /// this settlement — the first-admission stall.
        ///
        /// `RunOwnedWork` keeps the shutdown drain informed while letting the
        /// settlement's real failure propagate; `RunOrAwaitRecovery` clears a
        /// faulted run so a later caller retries the idempotent settlement instead
        /// of re-observing a stale failure forever.
        let settleLoadPhase () =
            scope.RunOwnedWork(fun () ->
                task {
                    // crash-reconciliation-020: the child work the previous process
                    // left behind can no longer be delivered, so it is voided
                    // (Abandoned(Voided)) — the obligation stays visible until
                    // join reports the void once and retires it.
                    match boot.Journal with
                    | Some journal -> do! ChildWorkRecovery.settleOrphanedChildRuns journal
                    | None -> ()

                    // crash-reconciliation-018: the durable load homing is done once
                    // the void settlement landed; the next real user instruction
                    // carries one restart status guidance.
                    scope.MarkRestartGuidancePending()
                })

        let reloadPendingSession sessionId =
            task {
                try
                    do! scope.ReloadChatSession sessionId
                    return None
                with error ->
                    Wanxiangshu.Foundation.Diagnostic.emit
                        "load-session-recovery-failed"
                        [ "session_id", SessionId.value sessionId; "reason", error.Message ]

                    return Some error
            }

        let reloadPendingSessions () =
            task {
                let mutable firstFailure: exn option = None

                for sessionId in scope.PendingChatRecoverySessions() do
                    let! failure = reloadPendingSession sessionId
                    firstFailure <- firstFailure |> Option.orElse failure

                match firstFailure with
                | Some error -> raise error
                | None -> ()
            }

        /// Host-observation recovery that must never block a handoff: delivery
        /// reconciliation and the boot-time reload sweep, sliced per session. Each
        /// session's sweep is single-flight on the recovery host, so an admission
        /// joins its own session's sweep while unrelated sessions' sweeps — however
        /// slow their Host answers — run entirely in the background.
        let reconcileHostObservations () =
            scope.RunBackground(fun () ->
                task {
                    match boot.Journal with
                    | Some journal ->
                        // crash-reconciliation-020: the void facts stay the first durable
                        // action — every per-session sweep chains on the same barrier.
                        do! journal.RunOrAwaitRecovery()

                        // provider-attempt-recovery-024: the abandoned stale requests
                        let! recoveryWorks = inspectRecoveryWorks journal boot.WorkspaceDirectory

                        do!
                            reconcilePendingDeliveries
                                recoveryWorks
                                boot.WorkspaceDirectory
                                snapshotPort
                                discoveryPort
                                retirementBlockers

                        // provider-attempt-recovery-023: the boot reload, one session at a
                        // time. A sweep's failure is isolated to its own session (other
                        // sessions still settle), emitted as a diagnostic, and re-raised
                        // at the end so the background failure slot keeps the first one.
                        do! reloadPendingSessions ()
                    | None -> ()
                })

        // crash-reconciliation-020: the load settlement must be observable by
        // the next handoff's identity judgment, which can happen before (or
        // without) the scope activator firing. The journal carries the barrier
        // so both dispatch and admission can trigger and await the very same
        // work; the host observation above only ever runs after it.
        match boot.Journal with
        | Some durable -> durable.EnqueueRecovery settleLoadPhase
        | None -> ()

        scope.AttachDurabilityActivation(fun () ->
            match boot.Journal with
            | Some durable -> scope.TrackDurabilityActivation(durable.RunOrAwaitRecovery())
            | None -> scope.TrackDurabilityActivation(settleLoadPhase ())

            reconcileHostObservations ())
