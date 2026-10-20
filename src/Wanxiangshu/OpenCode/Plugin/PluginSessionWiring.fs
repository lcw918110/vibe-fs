namespace Wanxiangshu.OpenCode

open Wanxiangshu.Execution.Delegation
open System.Threading.Tasks

#nowarn "3511"

open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Knowledge.Casebook

module PluginSessionWiring =

    /// SyncDelegate runtime, attached only when a durable journal exists.
    let attach (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : unit =
        let scope = boot.Scope
        let journal = boot.Journal
        let sessionPort = host.SessionPort
        let wired = host.Wired
        let workspaceDirectory = boot.WorkspaceDirectory

        /// The delegate's role is whatever its managed name resolves to; a name
        /// that resolves to nothing must fail loudly rather than guess a role.
        let roleForAgent (agent: string) : Role =
            match ManagedAgent.tryParse agent with
            | Some managed -> managed.Role
            | None -> invalidArg "agent" (sprintf "unknown managed agent '%s'" agent)

        /// The retry decorator's real plug for dedicated delegate children: the
        /// provider-owned engine decides, admits and re-dispatches; the delegate
        /// observes only continue-vs-terminal (delegation-023).
        let delegateRetryPort (durable: AgentJournal) : SyncDelegateRetryPort =
            { Retry =
                fun turn observer failure error ->
                    task {
                        let! verdict =
                            ProviderRecoveryWorkflow.continueDelegateCallAfterConfirmedFailure
                                sessionPort
                                host.RootWorkspace
                                durable
                                turn
                                observer
                                failure
                                error

                        match verdict with
                        | RetryVerdict.Dispatched
                        | RetryVerdict.Superseded -> return Ok()
                        | RetryVerdict.Terminal reason -> return Error reason
                    } }

        let seedDurableSessions (durable: AgentJournal) =
            let snapshot = AgentJournal.snapshot durable

            snapshot.AgentProjections.Sessions
            |> Map.iter (fun sessionId sessionProj ->
                sessionProj.PromptAuthority
                |> Option.bind (fun authority -> authority.ActiveLogicalRun)
                |> Option.iter (fun _ -> scope.Sessions.ModelRoutingSessions.Add(SessionId.value sessionId) |> ignore))

        match journal with
        | Some durable ->
            // durable-events-020: history-derived process bindings are semantic
            // state, so seeding them belongs to the first durable admission, not
            // plugin construction. This callback also forces the deferred
            // WorkspaceEventStore Current exactly at that activation boundary.
            scope.AttachDurabilityActivation(fun () -> seedDurableSessions durable)

            // SyncDelegate attaches whenever the durable journal exists.
            let attached =
                AttachedSessionRuntime(
                    registerParent =
                        (fun owner child ->
                            scope.Sessions.SessionParents.[SessionId.value child] <- SessionId.value owner),
                    isUsable = (fun _ -> true)
                )

            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            let registerDelegate (delegateId: SessionId) (agent: string) =
                wired.RegisterOwned(SessionId.value delegateId)

                wired.BindActiveRun delegateId (roleForAgent agent) workspaceDirectory

            let syncDelegate =
                new SyncDelegateRuntime(
                    sessionPort,
                    CausalAwait.awaitTask host.CausalWaitObserver,
                    CausalAwait.awaitTask host.CausalWaitObserver,
                    dispatcher,
                    durable,
                    (attached :> IAttachedSessionPort),
                    registerDelegate,
                    scope.Sessions.Quiescence,
                    delegateRetryPort durable,
                    toolMapForRole =
                        (fun role ->
                            // epistemic-reasoning-032: delegated Engineers have
                            // the same WorkMain capabilities as other Engineers.
                            PromptAuthority.toolCapabilitiesFor role ProviderRequestKind.WorkMain
                            |> StaticTools.requestToolMap role),
                    ?workspaceDirectory = workspaceDirectory,
                    ?onDelegatePrompt = Some CasebookLifecycle.notePrompt,
                    ?onDelegateAnswer = Some CasebookLifecycle.noteAnswer,
                    ?onDelegateCleanup = Some CasebookLifecycle.cleanupDraft
                )

            scope.AttachSyncDelegateRuntime syncDelegate
        | None -> ()
