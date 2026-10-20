namespace Wanxiangshu.Interaction.Repair

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

module InteractionRepairWorkflow =
    val repairUnfinishedWork:
        quiescence: ISessionQuiescenceGate ->
        context: ReconciledTurnContext ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        observer: ContinuationAcceptanceObserver option ->
            Task

    val repairMissingFinalReport:
        quiescence: ISessionQuiescenceGate ->
        context: ReconciledTurnContext ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        observation: TurnObservationJournalPort option ->
        observer: ContinuationAcceptanceObserver option ->
            Task

    val repairIncompleteInteraction:
        quiescence: ISessionQuiescenceGate ->
        context: ReconciledTurnContext ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        observation: TurnObservationJournalPort option ->
        observer: ContinuationAcceptanceObserver option ->
            Task
