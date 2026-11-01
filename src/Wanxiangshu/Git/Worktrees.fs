namespace Wanxiangshu.Git

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

type private NativeWorktree =
    abstract path: string
    abstract identity: string
    abstract markDurable: unit -> unit
    abstract release: unit -> Task<obj>
    abstract dispose: unit -> Task<unit>

type private NativeWorktrees =
    abstract create: identity: string * path: string * sourceRef: string -> Task<obj>
    abstract adopt: identity: string * path: string -> obj
    abstract remove: path: string -> Task<obj>
    abstract list: unit -> Task<obj>
    abstract listBranches: pattern: string -> Task<obj>
    abstract deleteBranch: identity: string -> Task<obj>

module private WorktreeInterop =
    [<Import("GitWorktrees", "wanxiangli/git")>]
    let constructor: obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let create (_constructor: obj) (_repo: string) (_runner: obj -> Task<obj>) : NativeWorktrees = jsNative

    let result (decode: obj -> 'T) (value: obj) : Result<'T, string> =
        if value?ok then
            Ok(decode value?value)
        else
            Error(value?error)

#if FABLE_COMPILER
    [<Emit("$0")>]
    let asValueTask (_operation: Task<unit>) : ValueTask = jsNative
#else
    let asValueTask (operation: Task<unit>) = ValueTask(operation)
#endif

/// 仅转换主体身份类型；资源状态和清理行为由库持有。
type WorktreeResource private (native: NativeWorktree) =
    member _.Path = WorktreePath.create native.path
    member _.Identity = WorktreeIdentity.create native.identity
    member _.MarkDurable() = native.markDurable ()

    member _.Release() =
        task {
            let! result = native.release ()
            return WorktreeInterop.result ignore result
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            native.dispose () |> WorktreeInterop.asValueTask

    static member FromNative(value: obj) = WorktreeResource(unbox value)

/// 公开库对象与主体类型之间的无状态适配。
type GitWorktrees private (native: NativeWorktrees) =
    member _.Native = box native

    member _.Create(identity: WorktreeIdentity, path: WorktreePath, sourceRef: TargetRef) =
        task {
            let! result =
                native.create (WorktreeIdentity.value identity, WorktreePath.value path, TargetRef.value sourceRef)

            return WorktreeInterop.result WorktreeResource.FromNative result
        }

    member _.Adopt(identity: WorktreeIdentity, path: WorktreePath) =
        native.adopt (WorktreeIdentity.value identity, WorktreePath.value path)
        |> WorktreeResource.FromNative

    member _.Remove(path: WorktreePath) =
        task {
            let! result = native.remove (WorktreePath.value path)
            return WorktreeInterop.result ignore result
        }

    member _.List() =
        task {
            let! result = native.list ()

            return
                WorktreeInterop.result
                    (fun value ->
                        unbox<obj array> value
                        |> Array.map (fun item ->
                            let identity: string = item?identity
                            WorktreePath.create item?path, Option.ofObj identity |> Option.map WorktreeIdentity.create)
                        |> Array.toList)
                    result
        }

    member _.ListBranches(pattern: string) =
        task {
            let! result = native.listBranches pattern

            return
                WorktreeInterop.result
                    (fun value -> unbox<string array> value |> Array.map WorktreeIdentity.create |> Array.toList)
                    result
        }

    member _.DeleteBranch(identity: WorktreeIdentity) =
        task {
            let! result = native.deleteBranch (WorktreeIdentity.value identity)
            return WorktreeInterop.result ignore result
        }

    static member FromNative(value: obj) = GitWorktrees(unbox value)

    static member CreateWithRunner(repo: string, runner: obj -> Task<obj>) =
        GitWorktrees(WorktreeInterop.create WorktreeInterop.constructor repo runner)
