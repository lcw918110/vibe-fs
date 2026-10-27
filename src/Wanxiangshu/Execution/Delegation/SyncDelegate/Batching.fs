namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Host.Domain
open Wanxiangshu.Participant.Provider

[<RequireQualifiedAccess>]
module SyncDelegateBatchRules =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let MergedReference = "tool/sync-delegate/merged-reference"

    let private batchOfMessage providerRun role currentCall (message: SessionMessage) =
        let callOrder =
            message.ToolParts
            |> Array.choose (fun part ->
                part.ToolName
                |> SyncDelegate.tryRoleOfToolName
                |> Option.filter (fun partRole -> partRole = role)
                |> Option.map (fun _ -> part.ToolCallId))
            |> Array.toList

        if callOrder |> List.exists (fun callId -> callId = currentCall) then
            Some
                { ProviderRun = providerRun
                  CallOrder = callOrder
                  CurrentCall = currentCall }
        else
            None

    let private batchOfSnapshotMessages providerRun role currentCall messages =
        let providerRunKey = ProviderRunIdentity.value providerRun

        messages
        |> List.tryFind (fun message -> message.Id = providerRunKey)
        |> Option.bind (batchOfMessage providerRun role currentCall)

    let private tryReadMessages (snapshot: ISessionSnapshotPort) (owner: SessionId) : Task<SessionMessage list option> =
        task {
            match! snapshot.GetMessages owner with
            | Error _ -> return None
            | Ok messages -> return Some messages
        }

    let private resolveFromSnapshot
        (snapshot: ISessionSnapshotPort option)
        (owner: SessionId)
        (providerRun: ProviderRunIdentity)
        (role: SyncDelegateRole)
        (currentCall: ToolCallId)
        : Task<SyncDelegateBatch option> =
        task {
            match snapshot with
            | None -> return None
            | Some snapshot ->
                let! messages = tryReadMessages snapshot owner
                return messages |> Option.bind (batchOfSnapshotMessages providerRun role currentCall)
        }

    let private callKey (callId: ToolCallId) = ToolCallId.value callId

    let private isPrefix (left: ToolCallId list) (right: ToolCallId list) =
        let leftKeys = left |> List.map callKey
        let rightKeys = right |> List.map callKey

        leftKeys.Length <= rightKeys.Length
        && leftKeys = (rightKeys |> List.take leftKeys.Length)

    let private longerBatch (observedBatch: SyncDelegateBatch) (snapshotBatch: SyncDelegateBatch) : SyncDelegateBatch =
        if observedBatch.CallOrder.Length >= snapshotBatch.CallOrder.Length then
            observedBatch
        else
            snapshotBatch

    let private moreCompleteBatch
        (observed: SyncDelegateBatch option)
        (snapshot: SyncDelegateBatch option)
        : SyncDelegateBatch option =
        match observed, snapshot with
        | None, None -> None
        | Some batch, None
        | None, Some batch -> Some batch
        | Some observedBatch, Some snapshotBatch when isPrefix observedBatch.CallOrder snapshotBatch.CallOrder ->
            Some snapshotBatch
        | Some observedBatch, Some snapshotBatch when isPrefix snapshotBatch.CallOrder observedBatch.CallOrder ->
            Some observedBatch
        | Some observedBatch, Some snapshotBatch -> Some(longerBatch observedBatch snapshotBatch)

    let private resolveBatch
        (runtime: SyncDelegateRuntime)
        (snapshot: ISessionSnapshotPort option)
        (owner: SessionId)
        (providerRun: ProviderRunIdentity)
        (role: SyncDelegateRole)
        (currentCall: ToolCallId)
        : Task<SyncDelegateBatch option> =
        task {
            let observed = runtime.TryObservedBatch(owner, providerRun, role, currentCall)
            let! snapshotBatch = resolveFromSnapshot snapshot owner providerRun role currentCall
            return moreCompleteBatch observed snapshotBatch
        }

    let resolve
        (runtime: SyncDelegateRuntime)
        (snapshot: ISessionSnapshotPort option)
        (role: SyncDelegateRole)
        (caller: DelegationCaller)
        =
        task {
            match caller.Session, caller.ProviderRunId, caller.ToolCallId with
            | Some owner, Some providerRun, Some currentCall ->
                return! resolveBatch runtime snapshot owner providerRun role currentCall
            | _ -> return None
        }

    let private mergedInstruction language canonicalCall =
        ProviderProse.render language Path.MergedReference (Map [ "call", ToolCallId.value canonicalCall ])

    type private DeferredCall =
        { CallId: ToolCallId
          Charge: string
          Keywords: string
          Estimate: int option }

    module private SyncDelegateDeferredInterop =
        [<Emit("$0 === null || $0 === undefined")>]
        let isAbsent (_value: obj) : bool = jsNative

        [<Import("deferredInspectionStore", "wanxiangli/delegate-batch")>]
        let deferredStore: obj = jsNative

    [<Emit("Promise.all($0)")>]
    let private promiseAll (promises: Task<'T> array) : Task<'T array> = jsNative

    /// One charge's result instruction: the delivered view, the merged canonical
    /// call, or the failure prose. The three cases are the invocation result's
    /// own shape, not three separate decisions.
    let private renderInvocationOutput
        (language: ProviderLanguage)
        (result: Result<SyncDelegateInvocationResult, string>)
        : string =
        match result with
        | Ok(SyncDelegateInvocationResult.View view) -> ReturnedMaterial.render view
        | Ok(SyncDelegateInvocationResult.MergedInto canonicalCall) -> mergedInstruction language canonicalCall
        | Error err -> sprintf "Engineer charge failed: %s" err

    let stageDeferredInspection
        (sessionId: string)
        (callId: ToolCallId)
        (charge: string)
        (keywords: string)
        (estimate: int option)
        : string =
        let estObj: obj =
            match estimate with
            | Some e -> box e
            | None -> null

        let res: obj =
            SyncDelegateDeferredInterop.deferredStore?stage(
                sessionId,
                ToolCallId.value callId,
                charge,
                keywords,
                estObj
            )

        let isOk =
            not (SyncDelegateDeferredInterop.isAbsent res)
            && unbox<bool> res?ok

        if not isOk then
            let reason =
                if SyncDelegateDeferredInterop.isAbsent res then "unknown"
                else string res?reason
            failwith (sprintf "Failed to stage deferred inspection: %s" reason)
        else
            sprintf "Engineer charge accepted and deferred for batch execution: %s" charge

    let settleDeferredInspections (runtime: SyncDelegateRuntime) (sessionId: string) : Task<unit> =
        task {
            let items: obj array =
                unbox<obj array> (SyncDelegateDeferredInterop.deferredStore?takePending sessionId)

            if items.Length > 0 then
                let calls =
                    items
                    |> Array.map (fun item ->
                        let est =
                            if SyncDelegateDeferredInterop.isAbsent item?estimate then
                                None
                            else
                                Some (unbox<int> item?estimate)
                        { CallId = ToolCallId.create (unbox<string> item?callId)
                          Charge = unbox<string> item?charge
                          Keywords = unbox<string> item?keywords
                          Estimate = est })
                    |> Array.toList

                let callOrder = calls |> List.map (fun c -> c.CallId)
                let combinedCharge = calls |> List.map (fun c -> c.Charge) |> String.concat "\n"

                let invoke (call: DeferredCall) =
                    let batch =
                        { ProviderRun = ProviderRunIdentity.create "deferred-batch"
                          CallOrder = callOrder
                          CurrentCall = call.CallId }

                    let preparePrompt () =
                        Task.FromResult(LlmFacing.instructions (calls |> List.map (fun c -> c.Charge)))

                    runtime.InvokeBatchPrepared(
                        sessionId,
                        SyncDelegateRole.Engineer,
                        combinedCharge,
                        batch,
                        preparePrompt,
                        ?expectedToolCalls = call.Estimate
                    )

                let tasks = calls |> List.map invoke
                let! results = promiseAll (List.toArray tasks)
                let lang = ProviderLanguageBinding.forSessionText sessionId

                for i in 0 .. calls.Length - 1 do
                    let rendered = renderInvocationOutput lang results.[i]
                    SyncDelegateDeferredInterop.deferredStore?recordReplacedResult(
                        ToolCallId.value calls.[i].CallId,
                        rendered,
                        sessionId
                    ) |> ignore
        }

    let hasReplacedResults () : bool =
        unbox<bool> (SyncDelegateDeferredInterop.deferredStore?hasReplacedResults())

    let tryReplacedResult (callId: string) : string option =
        let repObj: obj = SyncDelegateDeferredInterop.deferredStore?tryReplacedResult callId
        if isNull repObj || SyncDelegateDeferredInterop.isAbsent repObj then
            None
        else
            Some (unbox<string> repObj)

    let clearSession (sessionId: string) : unit =
        SyncDelegateDeferredInterop.deferredStore?clearSession sessionId |> ignore

    let clearAll () : unit =
        SyncDelegateDeferredInterop.deferredStore?clearAll() |> ignore
