namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// Turn observation policy for one reconciled turn (STRENGTH / RECOVERY-FAMILY / TurnWorkflow).
module HostTurnObserver =

    let private sendGuardContinuation
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (source: ProviderAttemptSource)
        (kind: DegenerationKind)
        (directory: string option)
        (observer: ContinuationAcceptanceObserver option)
        : Task<Result<unit, string>> =
        task {
            let prompt =
                ProviderProse.documentFor source.SessionId (LoopSensor.continuationPath kind) Map.empty

            let! outcome =
                HostSessionNudge.sendContinuationResult
                    sessionPort
                    rootWorkspace
                    source.SessionId
                    prompt
                    PromptAuthority.ContinuationKind.DegenerationGuard
                    directory
                    journal
                    PromptDispatcher.AwaitMode.Detached
                    observer

            return outcome |> Result.map ignore
        }

    let attachLoopSensor
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (emitDiagnostic: string -> (string * string) list -> unit)
        : unit =
        let continueSession (source: ProviderAttemptSource) kind directory observer =
            match HostSessionNudge.tryActiveProfile journal source.SessionId with
            | Some profile when profile.AuthorityRootUserMessageId <> source.AuthorityRootUserMessageId ->
                Task.FromResult(Error "Degeneration guard source authority is no longer active")
            | _ -> sendGuardContinuation sessionPort rootWorkspace journal source kind directory observer

        let sensor =
            LoopSensor.create
                scope.Sessions.OwnedSessions
                scope.Sessions.SessionParents
                sessionPort.InterruptAttempt
                continueSession
                emitDiagnostic

        scope.AttachLoopSensor sensor

    let private isDurableFissionOwner (journal: AgentJournal option) (sessionId: SessionId) =
        journal
        |> Option.exists (fun durable ->
            FissionProjection.tryActiveForOwner sessionId (AgentJournal.snapshot durable).AgentProjections.Fission
            |> Option.isSome)

    let private isFissionOwnerSession (journal: AgentJournal option) (sessionId: SessionId) =
        FissionRuntime.isSilentInterrupt sessionId
        || isDurableFissionOwner journal sessionId

    /// Host boundary consumes the guard's one-shot armed anomaly. Consumption
    /// also schedules the guard-owned continuation at this existing reconcile point.
    let private abortCauseOfTurn (scope: PluginRuntimeScope) (context: ReconciledTurnContext) : Task<AbortCause> =
        match context.Turn.Outcome with
        | ReconcileProgram.TurnAborted _ ->
            // DEG-OWN: exact-run owned consumption. Only an anomaly armed for this
            // SessionId + ProviderRun transfers, so a wrong/late run can never
            // consume a newer attempt's anomaly and session-only recovery is
            // never authorized.
            let source: ProviderAttemptSource =
                { SessionId = context.Turn.SessionId
                  PhysicalUserMessageId = context.Turn.PhysicalUserMessageId
                  AuthorityRootUserMessageId = context.Turn.AuthorityRootUserMessageId
                  ProviderRun = context.Turn.ProviderRun }

            let observer =
                scope.SyncDelegateRuntime
                |> Option.bind (fun runtime -> runtime.BindContinuationAcceptance source)

            scope.LoopSensor.ConsumeAbortCause(source, context.Turn.Directory, observer)
        | _ -> Task.FromResult AbortCause.External

    /// DEG-OWN: settle the owned interrupt/continuation task before admitting
    /// later continuation/business observation. The owned task is awaited,
    /// never discarded.
    /// The wait addresses the exact current run: a mismatched run waits on
    /// nothing while the stored run's task remains owned.
    let private awaitOwnedInterrupt
        (scope: PluginRuntimeScope)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : Task =
        task {
            match scope.LoopSensor.ActiveInterruptTask(sessionId, providerRun) with
            | None -> return ()
            | Some owned -> do! owned
        }

    let private observeApplicationTurn
        (observeTurnWorkflow: AbortCause -> ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (abortCause: AbortCause)
        (context: ReconciledTurnContext)
        : Task =
        task {
            // Sole Application turn entry (rabbit §6.5 / §18): Host no longer
            // multiplexes SyncDelegate / Manager handled-bools.
            do! observeTurnWorkflow abortCause context
        }

    let private observeCurrentTurn
        (observeTurnWorkflow: AbortCause -> ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (abortCause: AbortCause)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let turn = context.Turn
            let isFissionOwner = isFissionOwnerSession journal turn.SessionId

            let attempts: AttemptPlanCapability =
                { TryAttemptPlan = scope.TryAttemptPlan
                  TryBindAttemptPlan = scope.TryBindAttemptPlan
                  ConsumeAttemptPlan = scope.ConsumeAttemptPlan
                  FreezePendingAttemptPlan = scope.Recovery.FreezePendingAttemptPlan
                  TryPendingAttemptPlan = scope.Recovery.TryPendingAttemptPlan }

            let wirePort = journal |> Option.map AgentJournalPortAdapter.forWire
            do! XWire.reconcileAttempt wirePort attempts turn
            do! TurnRuntimePreparation.prepare scope.DisposeExecutorRuntime turn

            let! fissionHandled =
                FissionHost.observeLaneTurn
                    sessionPort
                    rootWorkspace
                    eventPort
                    journal
                    scope.Sessions.JoinGuardNudges
                    scope.Sessions.Quiescence
                    context.Quiescence
                    abortCause
                    turn

            if not isFissionOwner && not fissionHandled then
                do!
                    observeApplicationTurn
                        observeTurnWorkflow
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        scope
                        abortCause
                        context
        }

    let private observeBusinessTurn
        (observeTurnWorkflow: AbortCause -> ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let turn = context.Turn
            let! abortCause = abortCauseOfTurn scope context

            // DEG-OWN: the owned interrupt/continuation settles before later
            // business observation is admitted.
            do! awaitOwnedInterrupt scope turn.SessionId turn.ProviderRun

            // Current-process Host observation proceeds from its exact facts.
            // No durable-family gate is fabricated here: Join tools admit via
            // their exact current-process permit, and explicit /continue owns
            // future user-driven work.
            return!
                observeCurrentTurn
                    observeTurnWorkflow
                    sessionPort
                    rootWorkspace
                    eventPort
                    journal
                    scope
                    abortCause
                    context
        }

    let observe
        (observeTurnWorkflow: AbortCause -> ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (context: ReconciledTurnContext)
        : Task =
        observeBusinessTurn observeTurnWorkflow sessionPort rootWorkspace eventPort journal scope context
