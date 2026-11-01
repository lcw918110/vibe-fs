namespace Wanxiangshu.Change.Host

open System.Threading.Tasks
open Wanxiangshu.Change
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git

/// Remove worktrees and branches no active ManagerJob owns.
///
/// Cleanup only. It never derives a recovery action from what it finds on disk —
/// ORCH-007 forbids substituting filesystem state for a durable fact, and the
/// active-job set here comes from the projection.
module OrchestratorSweep =
    let private removeOne git path (cont: Task<Result<unit, string>>) =
        task {
            match! git.Worktrees.Remove path with
            | Ok() -> return! cont
            | Error error ->
                return Error(sprintf "stale worktree cleanup failed for %s: %s" (WorktreePath.value path) error)
        }

    let private removeWorktrees git paths =
        let rec loop remaining =
            task {
                match remaining with
                | [] -> return Ok()
                | path :: tail -> return! removeOne git path (loop tail)
            }

        loop paths

    let private deleteOne git identity (cont: Task<Result<unit, string>>) =
        task {
            match! git.Worktrees.DeleteBranch identity with
            | Ok() -> return! cont
            | Error error ->
                return
                    Error(
                        sprintf "stale manager branch cleanup failed for %s: %s" (WorktreeIdentity.value identity) error
                    )
        }

    let private deleteBranches git identities =
        let rec loop remaining =
            task {
                match remaining with
                | [] -> return Ok()
                | identity :: tail -> return! deleteOne git identity (loop tail)
            }

        loop identities

    let private ownedIdentities (activeJobs: ManagerJobProjection list) =
        activeJobs |> List.map (fun job -> WorktreeIdentity.value job.WorktreeIdentity)

    let private afterRemoveWorktrees (git: GitPort) (owned: string list) : Task<Result<unit, string>> =
        task {
            match! git.Worktrees.ListBranches("manager/*") with
            | Error error -> return Error(sprintf "cannot list manager branches for cleanup: %s" error)
            | Ok branches ->
                let stale =
                    GitPorcelain.staleBranches (branches |> List.map WorktreeIdentity.value) owned
                    |> List.map WorktreeIdentity.create

                return! deleteBranches git stale
        }

    /// Which worktrees and branches are stale is wanxiangli/git-porcelain's
    /// `staleWorktrees` / `staleBranches` (CUT-23 ②): only manager branches whose
    /// short name no active job owns, compared whole — the MAIN working tree
    /// (`branch refs/heads/main`) and detached worktrees are never ours to remove
    /// (measured on Host 1.18.9: removing the main tree killed every orchestrator
    /// canary in init). This module only performs the removals, in order.
    let private removeStaleWorktrees
        (git: GitPort)
        (owned: string list)
        (entries: (WorktreePath * WorktreeIdentity option) list)
        : Task<Result<unit, string>> =
        task {
            let stale =
                GitPorcelain.staleWorktrees
                    (entries
                     |> List.map (fun (path, identity) ->
                         WorktreePath.value path, identity |> Option.map WorktreeIdentity.value))
                    owned
                |> List.map WorktreePath.create

            match! removeWorktrees git stale with
            | Error error -> return Error error
            | Ok() -> return! afterRemoveWorktrees git owned
        }

    let sweepStaleArtifacts (git: GitPort) (activeJobs: ManagerJobProjection list) : Task<Result<unit, string>> =
        task {
            let owned = ownedIdentities activeJobs

            match! git.Worktrees.List() with
            | Error error -> return Error(sprintf "cannot list worktrees for cleanup: %s" error)
            | Ok entries -> return! removeStaleWorktrees git owned entries
        }

    let sweepLocked
        (lockPath: string)
        (git: GitPort)
        (activeJobs: unit -> ManagerJobProjection list)
        : Task<Result<unit, string>> =
        task {
            let! gate = IntegrationGate.acquire lockPath

            let! outcome =
                task {
                    try
                        return! sweepStaleArtifacts git (activeJobs ())
                    with ex ->
                        return Error ex.Message
                }

            do! gate.Release()
            return outcome
        }
