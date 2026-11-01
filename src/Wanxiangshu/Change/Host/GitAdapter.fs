namespace Wanxiangshu.Change.Host

open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Git
open Wanxiangshu.Process

/// 宿主进程输出适配；候选准备由公开库负责。
module OrchestratorGit =

    let private estimate =
        { EstimatedRuntime = RuntimeSeconds 30.0
          EstimatedOutput = OutputBytes 65536L
          EstimatedMemory = EstimatedMemory.Medium }

    /// spool 保存完整输出，读取后清理文件。
    let private readSpooled (path: string) : Task<string> =
        task {
            try
                let! bytes = Spool.readAllBytes path
                return Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length)
            finally
                Spool.delete path
        }

    let run (cmd: Command) : Task<int * string * string> =
        task {
            let ctx =
                { WorkingDirectory = cmd.WorkingDirectory
                  HardLimit = ProcessEstimate.DefaultHardLimit }

            let! res = ProcessRunner.run cmd estimate ctx CancellationToken.None

            match res with
            | Ok(ProcessOutcome.Completed(code, stdout, stderr, _)) -> return (code, stdout, stderr)
            | Ok(ProcessOutcome.Spooled(code, path, _, _)) ->
                let! stdout = readSpooled path
                return (code, stdout, "")
            | Error err -> return (1, "", sprintf "%A" err)
        }

    let finalizeWorktree
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        GitOperations.prepareWorktree runner worktree (sprintf "candidate: %s" managerId)
