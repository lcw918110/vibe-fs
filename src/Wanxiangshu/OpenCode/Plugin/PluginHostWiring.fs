namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

#nowarn "3511"

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Knowledge.Casebook


module PluginHostWiring =

    /// Graceful close: finalize the root-owned inspector draft through the
    /// workspace store once. Acquiring the store is pre-validated by the
    /// outer `try`; once held, lifecycle failures report via the task's
    /// settled signature instead of retrying the pyramid.
    let private settleFinalization workspaceRoot store delegateSessionId =
        task {
            try
                return! CasebookLifecycle.tryFinalizeDraft workspaceRoot store delegateSessionId

            with ex ->
                // A thrown boundary error (store acquisition or lifecycle
                // bug) has indeterminate durability: report Unknown and
                // retain the identity.
                return CaseFinalizeSettlement.unknown delegateSessionId ex.Message
        }

    let internal tryFinalizeWithin (settlements: CasebookSettlementOwner) workspaceRoot store delegateSessionId =
        task {
            let! settled = settleFinalization workspaceRoot store delegateSessionId

            match settled.Commitment with
            | CaseFinalizeCommitment.PersistenceFailed failure -> settlements.Observe failure |> ignore
            | _ -> ()

            return settled
        }

    let private acquireFinalizationStore workspaceRoot : Task<Result<IEventStore, exn>> =
        task {
            try
                let! commonDir = RuntimePath.stateRoot workspaceRoot
                return Ok(WorkspaceEventStore.acquire commonDir)
            with ex ->
                return Error ex
        }

    let private tryFinalize settlements workspaceRoot delegateSessionId =
        task {
            match! acquireFinalizationStore workspaceRoot with
            | Ok store -> return! tryFinalizeWithin settlements workspaceRoot store delegateSessionId
            | Error cause -> return CaseFinalizeSettlement.unknown delegateSessionId cause.Message
        }

    /// Composition-root handle for everything the Host needs after boot:
    /// the ports `HostSignalBootstrap.wire` produced plus the shared-terminal
    /// acquisition from `PluginHost.createHost`.

    type Host =
        { EventPort: IEventObservationPort
          SessionPort: ISessionHostPort
          SnapshotOpt: ISessionSnapshotPort option
          Wired: HostSignalBootstrap.WiredSignals
          SharedTerminalKey: string option
          SharedTerminalPort: ISessionTerminalSource option
          RootWorkspace: IRootWorkspaceReader

          CausalWaitObserver: IWaitObserver }

    /// Callback supplier: wiring hands the bound ports, the composition root
    /// returns the turn-workflow observation task. Keeps Host-side modules
    /// free of any static reference to the relay turn-workflow module.
    type ObserveTurnWorkflowSupplier =
        ISessionHostPort -> IEventObservationPort -> IRootWorkspaceReader -> ReconciledTurnContext -> Task

    let create (observeTurnWorkflowFor: ObserveTurnWorkflowSupplier) (boot: PluginBoot.Boot) : Task<Host> =
        task {
            let input = boot.Input
            let scope = boot.Scope
            let workspaceDirectory = boot.WorkspaceDirectory

            let terminalToResult (outcome: Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome) =
                match outcome with
                | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Completed _ -> Ok()
                | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Failed stop -> Error stop.Reason
                | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Aborted stop -> Error stop.Reason

            let sendResultToUnit (outcome: Wanxiangshu.Foundation.Outcome.SendOutcome) =
                match outcome with
                | Wanxiangshu.Foundation.Outcome.SendOutcome.AdmittedWithReceipt _
                | Wanxiangshu.Foundation.Outcome.SendOutcome.AdmittedWithPhysicalMessage _ -> Ok()
                | Wanxiangshu.Foundation.Outcome.SendOutcome.Retryable r
                | Wanxiangshu.Foundation.Outcome.SendOutcome.Fatal r
                | Wanxiangshu.Foundation.Outcome.SendOutcome.AcceptanceUnknown r -> Error r

            let completeHost
                eventPort
                (sessionPort: ISessionHostPort)
                snapshotOpt
                terminalKey
                sharedTerminalPort
                : Task<Host> =
                task {
                    match boot.Journal with
                    | Some journal ->
                        let kbPort =
                            { new Wanxiangshu.Repository.Knowledge.Casebook.ICasebookSessionPort with
                                member _.AbortSession childId = sessionPort.AbortSession childId

                                member _.SubscribeTerminal(childId, listener) =
                                    sessionPort.SubscribeTerminal(
                                        childId,
                                        (fun id outcome -> listener id (terminalToResult outcome))
                                    )

                                member _.SendPrompt(childId, text, agent) =
                                    task {
                                        let exactTools = Map.ofList [ "*", false; "todowrite-for-bookkeeper", true ]

                                        let opts: Wanxiangshu.Foundation.HostSession.SessionPromptOptions =
                                            { Model = None
                                              Agent = Some agent
                                              Directory = None
                                              Metadata = None
                                              Tools = Some exactTools
                                              DetachedListener = None }

                                        let! outcome = sessionPort.SendPrompt(childId, text, opts)
                                        return sendResultToUnit outcome
                                    }

                                member _.CreateSiblingSession(owner, title, agent) =
                                    sessionPort.CreateSiblingSession(
                                        owner,
                                        None,
                                        { Title = Some title
                                          Agent = Some agent
                                          Directory = None }
                                    ) }

                        BookkeeperRuntime.setPort kbPort (fun ownerSessionId ->
                            let projections = (AgentJournal.snapshot journal).AgentProjections
                            PromptAuthorityProjectionQueries.activeProfile ownerSessionId projections)
                    | None -> BookkeeperRuntime.resetRuntime ()

                    scope.AttachSharedTerminal(terminalKey, sharedTerminalPort)

                    for KeyValue(childId, parentId) in scope.Sessions.SessionParents do
                        scope.Sessions.OwnedSessions.Add childId |> ignore
                        scope.Sessions.OwnedSessions.Add parentId |> ignore

                    let causalWait = CausalWaitProcess.local ()

                    let rootWorkspace = RootWorkspaceProcess.local ()

                    // Keep the causal wait bridge on the root workspace.
                    rootWorkspace.Binder.TryBind workspaceDirectory |> ignore

                    rootWorkspace.Reader.TryRead()
                    |> Option.iter (fun workspace ->
                        causalWait.BindDiagnosticTarget(CausalWaitBridge.target workspace) |> ignore)

                    CasebookLifecycle.setEnabled workspaceDirectory

                    let! wired =
                        HostSignalBootstrap.wire
                            (observeTurnWorkflowFor sessionPort eventPort rootWorkspace.Reader)
                            sessionPort
                            eventPort
                            snapshotOpt
                            boot.Journal
                            scope

                            rootWorkspace.Reader
                            input
                            BookkeeperRuntime.tryConsumePromptAuthorization
                            (fun terminal ->
                                let outcome =
                                    match terminal.Outcome with
                                    | HostProviderTerminalOutcome.Completed _ -> Ok()
                                    | failure -> Error(sprintf "%A" failure)

                                BookkeeperRuntime.completePhysical terminal.SessionId outcome)
                            workspaceDirectory
                            (Some(tryFinalize boot.CasebookSettlements))
                            (Some CasebookLifecycle.cleanupDraft)

                    return
                        { EventPort = eventPort
                          SessionPort = sessionPort
                          SnapshotOpt = snapshotOpt
                          Wired = wired
                          SharedTerminalKey = terminalKey
                          SharedTerminalPort = sharedTerminalPort
                          RootWorkspace = rootWorkspace.Reader

                          CausalWaitObserver = causalWait.Observer }
                }

            let! commonDirOpt =
                match workspaceDirectory with
                | Some ws ->
                    task {
                        let! dir = RuntimePath.stateRoot ws
                        return Some dir
                    }
                | None -> Task.FromResult None

            // A completed work closes its authority run even while that run's
            // root is still present. The milestone 「已完成」 is the fact.
            let isLifecycleTerminated (sessionId: SessionId) =
                commonDirOpt
                |> Option.bind (fun commonDir ->
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir (SessionId.value sessionId))
                |> Option.exists Wanxiangshu.Mission.Lifecycle.DurableWorkHost.closesAuthority

            let isUserQuestionWaiting sessionId =
                boot.Journal
                |> Option.bind (fun durable -> UserQuestionJournal.pending durable sessionId)
                |> Option.isSome

            match!
                PluginHost.createHost
                    boot.WorkspaceDirectory
                    input
                    boot.PortOpt
                    (Some boot.FamilyParent)
                    (Some isLifecycleTerminated)
                    (Some isUserQuestionWaiting)
            with
            | Error err -> return raise (InvalidOperationException err)
            | Ok(eventPort, sessionPort, snapshotOpt, terminalKey, sharedTerminalPort) ->
                return! completeHost eventPort sessionPort snapshotOpt terminalKey sharedTerminalPort
        }
