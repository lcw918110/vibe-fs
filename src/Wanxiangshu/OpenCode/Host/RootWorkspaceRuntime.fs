namespace Wanxiangshu.OpenCode

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.HostSession

module private RootWorkspaceInterop =
    [<Import("RootWorkspaceCell", "wanxiangli/work")>]
    let cellClass: obj = jsNative

    [<Import("rootWorkspaceCell", "wanxiangli/work")>]
    let sharedCell: obj = jsNative

type RootWorkspaceRuntime internal (cell: obj) =

    let reader =
        { new IRootWorkspaceReader with
            member _.TryRead() = cell?tryRead() |> unbox<string> |> Option.ofObj }

    let binder =
        { new IRootWorkspaceBinder with
            member _.TryBind candidate = cell?tryBind(candidate |> Option.defaultValue null) }

    new() = RootWorkspaceRuntime(emitJsExpr RootWorkspaceInterop.cellClass "new $0()")

    member _.Binder = binder
    member _.Reader = reader

module RootWorkspaceProcess =
    let private runtime = RootWorkspaceRuntime(RootWorkspaceInterop.sharedCell)

    let local () = runtime
