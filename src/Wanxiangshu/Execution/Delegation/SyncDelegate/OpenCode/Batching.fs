namespace Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open ToolHostCodec

[<RequireQualifiedAccess>]
module SyncDelegateBatching =

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
        (context: HostToolContext)
        =
        task {
            match context.ProviderRunId, context.ToolCallId with
            | Some providerRun, Some currentCall when not (String.IsNullOrWhiteSpace context.SessionId) ->
                let owner = SessionId.create context.SessionId
                return! resolveBatch runtime snapshot owner providerRun role currentCall
            | _ -> return None
        }

    let mergedInstruction language canonicalCall =
        ProviderProse.render language MergedReference (Map [ "call", ToolCallId.value canonicalCall ])

    type private DeferredCall =
        { CallId: ToolCallId
          Charge: string
          Keywords: string
          Estimate: int option }

    let private pendingInspections = Dictionary<string, ResizeArray<DeferredCall>>()
    let private durableReplacedResults = Dictionary<string, string>()

    [<Emit("Promise.all($0)")>]
    let private promiseAll (promises: Task<'T> array) : Task<'T array> = jsNative

    let private formatInvocationOutput
        (lang: ProviderLanguage)
        (res: Result<SyncDelegateInvocationResult, string>)
        : string =
        match res with
        | Ok(SyncDelegateInvocationResult.WorkRecord workRecord) -> tomlObjectWithInstructions [ workRecord ] []
        | Ok(SyncDelegateInvocationResult.MergedInto canonicalCall) ->
            tomlObjectWithInstructions [ mergedInstruction lang canonicalCall ] []
        | Error err -> tomlObjectWithInstructions [ sprintf "Engineer charge failed: %s" err ] []

    let private recordSingleResult
        (lang: ProviderLanguage)
        (call: DeferredCall)
        (res: Result<SyncDelegateInvocationResult, string>)
        =
        let outputText = formatInvocationOutput lang res
        durableReplacedResults.[ToolCallId.value call.CallId] <- outputText

    let private recordCallResults
        (lang: ProviderLanguage)
        (calls: DeferredCall list)
        (results: Result<SyncDelegateInvocationResult, string>[])
        =
        lock durableReplacedResults (fun () ->
            List.iteri (fun i call -> recordSingleResult lang call results.[i]) calls)

    let private updatePartOutput (part: obj) (replacement: string) =
        if not (isNull part?state) then
            part?state?output <- replacement

    let private tryReplaceToolPart (part: obj) =
        let callId = string (part?callID)
        let found, replacement = durableReplacedResults.TryGetValue callId

        if found then
            updatePartOutput part replacement

    let private replacePartState (part: obj) =
        if not (isNull part) && string (part?``type``) = "tool" then
            tryReplaceToolPart part

    let private extractToolCallId (msg: obj) : string =
        if not (isNull msg?tool_call_id) then
            string (msg?tool_call_id)
        elif not (isNull msg?toolCallId) then
            string (msg?toolCallId)
        else
            ""

    let private replaceMessageContent (msg: obj) =
        let callId = extractToolCallId msg
        let found, replacement = durableReplacedResults.TryGetValue callId

        if found then
            msg?content <- replacement

    let private processSingleMessage (msg: obj) =
        if not (isNull msg) && not (isNull msg?parts) then
            let parts = unbox<obj array> msg?parts
            Array.iter replacePartState parts
        elif not (isNull msg) && string (msg?role) = "tool" then
            replaceMessageContent msg

    let stageDeferredInspection
        (sessionId: string)
        (callId: ToolCallId)
        (charge: string)
        (keywords: string)
        (estimate: int option)
        : string =
        lock pendingInspections (fun () ->
            let list =
                match pendingInspections.TryGetValue sessionId with
                | true, existing -> existing
                | false, _ ->
                    let created = ResizeArray<DeferredCall>()
                    pendingInspections.[sessionId] <- created
                    created

            list.Add
                { CallId = callId
                  Charge = charge
                  Keywords = keywords
                  Estimate = estimate }

            tomlObjectWithInstructions
                [ sprintf "Engineer charge accepted and deferred for batch execution: %s" charge ]
                [])

    let settleDeferredInspections
        (runtime: SyncDelegateRuntime)
        (workspaceDirectory: string option)
        (sessionId: string)
        : Task<unit> =
        task {
            let callsOpt =
                lock pendingInspections (fun () ->
                    match pendingInspections.TryGetValue sessionId with
                    | true, list when list.Count > 0 ->
                        let copy = list |> Seq.toList
                        pendingInspections.Remove sessionId |> ignore
                        Some copy
                    | _ -> None)

            match callsOpt with
            | None -> ()
            | Some calls ->
                let callOrder = calls |> List.map (fun c -> c.CallId)
                let combinedCharge = calls |> List.map (fun c -> c.Charge) |> String.concat "\n"

                let firstCall = List.head calls

                let tasks =
                    calls
                    |> List.map (fun call ->
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
                        ))

                let! results = promiseAll (List.toArray tasks)
                let lang = ProviderLanguageBinding.forSessionText sessionId
                recordCallResults lang calls results
        }

    let applyReplacedResults (messages: obj list) : obj list =
        lock durableReplacedResults (fun () ->
            if durableReplacedResults.Count = 0 then
                messages
            else
                List.iter processSingleMessage messages
                messages)
