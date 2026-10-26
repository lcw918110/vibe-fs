namespace Wanxiangshu.Repository.Programming.Js

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// Host composition only. The library owns every name, run and continuation.
module JsPrograms =
    [<Import("Program", "wanxiangli/program")>]
    let private create: unit -> obj = jsNative

    let private owner = create ()

    let forSession (sessionId: string) : obj = owner?session (sessionId)

    let pendingNames (sessionId: string) : string list =
        let session = forSession sessionId
        unbox<string array> (session?pendingNames ()) |> Array.toList

    let private callers = Dictionary<string, string>()

    let private callerKey sessionId callId = sessionId + "\n" + callId

    let noteCaller sessionId callId name =
        if not (String.IsNullOrEmpty callId) && not (String.IsNullOrEmpty name) then
            callers[callerKey sessionId callId] <- name

    let clearCaller sessionId callId =
        callers.Remove(callerKey sessionId callId) |> ignore

    let callerName sessionId callId =
        match callers.TryGetValue(callerKey sessionId callId) with
        | true, name -> Some name
        | _ -> None

    let deliveryBlockersWithChoicesForSession sessionId =
        pendingNames sessionId
        |> List.map (fun name -> name, "unpark(name, value?) or abandon(name)")

    let closeSession (sessionId: string) : Task<string list> =
        task {
            let names = pendingNames sessionId
            let! result = unbox<Task<obj>> (owner?close (sessionId))

            if string result?state <> "closed" then
                return failwith ("program cleanup is incomplete: " + JS.JSON.stringify result)
            else
                return names
        }

    let closeAll () : Task<unit> =
        task {
            let sessions: obj array = unbox (owner?inspect ())

            for session in sessions do
                let! _ = closeSession (string session?sessionId)
                ()
        }
