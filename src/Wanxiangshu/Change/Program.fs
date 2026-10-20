namespace Wanxiangshu.Change

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.Journal

/// One manager session owns one stable worktree. Human quality decisions come only from
/// Relay incumbencies; Change owns deterministic Git admission, rebase and CAS.
module OrchestratorProgram =

    type private PublishAttempt =
        | TargetMoved
        | Landed of CommitHash

    type private PublicationEvidence =
        { ManagerJobId: ManagerJobId
          TargetRef: TargetRef
          ExpectedHead: CommitHash
          CandidateCommit: CommitHash
          AuthorityRevision: AuthorityRevision }

    let private failed (job: ManagerJob) details =
        OrchestratorVerdict.IntegrationFailed(job.JobId, details)

    let private buildPublicationEvidence
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (rebasedReady:
            {| RebasedCommit: CommitHash
               TargetHeadSnapshot: CommitHash |})
        : Task<Result<PublicationEvidence, OrchestratorVerdict>> =
        task {
            match! deps.Relay.LifecycleReviewed job.ManagerSessionId job.Worktree.Path with
            | None -> return Error(failed job "publish refused: no durable Reviewed Promoted or Stayed")
            | Some(_, authority) ->
                return
                    Ok
                        { ManagerJobId = job.JobId
                          TargetRef = job.TargetRef
                          ExpectedHead = rebasedReady.TargetHeadSnapshot
                          CandidateCommit = rebasedReady.RebasedCommit
                          AuthorityRevision = AuthorityRevision.create authority }
        }

    let private buildClaimPublicationEvidence
        (job: ManagerJob)
        (claim:
            {| TargetRef: TargetRef
               RebasedCommit: CommitHash
               ExpectedHead: CommitHash
               AuthorityRevision: AuthorityRevision |})
        : PublicationEvidence =
        { ManagerJobId = job.JobId
          TargetRef = claim.TargetRef
          ExpectedHead = claim.ExpectedHead
          CandidateCommit = claim.RebasedCommit
          AuthorityRevision = claim.AuthorityRevision }

    let private mapTask (mapper: 'a -> 'b) (operation: Task<'a>) : Task<'b> =
        task {
            let! value = operation
            return mapper value
        }

    let private mapTaskError mapper operation =
        mapTask (Result.mapError mapper) operation

    let private continueResult binder operation =
        task {
            let! outcome = operation

            match outcome with
            | Ok value -> return! binder value
            | Error verdict -> return verdict
        }

    let private continueUnit binder operation =
        continueResult (fun () -> binder ()) operation

    let private append (deps: OrchestratorProgramDeps) (job: ManagerJob) fact =
        taskResult { do! deps.AppendFact StreamId.Workspace fact |> mapTaskError (failed job) }

    let private readHead (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        deps.Git.ReadHead job.Worktree.Path
        |> mapTaskError (fun error -> failed job (sprintf "Git head lookup failed: %s" error))

    let private targetHead (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        deps.Git.GetTargetHead job.TargetRef
        |> mapTaskError (fun error -> failed job (sprintf "Git target head lookup failed: %s" error))

    let private continueLoop (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        deps.Relay.ContinueLoop job.JobId
        |> mapTaskError (fun error -> failed job (sprintf "Manager loop continuation failed: %s" error))

    let private conflictedFilesResult (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        deps.Git.ConflictedFiles job.Worktree.Path
        |> mapTaskError (fun error -> failed job (sprintf "Conflict-file lookup failed: %s" error))

    let private prepareCandidateResult (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        deps.Relay.PrepareCandidate job.JobId
        |> mapTaskError (fun error -> failed job (sprintf "Candidate admission failed: %s" error))

    let private recordCandidate (deps: OrchestratorProgramDeps) (job: ManagerJob) (candidate: CommitHash) =
        append
            deps
            job
            (OrchestratorFact.CandidateReady
                {| ManagerJobId = job.JobId
                   CandidateCommit = candidate |})

    let private recordRebased (deps: OrchestratorProgramDeps) (job: ManagerJob) (target: CommitHash) =
        taskResult {
            let! rebased = readHead deps job

            do!
                append
                    deps
                    job
                    (OrchestratorFact.RebasedCandidateReady
                        {| ManagerJobId = job.JobId
                           RebasedCommit = rebased
                           TargetHeadSnapshot = target |})

            return rebased
        }

    let private recordConflict
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (candidate: CommitHash)
        (target: CommitHash)
        (files: string list)
        =
        append
            deps
            job
            (OrchestratorFact.ConflictDetected
                {| ManagerJobId = job.JobId
                   CandidateCommit = candidate
                   TargetHeadSnapshot = target
                   ConflictFiles = files
                   DiagnosticsDigest = HostDigest.sha256Hex (String.Join("\n", files)) |})

    let private publicationIdentityVerdict
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        : Result<unit, OrchestratorVerdict> =
        if evidence.ManagerJobId <> job.JobId then
            Error(failed job "Publication evidence job mismatch; refusing publish")
        elif evidence.TargetRef <> job.TargetRef then
            Error(failed job "Publication evidence target mismatch; refusing publish")
        elif current <> evidence.ExpectedHead then
            Error(failed job "Publish CAS head does not match publication evidence; refusing publish")
        else
            Ok()

    let private worktreePinVerdict
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (worktreeHead: CommitHash)
        : Result<unit, OrchestratorVerdict> =
        if worktreeHead <> evidence.CandidateCommit then
            Error(
                failed
                    job
                    (sprintf
                        "Worktree HEAD %s does not match candidate commit %s"
                        (CommitHash.value worktreeHead)
                        (CommitHash.value evidence.CandidateCommit))
            )
        else
            Ok()

    let private prePublishConflictsVerdict
        (job: ManagerJob)
        (conflicts: string list)
        : Result<unit, OrchestratorVerdict> =
        if List.isEmpty conflicts then
            Ok()
        else
            Error(failed job (sprintf "Worktree has unmerged conflicts before publish: %A" conflicts))

    let private ffMergeVerdict
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (merge: Result<CommitHash, string>)
        : Result<CommitHash option, OrchestratorVerdict> =
        match merge with
        | Error error when error = OrchestratorConstants.targetRefMovedError -> Ok None
        | Error error -> Error(failed job (sprintf "FF merge failed: %s" error))
        | Ok landed when landed <> evidence.CandidateCommit ->
            Error(
                failed
                    job
                    (sprintf
                        "FF merge landed %s which does not match pinned candidate %s"
                        (CommitHash.value landed)
                        (CommitHash.value evidence.CandidateCommit))
            )
        | Ok landed -> Ok(Some landed)

    let private appendPublishClaimed (deps: OrchestratorProgramDeps) (job: ManagerJob) (evidence: PublicationEvidence) =
        append
            deps
            job
            (OrchestratorFact.PublishClaimed
                {| ManagerJobId = evidence.ManagerJobId
                   TargetRef = evidence.TargetRef
                   RebasedCommit = evidence.CandidateCommit
                   ExpectedHead = evidence.ExpectedHead
                   AuthorityRevision = evidence.AuthorityRevision |})

    let private appendPublishedEvidence
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (landed: CommitHash)
        =
        append
            deps
            job
            (OrchestratorFact.Published
                {| ManagerJobId = evidence.ManagerJobId
                   CandidateCommit = evidence.CandidateCommit
                   ResultingTargetHead = landed |})

    let private finalizePublishedLanding
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (landed: CommitHash)
        =
        task {
            match! appendPublishedEvidence deps job evidence landed with
            | Error verdict -> return Error verdict
            | Ok() -> return Ok(Landed landed)
        }

    let private continueMergeAfterClassification
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (classified: Result<CommitHash option, OrchestratorVerdict>)
        =
        task {
            match classified with
            | Error verdict -> return Error verdict
            | Ok None -> return Ok TargetMoved
            | Ok(Some landed) -> return! finalizePublishedLanding deps job evidence landed
        }

    let private runPublishFfMerge
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        task {
            let! merge = deps.Git.FfMerge job.Worktree.Path job.TargetRef current evidence.CandidateCommit
            return! continueMergeAfterClassification deps job evidence (ffMergeVerdict job evidence merge)
        }

    let private claimRebasedForPublish
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        task {
            match! appendPublishClaimed deps job evidence with
            | Error verdict -> return Error verdict
            | Ok() -> return! runPublishFfMerge deps job evidence current
        }

    let private continuePublishAfterConflicts
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        (conflicts: string list)
        =
        task {
            match prePublishConflictsVerdict job conflicts with
            | Error verdict -> return Error verdict
            | Ok() -> return! claimRebasedForPublish deps job evidence current
        }

    let private verifyPublishWorktreeClean
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        task {
            match! conflictedFilesResult deps job with
            | Error verdict -> return Error verdict
            | Ok conflicts -> return! continuePublishAfterConflicts deps job evidence current conflicts
        }

    let private continuePublishAfterWorktreeHead
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        (worktreeHead: CommitHash)
        =
        task {
            match worktreePinVerdict job evidence worktreeHead with
            | Error verdict -> return Error verdict
            | Ok() -> return! verifyPublishWorktreeClean deps job evidence current
        }

    let private verifyPublishWorktreePin
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        task {
            match! readHead deps job with
            | Error verdict -> return Error verdict
            | Ok worktreeHead -> return! continuePublishAfterWorktreeHead deps job evidence current worktreeHead
        }

    let private completeClaimAndFf
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        task {
            match publicationIdentityVerdict job evidence current with
            | Error verdict -> return Error verdict
            | Ok() -> return! verifyPublishWorktreePin deps job evidence current
        }

    let private claimExpectationVerdict
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (expectedHead: CommitHash)
        : Result<unit, OrchestratorVerdict> =
        if expectedHead <> evidence.ExpectedHead then
            Error(failed job "Publish CAS expectation does not match publication evidence")
        else
            Ok()

    let private continueClaimWithCurrent
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        if current <> evidence.ExpectedHead then
            Task.FromResult(Ok TargetMoved)
        else
            completeClaimAndFf deps job evidence current

    let private fetchClaimCurrentAndContinue
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        =
        task {
            match! targetHead deps job with
            | Error verdict -> return Error verdict
            | Ok current -> return! continueClaimWithCurrent deps job evidence current
        }

    let private claimAndFf
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (expectedHead: CommitHash)
        =
        task {
            match claimExpectationVerdict job evidence expectedHead with
            | Error verdict -> return Error verdict
            | Ok() -> return! fetchClaimCurrentAndContinue deps job evidence
        }

    let private publishUnderGate
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (expectedHead: CommitHash)
        =
        task {
            let! gate = deps.AcquirePublishGate()

            let! outcome =
                task {
                    try
                        let! res = claimAndFf deps job evidence expectedHead
                        return Choice1Of2 res
                    with ex ->
                        return Choice2Of2 ex
                }

            do! gate.Release()

            match outcome with
            | Choice1Of2 res -> return res
            | Choice2Of2(:? OperationCanceledException as oce) -> return raise oce
            | Choice2Of2 error -> return Error(failed job (sprintf "Publish window failed: %s" error.Message))
        }

    let private releaseTerminalWorktree (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        task {
            do! deps.Relay.TerminateWorkResources job.JobId
            return! job.Worktree.Release()
        }

    let private settleLanded (deps: OrchestratorProgramDeps) (job: ManagerJob) (commit: CommitHash) =
        task {
            match! releaseTerminalWorktree deps job with
            | Ok() -> return OrchestratorVerdict.Published(job.JobId, commit)
            | Error error -> return OrchestratorVerdict.PublishedPendingCleanup(job.JobId, commit, error)
        }

    let private backfillPublished
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (candidate: CommitHash)
        (resultingHead: CommitHash)
        =
        task {
            match!
                append
                    deps
                    job
                    (OrchestratorFact.Published
                        {| ManagerJobId = job.JobId
                           CandidateCommit = candidate
                           ResultingTargetHead = resultingHead |})
            with
            | Error verdict -> return verdict
            | Ok() -> return! settleLanded deps job resultingHead
        }

    let private currentRecord (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        OrchestratorProjection.tryFind job.JobId (deps.Snapshot()).AgentProjections.Orchestrator

    let private requestAfterBindingChange (deps: OrchestratorProgramDeps) (job: ManagerJob) reason =
        ignore reason

        taskResult {
            let! _ = continueLoop deps job
            return ()
        }

    let rec private runManagerLoop (deps: OrchestratorProgramDeps) (job: ManagerJob) : Task<OrchestratorVerdict> =
        task {
            match! deps.Relay.AwaitLoopSignal job.JobId with
            | Error error -> return failed job (sprintf "Manager loop signal failed: %s" error)
            | Ok ManagerLoopSignal.Continue -> return! continueManagerLoop deps job
            | Ok ManagerLoopSignal.Candidate -> return! handleQualityCandidate deps job
            | Ok(ManagerLoopSignal.ExceptionalTerminal reason) -> return failed job reason
        }

    and private continueManagerLoop (deps: OrchestratorProgramDeps) (job: ManagerJob) : Task<OrchestratorVerdict> =
        task {
            match! continueLoop deps job with
            | Ok _ -> return! runManagerLoop deps job
            | Error verdict -> return verdict
        }

    and private handleRebase
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (candidate: CommitHash)
        (target: CommitHash)
        reason
        : Task<OrchestratorVerdict> =
        let afterRebasedRecord _ = continueManagerLoop deps job

        let onRebaseSuccess () =
            recordRebased deps job target |> continueResult afterRebasedRecord

        let afterConflictRecord _ = continueManagerLoop deps job

        let handleConflictFiles rebaseError files =
            if List.isEmpty files then
                Task.FromResult(failed job (sprintf "Rebase failed without conflicts: %s" rebaseError))
            else
                recordConflict deps job candidate target files
                |> continueResult afterConflictRecord

        let onRebaseFailure rebaseError =
            conflictedFilesResult deps job
            |> continueResult (handleConflictFiles rebaseError)

        let afterInvalidation () =
            task {
                let! rebase = deps.Git.Rebase job.Worktree.Path job.TargetRef

                match rebase with
                | Ok() -> return! onRebaseSuccess ()
                | Error rebaseError -> return! onRebaseFailure rebaseError
            }

        afterInvalidation ()

    and private publishCertified
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (candidate: CommitHash)
        (expectedHead: CommitHash)
        : Task<OrchestratorVerdict> =
        let afterTargetRefresh refreshed =
            handleRebase deps job candidate refreshed "PublishCasMissed"

        let handlePublishAttempt =
            function
            | Landed commit -> settleLanded deps job commit
            | TargetMoved -> targetHead deps job |> continueResult afterTargetRefresh

        let continueValidatedPublish (evidence: PublicationEvidence) =
            task {
                match! publishUnderGate deps job evidence evidence.ExpectedHead with
                | Error verdict -> return verdict
                | Ok attempt -> return! handlePublishAttempt attempt
            }

        let handleStaleSnapshotBeforePublish () =
            task {
                match! requestAfterBindingChange deps job "WorkspaceChangedAfterAssessment" with
                | Error verdict -> return verdict
                | Ok() -> return! runManagerLoop deps job
            }

        let publishValidated (evidence: PublicationEvidence) = continueValidatedPublish evidence

        let certifiedEvidence
            (rebasedReady:
                {| RebasedCommit: CommitHash
                   TargetHeadSnapshot: CommitHash |})
            : Task<Result<PublicationEvidence, OrchestratorVerdict>> =
            task {
                if candidate <> rebasedReady.RebasedCommit then
                    return
                        Error(
                            failed
                                job
                                (sprintf
                                    "Candidate %s does not match rebased evidence pin %s"
                                    (CommitHash.value candidate)
                                    (CommitHash.value rebasedReady.RebasedCommit))
                        )
                elif expectedHead <> rebasedReady.TargetHeadSnapshot then
                    return
                        Error(
                            failed
                                job
                                (sprintf
                                    "Target head %s does not match rebased evidence snapshot %s"
                                    (CommitHash.value expectedHead)
                                    (CommitHash.value rebasedReady.TargetHeadSnapshot))
                        )
                else
                    return! buildPublicationEvidence deps job rebasedReady
            }

        let continueCertifiedWithRebased
            (rebasedReady:
                {| RebasedCommit: CommitHash
                   TargetHeadSnapshot: CommitHash |})
            =
            task {
                match! certifiedEvidence rebasedReady with
                | Error verdict -> return verdict
                | Ok evidence -> return! publishValidated evidence
            }

        let continueCertifiedWithRecord (record: ManagerJobProjection) =
            task {
                match record.RebasedCandidateReady with
                | None -> return failed job "Missing RebasedCandidateReady evidence for certified publish"
                | Some rebasedReady -> return! continueCertifiedWithRebased rebasedReady
            }

        task {
            match currentRecord deps job with
            | None -> return failed job "No record found for certified publish"
            | Some record -> return! continueCertifiedWithRecord record
        }

    and private handleQualityCandidate (deps: OrchestratorProgramDeps) (job: ManagerJob) : Task<OrchestratorVerdict> =
        let resumeLoop () = runManagerLoop deps job

        let bindingChanged () =
            requestAfterBindingChange deps job "WorkspaceChangedAfterAssessment"
            |> continueUnit resumeLoop

        let afterConflictBindingChange () = runManagerLoop deps job

        let afterConflictRecord () =
            requestAfterBindingChange deps job "ArtifactAdmissionUnmerged"
            |> continueUnit afterConflictBindingChange

        let recordObservedConflict files candidate target =
            recordConflict deps job candidate target files
            |> continueUnit afterConflictRecord

        let afterConflictHead files candidate =
            targetHead deps job |> continueResult (recordObservedConflict files candidate)

        let handleObservedConflict files =
            readHead deps job |> continueResult (afterConflictHead files)

        let rebaseReason =
            function
            | Some _ -> "TargetAdvanced"
            | None -> "InitialRebaseRequired"

        let recordInitialCandidate candidate target rebased =
            let afterCandidateRecord () =
                handleRebase deps job candidate target (rebaseReason rebased)

            recordCandidate deps job candidate |> continueUnit afterCandidateRecord

        let admitPreparedCandidate candidate target =
            let rebased =
                currentRecord deps job
                |> Option.bind (fun record -> record.RebasedCandidateReady)

            match rebased with
            | Some admitted when admitted.RebasedCommit = candidate && admitted.TargetHeadSnapshot = target ->
                publishCertified deps job candidate target
            | _ -> recordInitialCandidate candidate target rebased

        let onPreparedCandidate candidate =
            targetHead deps job |> continueResult (admitPreparedCandidate candidate)

        let handleConflictFiles files =
            match files with
            | [] -> prepareCandidateResult deps job |> continueResult onPreparedCandidate
            | _ -> handleObservedConflict files

        let onPrepareFailure _ =
            conflictedFilesResult deps job |> continueResult handleConflictFiles

        task {
            let! outcome = prepareCandidateResult deps job

            match outcome with
            | Ok candidate -> return! onPreparedCandidate candidate
            | Error error -> return! onPrepareFailure error
        }

    let private resumePublishReady
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (expectedHead: CommitHash)
        =
        task {
            match! publishUnderGate deps job evidence expectedHead with
            | Error verdict -> return verdict
            | Ok(Landed commit) -> return! settleLanded deps job commit
            | Ok TargetMoved -> return! runManagerLoop deps job
        }

    let private resumePublishReality
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        (reality: PublishClaimReality)
        =
        match reality with
        | PublishClaimReality.HeadUnreadable ->
            Task.FromResult(failed job "GetTargetHead failed during publish recovery")
        | PublishClaimReality.AlreadyFastForwarded -> backfillPublished deps job evidence.CandidateCommit current
        | PublishClaimReality.PublishReady -> resumePublishReady deps job evidence evidence.ExpectedHead
        | PublishClaimReality.ClaimExpired -> runManagerLoop deps job

    let private reentryClaimTargetVerdict (job: ManagerJob) (targetRef: TargetRef) : Result<unit, OrchestratorVerdict> =
        if targetRef <> job.TargetRef then
            Error(failed job "Publish claim target mismatch; refusing publish recovery")
        else
            Ok()

    let private reentryRebasedMatches
        (rebasedCommit: CommitHash)
        (expectedHead: CommitHash)
        (rebasedReady:
            {| RebasedCommit: CommitHash
               TargetHeadSnapshot: CommitHash |})
        =
        rebasedCommit = rebasedReady.RebasedCommit
        && expectedHead = rebasedReady.TargetHeadSnapshot

    let private continueReentryWithTarget
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (current: CommitHash)
        =
        let reality =
            OrchestratorProjection.classifyPublishClaim (Some current) evidence.CandidateCommit evidence.ExpectedHead

        resumePublishReality deps job evidence current reality

    let private resolveReentryTarget (deps: OrchestratorProgramDeps) (job: ManagerJob) (evidence: PublicationEvidence) =
        task {
            let! targetResult = deps.Git.GetTargetHead job.TargetRef

            match targetResult with
            | Error _ -> return failed job "GetTargetHead failed; refusing publish recovery"
            | Ok current -> return! continueReentryWithTarget deps job evidence current
        }

    let private continueReentryAfterHead
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        (worktreeHead: CommitHash)
        =
        if worktreeHead <> evidence.CandidateCommit then
            Task.FromResult(
                failed
                    job
                    (sprintf
                        "Worktree HEAD %s does not match claimed rebased commit %s"
                        (CommitHash.value worktreeHead)
                        (CommitHash.value evidence.CandidateCommit))
            )
        else
            resolveReentryTarget deps job evidence

    let private verifyReentryHead (deps: OrchestratorProgramDeps) (job: ManagerJob) (evidence: PublicationEvidence) =
        task {
            match! readHead deps job with
            | Error err -> return err
            | Ok worktreeHead -> return! continueReentryAfterHead deps job evidence worktreeHead
        }

    let private continueReentryWithEvidence
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (evidence: PublicationEvidence)
        =
        verifyReentryHead deps job evidence

    let private continueReentryWithRebased
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (claim:
            {| TargetRef: TargetRef
               RebasedCommit: CommitHash
               ExpectedHead: CommitHash
               AuthorityRevision: AuthorityRevision |})
        (rebasedReady:
            {| RebasedCommit: CommitHash
               TargetHeadSnapshot: CommitHash |})
        =
        if reentryRebasedMatches claim.RebasedCommit claim.ExpectedHead rebasedReady then
            continueReentryWithEvidence deps job (buildClaimPublicationEvidence job claim)
        else
            runManagerLoop deps job

    let private continueReentryWithRecord
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (claim:
            {| TargetRef: TargetRef
               RebasedCommit: CommitHash
               ExpectedHead: CommitHash
               AuthorityRevision: AuthorityRevision |})
        (record: ManagerJobProjection)
        =
        task {
            match record.RebasedCandidateReady with
            | None -> return failed job "Missing RebasedCandidateReady evidence for publish claim reentry"
            | Some rebasedReady -> return! continueReentryWithRebased deps job claim rebasedReady
        }

    let private fetchReentryRecordAndContinue
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (claim:
            {| TargetRef: TargetRef
               RebasedCommit: CommitHash
               ExpectedHead: CommitHash
               AuthorityRevision: AuthorityRevision |})
        =
        task {
            match currentRecord deps job with
            | None -> return failed job "No record found for publish claim reentry"
            | Some record -> return! continueReentryWithRecord deps job claim record
        }

    let private reenterPublishClaim
        (deps: OrchestratorProgramDeps)
        (job: ManagerJob)
        (claim:
            {| TargetRef: TargetRef
               RebasedCommit: CommitHash
               ExpectedHead: CommitHash
               AuthorityRevision: AuthorityRevision |})
        =
        task {
            match reentryClaimTargetVerdict job claim.TargetRef with
            | Error verdict -> return verdict
            | Ok() -> return! fetchReentryRecordAndContinue deps job claim
        }

    let private cleanUp (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        task {
            match! releaseTerminalWorktree deps job with
            | Ok() -> return OrchestratorVerdict.Empty
            | Error error -> return failed job (sprintf "Terminal job cleanup failed: %s" error)
        }

    let private program (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        match currentRecord deps job with
        | Some { Terminal = Some _ } -> cleanUp deps job
        | Some { PublishClaimed = Some claim } -> reenterPublishClaim deps job claim
        | _ -> runManagerLoop deps job

    let run (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        task {
            try
                return! program deps job
            with
            | :? OperationCanceledException -> return OrchestratorVerdict.Cancelled job.JobId
            | error -> return failed job (sprintf "%A" error)
        }
