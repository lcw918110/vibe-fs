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

    let private seedDevOpsTarget roadSessionId childSessionId target =
        try
            ModelRouting.seedBoundDevOpsModel childSessionId target
        with ex ->
            invalidOp (
                sprintf
                    "execution-model-routing-019: seeding the fixed DevOps target failed (road session %s, devops session %s, target '%s'): %s"
                    (SessionId.value roadSessionId)
                    (SessionId.value childSessionId)
                    target
                    ex.Message
            )

    /// Seed every road's fixed DevOps target into the process-shared routing
    /// table from the durable road projection (execution-model-routing-019).
    ///
    /// The authority chain is durable end to end: the road projection holds the
    /// target string, the road session's handle projection resolves the DevOps
    /// child session, and ModelRouting owns the binding. A road without a bound
    /// target is skipped; a malformed one is refused by the parser rather than
    /// replaced by the current scheduler preference.
    let private seedBoundDevOpsModelTargets
        (journal: AgentJournal)
        (works: (SessionId * Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) array)
        =
        let snapshot = AgentJournal.snapshot journal

        for sessionId, work in works do
            let target =
                work.DevOpsTarget
                |> Option.filter (fun text -> not (String.IsNullOrWhiteSpace text))

            let child =
                snapshot.AgentProjections.Sessions
                |> Map.tryFind sessionId
                |> Option.bind (fun projection -> projection.Handles)
                |> Option.bind (HandleProjection.tryFindByByname "devops")
                |> Option.map (fun handle -> handle.ChildSessionId)

            Option.map2 (seedDevOpsTarget sessionId) child target |> ignore

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
        let snapshotPort = SessionSnapshotPort.create boot.WorkspaceDirectory boot.Input

        let discoveryPort =
            SessionSnapshotPort.createSessionPort boot.WorkspaceDirectory boot.Input

        let retirementBlockers sessionId =
            scope.RetirementBlockersFor sessionId |> List.toArray

        // Restart dropped the process-local execution bindings; the durable
        // handle records are the evidence for which parented children a road
        // still owns, so its fixed DevOps can be dispatched to again
        // (crash-reconciliation-020).
        match boot.Journal with
        | Some journal -> SessionBindingRecovery.install journal
        | None -> ()

        let recovery () =
            scope.RunBackgroundTracked(fun () ->
                task {
                        // crash-reconciliation-020: the child work the previous process
                        // left behind can no longer be delivered, so it is voided
                        // (Abandoned(Voided)) — the obligation stays visible until
                        // join reports the void once and retires it.
                        match boot.Journal with
                        | Some journal -> do! ChildWorkRecovery.settleOrphanedChildRuns journal
                        | None -> ()

                        // provider-attempt-recovery-024: the abandoned stale requests
                        let! recoveryWorks =
                            match boot.Journal with
                            | Some journal -> inspectRecoveryWorks journal boot.WorkspaceDirectory
                            | None -> Task.FromResult [||]

                        match boot.Journal with
                        | Some journal ->
                            do!
                                reconcilePendingDeliveries
                                    recoveryWorks
                                    boot.WorkspaceDirectory
                                    snapshotPort
                                    discoveryPort
                                    retirementBlockers
                        | None -> ()

                        do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.PluginRuntimeReloaded)

                        do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.CapacityProjectionReplayed)

                        // execution-model-routing-019: reseed each road's fixed DevOps
                        // model target from the durable road projection. Restart dropped
                        // the process-local binding; without this the first Normal
                        // admission would take the target from the current scheduler
                        // preference and silently overwrite the road's fixed one.
                        match boot.Journal with
                        | Some journal -> seedBoundDevOpsModelTargets journal recoveryWorks
                        | None -> ()

                        // crash-reconciliation-018: the load-phase normalization above owes
                        // one restart status guidance to the next real user instruction.
                        scope.MarkRestartGuidancePending()
                    })

        // crash-reconciliation-020: the same recovery must be observable by the
        // next handoff's identity judgment, which can happen before (or without)
        // the scope activator firing. The journal carries the barrier so both
        // dispatch and admission can trigger and await the very same work.
        match boot.Journal with
        | Some durable -> durable.EnqueueRecovery recovery
        | None -> ()

        scope.AttachDurabilityActivation(fun () ->
            match boot.Journal with
            | Some durable -> scope.TrackDurabilityActivation(durable.RunOrAwaitRecovery())
            | None -> scope.TrackDurabilityActivation(recovery ()))
