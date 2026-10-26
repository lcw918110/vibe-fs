namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports

module Events =

    /// A fresh terminal source. The product owns the terminal facts; this is
    /// the only adapter-side place that names the product's implementation, and
    /// it names it only to obtain an instance — never to hold one.
    let newTerminalSource () : ISessionTerminalSource = TerminalWiring.createSource ()

    let private notifyOne (listener: HostSignal -> Task) (signal: HostSignal) : Task =
        task {
            try
                do! listener signal
            with _ ->
                ()
        }

    [<Fable.Core.Import("SignalBroadcaster", "wanxiangli/ownership")>]
    let private signalBroadcasterClass: obj = jsNative

    [<Fable.Core.Emit("new $0()")>]
    let private createSignalBroadcaster (constructor: obj) : obj = jsNative

    /// HNI P-07: host-neutral signal source for OpenCode host events.
    type HostSignalSource() =
        let broadcaster = createSignalBroadcaster signalBroadcasterClass

        member _.Publish(signal: HostSignal) : Task =
            task {
                let snapshot: (HostSignal -> Task) array =
                    Fable.Core.JsInterop.(?) broadcaster "snapshot" ()

                for listener in snapshot do
                    do! notifyOne listener signal
            }

        interface ISignalSource with
            member _.Subscribe(listener: HostSignal -> Task) : IDisposable =
                let subscription: obj = Fable.Core.JsInterop.(?) broadcaster "subscribe" listener
                { new IDisposable with
                    member _.Dispose() =
                        Fable.Core.JsInterop.(?) subscription "dispose" () }

    /// The one event port.
    ///
    /// The terminal facts, the sticky cache and the listener list live in the
    /// terminal registry (`wanxiangli/terminal`): the
    /// host does not own a terminal outcome, it only observes one. This port is
    /// the host-facing face of that registry — it holds no second copy of the
    /// sticky cache, of the idempotence cache or of the listener registry, so
    /// there is exactly one truth about a terminal fact.
    ///
    /// The port implements the shared `ISessionTerminalSource` contract; the
    /// product's registry is handed in, never constructed here.
    type HostEventPort(terminalSource: ISessionTerminalSource) =

        interface IEventObservationPort with
            member _.SubscribeTerminalListener(listener) =
                terminalSource.SubscribeTerminalListener(listener)

            member _.SubscribeFutureTerminalListener(listener) =
                terminalSource.SubscribeFutureTerminalListener(listener)

            member _.NotifyTerminal sessionId outcome =
                terminalSource.NotifyTerminal sessionId outcome

        interface ISessionTerminalSource with
            member _.SubscribeTerminal(sessionId, listener) =
                terminalSource.SubscribeTerminal(sessionId, listener)

            member _.SubscribeFutureTerminal(sessionId, listener) =
                terminalSource.SubscribeFutureTerminal(sessionId, listener)

            member _.HasSubscriber(sessionId) = terminalSource.HasSubscriber(sessionId)
