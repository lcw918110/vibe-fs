namespace Wanxiangshu.Change

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Work

[<RequireQualifiedAccess>]
type TerminalOutcome =
    | Published of
        {| CandidateCommit: CommitHash
           ResultingTargetHead: CommitHash |}
    | Failed of reason: string
    | Abandoned

type ManagerJobProjection =
    { ManagerJobId: ManagerJobId
      ManagerSessionId: SessionId
      ManagerAgent: string
      Byname: string
      WorktreeIdentity: WorktreeIdentity
      WorktreePath: WorktreePath
      TargetRef: TargetRef
      TargetBranchFrozen: string
      CandidateReady: {| CandidateCommit: CommitHash |} option
      ConflictDetected:
          {| CandidateCommit: CommitHash
             TargetHeadSnapshot: CommitHash
             ConflictFiles: string list
             DiagnosticsDigest: string |} option
      RebasedCandidateReady:
          {| RebasedCommit: CommitHash
             TargetHeadSnapshot: CommitHash |} option
      PublishClaimed:
          {| TargetRef: TargetRef
             RebasedCommit: CommitHash
             ExpectedHead: CommitHash |} option
      Terminal: TerminalOutcome option }

[<RequireQualifiedAccess>]
type WorktreeEffectStatus =
    | Requested of
        {| ManagerJobId: ManagerJobId
           WorktreePath: WorktreePath |}
    | Created of
        {| ManagerJobId: ManagerJobId
           WorktreePath: WorktreePath |}

[<RequireQualifiedAccess>]
type WorktreeReconciliationObservation =
    | NoDurableEffect
    | RequestedConflict of recordedJobId: ManagerJobId * recordedPath: WorktreePath
    | RequestedAmbiguity of
        recordedJobId: ManagerJobId *
        recordedPath: WorktreePath *
        physical: Result<(WorktreePath * WorktreeIdentity option) list, string>
    | CreatedReceipt of recordedJobId: ManagerJobId * recordedPath: WorktreePath

[<RequireQualifiedAccess>]
type WorktreeReconciliationFailure =
    | DurableOwnershipConflict
    | WorktreeQueryFailed of string
    | PhysicalIdentityPathConflict

[<RequireQualifiedAccess>]
type WorktreeReconciliationDecision =
    | RequestThenCreate
    | CreateAfterProvenMissing
    | AdoptThenRecordCreated
    | AdoptCreated
    | Reject of WorktreeReconciliationFailure

type OrchestratorProjection

[<RequireQualifiedAccess>]
type RebasedCandidateReality =
    | HeadUnreadable
    | PublishReady
    | NeedsRebase

[<RequireQualifiedAccess>]
type PublishClaimReality =
    | HeadUnreadable
    | AlreadyFastForwarded
    | PublishReady
    | ClaimExpired

/// Whether a prepared candidate may publish as it stands.
[<RequireQualifiedAccess>]
type CandidateAdmission =
    | PublishCertified
    | Rebase of reason: string

/// Whether the rebased pin still certifies the candidate about to publish.
[<RequireQualifiedAccess>]
type PublicationCertification =
    | Certified
    | MissingRebased
    | CandidatePinMismatch
    | SnapshotPinMismatch

/// Whether the publication request still matches the job and target it claims.
[<RequireQualifiedAccess>]
type PublicationCheck =
    | Matched
    | JobMismatch
    | TargetMismatch
    | CasMismatch

/// What the fast-forward actually did.
[<RequireQualifiedAccess>]
type FastForwardOutcome =
    | Landed of CommitHash
    | LandedMismatch of CommitHash
    | TargetMoved
    | Failed of string

/// Whether a failed rebase is a conflict this job owns.
[<RequireQualifiedAccess>]
type ConflictOwnership =
    | Conflict of files: string list
    | NotAConflict

/// Where a recovered job continues.
[<RequireQualifiedAccess>]
type RecoveryStep =
    | CleanUp
    | ReenterClaim
    | RunLoop

/// Why a durable publish claim cannot be re-entered.
[<RequireQualifiedAccess>]
type ClaimReentryRefusal =
    | TargetMismatch
    | MissingRecord
    | MissingRebased

/// What a durable publish claim may do after a restart.
[<RequireQualifiedAccess>]
type ClaimReentry =
    | VerifyHead
    | RunLoop
    | Refused of ClaimReentryRefusal

/// The publication decisions Change owns.
[<RequireQualifiedAccess>]
module PublicationLaw =
    val resume: record: ManagerJobProjection option -> RecoveryStep

    val reenterClaim:
        jobTargetRef: TargetRef ->
        claimTargetRef: TargetRef ->
        claimRebased: CommitHash ->
        claimExpected: CommitHash ->
        record: ManagerJobProjection option ->
            ClaimReentry

    val admitCandidate:
        candidate: CommitHash ->
        target: CommitHash ->
        rebased:
            {| RebasedCommit: CommitHash
               TargetHeadSnapshot: CommitHash |} option ->
            CandidateAdmission

    val certify:
        candidate: CommitHash ->
        expectedHead: CommitHash ->
        rebased:
            {| RebasedCommit: CommitHash
               TargetHeadSnapshot: CommitHash |} option ->
            PublicationCertification

    val rebaseFailed: conflictFiles: string list -> ConflictOwnership

    val checkPublication:
        jobId: ManagerJobId ->
        jobTargetRef: TargetRef ->
        evidenceJobId: ManagerJobId ->
        evidenceTargetRef: TargetRef ->
        evidenceExpectedHead: CommitHash ->
        current: CommitHash ->
            PublicationCheck

    val fastForward:
        merge: Result<CommitHash, string> -> candidate: CommitHash -> targetMovedError: string -> FastForwardOutcome

module OrchestratorProjection =
    val native: OrchestratorProjection -> obj
    val fromJob: obj -> ManagerJobProjection
    val toJob: ManagerJobProjection -> obj
    val applyEvent: obj -> OrchestratorProjection -> Result<OrchestratorProjection, OrchestratorFoldRejection>
    val empty: OrchestratorProjection
    val tryFind: ManagerJobId -> OrchestratorProjection -> ManagerJobProjection option
    val tryFindByByname: string -> OrchestratorProjection -> ManagerJobProjection option
    val tryWorktreeEffect: WorktreeIdentity -> OrchestratorProjection -> WorktreeEffectStatus option

    val decideWorktreeReconciliation:
        ManagerJobId ->
        WorktreeIdentity ->
        WorktreePath ->
        WorktreeReconciliationObservation ->
            WorktreeReconciliationDecision

    val requestWorktree:
        WorktreeIdentity -> WorktreePath -> ManagerJobId -> OrchestratorProjection -> OrchestratorProjection

    val acceptWorktree:
        WorktreeIdentity -> WorktreePath -> ManagerJobId -> OrchestratorProjection -> OrchestratorProjection

    val tryFindByManagerSession: SessionId -> OrchestratorProjection -> ManagerJobProjection option
    val activeJobs: OrchestratorProjection -> ManagerJobProjection list

    val createJob:
        {| ManagerJobId: ManagerJobId
           ManagerSessionId: SessionId
           ManagerAgent: string
           Byname: string
           WorktreeIdentity: WorktreeIdentity
           WorktreePath: WorktreePath
           TargetRef: TargetRef
           TargetBranchFrozen: string |} ->
        OrchestratorProjection ->
            OrchestratorProjection

    val recordCandidateReady:
        ManagerJobId -> {| CandidateCommit: CommitHash |} -> OrchestratorProjection -> OrchestratorProjection

    val recordConflictDetected:
        ManagerJobId ->
        {| CandidateCommit: CommitHash
           TargetHeadSnapshot: CommitHash
           ConflictFiles: string list
           DiagnosticsDigest: string |} ->
        OrchestratorProjection ->
            OrchestratorProjection

    val recordRebasedCandidateReady:
        ManagerJobId ->
        {| RebasedCommit: CommitHash
           TargetHeadSnapshot: CommitHash |} ->
        OrchestratorProjection ->
            OrchestratorProjection

    val recordPublishClaimed:
        ManagerJobId ->
        {| TargetRef: TargetRef
           RebasedCommit: CommitHash
           ExpectedHead: CommitHash |} ->
        OrchestratorProjection ->
            OrchestratorProjection

    val recordTerminal: ManagerJobId -> TerminalOutcome -> OrchestratorProjection -> OrchestratorProjection
    val classifyRebasedCandidate: CommitHash option -> CommitHash -> CommitHash -> RebasedCandidateReality
    val classifyPublishClaim: CommitHash option -> CommitHash -> CommitHash -> PublishClaimReality
