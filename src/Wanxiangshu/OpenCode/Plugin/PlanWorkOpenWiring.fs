namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.OpenCode.Host
open FsToolkit.ErrorHandling

// work-036 entry wiring: lifecycle opening owns its separate wanxiangli
// journal. The legacy workspace EventStore is not an admission precondition.
module PlanWorkOpenWiring =

    let private runPlanWorkOpen
        (gitCommonDir: string)
        (root: string)
        (sid: string)
        (observedHead: int64 option)
        : Task<unit> =
        task {
            match!
                Wanxiangshu.Mission.Planning.PlanWorkOpen.ensurePlanWorkOpened gitCommonDir root sid observedHead
            with
            | Ok() -> ()
            | Error err ->
                Diagnostic.emit "plan-work-open-failed" [ "work_id", sid; "error", err ]
                invalidOp ("Plan lifecycle opening failed: " + err)
        }

    let private queryWorktreeRoot (workspace: string) : Task<string> =
        task {
            let! raw = Wanxiangshu.Git.GitSubject.execIn workspace [| "rev-parse"; "--show-toplevel" |]
            return raw.TrimEnd('\r', '\n')
        }

    let private resolveWorktreeRoot (workspaceDirectory: string option) : Task<Result<string, string>> =
        task {
            match
                workspaceDirectory
                |> Option.filter (fun dir -> not (System.String.IsNullOrWhiteSpace dir))
            with
            | None -> return Error "workspace directory is unavailable"
            | Some workspace ->
                let! root = queryWorktreeRoot workspace

                if System.String.IsNullOrWhiteSpace root then
                    return Error "Git did not identify a worktree root"
                else
                    return Ok root
        }

    let ensurePlanWorkOpenedForSession
        (workspaceDirectory: string option)
        (sid: string)
        (observedHead: int64 option)
        : Task<unit> =
        task {
            let! resolved = resolveWorktreeRoot workspaceDirectory

            match resolved with
            | Ok root ->
                // Root is the worktree the session was opened in. The state
                // directory is only where the journal lives; writing it as root
                // makes the same transform's manager opening see a different
                // work and refuse the first prompt.
                let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.initializeStateRoot root
                do! runPlanWorkOpen commonDir root sid observedHead
            | Error reason ->
                Diagnostic.emit "plan-work-open-directory-unavailable" [ "work_id", sid ]
                invalidOp ("Plan lifecycle opening failed: " + reason)
        }
