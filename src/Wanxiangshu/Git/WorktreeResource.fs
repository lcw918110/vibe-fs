namespace Wanxiangshu.Git

open System
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Change
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process

module private WorktreeDisposal =
#if FABLE_COMPILER
    [<Emit("$0")>]
    let asValueTask (operation: Task) : ValueTask = jsNative
#else
    let asValueTask (operation: Task) = ValueTask(operation)
#endif

/// Owned manager worktree. Release is idempotent; DisposeAsync performs the
/// same physical cleanup when a program exits before publish.
///
/// `Identity` is the stable name (the `manager/<job>` branch) and `Path` is where
/// it currently lives. ORCH-006 keeps both for exactly this reason: recovery
/// locates by identity, diagnostics show the path, and a moved worktree must not
/// orphan its job.
type WorktreeResource
    private (path: WorktreePath, identity: WorktreeIdentity, git: GitPort, releaseOnDisposeInitially: bool) =
    // DSL-MUTABLE: resource — one-shot worktree release latch
    let mutable released = false
    // DSL-MUTABLE: resource — durable-mark flag for worktree dispose policy
    let mutable releaseOnDispose = releaseOnDisposeInitially

    member _.Path = path
    member _.Identity = identity
    member _.MarkDurable() = releaseOnDispose <- false

    member private _.ReleaseOutcome worktree branchResult =
        match worktree, branchResult with
        | Ok(), Ok() ->
            released <- true
            Ok()
        | Error left, Error right -> Error(sprintf "worktree=%s; branch=%s" left right)
        | Error error, _ -> Error("worktree=" + error)
        | _, Error error -> Error("branch=" + error)

    member this.Release() =
        task {
            if released then
                return Ok()
            else
                let! worktree = git.RemoveWorktree path
                let! branchResult = git.DeleteBranch identity
                return this.ReleaseOutcome worktree branchResult
        }

    member private this.DisposeRelease() =
        WorktreeDisposal.asValueTask (
            task {
                try
                    let! _ = this.Release()
                    ()
                with _ ->
                    ()
            }
        )

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            if releaseOnDispose then
                this.DisposeRelease()
            else
                WorktreeDisposal.asValueTask (AsyncSupport.completedTask ())

    static member Create(git: GitPort, jobId: ManagerJobId, path: WorktreePath) =
        task {
            match! git.CreateWorktree jobId path with
            | Error error -> return Error error
            | Ok identity -> return Ok(WorktreeResource(path, identity, git, true))
        }

    /// Re-adopt an existing worktree during recovery (ORCH-007: never create a new
    /// one). The identity comes from the durable job record, not from re-deriving it
    /// from the id — a job whose branch was renamed must still be found.
    static member Adopt(git: GitPort, identity: WorktreeIdentity, path: WorktreePath) =
        WorktreeResource(path, identity, git, false)

/// Process-backed worktree verbs used by GitOperations.
///
/// `repo` is bound once by `GitOperations.createWithRepo`; no verb takes it as a
/// per-call argument, so a caller cannot address a repository the port was not
/// built for.
module WorktreeCommands =

    /// The branch a job's worktree lives on IS its stable identity (ORCH-006).
    ///
    /// Spelled once. It used to appear in `WorktreeResource.Create`, `Adopt` and
    /// `create` — three copies of one naming rule, and `Adopt` re-derived it during
    /// recovery instead of reading the durable record.
    let identityOf (jobId: ManagerJobId) =
        WorktreeIdentity.create (sprintf "manager/%s" (ManagerJobId.value jobId))

    let private command cwd args =
        { FileName = "git"
          Arguments = args
          WorkingDirectory = cwd
          Environment = None
          Stdin = None
          PtyOptions = None }

    let private outcome code stdout stderr =
        if code = 0 then
            Ok()
        else
            Error(if String.IsNullOrWhiteSpace stderr then stdout else stderr)

    let private failure stdout stderr =
        if String.IsNullOrWhiteSpace stderr then stdout else stderr

    let isDirty (runner: Command -> Task<int * string * string>) (path: WorktreePath) =
        task {
            let! code, stdout, stderr = runner (command (Some(WorktreePath.value path)) [ "status"; "--porcelain" ])

            if code <> 0 then
                return Error(failure stdout stderr)
            else
                return Ok(not (String.IsNullOrWhiteSpace stdout))
        }

    let create (runner: Command -> Task<int * string * string>) repo (jobId: ManagerJobId) (path: WorktreePath) =
        task {
            let identity = identityOf jobId

            let! code, stdout, stderr =
                runner (
                    command
                        (Some repo)
                        [ "worktree"
                          "add"
                          WorktreePath.value path
                          "-b"
                          WorktreeIdentity.value identity ]
                )

            // Returns the identity rather than unit: it is what recovery locates the
            // worktree by, and a caller that had to re-derive it would be a second
            // copy of `identityOf`.
            return outcome code stdout stderr |> Result.map (fun () -> identity)
        }

    let remove (runner: Command -> Task<int * string * string>) (path: WorktreePath) =
        task {
            let! code, stdout, stderr =
                runner (command None [ "worktree"; "remove"; "--force"; WorktreePath.value path ])

            return outcome code stdout stderr
        }

    let private finishWorktree acc currentPath currentBranch =
        match currentPath with
        | Some path -> List.rev ((path, currentBranch) :: acc)
        | None -> List.rev acc

    let private parseList lines =
        let rec loop acc currentPath currentBranch rest =
            match rest with
            | [] -> finishWorktree acc currentPath currentBranch
            | "" :: tail ->
                let next =
                    currentPath |> Option.map (fun path -> path, currentBranch) |> Option.toList

                loop (List.append next acc) None None tail
            | (line: string) :: tail when line.StartsWith "worktree " ->
                loop acc (Some(WorktreePath.create (line.Substring(9).Trim()))) currentBranch tail
            | (line: string) :: tail when line.StartsWith "branch " ->
                loop acc currentPath (Some(WorktreeIdentity.create (line.Substring(7).Trim()))) tail
            | _ :: tail -> loop acc currentPath currentBranch tail

        loop [] None None lines

    let list (runner: Command -> Task<int * string * string>) repo () =
        task {
            let! code, stdout, stderr = runner (command (Some repo) [ "worktree"; "list"; "--porcelain" ])

            if code <> 0 then
                return Error(failure stdout stderr)
            else
                return
                    stdout.Split([| '\n'; '\r' |], StringSplitOptions.None)
                    |> Array.toList
                    |> parseList
                    |> Ok
        }

    let listBranches (runner: Command -> Task<int * string * string>) repo () =
        task {
            let! code, stdout, stderr = runner (command (Some repo) [ "branch"; "--list"; "manager/*" ])

            if code <> 0 then
                return Error(failure stdout stderr)
            else
                return
                    stdout.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                    // `git branch` prefixes a branch checked out in ANOTHER worktree
                    // with `+` (and the current branch of this repo with `*`). The
                    // manager worktree keeps its branch checked out, so an owned
                    // job's branch arrives as `+ manager/<job>` — without stripping
                    // the marker, `isStale` classifies the job's own branch as
                    // stale and the sweep tries to delete it (measured: sweep init
                    // failed with "branch '+ manager/<job>' not found").
                    |> Array.map (fun value -> value.Trim().TrimStart('*', '+').Trim())
                    |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    |> Array.map WorktreeIdentity.create
                    |> Array.toList
                    |> Ok
        }

    let deleteBranch (runner: Command -> Task<int * string * string>) repo (identity: WorktreeIdentity) =
        task {
            let! code, stdout, stderr = runner (command (Some repo) [ "branch"; "-D"; WorktreeIdentity.value identity ])

            return outcome code stdout stderr
        }
