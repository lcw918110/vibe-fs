namespace Wanxiangshu.Repository.Programming.Js

open System.Collections.Generic
open System.Threading.Tasks

type JsDelegationHost =
    { Delegate: string -> obj -> Task<obj>
      Fission: obj -> Task<obj>
      Horizon: unit -> Task<obj>
      Review: obj -> Task<obj> } // (findings)

module JsDelegationBridge =

    // DSL-MUTABLE: resource — sessionId → delegation host
    let private hosts = Dictionary<string, JsDelegationHost>()

    // DSL-MUTABLE: resource — sessionId → ask channel
    let private askChannels = Dictionary<string, string -> Task<Result<unit, string>>>()

    let install (sessionId: string) (host: JsDelegationHost) : unit = hosts.[sessionId] <- host

    let hostFor (sessionId: string) : JsDelegationHost option =
        match hosts.TryGetValue sessionId with
        | true, host -> Some host
        | false, _ -> None

    let installAsk (sessionId: string) (ask: string -> Task<Result<unit, string>>) : unit =
        askChannels.[sessionId] <- ask

    let askFor (sessionId: string) : (string -> Task<Result<unit, string>>) option =
        match askChannels.TryGetValue sessionId with
        | true, ask -> Some ask
        | false, _ -> None

    let clear (sessionId: string) : unit =
        hosts.Remove sessionId |> ignore
        askChannels.Remove sessionId |> ignore

    let clearAll () : unit =
        hosts.Clear()
        askChannels.Clear()
