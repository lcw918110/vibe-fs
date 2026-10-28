namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

module private JoinAttemptLibrary =
    [<Import("joinAttemptRegistry", "wanxiangli/handle-lifecycle")>]
    let joinAttemptRegistry: obj = jsNative

open JoinAttemptLibrary

[<AttachMembers>]
type private HostJoinAttempt(interrupt: JoinInterrupt, unregister: unit -> unit) =
    member _.Wait: Task<JoinInterruptReason> = interrupt.Wait

    member _.SignalOperatorAbort() =
        interrupt.Signal JoinInterruptReason.OperatorAbort

    member _.SignalUserMessage() =
        interrupt.Signal JoinInterruptReason.UserMessageArrived

    member _.SignalDeadline() =
        interrupt.Signal JoinInterruptReason.DeadlineExpired

    interface IDisposable with
        member _.Dispose() = unregister ()

[<AttachMembers>]
type private HostJoinAttempts() =
    let activeInterrupts = Dictionary<string, JoinInterrupt>()

    member _.Begin(sessionId: SessionId, toolCallId: ToolCallId option) : obj =
        let sid = SessionId.value sessionId

        let aid =
            match toolCallId with
            | Some tid -> ToolCallId.value tid
            | None -> Guid.NewGuid().ToString("N")

        joinAttemptRegistry?acquire (sid, aid) |> ignore
        let completion = TaskCompletionSource<JoinInterruptReason>()

        let interrupt =
            { Wait = completion.Task
              Signal = fun reason -> AsyncSupport.trySetResult completion reason |> ignore }

        activeInterrupts.[sid] <- interrupt

        box (
            new HostJoinAttempt(
                interrupt,
                (fun () ->
                    joinAttemptRegistry?release (sid, aid) |> ignore
                    activeInterrupts.Remove sid |> ignore)
            )
        )

    member _.SignalVisibleUserMessage(sessionId: SessionId, messageId: PhysicalUserMessageId) =
        let sid = SessionId.value sessionId
        let mid = PhysicalUserMessageId.value messageId
        let verdict: obj = joinAttemptRegistry?signalVisibleUserMessage (sid, mid)

        let signalInterrupt () =
            match activeInterrupts.TryGetValue sid with
            | true, interrupt -> interrupt.Signal JoinInterruptReason.UserMessageArrived
            | false, _ -> ()

        if not (isNull verdict) && unbox<bool> verdict?woken then
            signalInterrupt ()

    member _.ClearSession(sessionId: SessionId) =
        let sid = SessionId.value sessionId
        joinAttemptRegistry?clearSession (sid) |> ignore
        activeInterrupts.Remove sid |> ignore

module HostJoinAttemptFactory =
    let create () : obj = box (new HostJoinAttempts())
