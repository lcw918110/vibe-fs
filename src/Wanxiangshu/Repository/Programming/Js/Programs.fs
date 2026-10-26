namespace Wanxiangshu.Repository.Programming.Js

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// Host composition only. The library owns every name, run and continuation.
module JsPrograms =
    [<Import("Program", "wanxiangli/program")>]
    let private create: unit -> obj = jsNative

    [<Import("programCallers", "wanxiangli/program")>]
    let private callers: obj = jsNative

    let private owner = create ()

    let forSession (sessionId: string) : obj = owner?session (sessionId)

    let pendingNames (sessionId: string) : string list =
        let session = forSession sessionId
        unbox<string array> (session?pendingNames ()) |> Array.toList

    let noteCaller (sessionId: string) (callId: string) (name: string) : unit =
        callers?noteCaller (sessionId, callId, name) |> ignore

    let clearCaller (sessionId: string) (callId: string) : unit =
        callers?clearCaller (sessionId, callId) |> ignore

    let callerName (sessionId: string) (callId: string) : string option =
        match box (callers?callerName (sessionId, callId)) with
        | :? string as name when not (String.IsNullOrEmpty name) -> Some name
        | _ -> None

    let deliveryBlockersWithChoicesForSession sessionId =
        pendingNames sessionId
        |> List.map (fun name -> name, "unpark(name, value?) or abandon(name)")

    let closeSession (sessionId: string) : Task<string list> =
        task {
            let names = pendingNames sessionId
            let! result = unbox<Task<obj>> (owner?close (sessionId))
            callers?clearSession (sessionId) |> ignore

            match string result?state with
            | "closed" -> return names
            | _ -> return failwith ("program cleanup is incomplete: " + JS.JSON.stringify result)
        }

    let closeAll () : Task<unit> =
        task {
            let sessions: obj array = unbox (owner?inspect ())

            for session in sessions do
                let! _ = closeSession (string session?sessionId)
                ()

            callers?clearAll () |> ignore
        }
