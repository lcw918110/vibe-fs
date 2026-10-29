namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.TerminalSignal

module HostSignalSubscribeSurface =

    let recordInterrupt (sessionId: string) =
        DegenerationInterrupts.record (SessionId.create sessionId)

    let refuseInterrupt (sessionId: string) =
        DegenerationInterrupts.unrecord (SessionId.create sessionId)

    let forgetInterrupts (sessionId: string) =
        DegenerationInterrupts.forget (SessionId.create sessionId)

    let interruptCursor (sessionId: string) : unit -> bool =
        let cursor = DegenerationInterrupts.Cursor(SessionId.create sessionId)
        fun () -> cursor.IsOwnAbort()

    [<Emit("typeof $0 === 'function'")>]
    let private isFunction (value: obj) : bool = jsNative

    let private errorMessage =
        function
        | HostSignalSubscribe.HostSignalSubscriptionError.InvalidInput -> "OPENCODE-SIGNAL-SUBSCRIBE: invalid input"
        | HostSignalSubscribe.HostSignalSubscriptionError.EventsListenUnavailable ->
            "OPENCODE-SIGNAL-SUBSCRIBE: events.listen unavailable"
        | HostSignalSubscribe.HostSignalSubscriptionError.EventsListenReturnedInvalidDisposer ->
            "OPENCODE-SIGNAL-SUBSCRIBE: events.listen returned invalid disposer"
        | HostSignalSubscribe.HostSignalSubscriptionError.EventsListenFailed diagnostic ->
            $"OPENCODE-SIGNAL-SUBSCRIBE: events.listen failed: {diagnostic}"

    let private translate =
        function
        | Error error ->
            box
                {| ok = false
                   error = errorMessage error |}
        | Ok HostSignalSubscribe.HostSignalSubscriptionMode.LocalEventHook ->
            box
                {| ok = true
                   mode = "LocalEventHook"
                   dispose = null |}
        | Ok(HostSignalSubscribe.HostSignalSubscriptionMode.EventsListen subscription) ->
            box
                {| ok = true
                   mode = "EventsListen"
                   dispose = box (fun () -> (subscription :> IDisposable).Dispose()) |}

    let trySubscribe (input: obj) (onSignalEvent: obj) : Task<obj> =
        if not (isFunction onSignalEvent) then
            Task.FromResult(
                box
                    {| ok = false
                       error = "OPENCODE-SIGNAL-SUBSCRIBE: callback unavailable" |}
            )
        else
            task {
                let! result = HostSignalSubscribe.trySubscribe input (unbox<obj -> unit> onSignalEvent)
                return translate result
            }
