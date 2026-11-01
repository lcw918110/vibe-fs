namespace Wanxiangshu.Change.Host

open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Git
open Wanxiangshu.Process

/// Git command plumbing for the production Orchestrator host.
module OrchestratorGit =

    let private estimate =
        { EstimatedRuntime = RuntimeSeconds 30.0
          EstimatedOutput = OutputBytes 65536L
          EstimatedMemory = EstimatedMemory.Medium }

    /// A spooled outcome still carries the complete output. Returning "" for it
    /// made a large `git grep` for conflict markers or a long unmerged-path list
    /// look empty. The spool file merges both channels in arrival order, so the
    /// full text is returned as stdout; the file is removed once read.
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

    let command (dir: string) (args: string list) : Command =
        { FileName = GitSubject.Executable
          Arguments = args
          WorkingDirectory = Some dir
          Environment = None
          Stdin = None
          PtyOptions = None }

    /// True when an in-progress rebase is present (rebase-merge / rebase-apply,
    /// never a stale REBASE_HEAD; ORCH-003).
    let hasRebaseHead (runner: Command -> Task<int * string * string>) (worktree: string) : Task<bool> =
        GitOperations.rebaseInProgress runner worktree

    /// After a manager terminal: stage everything, then either continue an
    /// in-flight rebase or create the candidate commit.
    ///
    /// Stage first (`git add -A`), then refuse leftover unmerged paths / conflict
    /// markers. Checking unmerged *before* add rejected a resolved file that was
    /// still unstaged (ORCH-003 conflict resume: Coder rewrote the file; Manager
    /// joined; finalize must stage then continue).
    let private continueRebase
        (runner: Command -> Task<int * string * string>)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! contCode, _, contErr = runner (command worktree [ "-c"; "core.editor=true"; "rebase"; "--continue" ])

            if contCode = 0 then
                return Ok()
            else
                return Error(sprintf "git rebase --continue failed: %s" contErr)
        }

    // semantic-decorator-owner: change-integration
    // semantic-decorator-WHAT: change-integration-003
    // semantic-decorator-trace-relation: delete stale REBASE_HEAD exactly once before the single candidate commit attempt
    // semantic-decorator-proof: requirements/change-integration/tests/git-operations.test.mjs::WHAT[change-integration-003] GIT_candidate_commit_deletes_stale_rebase_head_before_commit_and_surfaces_failure
    // semantic-decorator-failure-policy: ignore stale-ref deletion outcome; surface commit stderr unless git reports nothing to commit
    // semantic-decorator-cancel-policy: cancellation is owned by the injected command runner and stops before the next command
    // semantic-decorator-deadline-policy: each command uses the runner deadline; no sequence-level deadline extension
    // semantic-decorator-invocation-bound: 2
    let private commitCandidate
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! _ = runner (command worktree [ "update-ref"; "-d"; "REBASE_HEAD" ])

            let! commitCode, commitOut, commitErr =
                runner (command worktree [ "commit"; "-m"; sprintf "candidate: %s" managerId ])

            if commitCode = 0 then
                return Ok()
            elif (commitOut + commitErr).Contains("nothing to commit") then
                return Ok()
            else
                return Error(sprintf "git commit failed: %s" commitErr)
        }

    let private finalizeCommitOrRebase
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! hasRb = hasRebaseHead runner worktree

            if hasRb then
                return! continueRebase runner worktree
            else
                return! commitCandidate runner managerId worktree
        }

    let private refuseConflictMarkers
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! grepCode, grepOut, _ =
                runner (command worktree [ "grep"; "-I"; "-n"; "-E"; "^<<<<<<< |^>>>>>>> "; "--"; "." ])

            // git grep exit 0 = matches, 1 = no match, >1 = error
            if grepCode = 0 && not (String.IsNullOrWhiteSpace grepOut) then
                return Error(sprintf "conflict markers remain in worktree:\n%s" (grepOut.Trim()))
            elif grepCode > 1 then
                return Error "conflict-marker scan failed"
            else
                return! finalizeCommitOrRebase runner managerId worktree
        }

    let private afterGitAdd
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! unmergedCode, unmergedOut, unmergedErr =
                runner (command worktree [ "diff"; "--name-only"; "--diff-filter=U" ])

            if unmergedCode <> 0 then
                return Error(sprintf "conflict scan failed: %s" unmergedErr)
            elif not (String.IsNullOrWhiteSpace unmergedOut) then
                return Error(sprintf "unmerged paths remain after stage: %s" (unmergedOut.Trim()))
            else
                return! refuseConflictMarkers runner managerId worktree
        }

    let finalizeWorktree
        (runner: Command -> Task<int * string * string>)
        (managerId: string)
        (worktree: string)
        : Task<Result<unit, string>> =
        task {
            let! addCode, _, addErr = runner (command worktree [ "add"; "-A" ])

            if addCode <> 0 then
                return Error(sprintf "git add failed: %s" addErr)
            else
                return! afterGitAdd runner managerId worktree
        }
