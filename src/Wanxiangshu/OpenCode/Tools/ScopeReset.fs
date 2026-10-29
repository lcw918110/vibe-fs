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

/// Durable ScopeReanchored archive commit. Triggered by scope.kind=resetScope on
/// any tool (common law «Scope»); there is no separate resetScope tool.
module ScopeReset =

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    // scope-progress product boundary (WP0, 2026-10-29): only epoch bookkeeping (reset/ensureEpoch/status.epoch/bind/clear) is live here; estimate record/settle/resetDue are PACKAGE-HISTORICAL. Lifecycle budget = scope-retention.
    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Import("advisorProcessQueue", "wanxiangli/scope-progress")>]
    let private advisorProcessQueue: obj = jsNative

    [<Import("scopeRetention", "wanxiangli/scope-retention")>]
    let private scopeRetention: obj = jsNative

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

    let alreadyCommitted (journalOpt: AgentJournal option) (ctx: HostToolContext) =
        match journalOpt, ctx.ToolCallId with
        | Some journal, Some callId ->
            let state = AgentJournal.snapshot journal
            AgentProjection.tryFind (SessionId.create ctx.SessionId) state.AgentProjections
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.exists (PrefixEpochProjection.isScopeReanchored (ToolCallId.value callId))
        | _ -> false

    /// Commit ScopeReanchored from a resetScope annotation. Seeds the new
    /// retention epoch with `retentionBudget` so the next keepLast does not
    /// see an uninitialized scope.
    let commit
        (journalOpt: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (ctx: HostToolContext)
        (objective: string)
        (oldVerbatim: string)
        (retentionBudget: int)
        : Task<Result<string, string>> =
        task {
            // Verbatim rides in the sealed outbound archive; keep the arg for annotation symmetry.
            let _ = oldVerbatim
            if alreadyCommitted journalOpt ctx then
                return Ok($"Scope reset. Objective: %s{objective}")
            elif Surface.negotiationRequired ctx.SessionId then
                return
                    Error
                        "scope.kind=resetScope is unavailable until the first negotiate(questions) call has been accepted. Call negotiate now; once accepted, resetScope annotation takes effect immediately."
            elif Surface.isPreviousScopePending ctx.SessionId then
                return
                    Error
                        "scope.kind=resetScope is unavailable until askPreviousScope(questions) has been called. Call askPreviousScope now; ordinary tools remain available."
            else
                match ctx.ToolCallId with
                | None -> return Error "scope reset requires an exact tool call id (HOST-011)"
                | Some callId ->
                    match journalOpt with
                    | None ->
                        return Error "scope reset requires a durable agent journal for ScopeReanchored prefix epoch advance"
                    | Some journal ->
                        let rawSnap: obj = outboundViewStore?tryGetSealed (ctx.SessionId)
                        if isNullOrUndefined rawSnap then
                            return Error "scope reset failed: no sealed outbound view snapshot captured for this session."
                        else
                            let archiveJson = string (outboundViewStore?serializeSnapshot (rawSnap))
                            let! blobResult = journal.WriteBlob archiveJson
                            match blobResult with
                            | Error failure ->
                                return Error($"scope reset failed: failed to write archive blob: %s{failure}")
                            | Ok receipt ->
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
                                let! appended =
                                    AgentJournal.appendAgent (StreamId.Session sessionId) ctx.ProviderRunId fact journal
                                match appended with
                                | Error failure -> raise (JournalAppendException failure)
                                | Ok _ -> ()
                                outboundViewStore?discardSealed (ctx.SessionId) |> ignore
                                let nextScopeNumber = Set.count epoch.ReanchoredScopes + 1
                                let nextEpochKey = string nextScopeNumber
                                let budget = max 0 retentionBudget
                                Surface.resetScopeState ctx.SessionId budget
                                scopeProgress?reset (ctx.SessionId, nextEpochKey) |> ignore
                                advisorProcessQueue?reset (ctx.SessionId, nextEpochKey) |> ignore
                                // New retention epoch carries the annotation budget.
                                scopeRetention?reset (ctx.SessionId, nextEpochKey) |> ignore
                                if budget >= 1 then
                                    scopeRetention?seed (ctx.SessionId, budget) |> ignore
                                    Surface.setRetentionBudget ctx.SessionId budget
                                if not (Set.isEmpty epoch.ReanchoredScopes) then
                                    Surface.beginAskPreviousScope ctx.SessionId
                                Surface.setHasPreviousScope ctx.SessionId true
                                let! outstanding = outstandingReminder journal snapshotPort sessionId
                                return Ok($"Scope reset. Objective: %s{objective}" + outstanding)
        }
