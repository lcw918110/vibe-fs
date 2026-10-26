namespace Wanxiangshu.Ablation

open Fable.Core
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module AblationFactMap =

    [<Import("ablationFactMapStore", "wanxiangli/ablation")>]
    let private store: obj = jsNative

    let private loadEntries () : (string * string)[] =
        match AblationManifest.loadFactMap () with
        | Ok document -> document.Facts |> Map.toArray
        | Error _ -> [||]

    let resetCache () : unit = store?resetCache () |> ignore

    let tryNode (factTag: string) : AblationNodeId option =
        let found: string = store?tryNode (factTag, loadEntries)
        found |> Option.ofObj |> Option.map AblationNodeId.create
