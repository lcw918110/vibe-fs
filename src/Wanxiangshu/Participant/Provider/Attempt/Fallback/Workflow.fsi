namespace Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

module ProviderRecoveryWorkflow =
    /// True only when this failed attempt is itself the verbatim retry of the same provider.
    val failureIsSameProviderRetry:
        durable: AgentJournal ->
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
            bool

    /// The request kind a confirmed failure continues with: the persisted ProviderStarted
    /// kind, or the accepted evidence's ordinary kind when the start fact was never
    /// persisted. A satellite session without that fact is not guessed.
    val requestKindFor:
        durable: AgentJournal ->
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
            ProviderRequestKind option

    val continueAfterConfirmedFailure:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        turn: ReconciledTurn ->
        failure: ExecutionFailure ->
        error: string ->
        continuationPrompt: string ->
            Task

    val continueDelegateCallAfterConfirmedFailure:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        durable: AgentJournal ->
        turn: ReconciledTurn ->
        observer: ContinuationAcceptanceObserver option ->
        failure: ExecutionFailure ->
        error: string ->
            Task<RetryVerdict>
