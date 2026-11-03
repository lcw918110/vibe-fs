namespace Wanxiangshu.Git

open System.Threading.Tasks
open Wanxiangshu.Change
open Wanxiangshu.Process

module GitOperations =
    val createWithRepo: repoPath: string -> runner: (Command -> Task<int * string * string>) -> GitPort

    val prepareWorktree:
        runner: (Command -> Task<int * string * string>) -> dir: string -> message: string -> Task<Result<unit, string>>
