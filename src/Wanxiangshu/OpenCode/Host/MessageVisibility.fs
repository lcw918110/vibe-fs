namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module private MessageVisibilityWiring =

    [<Import("MessageVisibilityHub", "wanxiangli/terminal")>]
    let private messageVisibilityHubCtor: obj = jsNative

    let createHub (timerPort: ITimerPort) : obj =
        let delayFactory = fun (ms: int) -> timerPort.Delay ms :> obj
        createNew messageVisibilityHubCtor delayFactory

/// host-boundary-008 event-driven projection catch-up.
/// State management and deadline coordination are fully owned by wanxiangli/terminal.
type MessageVisibilityHub(timerPort: ITimerPort) =
    let inner: obj = MessageVisibilityWiring.createHub timerPort

    /// Wake every waiter whose session just published a message lifecycle event.
    member _.Notify(sessionId: SessionId) : unit =
        inner?Notify(SessionId.value sessionId)

    /// Resolves on the next `message.updated` for the session, or when the
    /// deadline budget expires without one. A settled waiter never stays registered.
    member _.AwaitChange (sessionId: SessionId) (budgetMilliseconds: int) : Task<unit> =
        inner?AwaitChange(SessionId.value sessionId, budgetMilliseconds)

    /// Pending waiter count for one session — leak-detection probe.
    member _.PendingCount(sessionId: SessionId) : int =
        inner?PendingCount(SessionId.value sessionId)

/// Raw-event intake for the hub: any `message.updated` is a potential
/// projection change for its session.
[<RequireQualifiedAccess>]
module MessageVisibilitySignal =

    let observeEvent (hub: MessageVisibilityHub) (rawInput: obj) : unit =
        let raw = HostEventEnvelope.unwrap rawInput

        if HostEventEnvelope.eventTypeOf raw = "message.updated" then
            HostEventEnvelope.tryMessageSessionId raw |> Option.iter hub.Notify
