namespace Wanxiangshu.Ablation

open Fable.Core
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module AblationToolMap =

    [<Import("ablationToolMapStore", "wanxiangli/ablation")>]
    let private store: obj = jsNative

    let private loadEntries () : (string * string)[] =
        match AblationManifest.loadToolMap () with
        | Ok document -> document.Tools |> Map.toArray
        | Error _ -> [||]

    let resetCache () : unit = store?resetCache () |> ignore

    let tryNode (toolName: string) : AblationNodeId option =
        let found: string = store?tryNode (toolName, loadEntries)
        found |> Option.ofObj |> Option.map AblationNodeId.create
