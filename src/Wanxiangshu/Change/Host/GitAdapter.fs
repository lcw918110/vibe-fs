namespace Wanxiangshu.Change.Host

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Git
open Wanxiangshu.Process

/// 宿主进程输出适配；候选准备由公开库负责。
module OrchestratorGit =

    [<Import("runProcess", "wanxiangli/runtime")>]
    let private runProcess (request: obj) : Task<obj> = jsNative

    [<RequireQualifiedAccess>]
    type private ProcessError =
        | TimeoutExceeded of duration: TimeSpan
        | SpawnFailed of reason: string
        | ProcessCancelled of reason: string
        | ExecutionFailed of reason: string

    let private errorOf (value: obj) : ProcessError =
        match unbox<string> value?kind with
        | "TimeoutExceeded" -> ProcessError.TimeoutExceeded(TimeSpan.FromMilliseconds(unbox<float> value?durationMs))
        | "SpawnFailed" -> ProcessError.SpawnFailed(unbox value?reason)
        | "ProcessCancelled" -> ProcessError.ProcessCancelled(unbox value?reason)
        | "ExecutionFailed" -> ProcessError.ExecutionFailed(unbox value?reason)
        | kind -> invalidOp $"unknown process error kind: {kind}"

    let private requestOf (cmd: Command) : obj =
        let environment =
            cmd.Environment
            |> Option.map (fun values -> values |> Map.toSeq |> Seq.map (fun (name, value) -> name, box value) |> createObj)
            |> Option.defaultValue null

        createObj
            [ "command"
              ==> createObj
                  [ "fileName" ==> cmd.FileName
                    "args" ==> List.toArray cmd.Arguments
                    "cwd" ==> Option.toObj cmd.WorkingDirectory
                    "env" ==> environment
                    "stdin" ==> Option.toObj cmd.Stdin ]
              "estimate"
              ==> createObj [ "runtimeSeconds" ==> 30.0; "outputBytes" ==> 65536L; "memory" ==> "medium" ]
              "context" ==> createObj [ "hardLimitMs" ==> 3600000.0 ] ]

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
            let! result = runProcess (requestOf cmd)

            if not (unbox<bool> result?ok) then
                return (1, "", sprintf "%A" (errorOf result?error))
            else
                let outcome: obj = result?value
                let code: int = unbox outcome?exitCode

                match unbox<string> outcome?kind with
                | "Completed" -> return (code, unbox outcome?stdout, unbox outcome?stderr)
                | "Spooled" ->
                    let! stdout = readSpooled (unbox outcome?spoolPath)
                    return (code, stdout, "")
                | kind -> return invalidOp $"unknown process outcome kind: {kind}"
        }

    let finalizeWorktree
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        GitOperations.prepareWorktree runner worktree (sprintf "candidate: %s" managerId)
