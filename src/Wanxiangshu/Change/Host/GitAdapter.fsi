namespace Wanxiangshu.Change.Host

open System.Threading.Tasks
open Wanxiangshu.Process

module OrchestratorGit =
    val run: cmd: Command -> Task<int * string * string>

    val finalizeWorktree:
        runner: (Command -> Task<int * string * string>) ->
        managerId: string ->
        worktree: string ->
            Task<Result<unit, string>>
