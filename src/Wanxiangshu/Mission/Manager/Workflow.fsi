namespace Wanxiangshu.Mission.Manager

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

/// Manager current-action guidance and retired-idle continuation ownership.
module ManagerWorkflow =

    val observeIdle:
        quiescence: ISessionQuiescenceGate ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        context: ReconciledTurnContext ->
            Task

    /// Observe one Manager-role turn. Manager-specific business branches stay here;
    /// non-Manager terminal semantics are delegated through the injected ordinary
    /// workflow rather than returned as a handled-bool program counter.
    val observe:
        quiescence: ISessionQuiescenceGate ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        observeOrdinary: (ReconciledTurnContext -> Task) ->
        context: ReconciledTurnContext ->
            Task

    val ensureManagerWorkOpened:
        workspaceDirectory: string option -> observedHead: int64 option -> sessionIdTextOpt: string option -> Task<unit>

    val maybeDeliverLoop:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        workspaceDirectory: string option ->
        sessionIdTextOpt: string option ->
            Task<unit>

    /// Read-only probe: the successor-prompt occasion this Manager session's work
    /// currently owes. It reads the same fold the dispatch reads and dispatches
    /// nothing.
    val successorPromptContextOf: workspaceDirectory: string -> sessionIdText: string -> Task<obj>

    val continueAfterRetiredAttempt:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        workspaceDirectory: string option ->
        stopRetiredAttempt: (SessionId -> Task<unit>) ->
        sessionId: SessionId ->
            Task<unit>

    /// The closeout instruction substituted into `runtime/manager-finish`.
    val finishCloseoutText: confirmed: bool -> string
