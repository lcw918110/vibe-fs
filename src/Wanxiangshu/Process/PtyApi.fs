namespace Wanxiangshu.Process

open System
open System.Text
open Fable.Core
open Fable.Core.JsInterop

/// PTY id minting, byte encoding and the cross-runtime parent-abort registry.
module Pty =
    let bytes (text: string) = Encoding.UTF8.GetBytes text

    let newId () =
        PtyId("pty-" + Guid.NewGuid().ToString("N").Substring(0, 8))

    [<Import("parentAbortRegistry", "wanxiangli/ownership")>]
    let private parentAbortRegistry: obj = jsNative

    let registerParentAbort (parentId: string) (abort: unit -> unit) : int =
        unbox<int> (parentAbortRegistry?register(parentId, abort))

    let unregisterParentAbort (parentId: string) (token: int) : unit =
        parentAbortRegistry?unregister(parentId, token) |> ignore

    let abortParent (parentId: string) : unit =
        parentAbortRegistry?abortParent(parentId) |> ignore
