namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.OpenCode

/// Composition-internal CE module: referenced only by SyncDelegateRuntime.
module internal SyncDelegateWorkflow =

    /// Everything SyncDelegateRuntime.Invoke needs from its host, so the workflow
    /// runs against the store + dependencies without touching runtime fields.
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
          SubscribeFutureTerminal: SessionId -> TerminalCompletionListener -> System.IDisposable }

    let private completeError (invocations: SyncDelegateInvocation list) error =
        for invocation in invocations do
            AsyncSupport.trySetResult invocation.Completion (Error error) |> ignore

    let private completeMergedSiblings (siblings: SyncDelegateInvocation list) (canonicalCall: ToolCallId) =
        for sibling in siblings do
            AsyncSupport.trySetResult sibling.Completion (Ok(SyncDelegateInvocationResult.MergedInto canonicalCall))
            |> ignore

    let private completeUngroupedSiblings (siblings: SyncDelegateInvocation list) =
        for sibling in siblings do
            AsyncSupport.trySetResult
                sibling.Completion
                (Error "sync delegate protocol defect: ungrouped invocation has siblings")
            |> ignore

    let private completeCanonicalBatch
        (canonical: SyncDelegateInvocation)
        (siblings: SyncDelegateInvocation list)
        (view: string)
        =
        AsyncSupport.trySetResult canonical.Completion (Ok(SyncDelegateInvocationResult.View view))
        |> ignore

        match canonical.Batch with
        | Some batch -> completeMergedSiblings siblings (List.head batch.CallOrder)
        | None -> completeUngroupedSiblings siblings

    let private completeBatch (invocations: SyncDelegateInvocation list) answered =
        match answered, invocations with
        | Error error, _ -> completeError invocations error
        | Ok _, [] -> ()
        | Ok view, canonical :: siblings -> completeCanonicalBatch canonical siblings view

    let private failAdmission
        (store: SyncDelegateCallStore)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (invocations: SyncDelegateInvocation list)
        (error: string)
        =
        store.ReleaseAdmission(ownerScope, role)
        completeError invocations error

    let private maybeCleanupDelegateDraft
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        =
        match role with
        | SyncDelegateRole.Inspector
        | SyncDelegateRole.Engineer
        | SyncDelegateRole.DevOps ->
            store.TryTakeDeletedDelegate ownerScope
            |> Option.iter (fun sessionId -> deps.CleanupDelegateDraft(SessionId.value sessionId))
        | SyncDelegateRole.Coder -> ()

    let private sumExpectedToolCalls (invocations: SyncDelegateInvocation list) =
        match invocations |> List.choose (fun item -> item.ExpectedToolCalls) with
        | [] -> None
        | values -> Some(List.sum values)

    let private prepareOnePrompt (item: SyncDelegateInvocation) : Task<LlmFacing.Document> =
        task {
            try
                return! item.PrepareProviderPrompt()
            with
            | error when item.ObserveAdmission.IsSome -> return raise error
            | _ -> return LlmFacing.instruction item.Charge
        }

    let private prepareAllPrompts (invocations: SyncDelegateInvocation list) : Task<LlmFacing.Document list> =
        task {
            // DSL-MUTABLE: algorithm-scratch — prompt result accumulator
            let results = ResizeArray<LlmFacing.Document>()

            for item in invocations do
                let! prompt = prepareOnePrompt item
                results.Add prompt

            return results |> Seq.toList
        }

    let private failCurrentCall
        (store: SyncDelegateCallStore)
        (delegateSession: SessionId)
        (stop: TerminalStop)
        (message: string)
        =
        store.TryPeekCallByDelegate delegateSession
        |> Option.filter (fun call ->
            match call.TerminalFailureScope with
            | Some(FreshAuthorityRoot root) -> TerminalStop.belongsTo root stop
            | Some(ExistingAuthorityContinuation _)
            | None -> false)
        |> Option.bind store.TryPopExactCall
        |> Option.iter (fun current -> store.FailCall(current, message))

    let private onTerminalOutcome
        (store: SyncDelegateCallStore)
        (delegateSession: SessionId)
        (_sessionId: SessionId)
        (outcome: TerminalOutcome)
        =
        match outcome with
        | TerminalOutcome.Failed stop ->
            failCurrentCall store delegateSession stop (sprintf "SyncDelegate run failed: %s" stop.Reason)
        | TerminalOutcome.Aborted stop ->
            failCurrentCall store delegateSession stop (sprintf "SyncDelegate run aborted: %s" stop.Reason)
        | TerminalOutcome.Completed _ -> ()

    let private sendAndAwait
        (deps: Dependencies)
        (store: SyncDelegateCallStore)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (delegateSession: SessionId)
        (call: SyncDelegateCall)
        (request: SyncDelegatePromptRequest)
        : Task<Result<string, string>> =
        taskResult {
            use _terminalSub =
                deps.SubscribeFutureTerminal delegateSession (onTerminalOutcome store delegateSession)

            do! deps.SendPrompt call request

            if
                role = SyncDelegateRole.Inspector
                || role = SyncDelegateRole.Engineer
                || role = SyncDelegateRole.DevOps
            then
                deps.NoteDelegatePrompt (SessionId.value delegateSession) request.Charge

            let! view =
                deps.AwaitView
                    (deps.DescribeWait(DelegateCompletion(batchOwner, delegateSession, role)))
                    call.Answer.Task

            return view
        }

    let private dispatchBegunCall
        (deps: Dependencies)
        (store: SyncDelegateCallStore)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (delegateSession: SessionId)
        (invocations: SyncDelegateInvocation list)
        (combinedCharge: string)
        (combinedProviderPrompt: LlmFacing.Document)
        (begun: SyncDelegateCall * System.IDisposable)
        : Task<unit> =
        task {
            let call, registration = begun
            use _registration = registration

            let request =
                SyncDelegatePrompt.withProviderPrompt combinedCharge combinedProviderPrompt

            let! answered = sendAndAwait deps store role batchOwner delegateSession call request
            store.ReleaseAdmission(ownerScope, role)
            registration.Dispose()
            completeBatch invocations answered
        }

    let private beginAndDispatch
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (delegateSession: SessionId)
        (sendAgent: string)
        (invocations: SyncDelegateInvocation list)
        (combinedCharge: string)
        (combinedProviderPrompt: LlmFacing.Document)
        : Task<unit> =
        match store.BeginCall(batchOwner, ownerScope, role, delegateSession, sendAgent, invocations) with
        | Error error ->
            failAdmission store ownerScope role invocations error
            Task.FromResult(())
        | Ok begun ->
            dispatchBegunCall
                deps
                store
                ownerScope
                role
                batchOwner
                delegateSession
                invocations
                combinedCharge
                combinedProviderPrompt
                begun

    let private afterAttached
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (invocations: SyncDelegateInvocation list)
        (delegateSession: SessionId)
        (attachedAgent: string)
        : Task<unit> =
        task {
            do! deps.ReplaceToolEstimate delegateSession (sumExpectedToolCalls invocations)
            let! preparedPrompts = prepareAllPrompts invocations

            let combinedCharge =
                invocations |> List.map (fun item -> item.Charge) |> String.concat "\n\n"

            let combinedProviderPrompt = preparedPrompts |> LlmFacing.combine

            let sendAgent =
                deps.ResolveBoundAgent delegateSession |> Option.defaultValue attachedAgent

            if invocations |> List.exists (fun invocation -> invocation.IsCancelled()) then
                failAdmission store ownerScope role invocations "Sync delegate call was cancelled before dispatch"
            else
                do!
                    beginAndDispatch
                        store
                        deps
                        ownerScope
                        role
                        batchOwner
                        delegateSession
                        sendAgent
                        invocations
                        combinedCharge
                        combinedProviderPrompt
        }

    let private runAttachedPair
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (invocations: SyncDelegateInvocation list)
        (attached: SessionId * string)
        : Task<unit> =
        let delegateSession, attachedAgent = attached
        afterAttached store deps ownerScope role batchOwner invocations delegateSession attachedAgent

    let private acquireAndRun
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (agentName: string)
        (invocations: SyncDelegateInvocation list)
        : Task<unit> =
        task {
            let! attached =
                deps.Attached.GetOrCreate(
                    batchOwner,
                    role,
                    agentName,
                    deps.Directory,
                    deps.ObserveChild,
                    deps.CreateChild,
                    deps.BindChild,
                    deps.OnDelegateReady
                )

            match attached with
            | Error error -> failAdmission store ownerScope role invocations error
            | Ok pair -> do! runAttachedPair store deps ownerScope role batchOwner invocations pair
        }

    let private runReadyWithRole
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (batchOwner: SessionId)
        (invocations: SyncDelegateInvocation list)
        : Task<unit> =
        let agentName = SyncDelegate.agentNameFor role

        task {
            try
                do! acquireAndRun store deps ownerScope role batchOwner agentName invocations
            with ex ->
                failAdmission store ownerScope role invocations ex.Message
        }

    let private runReadyBatch
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerScope: ReuseScopeId)
        (role: SyncDelegateRole)
        (invocations: SyncDelegateInvocation list)
        : Task<unit> =
        let first = List.head invocations
        let batchOwner = first.Owner
        maybeCleanupDelegateDraft store deps ownerScope role

        runReadyWithRole store deps ownerScope role batchOwner invocations

    /// EXEC-026 / EXEC-031: reusable SyncDelegate CE. A semantic batch is fixed by
    /// ProviderRun tool-call membership before dispatch; scheduler timing never
    /// changes which invocations are concatenated.
    let invoke
        (store: SyncDelegateCallStore)
        (deps: Dependencies)
        (ownerSessionId: SessionId)
        (role: SyncDelegateRole)
        (charge: string)
        (expectedToolCalls: int option)
        (batch: SyncDelegateBatch option)
        (prepareProviderPrompt: unit -> Task<LlmFacing.Document>)
        (observeAdmission: (SyncDelegateObservedAdmission -> unit) option)
        (captureResponse: (SyncDelegateTerminalResponse -> unit) option)
        (isCancelled: unit -> bool)
        : Task<Result<SyncDelegateInvocationResult, string>> =
        task {
            let owner = ownerSessionId
            let ownerScope = ReuseScope.ofSession owner

            let completion =
                TaskCompletionSource<Result<SyncDelegateInvocationResult, string>>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let invocation: SyncDelegateInvocation =
                { Owner = owner
                  OwnerScope = ownerScope
                  Role = role
                  Charge = charge
                  ExpectedToolCalls = expectedToolCalls
                  PrepareProviderPrompt = prepareProviderPrompt
                  ObserveAdmission = observeAdmission
                  CaptureResponse = captureResponse
                  IsCancelled = isCancelled
                  Batch = batch
                  Completion = completion
                  StartCursor = None }

            match store.Admit invocation with
            | SyncDelegateAdmission.Rejected error -> AsyncSupport.trySetResult completion (Error error) |> ignore
            | SyncDelegateAdmission.Waiting -> ()
            | SyncDelegateAdmission.Ready invocations -> do! runReadyBatch store deps ownerScope role invocations

            return! deps.AwaitInvocation (deps.DescribeWait(InvocationJoin(owner, role))) completion.Task
        }
