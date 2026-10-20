namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

/// Ordinary turn observation policy (INTERACTION-REPAIR / PROVIDER-RECOVERY / TERMINAL-REPORT).
module OrdinaryTurnWorkflow =

    /// Revisit a previously delivered turn only for work whose authority comes
    /// from a fresh idle observation. Terminal plumbing remains first-delivery only.
    val observeIdle:
        quiescence: ISessionQuiescenceGate ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        observation: TurnObservationJournalPort option ->
        context: ReconciledTurnContext ->
        observer: ContinuationAcceptanceObserver option ->
            Task

    val observe:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        observation: TurnObservationJournalPort option ->
        joinGuardNudges: HashSet<string> ->
        hasLivePty: (string -> bool) ->
        abortCause: AbortCause ->
        quiescence: ISessionQuiescenceGate ->
        context: ReconciledTurnContext ->
        observer: ContinuationAcceptanceObserver option ->
            Task
