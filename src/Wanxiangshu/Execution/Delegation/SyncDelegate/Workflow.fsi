namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

module internal SyncDelegateWorkflow =
    type Dependencies =
        { Attached: IAttachedSessionPort
          AwaitView: DiagnosticWait -> Task<Result<string, string>> -> Task<Result<string, string>>
          AwaitInvocation:
              DiagnosticWait
                  -> Task<Result<SyncDelegateInvocationResult, string>>
                  -> Task<Result<SyncDelegateInvocationResult, string>>
          ObserveChild:
              SessionId -> ReuseScopeId -> SyncDelegateRole -> string -> Task<Result<AttachedChildObservation, string>>
          CreateChild:
              SessionId
                  -> ReuseScopeId
                  -> SyncDelegateRole
                  -> string
                  -> string option
                  -> Task<Result<SessionId, string>>
          BindChild: SessionId -> SessionId -> string -> unit
          OnDelegateReady: SessionId -> string -> unit
          NoteDelegatePrompt: string -> string -> unit
          CleanupDelegateDraft: string -> unit
          Directory: string option
          ReplaceToolEstimate: SessionId -> int option -> Task<unit>
          SendPrompt: SyncDelegateCall -> SyncDelegatePromptRequest -> Task<Result<unit, string>>
          ResolveBoundAgent: SessionId -> string option
          DescribeWait: SyncDelegateWait -> DiagnosticWait
          SubscribeFutureTerminal: SessionId -> TerminalCompletionListener -> IDisposable }

    val invoke:
        store: SyncDelegateCallStore ->
        deps: Dependencies ->
        ownerSessionId: SessionId ->
        role: SyncDelegateRole ->
        charge: string ->
        expectedToolCalls: int option ->
        batch: SyncDelegateBatch option ->
        prepareProviderPrompt: (unit -> Task<LlmFacing.Document>) ->
        observeAdmission: (SyncDelegateObservedAdmission -> unit) option ->
        captureResponse: (SyncDelegateTerminalResponse -> unit) option ->
        isCancelled: (unit -> bool) ->
            Task<Result<SyncDelegateInvocationResult, string>>
