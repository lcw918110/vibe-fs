namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.Journal.AgentJournal
open Wanxiangshu.Context.Scope
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Composition.Durable
open Fable.Core
open Fable.Core.JsInterop

module ResetScopeTool =

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Import("advisorProcessQueue", "wanxiangli/scope-progress")>]
    let private advisorProcessQueue: obj = jsNative

    [<Emit("$0 == null")>]
    let private isNullOrUndefined (x: obj) : bool = jsNative

    let private pendingChildHandles (journal: AgentJournal) (sessionId: SessionId) =
        AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind (fun session -> session.Handles)
        |> Option.map (fun projection -> projection.Handles |> Map.toList |> List.map snd)
        |> Option.defaultValue []
        |> List.filter (fun handle -> handle.Lifecycle = Wanxiangshu.Execution.Delegation.HandleLifecycle.Active)
        |> List.sortBy (fun handle -> handle.CreationOrder)

    let private latestAcceptedPrompt (snapshot: ISessionSnapshotPort) (child: SessionId) : Task<string option> =
        task {
            let! fetched = snapshot.GetMessages child
            return
                fetched
                |> Result.toOption
                |> Option.bind (fun messages ->
                    messages
                    |> List.filter (fun message -> message.Role = "user" && message.PromptKey.IsSome)
                    |> List.tryLast)
                |> Option.map (fun message ->
                    message.Parts
                    |> Array.choose (function
                        | MessagePart.Text text -> Some text
                        | _ -> None)
                    |> String.concat "\n")
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
        }

    let private outstandingPromptNotice (snapshotPort: ISessionSnapshotPort option) (handle: HandleRecord) : Task<string> =
        task {
            let! prompt =
                snapshotPort
                |> Option.map (fun snapshot -> latestAcceptedPrompt snapshot handle.ChildSessionId)
                |> Option.defaultValue (Task.FromResult None)
            let text =
                prompt
                |> Option.defaultValue "最近一次派出指令全文：Host 没有提供可验证的已接受提示词快照。"
            return "## " + handle.Byname + " · " + SessionId.value handle.ChildSessionId
                   + "\n" + text
        }

    let rec private collectOutstanding (snapshotPort: ISessionSnapshotPort option) handles : Task<string list> =
        task {
            match handles with
            | [] -> return []
            | head :: tail ->
                let! current = outstandingPromptNotice snapshotPort head
                let! remaining = collectOutstanding snapshotPort tail
                return current :: remaining
        }

    let private outstandingReminder (journal: AgentJournal) (snapshotPort: ISessionSnapshotPort option) (sessionId: SessionId) : Task<string> =
        task {
            let pending = pendingChildHandles journal sessionId
            let! notices = collectOutstanding snapshotPort pending
            if List.isEmpty notices then return ""
            else return "\n\n尚未归队的外派工作（最近一次已接受的指令全文）：\n" + String.concat "\n\n" notices
        }

    let private commitArchivedScope
        (journalOpt: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (ctx: HostToolContext)
        (objective: string)
        (rawSnap: obj)
        : Task<string> =
        task {
            let callId =
                match ctx.ToolCallId with
                | Some id -> id
                | None -> failwith "resetScope requires an exact tool call id in host context (HOST-011)"

            let journal =
                match journalOpt with
                | Some j -> j
                | None ->
                    failwith "resetScope requires a durable agent journal for ScopeReanchored prefix epoch advance"

            let archiveJson = string (outboundViewStore?serializeSnapshot (rawSnap))
            let! blobResult = journal.WriteBlob archiveJson

            let finalize (receipt: BlobWriteReceipt) =
                task {
                    let sessionId = SessionId.create ctx.SessionId
                    let journalSnapshot = AgentJournal.snapshot journal

                    let epoch =
                        AgentProjection.tryFind sessionId journalSnapshot.AgentProjections
                        |> Option.bind (fun session -> session.PrefixEpoch)
                        |> Option.defaultValue PrefixEpochProjection.empty

                    let nextEpoch = PrefixEpochId.next epoch.EpochId

                    let fact =
                        ContextFact.ScopeReanchored
                            {| SessionId = sessionId
                               PreviousEpochId = epoch.EpochId
                               NextEpochId = nextEpoch
                               CallId = callId
                               ArchiveRef = Some receipt.BlobRef
                               ArchiveDigest = Some receipt.BlobDigest |}

                    let! appended = AgentJournal.appendAgent (StreamId.Session sessionId) ctx.ProviderRunId fact journal

                    match appended with
                    | Ok _ -> ()
                    | Error failure -> raise (JournalAppendException failure)

                    outboundViewStore?discardSealed (ctx.SessionId) |> ignore
                    Surface.resetScopeState ctx.SessionId 0
                    let nextScopeNumber = Set.count epoch.ReanchoredScopes + 1
                    scopeProgress?reset(ctx.SessionId, string nextScopeNumber) |> ignore
                    advisorProcessQueue?reset(ctx.SessionId, string nextScopeNumber) |> ignore

                    if not (Set.isEmpty epoch.ReanchoredScopes) then
                        Surface.beginAskPreviousScope ctx.SessionId

                    Surface.setHasPreviousScope ctx.SessionId true
                    let! outstanding = outstandingReminder journal snapshotPort sessionId
                    return $"Scope reset. Objective: %s{objective}" + outstanding
                }

            match blobResult with
            | Error failure -> return $"resetScope failed: failed to write archive blob: %s{failure}"
            | Ok receipt -> return! finalize receipt
        }

    let private executeCommitted
        (journalOpt: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let objective = args.Text "newScopeObjective"
            let rawSnap: obj = outboundViewStore?tryGetSealed(ctx.SessionId)
            if isNullOrUndefined rawSnap then
                return "resetScope failed: no sealed outbound view snapshot captured for this session."
            else
                return! commitArchivedScope journalOpt snapshotPort ctx objective rawSnap
        }

    let private resetAlreadyCommitted (journalOpt: AgentJournal option) (ctx: HostToolContext) =
        match journalOpt, ctx.ToolCallId with
        | Some journal, Some callId ->
            let state = AgentJournal.snapshot journal

            AgentProjection.tryFind (SessionId.create ctx.SessionId) state.AgentProjections
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.exists (PrefixEpochProjection.isScopeReanchored (ToolCallId.value callId))
        | _ -> false

    let private execute (journalOpt: AgentJournal option) (snapshotPort: ISessionSnapshotPort option) (args: HostToolArguments) (ctx: HostToolContext) : Task<string> =
        if resetAlreadyCommitted journalOpt ctx then
            Task.FromResult("Scope reset. Objective: " + args.Text "newScopeObjective")
        elif Surface.negotiationRequired ctx.SessionId then
            Task.FromResult(
                "resetScope is unavailable until the first negotiate(questions) reply has arrived as a user steer. Ask the delegator clarifying questions now, and continue while the reply is pending."
            )
        elif Surface.isPreviousScopePending ctx.SessionId then
            Task.FromResult(
                "resetScope is unavailable until askPreviousScope(questions) has been called to clarify questions with the previous scope. Call askPreviousScope now; ordinary tools remain available."
            )
        else
            executeCommitted journalOpt snapshotPort args ctx

    let spec (factory: HostToolFactory) (journal: AgentJournal option) (snapshotPort: ISessionSnapshotPort option) : ToolSpec =
        { Name = Surface.ResetScopeTool
          Description =
            "End the current Scope, preserve operator and available assistant prose/reasoning plus critical consultation calls/results, and remove ordinary old tool process. Return accepted outstanding delegation prompts."
          Arguments =
            [ "oldScopeVerboseVerbatimExperience",
              ToolHostCodec.optionalStringSchemaDescribed
                  "Write out your experience and findings from the closing scope, verbosely: everything between the previous resetScope call (or session start) and this call. Any exact text needed in the next scope (such as function signatures, error stack traces, or exact line numbers) MUST be copied VERBATIM here. Null or omitted if there is no prior work."
                  factory
              "newScopeObjective",
              ToolHostCodec.stringSchemaDescribed
                  "The objective for the new scope. The scope must form a self-contained state closed-loop (状态闭环)."
                  factory ]
          Execute = execute journal snapshotPort }
