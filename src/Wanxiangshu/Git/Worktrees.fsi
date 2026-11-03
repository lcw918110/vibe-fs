namespace Wanxiangshu.Git

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity

[<Class>]
type WorktreeResource =
    member Path: WorktreePath
    member Identity: WorktreeIdentity
    member MarkDurable: unit -> unit
    member Release: unit -> Task<Result<unit, string>>
    interface IAsyncDisposable
    static member FromNative: value: obj -> WorktreeResource

[<Class>]
type GitWorktrees =
    member Native: obj

    member Create:
        identity: WorktreeIdentity * path: WorktreePath * sourceRef: TargetRef -> Task<Result<WorktreeResource, string>>

    member Adopt: identity: WorktreeIdentity * path: WorktreePath -> WorktreeResource
    member Remove: path: WorktreePath -> Task<Result<unit, string>>
    member List: unit -> Task<Result<(WorktreePath * WorktreeIdentity option) list, string>>
    member ListBranches: pattern: string -> Task<Result<WorktreeIdentity list, string>>
    member DeleteBranch: identity: WorktreeIdentity -> Task<Result<unit, string>>
    static member FromNative: value: obj -> GitWorktrees
    static member CreateWithRunner: repo: string * runner: (obj -> Task<obj>) -> GitWorktrees
