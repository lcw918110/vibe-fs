namespace Wanxiangshu.Git

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Change
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process

type private NativePublication =
    abstract dirtyPaths: path: string -> Task<obj>
    abstract currentBranch: unit -> Task<obj>
    abstract readHead: path: string -> Task<obj>
    abstract targetHead: target: string -> Task<obj>
    abstract rebaseInProgress: path: string -> Task<bool>
    abstract rebase: path: string * target: string -> Task<obj>
    abstract conflictedPaths: path: string -> Task<obj>
    abstract prepare: path: string * message: string -> Task<obj>
    abstract fastForward: request: obj -> Task<obj>

/// 只转换主体身份、结果和命令载体；物理发布顺序由库负责。
module GitOperations =

    [<Import("GitPublication", "wanxiangli/git")>]
    let private constructor: obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let private createNative (_constructor: obj) (_repo: string) (_runner: obj -> Task<obj>) : NativePublication =
        jsNative

    let private adaptRunner (runner: Command -> Task<int * string * string>) (request: obj) =
        task {
            let cwd: string = request?cwd
            let args: string array = request?args

            let! code, stdout, stderr =
                runner
                    { FileName = GitSubject.Executable
                      Arguments = Array.toList args
                      WorkingDirectory = Option.ofObj cwd
                      Environment = None
                      Stdin = None
                      PtyOptions = None }

            return
                box
                    {| code = code
                       stdout = stdout
                       stderr = stderr |}
        }

    let private result (decode: obj -> 'T) (value: obj) : Result<'T, string> =
        let error () =
            let code: string = value?code

            if code = "target-moved" then
                OrchestratorConstants.targetRefMovedError
            else
                value?error

        if value?ok then Ok(decode value?value) else Error(error ())

    let private strings value =
        unbox<string array> value |> Array.toList

    let private commit value = CommitHash.create (unbox<string> value)

    let prepareWorktree (runner: Command -> Task<int * string * string>) dir message =
        task {
            let native = createNative constructor dir (adaptRunner runner)
            let! prepared = native.prepare (dir, message)
            return result ignore prepared
        }

    let createWithRepo repoPath (runner: Command -> Task<int * string * string>) : GitPort =
        let native = createNative constructor repoPath (adaptRunner runner)

        { IsDirty =
            fun path ->
                task {
                    let! inspected = native.dirtyPaths (WorktreePath.value path)
                    return result strings inspected
                }
          Worktrees = GitWorktrees.CreateWithRunner(repoPath, adaptRunner runner)
          FreezeTargetBranch =
            fun () ->
                task {
                    let! branch = native.currentBranch ()
                    return result (unbox<string> >> TargetRef.create) branch
                }
          Rebase =
            fun path target ->
                task {
                    let! rebased = native.rebase (WorktreePath.value path, TargetRef.value target)
                    return result ignore rebased
                }
          ConflictedFiles =
            fun path ->
                task {
                    let! conflicted = native.conflictedPaths (WorktreePath.value path)
                    return result strings conflicted
                }
          FfMerge =
            fun path target expectedHead candidate ->
                task {
                    let! published =
                        native.fastForward (
                            box
                                {| worktree = WorktreePath.value path
                                   target = TargetRef.value target
                                   expectedHead = CommitHash.value expectedHead
                                   candidate = CommitHash.value candidate |}
                        )

                    return result commit published
                }
          HasRebaseHead = fun path -> native.rebaseInProgress (WorktreePath.value path)
          ReadHead =
            fun path ->
                task {
                    let! head = native.readHead (WorktreePath.value path)
                    return result commit head
                }
          GetTargetHead =
            fun target ->
                task {
                    let! head = native.targetHead (TargetRef.value target)
                    return result commit head
                } }
