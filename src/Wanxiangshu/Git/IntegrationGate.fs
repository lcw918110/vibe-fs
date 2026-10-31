namespace Wanxiangshu.Git

open System
open System.IO
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host

module private IntegrationGateDisposal =
    [<Emit("$0.release()")>]
    let release (lease: obj) : Task<unit> = jsNative

#if FABLE_COMPILER
    [<Emit("$0")>]
    let asValueTask (operation: Task) : ValueTask = jsNative
#else
    let asValueTask (operation: Task) = ValueTask(operation)
#endif

/// Cross-process publish serialization represented as one disposable resource.
type IntegrationGate(lease: obj) =
    member _.Release() = IntegrationGateDisposal.release lease

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            IntegrationGateDisposal.asValueTask (this.Release())

module IntegrationGate =

    [<Import("acquire", "wanxiangli/file-lock")>]
    let private acquireLease (path: string) (wait: obj) : Task<obj> = jsNative

    let lockPath (repoPath: string) (branch: string) =
        let key = HostDigest.sha256Hex (repoPath + "\u0000" + branch)
        Path.Combine(Path.GetTempPath(), sprintf "wanxiangshu-publish-%s" key)

    let acquire (path: string) : Task<IntegrationGate> =
        task {
            try
                let! lease =
                    acquireLease
                        path
                        (createObj
                            [ "kind" ==> "Retry"
                              "retries" ==> 50
                              "minTimeoutMs" ==> 50
                              "maxTimeoutMs" ==> 250
                              "factor" ==> 1.1 ])

                return IntegrationGate lease
            with ex ->
                return
                    raise (InvalidOperationException(sprintf "publish lock acquire failed for %s: %s" path ex.Message))
        }
