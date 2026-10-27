namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Prefix.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Mission.Work
open Fable.Core
open Fable.Core.JsInterop

/// Turn observation policy for one reconciled turn (STRENGTH / RECOVERY-FAMILY / TurnWorkflow).
module HostTurnObserver =

    [<Import("deliveryAttemptLedger", "wanxiangli/dispatch")>]
    let private deliveryAttemptLedger: obj = jsNative
    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private fissionRuntime: obj = jsNative

    let private isDurableFissionOwner (journal: AgentJournal option) (sessionId: SessionId) =
        journal
        |> Option.exists (fun durable ->
            FissionProjection.tryActiveForOwner sessionId (AgentJournal.snapshot durable).AgentProjections.Fission
            |> Option.isSome)

    let private isFissionOwnerSession (journal: AgentJournal option) (sessionId: SessionId) =
        fissionRuntime?isSilentInterrupt (SessionId.value sessionId)
        || isDurableFissionOwner journal sessionId

    /// Settle the owned interrupt task before admitting later continuation/
    /// business observation. The owned task is awaited, never discarded.
    /// The wait addresses the exact current run: a mismatched run waits on
    /// nothing while the stored run's task remains owned.
    let private awaitOwnedInterrupt
        (scope: PluginRuntimeScope)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : Task =
        task {
            // 条款 4：「已发出」不等于「已观察到」。交付自己发起的那次中断
            // 也是本观察者自有任务，同样按精确 run 取；两者都未发出时不等待
            // （没有中断不得变成阻塞）。
            let deliveryTask: Task option =
                let taskOpt: obj option =
                    deliveryAttemptLedger?activeInterruptTask(SessionId.value sessionId, ProviderRunIdentity.value providerRun)
                taskOpt |> Option.map (fun t -> unbox<Task> t)

            match deliveryTask with
            | None -> return ()
            | Some owned -> do! owned
        }

    let private observeApplicationTurn
        (observeTurnWorkflow: ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (context: ReconciledTurnContext)
        : Task =
        task {
            // Sole Application turn entry (rabbit §6.5 / §18): Host no longer
            // multiplexes SyncDelegate / Manager handled-bools.
            do! observeTurnWorkflow context
        }

    let private observeCurrentTurn
        (observeTurnWorkflow: ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
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
                    turn

            if not isFissionOwner && not fissionHandled then
                do! observeApplicationTurn observeTurnWorkflow sessionPort rootWorkspace eventPort journal scope context
        }

    let private observeBusinessTurn
        (observeTurnWorkflow: ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let turn = context.Turn

            // The owned interrupt/continuation settles before later business
            // observation is admitted.
            do! awaitOwnedInterrupt scope turn.SessionId turn.ProviderRun

            // Current-process Host observation proceeds from its exact facts.
            // No durable-family gate is fabricated here: Join tools admit via
            // their exact current-process permit, and explicit /continue owns
            // future user-driven work.
            return! observeCurrentTurn observeTurnWorkflow sessionPort rootWorkspace eventPort journal scope context
        }

    let observe
        (observeTurnWorkflow: ReconciledTurnContext -> Task)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (scope: PluginRuntimeScope)
        (context: ReconciledTurnContext)
        : Task =
        observeBusinessTurn observeTurnWorkflow sessionPort rootWorkspace eventPort journal scope context
