namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.Journal

module HostForkProse =
    val forkInstructions: sessionId: SessionId -> ForkChildInstructions
    val chargeDocument: owner: SessionId -> charge: string -> LlmFacing.Document

module internal SyncDelegatePhysicalIdentity =
    val title: scope: ReuseScopeId -> role: SyncDelegateRole -> agentName: string -> string

/// Retry-decorator plug for dedicated delegate children (delegation-023): the caller
/// observes only the decorator's verdict, never a single transient attempt
/// failure. `Ok unit` keeps the invocation pending (a fresh attempt was admitted
/// or the episode was superseded); `Error reason` folds it as terminal.
type SyncDelegateRetryPort =
    { Retry:
        ReconciledTurn
            -> ContinuationAcceptanceObserver option
            -> Wanxiangshu.Execution.Failure.ExecutionFailure
            -> string
            -> Task<Result<unit, string>> }

type SyncDelegateRuntime =
    new:
        sessions: ISessionHostPort *
        awaitView: (DiagnosticWait -> Task<Result<string, string>> -> Task<Result<string, string>>) *
        awaitInvocation:
            (DiagnosticWait
                -> Task<Result<SyncDelegateInvocationResult, string>>
                -> Task<Result<SyncDelegateInvocationResult, string>>) *
        dispatcher: PromptDispatcher.Runtime *
        journal: AgentJournal *
        attached: IAttachedSessionPort *
        onDelegateReady: (SessionId -> string -> unit) *
        quiescence: ISessionQuiescenceGate *
        retryPort: SyncDelegateRetryPort *
        ?toolMapForRole: (Role -> Map<string, bool>) *
        ?workspaceDirectory: string *
        ?onDelegatePrompt: (string -> string -> unit) *
        ?onDelegateAnswer: (string -> string -> unit) *
        ?onDelegateCleanup: (string -> unit) ->
            SyncDelegateRuntime

    member Attached: IAttachedSessionPort
    member BindContinuationAcceptance: source: ProviderAttemptSource -> ContinuationAcceptanceObserver option

    member ObserveProviderToolCall:
        ownerSessionId: SessionId * providerRun: ProviderRunIdentity * role: SyncDelegateRole * callId: ToolCallId ->
            unit

    member TryObservedBatch:
        ownerSessionId: SessionId * providerRun: ProviderRunIdentity * role: SyncDelegateRole * currentCall: ToolCallId ->
            SyncDelegateBatch option

    member TryFind: ownerSessionId: SessionId * role: SyncDelegateRole -> SessionId option
    member TryFindDelegateOwner: delegateSessionId: SessionId -> SessionId option
    member TryFindForScopeClose: ownerSessionId: SessionId * role: SyncDelegateRole -> SessionId option
    member StageDeletedDelegate: ownerSessionId: SessionId * delegateSessionId: SessionId -> bool
    member StageDeletedDelegateBySession: delegateSessionId: SessionId -> SessionId option

    member Invoke:
        ownerSessionKey: string * role: SyncDelegateRole * charge: string * ?expectedToolCalls: int ->
            Task<Result<string, string>>

    member InvokePrepared:
        ownerSessionKey: string *
        role: SyncDelegateRole *
        charge: string *
        prepareProviderPrompt: (unit -> Task<LlmFacing.Document>) *
        ?expectedToolCalls: int ->
            Task<Result<string, string>>

    member InvokeBatchPrepared:
        ownerSessionKey: string *
        role: SyncDelegateRole *
        charge: string *
        batch: SyncDelegateBatch *
        prepareProviderPrompt: (unit -> Task<LlmFacing.Document>) *
        ?expectedToolCalls: int ->
            Task<Result<SyncDelegateInvocationResult, string>>

    member InvokeResponsePrepared:
        ownerSessionKey: string *
        role: SyncDelegateRole *
        charge: string *
        prepareProviderPrompt: (unit -> Task<LlmFacing.Document>) *
        ?isCancelled: (unit -> bool) ->
            Task<Result<string, string>>

    member ValidateObservedOwner: ownerSessionId: SessionId -> Result<unit, string>

    member InvokeObservedPrepared:
        ownerSessionId: SessionId *
        charge: string *
        prepareProviderPrompt: (unit -> Task<LlmFacing.Document>) *
        ?isCancelled: (unit -> bool) ->
            SyncDelegateObservedExecution

    member HandleTurn:
        turn: ReconciledTurn *
        failure: Wanxiangshu.Execution.Failure.ExecutionFailure option *
        permit: QuiescencePermit option ->
            Task<bool>

    member HasOpeningCursor: sessionId: SessionId -> bool
    member AwaitAssignmentReady: sessionId: SessionId -> Task<bool>
    member TryAcceptedAuthorityRoot: sessionId: SessionId -> string option
    /// delegation-031: settle a completed turn from its own parts when the terminal
    /// trace capture reports NotCommitted/Unknown. True iff a live call
    /// consumed the turn.
    member SettleCompletedFromTurn: turn: ReconciledTurn -> bool
    member CancelSession: sessionId: SessionId -> unit
    member Dispose: unit -> unit
    interface IDisposable
