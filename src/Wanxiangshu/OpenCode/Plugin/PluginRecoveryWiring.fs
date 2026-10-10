namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Mission.Planning
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

module PluginRecoveryWiring =

    let private isFlightActive
        (bloggerHost: IBloggerRuntimeHost)
        (bloggerSessionId: SessionId)
        (requestId: BloggerRequestId)
        : bool =
        match bloggerHost.TryGetFlight(SessionId.value bloggerSessionId) with
        | Some flight -> BloggerRequestContext.requestId flight = requestId
        | None -> false

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
    let private seedBoundDevOpsModelTargets (journal: AgentJournal) =
        let snapshot = AgentJournal.snapshot journal

        for KeyValue(sessionId, projection) in snapshot.AgentProjections.Sessions do
            let target =
                projection.Relay
                |> Option.bind (fun state -> Fold.view state (RoadId.create (SessionId.value sessionId)))
                |> Option.bind (fun view -> view.BoundDevOpsModelTarget)

            let child =
                projection.Handles
                |> Option.bind (HandleProjection.tryFindByByname "devops")
                |> Option.map (fun handle -> handle.ChildSessionId)

            Option.map2 (seedDevOpsTarget sessionId) child target |> ignore

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

    let private emitRecoveryEffect (effect: PlanRecoveryEffect) : unit =
        match effect with
        | PlanRecoveryEffect.DeliveredIdempotent(workId, path) ->
            Diagnostic.emit "plan-work-delivered-idempotent" [ "work_id", workId; "path", path ]
        | PlanRecoveryEffect.ActiveRebound(workId, incumbencyId, stage, true) ->
            Diagnostic.emit
                "plan-work-active-rebound"
                [ "work_id", workId
                  "incumbency_id", incumbencyId
                  "stage", stage
                  "plan_exists", "true" ]
        | PlanRecoveryEffect.ActiveRebound(workId, incumbencyId, stage, false) ->
            Diagnostic.emit
                "plan-work-active-file-missing"
                [ "work_id", workId
                  "incumbency_id", incumbencyId
                  "stage", stage
                  "plan_exists", "false" ]
        | PlanRecoveryEffect.Conflict(workId, reason) ->
            Diagnostic.emit "plan-recovery-conflict" [ "work_id", workId; "reason", reason ]

    let private evaluatePlanRecovery (viewsOpt: PlanWorkView list option) (workspaceDirOpt: string option) : unit =
        match viewsOpt, workspaceDirOpt with
        | Some views, Some root ->
            let readPlan = tryReadPlanFile root
            let effects = PlanRecovery.evaluateRecoveryEffects views readPlan
            effects |> List.iter emitRecoveryEffect
        | _ -> ()

    let private tryResolveViews (workspace: string) : PlanWorkView list option =
        try
            let commonDir = RuntimePath.gitCommonDir workspace

            WorkspaceEventStore.tryCurrent commonDir
            |> Option.map PlanEventStore.allWorkViews
        with _ ->
            None

    let private tryResolveWorkspaceViews (workspaceDirOpt: string option) : PlanWorkView list option =
        match workspaceDirOpt with
        | Some workspace when not (String.IsNullOrWhiteSpace workspace) -> tryResolveViews workspace
        | _ -> None

    let attach (boot: PluginBoot.Boot) : unit =
        let scope = boot.Scope

        // Restart dropped the process-local execution bindings; the durable
        // handle records are the evidence for which parented children a road
        // still owns, so its fixed DevOps can be dispatched to again
        // (crash-reconciliation-020).
        match boot.Journal with
        | Some journal -> SessionBindingRecovery.install journal
        | None -> ()

        scope.AttachDurabilityActivation(fun () ->
            scope.RunBackground(fun () ->
                task {
                    // crash-reconciliation-020: settle the child work runs the
                    // previous runtime left active, so the next handoff to that
                    // child is a fresh root instead of a refused identity.
                    // provider-attempt-recovery-024: the abandoned stale requests
                    // owed one more settlement — their same-source accepted
                    // executions never reached a provider. Decide them before the
                    // runtime reload signal; one failed session must not block the
                    // others, and a boot-time release is an idempotent request.
                    let settleStaleBloggerSession (bloggerSessionId: SessionId) : Task =
                        task {
                            try
                                do!
                                    scope.SignalChatRecovery(
                                        ChatExecutionRecoveryLifecycleEvent.BloggerStaleRequestAbandoned
                                            bloggerSessionId
                                    )
                            with error ->
                                Diagnostic.emit
                                    "stale-blogger-execution-settlement-failed"
                                    [ "blogger_session_id", SessionId.value bloggerSessionId
                                      "error", error.Message ]
                        }

                    // ... and the Blog materializations it left open. No live
                    // execution can own one, and while it stays open the
                    // coordinator never materializes a fresh request — the
                    // Blogger would never ingest the raw tail again
                    // (crash-reconciliation-020 / context-compression-024).
                    let settleAbandonedStaleBloggerSessions (journal: AgentJournal) =
                        task {
                            let bloggerHost = scope.BloggerRuntimeHost
                            let liveFlight = isFlightActive bloggerHost

                            let! staleBloggerSessions = BloggerAbandon.settleStaleOpenAtLoad liveFlight journal

                            for bloggerSessionId in staleBloggerSessions do
                                do! settleStaleBloggerSession bloggerSessionId
                        }

                    match boot.Journal with
                    | Some journal ->
                        do! ChildWorkRecovery.settleOrphanedChildRuns journal
                        do! settleAbandonedStaleBloggerSessions journal
                    | None -> ()

                    do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.PluginRuntimeReloaded)

                    do! scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.CapacityProjectionReplayed)

                    // execution-model-routing-019: reseed each road's fixed DevOps
                    // model target from the durable road projection. Restart dropped
                    // the process-local binding; without this the first Normal
                    // admission would take the target from the current scheduler
                    // preference and silently overwrite the road's fixed one.
                    match boot.Journal with
                    | Some journal -> seedBoundDevOpsModelTargets journal
                    | None -> ()

                    // planning-018 / Algorithm J: Plan crash recovery idempotent evaluation
                    // Recover Plan position from durable projection without text guessing.
                    // Delivered remains final; Active rebinds without recreating work or resuming uninvited DevOps.
                    try
                        let viewsOpt = tryResolveWorkspaceViews boot.WorkspaceDirectory
                        evaluatePlanRecovery viewsOpt boot.WorkspaceDirectory
                    with ex ->
                        Diagnostic.emit "plan-crash-recovery-failed" [ "error", ex.Message ]

                    // crash-reconciliation-018: the load-phase normalization above owes
                    // one restart status guidance to the next real user instruction.
                    scope.MarkRestartGuidancePending()
                }))
