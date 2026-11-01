namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation.TerminalSignal

module internal SyncDelegateWorkflow =
    type Dependencies =
        { Attached: IAttachedSessionPort
          AwaitView: DiagnosticWait -> Task<Result<string, string>> -> Task<Result<string, string>>
          AwaitInvocation: DiagnosticWait -> Task<Result<SyncDelegateInvocationResult, string>> -> Task<Result<SyncDelegateInvocationResult, string>>
          ObserveChild: SessionId -> ReuseScopeId -> SyncDelegateRole -> string -> Task<Result<AttachedChildObservation, string>>
          CreateChild: SessionId -> ReuseScopeId -> SyncDelegateRole -> string -> string option -> Task<Result<SessionId, string>>
          BindChild: SessionId -> SessionId -> string -> unit
          OnDelegateReady: SessionId -> string -> unit
          NoteDelegatePrompt: string -> string -> unit
          CleanupDelegateDraft: string -> unit
          Directory: string option
          ReplaceToolEstimate: SessionId -> int option -> Task<unit>
          SendPrompt: SyncDelegateCall -> SyncDelegatePromptRequest -> Task<Result<unit, string>>
          ResolveBoundAgent: SessionId -> string option
          DescribeWait: SyncDelegateWait -> DiagnosticWait
          SubscribeFutureTerminal: SessionId -> TerminalCompletionListener -> System.IDisposable }

    let private effects (store: SyncDelegateCallStore) (deps: Dependencies) =
        let attach (request: obj) =
            let owner = SessionId.create (unbox request?owner)
            let role = DelegateValues.roleOf (unbox request?role)
            task {
                let! attached = deps.Attached.GetOrCreate(owner, role, SyncDelegate.agentNameFor role, deps.Directory, deps.ObserveChild, deps.CreateChild, deps.BindChild, deps.OnDelegateReady)
                return
                    match attached with
                    | Error reason -> DelegateValues.resultToNative (Error reason: Result<string, string>)
                    | Ok(session, agent) -> createObj [ "ok" ==> true; "session" ==> SessionId.value session; "agent" ==> agent ]
            }
        let subscribe (raw: obj) =
            let call = SyncDelegateCall raw
            let degeneration = DegenerationInterrupts.Cursor call.Delegate
            let report (_: SessionId) outcome =
                let payload =
                    match outcome with
                    | TerminalOutcome.Completed _ -> None
                    | TerminalOutcome.Failed stop -> Some("failed", stop, false)
                    | TerminalOutcome.Aborted stop -> Some("aborted", stop, degeneration.IsOwnAbort())
                payload |> Option.iter (fun (kind, stop, ownAbort) ->
                    let event = createObj [ "kind" ==> kind; "root" ==> DelegateValues.nullable AuthorityRootUserMessageId.value stop.AuthorityRootUserMessageId; "reason" ==> stop.Reason; "ownAbort" ==> ownAbort ]
                    store.Raw?observeTerminal(SessionId.value call.Delegate, event) |> ignore)
            let subscription = deps.SubscribeFutureTerminal call.Delegate report
            fun () -> subscription.Dispose()
        let awaitView (raw: obj) (pending: Task<obj>) =
            let call = SyncDelegateCall raw
            let translated = task { let! result = pending
                                    return DelegateValues.resultFromNative result }
            task {
                let! result = deps.AwaitView (deps.DescribeWait(DelegateCompletion(call.Owner, call.Delegate, call.Role))) translated
                return DelegateValues.resultToNative result
            }
        let awaitInvocation (request: obj) (pending: Task<obj>) =
            let owner = SessionId.create (unbox request?owner)
            let role = DelegateValues.roleOf (unbox request?role)
            let translated = task { let! result = pending
                                    return DelegateValues.invocationFromNative result }
            task {
                let! result = deps.AwaitInvocation (deps.DescribeWait(InvocationJoin(owner, role))) translated
                return DelegateValues.invocationToNative result
            }
        createObj
            [ "attach" ==> attach
              "cleanupDraft" ==> deps.CleanupDelegateDraft
              "replaceEstimate" ==> (fun (session: string) (estimate: obj) -> deps.ReplaceToolEstimate (SessionId.create session) (DelegateValues.optional id estimate))
              "fallbackPrompt" ==> (fun (charge: string) -> box (LlmFacing.instruction charge))
              "combinePrompts" ==> (fun (prompts: obj array) -> prompts |> Array.map unbox<LlmFacing.Document> |> Array.toList |> LlmFacing.combine |> box)
              "resolveAgent" ==> (fun (session: string) -> deps.ResolveBoundAgent (SessionId.create session) |> DelegateValues.nullable id)
              "subscribe" ==> subscribe
              "send" ==> (fun (raw: obj) (charge: string) (prompt: obj) -> task {
                  let request = SyncDelegatePrompt.withProviderPrompt charge (unbox prompt)
                  let! result = deps.SendPrompt (SyncDelegateCall raw) request
                  return DelegateValues.resultToNative result })
              "notePrompt" ==> (fun (session: string) (charge: string) -> deps.NoteDelegatePrompt session charge)
              "awaitView" ==> (fun (call: obj) (pending: Task<obj>) -> awaitView call pending)
              "awaitInvocation" ==> (fun (request: obj) (pending: Task<obj>) -> awaitInvocation request pending) ]

    let private request
        (owner: SessionId)
        (role: SyncDelegateRole)
        (charge: string)
        (expectedToolCalls: int option)
        (batch: SyncDelegateBatch option)
        (prepareProviderPrompt: unit -> Task<LlmFacing.Document>)
        (observeAdmission: (SyncDelegateObservedAdmission -> unit) option)
        (captureResponse: (SyncDelegateTerminalResponse -> unit) option)
        (isCancelled: unit -> bool)
        : obj =
        let batchValue = batch |> Option.map (fun value ->
            createObj [ "providerRun" ==> ProviderRunIdentity.value value.ProviderRun
                        "callOrder" ==> (value.CallOrder |> List.map ToolCallId.value |> List.toArray)
                        "currentCall" ==> ToolCallId.value value.CurrentCall ]) |> Option.defaultValue null
        let request =
            createObj
                [ "owner" ==> SessionId.value owner
                  "scope" ==> ReuseScopeId.value (ReuseScope.ofSession owner)
                  "role" ==> DelegateValues.roleName role
                  "charge" ==> charge
                  "estimate" ==> DelegateValues.nullable id expectedToolCalls
                  "batch" ==> batchValue
                  "prepare" ==> (fun () -> task { let! prompt = prepareProviderPrompt ()
                                                 return box prompt })
                  "observed" ==> observeAdmission.IsSome
                  "observeAdmission" ==> DelegateValues.nullable id observeAdmission
                  "captureResponse" ==> DelegateValues.nullable id captureResponse
                  "isCancelled" ==> isCancelled
                  "noteDraft" ==> (role <> SyncDelegateRole.Coder) ]
        request

    let invoke (store: SyncDelegateCallStore) deps owner role charge expectedToolCalls batch prepareProviderPrompt observeAdmission captureResponse isCancelled : Task<Result<SyncDelegateInvocationResult, string>> =
        let value = request owner role charge expectedToolCalls batch prepareProviderPrompt observeAdmission captureResponse isCancelled
        let pending = DelegateValues.invoke store.Raw value (effects store deps)
        task { let! result = pending
               return DelegateValues.invocationFromNative result }

    let invokeResponse (store: SyncDelegateCallStore) deps owner role charge prepare isCancelled =
        let value = request owner role charge None None prepare None None isCancelled
        DelegateValues.invokeResponse store.Raw value (effects store deps)

    let invokeObserved (store: SyncDelegateCallStore) deps owner charge prepare isCancelled =
        let value = request owner SyncDelegateRole.Engineer charge None None prepare None None isCancelled
        DelegateValues.invokeObserved store.Raw value (effects store deps) (SyncDelegateObservedAdmission.NotDispatched >> box)
