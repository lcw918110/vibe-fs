namespace Wanxiangshu.Git

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Change
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process

/// Typed Git verbs. Process command construction and output classification are
/// contained here; workflow sequencing stays in OrchestratorProgram.
module GitOperations =

    let private command repo args =
        { FileName = GitSubject.Executable
          Arguments = args
          WorkingDirectory = Some repo
          Environment = None
          Stdin = None
          PtyOptions = None }

    let private failure stdout stderr =
        if String.IsNullOrWhiteSpace stderr then stdout else stderr

    let private stderrOr (missing: string) (stderr: string) =
        if String.IsNullOrWhiteSpace stderr then
            missing
        else
            stderr.Trim()

    let private renamedTarget (path: string) =
        let arrow = path.IndexOf(" -> ", StringComparison.Ordinal)

        if arrow >= 0 then
            path.Substring(arrow + 4).Trim()
        else
            path

    let private pathOfRenamed (renamed: string) =
        if String.IsNullOrWhiteSpace renamed then
            None
        else
            Some renamed

    let private singleBlockingPath (line: string) =
        if line.Length <= 3 then
            None
        else
            let path = line.Substring(3).Trim()
            pathOfRenamed (renamedTarget path)

    /// The paths that block a modification of this worktree.
    ///
    /// `git status --porcelain` prints `XY <path>`; a rename or copy prints
    /// `old -> new`, and the new name is the one that blocks.
    let private blockingPaths (porcelain: string) =
        porcelain
        |> fun text -> text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose singleBlockingPath
        |> Array.toList

    /// The target worktree is the one the publish would modify, so its
    /// uncommitted content is what the merge could overwrite. The refusal
    /// names the blocking path instead of demanding a clean tree in general.
    let private verifyClean runner repo =
        task {
            let! code, stdout, stderr = runner (command repo [ "status"; "--porcelain" ])

            if code <> 0 then
                return Error(failure stdout stderr)
            elif String.IsNullOrWhiteSpace stdout then
                return Ok()
            else
                return
                    Error(
                        "target worktree has uncommitted changes; refusing ff-only merge: "
                        + (blockingPaths stdout |> String.concat ", ")
                    )
        }

    let private verifyHead runner repo (expected: CommitHash) =
        task {
            let! code, stdout, stderr = runner (command repo [ "rev-parse"; "HEAD" ])

            if code <> 0 then
                return Error(failure stdout stderr)
            elif stdout.Trim() = CommitHash.value expected then
                return Ok expected
            else
                return
                    Error(
                        sprintf
                            "ff-only merge did not advance HEAD to candidate %s (got %s)"
                            (CommitHash.value expected)
                            (stdout.Trim())
                    )
        }

    /// ORCH-005 CAS: the ref moved between the head read and the update.
    ///
    /// Recognised from git's own lock diagnostics. A moved target is not a failure
    /// to report upward — ORCH-005 answers it by rebasing and re-reviewing, and the
    /// old post-rebase witness is discarded.
    let private isRefMoved (message: string) =
        [ "cannot lock ref"; "update-ref"; "expected" ]
        |> List.exists (fun marker -> message.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)

    let private revParse runner repo (spec: string) (missing: string) =
        task {
            let! code, stdout, stderr = runner (command repo [ "rev-parse"; spec ])

            if code <> 0 then
                return Error(stderrOr missing stderr)
            elif String.IsNullOrWhiteSpace stdout then
                return Error missing
            else
                return Ok(CommitHash.create (stdout.Trim()))
        }

    let private targetSpec (target: TargetRef) =
        sprintf "refs/heads/%s" (TargetRef.value target)

    let private checkedOutBranch (branchCode: int) (branchOut: string) =
        if branchCode = 0 then
            branchOut.Trim()
        else
            "<detached HEAD>"

    let private requirePublishBranch
        (runner: Command -> Task<int * string * string>)
        repoPath
        (target: TargetRef)
        : Task<Result<unit, string>> =
        task {
            let! branchCode, branchOut, _ = runner (command repoPath [ "symbolic-ref"; "--short"; "HEAD" ])

            if branchCode <> 0 || branchOut.Trim() <> TargetRef.value target then
                return
                    Error(
                        sprintf
                            "publish branch mismatch: target repo is on '%s' but publish is frozen to '%s'"
                            (checkedOutBranch branchCode branchOut)
                            (TargetRef.value target)
                    )
            else
                return Ok()
        }

    let private assertExpectedHead (currentHead: CommitHash) (expectedHead: CommitHash) =
        if currentHead <> expectedHead then
            Error OrchestratorConstants.targetRefMovedError
        else
            Ok()

    let private requireAncestor
        (runner: Command -> Task<int * string * string>)
        repoPath
        (currentHead: CommitHash)
        (candidate: CommitHash)
        : Task<Result<unit, string>> =
        task {
            let! ancestorCode, _, ancestorError =
                runner (
                    command
                        repoPath
                        [ "merge-base"
                          "--is-ancestor"
                          CommitHash.value currentHead
                          CommitHash.value candidate ]
                )

            if ancestorCode <> 0 then
                return Error(stderrOr "candidate is not a fast-forward of the target branch" ancestorError)
            else
                return Ok()
        }

    let private mergeFailure message =
        if isRefMoved message then
            OrchestratorConstants.targetRefMovedError
        else
            message

    let private mergeFf
        (runner: Command -> Task<int * string * string>)
        repoPath
        (candidate: CommitHash)
        : Task<Result<CommitHash, string>> =
        task {
            let! mergeCode, mergeOut, mergeError =
                runner (command repoPath [ "merge"; "--ff-only"; CommitHash.value candidate ])

            if mergeCode = 0 then
                return! verifyHead runner repoPath candidate
            else
                let message = failure mergeOut mergeError
                let classified = mergeFailure message
                return Error classified
        }

    /// ff-only publish inside the short Integration Gate (ORCH-005).
    ///
    /// `expectedHead` is mandatory. Every publish is preceded by a head read inside
    /// the gate, so there is no legitimate "publish without an expectation" — and
    /// making it optional is what allowed a lost update.
    let private ffMerge
        (runner: Command -> Task<int * string * string>)
        repoPath
        (worktree: WorktreePath)
        (target: TargetRef)
        (expectedHead: CommitHash)
        (pinnedCandidate: CommitHash)
        : Task<Result<CommitHash, string>> =
        taskResult {
            let! candidate = revParse runner (WorktreePath.value worktree) "HEAD" "candidate HEAD is empty"
            // R12: the physical FF is bound to the durable pin. The worktree
            // candidate must equal the pin; the merge itself runs on the pin
            // so a worktree move after the read cannot redirect the publish.
            do!
                if candidate <> pinnedCandidate then
                    Error(
                        sprintf
                            "worktree HEAD %s does not match pinned candidate %s"
                            (CommitHash.value candidate)
                            (CommitHash.value pinnedCandidate)
                    )
                else
                    Ok()
            // ORCH-008: the publish target is frozen at fork time. If the repo has
            // since moved to another branch, refuse rather than publish to whichever
            // branch happens to be checked out.
            do! requirePublishBranch runner repoPath target
            let! currentHead = revParse runner repoPath (targetSpec target) "target branch not found"
            do! assertExpectedHead currentHead expectedHead
            do! requireAncestor runner repoPath currentHead pinnedCandidate
            do! verifyClean runner repoPath
            return! mergeFf runner repoPath pinnedCandidate
        }

    let continueRebase (runner: Command -> Task<int * string * string>) dir =
        task {
            // Stage any Manager/Coder resolution before continue (ORCH-003).
            // Predecessor's finalizeWorktree should already have staged, but a
            // missed finalize leaves unmerged paths; add here is idempotent.
            let! addCode, _, addErr = runner (command dir [ "add"; "-A" ])

            if addCode <> 0 then
                return Error(failure "" addErr)
            else
                let! code, stdout, stderr = runner (command dir [ "-c"; "core.editor=true"; "rebase"; "--continue" ])

                return if code = 0 then Ok() else Error(failure stdout stderr)
        }

    let stageAll (runner: Command -> Task<int * string * string>) dir =
        task {
            let! code, stdout, stderr = runner (command dir [ "add"; "-A" ])
            return if code = 0 then Ok() else Error(failure stdout stderr)
        }

    let candidateCommit (runner: Command -> Task<int * string * string>) dir (msg: string) =
        task {
            let! _ = runner (command dir [ "update-ref"; "-d"; "REBASE_HEAD" ])
            let! code, stdout, stderr = runner (command dir [ "commit"; "-m"; msg ])
            return if code = 0 then Ok() else Error(failure stdout stderr)
        }

    let private freshRebase (runner: Command -> Task<int * string * string>) dir (target: TargetRef) =
        task {
            // Clear stale REBASE_HEAD so the fresh rebase is not confused.
            let! _ = runner (command dir [ "update-ref"; "-d"; "REBASE_HEAD" ])
            let! code, stdout, stderr = runner (command dir [ "rebase"; TargetRef.value target ])
            return if code = 0 then Ok() else Error(failure stdout stderr)
        }

    let rebaseInProgress (runner: Command -> Task<int * string * string>) dir =
        task {
            let! mergeCode, mergePath, _ = runner (command dir [ "rev-parse"; "--git-path"; "rebase-merge" ])
            let! applyCode, applyPath, _ = runner (command dir [ "rev-parse"; "--git-path"; "rebase-apply" ])

            return
                GitPorcelain.rebaseStatePaths (mergeCode, mergePath) (applyCode, applyPath)
                |> List.exists System.IO.Directory.Exists
        }

    let private worktrees repoPath (runner: Command -> Task<int * string * string>) =
        GitWorktrees.CreateWithRunner(
            repoPath,
            fun request ->
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
        )

    let createWithRepo repoPath (runner: Command -> Task<int * string * string>) : GitPort =
        { IsDirty =
            fun path ->
                task {
                    let! code, stdout, stderr = runner (command (WorktreePath.value path) [ "status"; "--porcelain" ])

                    return
                        if code = 0 then
                            Ok(blockingPaths stdout)
                        else
                            Error(failure stdout stderr)
                }
          Worktrees = worktrees repoPath runner

          // ORCH-008: freeze by symbolic-ref. A detached HEAD has no branch to
          // publish to, so this fails closed instead of resolving HEAD to a commit
          // and treating that as the target.
          FreezeTargetBranch =
            fun () ->
                task {
                    let! code, stdout, stderr = runner (command repoPath [ "symbolic-ref"; "--short"; "HEAD" ])

                    if code <> 0 || String.IsNullOrWhiteSpace stdout then
                        return Error(stderrOr "cannot freeze target branch: repository HEAD is detached" stderr)
                    else
                        return Ok(TargetRef.create (stdout.Trim()))
                }

          Rebase =
            fun worktree target ->
                task {
                    let dir = WorktreePath.value worktree
                    let! inProgress = rebaseInProgress runner dir

                    if inProgress then
                        return! continueRebase runner dir
                    else
                        return! freshRebase runner dir target
                }

          ConflictedFiles =
            fun worktree ->
                task {
                    let! code, stdout, stderr =
                        runner (command (WorktreePath.value worktree) [ "diff"; "--name-only"; "--diff-filter=U" ])

                    if code <> 0 then
                        return Error(failure stdout stderr)
                    else
                        return
                            stdout.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.toList
                            |> Ok
                }

          FfMerge = ffMerge runner repoPath

          HasRebaseHead =
            fun worktree ->
                task {
                    let dir = WorktreePath.value worktree
                    return! rebaseInProgress runner dir
                }

          ReadHead = fun path -> revParse runner (WorktreePath.value path) "HEAD" "HEAD is empty"

          GetTargetHead =
            fun target ->
                revParse
                    runner
                    repoPath
                    (targetSpec target)
                    (sprintf "target branch not found: %s" (TargetRef.value target)) }
