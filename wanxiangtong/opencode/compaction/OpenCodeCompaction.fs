namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Persistence.Journal

/// One plugin instance shares compaction hooks, snapshot observation and history bypass.
[<AttachMembers>]
type OpenCodeCompaction(journal: AgentJournal option) =
    let active = HashSet<string>()
    let overflowed = HashSet<string>()
    let startupGate = obj ()
    let mutable startupObserved = false
    let mutable settingGap: CompactionSetting option = None

    let sessionOf (input: obj) =
        if isNull input || isNull input?sessionID then "<unknown>"
        else unbox<string> input?sessionID

    let write (output: obj) field (value: obj) =
        if not (isNull output) then output?(field) <- value

    let markOverflow isTemporary session =
        if isTemporary session then
            overflowed.Add session |> ignore
            true
        else false

    let abortTemporary (client: obj) sessionId =
        let session = if isNull client then null else client?session
        if not (isNull session) then
            task {
                try
                    let action: obj = session?abort
                    if not (isNull action) then
                        let payload = createObj [ "sessionID" ==> sessionId; "path" ==> createObj [ "id" ==> sessionId ]; "body" ==> createObj [] ]
                        let! _ = unbox<Task<obj>> (emitJsExpr (session, action, payload) "$1.call($0, $2)")
                        ()
                with _ -> ()
            } |> ignore<Task<unit>>

    let observeStartup session messages =
        if lock startupGate (fun () -> not startupObserved) then
            let window =
                messages |> List.map (fun (message: SessionMessage) ->
                    { CompletedAssistant = message.Role = "assistant" && message.Completed
                      Compaction = HostCompactionPolicy.isContainableCompaction message.IsCompaction })
            match HostCompactionPolicy.firstTurnCompactionRuns window with
            | None -> ()
            | Some count ->
                let verdict = HostCompactionPolicy.judgeFirstTurn settingGap session count
                let claimed = lock startupGate (fun () ->
                    if startupObserved then false
                    else
                        startupObserved <- true
                        true)
                if claimed then
                    match verdict with
                    | CompactionGateVerdict.Satisfied -> ()
                    | failed -> raise (InvalidOperationException(HostCompactionPolicy.describeVerdict failed))

    member _.RecordSettingGap(gap: CompactionSetting option) = settingGap <- gap
    member _.IsCompacting(session: string) = active.Contains session
    member _.TemporaryOverflowed(session: string) = overflowed.Contains session
    member _.FinishTemporary(session: string) = overflowed.Remove session |> ignore
    member _.ForgetSession(session: string) = active.Remove session |> ignore

    member _.TryConsumeHistoryBypass(input: obj, output: obj) =
        let direct = if isNull input || isNull input?sessionID then "" else string input?sessionID
        let session =
            if not (String.IsNullOrWhiteSpace direct) then Some direct
            elif isNull output || isNull output?messages || unbox<int> output?messages?length = 0 then None
            else
                let first = output?messages?(0)
                let raw = if isNull first?sessionID then first?info?sessionID else first?sessionID
                if isNull raw then None else Some(string raw)
        match session with
        | Some id when not (String.IsNullOrWhiteSpace id) -> active.Remove id
        | _ -> false

    member _.OnCompacting(product: ICompactionGate, isTemporary: string -> bool, client: obj, input: obj, output: obj) : Task<unit> =
        task {
            let session = sessionOf input
            if markOverflow isTemporary session then
                Diagnostic.emit "temporary_consultation_overflow" [ "session_id", session; "result", "consultation-ends" ]
                abortTemporary client session
            else
                Diagnostic.emit "compaction_started" [ "session_id", session; "result", "cannot-refuse" ]
                active.Add session |> ignore
                let! contribution = product.OnCompacting(SessionId.create session)
                match contribution with
                | CompactionContribution.Nothing -> ()
                | CompactionContribution.Context lines ->
                    let existing = if isNull output || isNull output?context then [||] else unbox<string array> output?context
                    write output "context" (box (Array.append existing (List.toArray lines)))
                | CompactionContribution.ReplacePrompt prompt -> write output "prompt" (box prompt)
        }

    member _.OnAutoContinue(product: ICompactionGate, isTemporary: string -> bool, input: obj, output: obj) : Task<unit> =
        task {
            let session = sessionOf input
            if markOverflow isTemporary session then write output "enabled" (box false)
            else
                let! verdict = product.AutoContinue(SessionId.create session)
                match verdict with
                | ContinueVerdict.Allow -> write output "enabled" (box true)
                | ContinueVerdict.Deny _ -> write output "enabled" (box false)
        }

    member _.ObserveSnapshot(session: SessionId, messages: SessionMessage list) : Task =
        task {
            observeStartup session messages
            match journal with
            | None -> ()
            | Some durable ->
                let observed = messages |> List.filter (fun message -> HostCompactionPolicy.isContainableCompaction message.IsCompaction)
                                        |> List.map (fun message -> ProviderRunIdentity.create message.Id)
                if not (List.isEmpty observed) then
                    match! PrefixReanchor.observed durable session observed with
                    | Ok _ -> ()
                    | Error reason -> Diagnostic.emit "reanchor_failed" [ "session_id", SessionId.value session; "result", reason ]
        }
