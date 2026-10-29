namespace Wanxiangshu.Process

open System
open System.Threading
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.AsyncSupport

/// Single-holder large-process gate delegated to wanxiangli/runtime.
module LargeGate =

    [<Import("largeGate", "wanxiangli/runtime")>]
    let private gate: obj = jsNative

    let getCount () : int = unbox<int> (gate?getCount ())

    let private disposeReg (reg: IDisposable) : unit =
        if not (isNull reg) then
            reg.Dispose()

    let private createCanceledTask () : Task =
        let tcs = TaskCompletionSource<unit>()
        trySetCanceled tcs |> ignore
        tcs.Task :> Task

    let private enqueueWaiter (ct: CancellationToken) : Task =
        let tcs = TaskCompletionSource<unit>()
        // DSL-MUTABLE: cancellation — registration owned by this queued acquisition
        let mutable reg: IDisposable = null

        let onGrant () =
            disposeReg reg
            trySetResult tcs () |> ignore

        let cancel: unit -> unit = unbox (gate?enqueue (onGrant))

        let onCancel () =
            cancel ()
            trySetCanceled tcs |> ignore

        reg <- ct.Register(onCancel)
        tcs.Task :> Task

    let private acquireActive (ct: CancellationToken) : Task =
        if unbox<bool> (gate?tryAcquire ()) then
            Task.FromResult() :> Task
        else
            enqueueWaiter ct

    let acquire (ct: CancellationToken) : Task =
        if ct.IsCancellationRequested then
            createCanceledTask ()
        else
            acquireActive ct

    let release () : unit = gate?release () |> ignore
