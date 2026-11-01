namespace Wanxiangshu.Git

open System.Threading.Tasks
open Wanxiangshu.Change
open Wanxiangshu.Process

module GitOperations =
    val rebaseInProgress: runner: (Command -> Task<int * string * string>) -> dir: string -> Task<bool>
    val createWithRepo: repoPath: string -> runner: (Command -> Task<int * string * string>) -> GitPort
    val continueRebase: runner: (Command -> Task<int * string * string>) -> dir: string -> Task<Result<unit, string>>
    val stageAll: runner: (Command -> Task<int * string * string>) -> dir: string -> Task<Result<unit, string>>

    val candidateCommit:
        runner: (Command -> Task<int * string * string>) -> dir: string -> msg: string -> Task<Result<unit, string>>
