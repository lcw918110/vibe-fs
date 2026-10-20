namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

/// Sole Application entry for a reconciled turn observation (rabbit §6.5).
///
/// Host hands a stable observation here; this module owns bounded-context fan-out:
/// SyncDelegate-owned → Manager → Ordinary.
module TurnWorkflow =

    /// Route one stable observation to SyncDelegate-owned / Manager / Ordinary.
    val observe:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        syncDelegate: SyncDelegateRuntime option ->
        nudgeSent: HashSet<string> ->
        joinGuardNudges: HashSet<string> ->
        hasLivePty: (string -> bool) ->
        abortCause: AbortCause ->
        quiescence: ISessionQuiescenceGate ->
        context: ReconciledTurnContext ->
            Task
