namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Persistence.Journal

/// Turn observation policy for one reconciled turn (RECOVERY-FAMILY / TurnWorkflow).
module HostTurnObserver =

    val attachLoopSensor:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        scope: PluginRuntimeScope ->
        emitDiagnostic: (string -> (string * string) list -> unit) ->
            unit

    val observe:
        observeTurnWorkflow: (AbortCause -> ReconciledTurnContext -> Task) ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        scope: PluginRuntimeScope ->

        context: ReconciledTurnContext ->
            Task
