namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.OpenCode

/// Generated js-* Host boundary. Host schema factories and ToolSpec records are
/// opaque; the registered tool exposes only metadata and bounded execution.
[<RequireQualifiedAccess>]
module JsToolHostSurface =

    type private JsRegisteredToolHandle(spec: ToolSpec, registered: obj) =
        member _.Spec = spec
        member _.Registered = registered

    [<Emit("$0.execute($1, $2)")>]
    let private invokeRegistered (registered: obj) (args: obj) (context: obj) : Task<obj> = jsNative

    [<Emit("$0===undefined||$0===null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("Object.entries($0).map(function (entry) { return [String(entry[0]), String(entry[1])] })")>]
    let private sessionDirectoryEntries (value: obj) : (string * string) array = jsNative

    /// The per-session directories this registration resolves against, mirroring
    /// the plugin's live map (session id → the directory its owner registered).
    let private sessionDirectoryMap (value: obj) : System.Collections.Generic.Dictionary<string, string> =
        let map = System.Collections.Generic.Dictionary<string, string>()

        if not (isNullish value) then
            for key, directory in sessionDirectoryEntries value do
                map.[key] <- directory

        map

    let createRegistered
        (toolModule: obj)
        (role: string)
        (language: string)
        (workspaceRoot: string)
        (store: obj)
        (sessionDirectories: obj)
        : obj =
        match JsGeneratorSurface.typedRole role language with
        | None -> null
        | Some surface ->
            let persistence =
                if isNull store then
                    None
                else
                    Some(JsTransactionSurface.persistenceOf store)

            let lifecyclePersistence =
                if isNull store then
                    None
                else
                    Some(JsTransactionSurface.lifecyclePersistenceOf store)

            let factory = ToolHostCodec.factory toolModule

            let spec =
                let rootOf =
                    if isNullish sessionDirectories then
                        fun _ -> workspaceRoot
                    else
                        let map = sessionDirectoryMap sessionDirectories
                        JsToolPolicy.rootForSession map workspaceRoot

                // This surface carries no host attribution: the delivery witness
                // of a call is settled by the host's own tool registration.
                JsToolSpec.create factory surface workspaceRoot persistence lifecyclePersistence None rootOf (fun _ ->
                    Task.FromResult None) (fun _ -> "0")

            let registered = ToolHostCodec.register factory spec
            box (JsRegisteredToolHandle(spec, registered))

    let name (handle: obj) : string =
        (unbox<JsRegisteredToolHandle> handle).Spec.Name

    let description (handle: obj) : string =
        (unbox<JsRegisteredToolHandle> handle).Spec.Description

    let argumentNames (handle: obj) : string array =
        (unbox<JsRegisteredToolHandle> handle).Spec.Arguments
        |> List.map fst
        |> List.toArray

    let argumentSchema (handle: obj) (name: string) : obj =
        (unbox<JsRegisteredToolHandle> handle).Spec.Arguments
        |> List.tryFind (fun (argumentName, _) -> argumentName = name)
        |> Option.map (fun (_, schema) -> ToolHostCodec.schemaValue schema)
        |> Option.defaultValue null

    let execute (handle: obj) (args: obj) (context: obj) : Task<obj> =
        invokeRegistered (unbox<JsRegisteredToolHandle> handle).Registered args context
