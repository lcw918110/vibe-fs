namespace Wanxiangshu.Change

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Work
open Fable.Core
open Fable.Core.JsInterop

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

/// Complete evidence presented to the pure worktree reconciliation boundary.
/// Physical evidence is representable only for the ambiguous Requested state.
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

type OrchestratorProjection = private { History: obj }

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

module private NativePublication =
    [<Fable.Core.Import("ChangePublication", "wanxiangli/change")>]
    let constructor: obj = jsNative
    let value: obj = Fable.Core.JsInterop.createNew constructor ()
    let rebased (ready: {| RebasedCommit: CommitHash; TargetHeadSnapshot: CommitHash |}) = createObj [ "commit" ==> CommitHash.value ready.RebasedCommit; "target" ==> CommitHash.value ready.TargetHeadSnapshot ]
    let optional mapping value = value |> Option.map (mapping >> box) |> Option.defaultValue null
    let record (job: ManagerJobProjection) = createObj [ "rebased" ==> optional rebased job.RebasedCandidateReady; "terminal" ==> optional (fun _ -> true) job.Terminal; "claim" ==> optional (fun _ -> true) job.PublishClaimed ]

[<RequireQualifiedAccess>]
module PublicationLaw =
    let resume (record: ManagerJobProjection option) : RecoveryStep =
        match unbox<string> (NativePublication.value?resume(NativePublication.optional NativePublication.record record)) with
        | "CleanUp" -> RecoveryStep.CleanUp | "ReenterClaim" -> RecoveryStep.ReenterClaim | _ -> RecoveryStep.RunLoop
    let reenterClaim (jobTargetRef: TargetRef) (claimTargetRef: TargetRef) (claimRebased: CommitHash) (claimExpected: CommitHash) (record: ManagerJobProjection option) : ClaimReentry =
        match unbox<string> (NativePublication.value?reenterClaim(TargetRef.value jobTargetRef, TargetRef.value claimTargetRef, CommitHash.value claimRebased, CommitHash.value claimExpected, NativePublication.optional NativePublication.record record)) with
        | "TargetMismatch" -> ClaimReentry.Refused ClaimReentryRefusal.TargetMismatch
        | "MissingRecord" -> ClaimReentry.Refused ClaimReentryRefusal.MissingRecord
        | "MissingRebased" -> ClaimReentry.Refused ClaimReentryRefusal.MissingRebased
        | "VerifyHead" -> ClaimReentry.VerifyHead | _ -> ClaimReentry.RunLoop
    let admitCandidate (candidate: CommitHash) (target: CommitHash) (rebased: {| RebasedCommit: CommitHash; TargetHeadSnapshot: CommitHash |} option) : CandidateAdmission =
        match unbox<string> (NativePublication.value?admitCandidate(CommitHash.value candidate, CommitHash.value target, NativePublication.optional NativePublication.rebased rebased)) with
        | "PublishCertified" -> CandidateAdmission.PublishCertified | reason -> CandidateAdmission.Rebase reason
    let certify (candidate: CommitHash) (expectedHead: CommitHash) (rebased: {| RebasedCommit: CommitHash; TargetHeadSnapshot: CommitHash |} option) : PublicationCertification =
        match unbox<string> (NativePublication.value?certify(CommitHash.value candidate, CommitHash.value expectedHead, NativePublication.optional NativePublication.rebased rebased)) with
        | "MissingRebased" -> PublicationCertification.MissingRebased
        | "CandidatePinMismatch" -> PublicationCertification.CandidatePinMismatch
        | "SnapshotPinMismatch" -> PublicationCertification.SnapshotPinMismatch
        | _ -> PublicationCertification.Certified
    let rebaseFailed (conflictFiles: string list) : ConflictOwnership =
        match unbox<string> (NativePublication.value?rebaseFailed(List.toArray conflictFiles)) with
        | "Conflict" -> ConflictOwnership.Conflict conflictFiles | _ -> ConflictOwnership.NotAConflict
    let checkPublication (jobId: ManagerJobId) (jobTargetRef: TargetRef) (evidenceJobId: ManagerJobId) (evidenceTargetRef: TargetRef) (evidenceExpectedHead: CommitHash) (current: CommitHash) : PublicationCheck =
        match unbox<string> (NativePublication.value?checkPublication(ManagerJobId.value jobId, TargetRef.value jobTargetRef, ManagerJobId.value evidenceJobId, TargetRef.value evidenceTargetRef, CommitHash.value evidenceExpectedHead, CommitHash.value current)) with
        | "JobMismatch" -> PublicationCheck.JobMismatch | "TargetMismatch" -> PublicationCheck.TargetMismatch | "CasMismatch" -> PublicationCheck.CasMismatch | _ -> PublicationCheck.Matched
    let fastForward (merge: Result<CommitHash, string>) (candidate: CommitHash) (targetMovedError: string) : FastForwardOutcome =
        let merged = match merge with | Ok commit -> createObj [ "ok" ==> true; "value" ==> CommitHash.value commit ] | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
        let raw: obj = NativePublication.value?fastForward(merged, CommitHash.value candidate, targetMovedError)
        match unbox<string> raw?kind with
        | "Landed" -> FastForwardOutcome.Landed(CommitHash.create raw?head)
        | "LandedMismatch" -> FastForwardOutcome.LandedMismatch(CommitHash.create raw?head)
        | "TargetMoved" -> FastForwardOutcome.TargetMoved | _ -> FastForwardOutcome.Failed raw?error

module OrchestratorProjection =
    [<Fable.Core.Import("ChangeHistory", "wanxiangli/change")>]
    let private constructor: obj = jsNative

    let empty: OrchestratorProjection = { History = createNew constructor () }
    let native (projection: OrchestratorProjection) = projection.History
    let private optional decode (value: obj) = if isNull value then None else Some(decode value)
    let private text (value: obj) key : string = value?(key)
    let fromJob (value: obj) : ManagerJobProjection =
        let commit value key = CommitHash.create (text value key)
        let terminal value =
            match text value "kind" with
            | "Published" -> TerminalOutcome.Published {| CandidateCommit = commit value "candidate"; ResultingTargetHead = commit value "head" |}
            | "Failed" -> TerminalOutcome.Failed(text value "reason")
            | _ -> TerminalOutcome.Abandoned
        { ManagerJobId = ManagerJobId.create (text value "id")
          ManagerSessionId = SessionId.create (text value "session")
          ManagerAgent = text value "agent"
          Byname = text value "byname"
          WorktreeIdentity = WorktreeIdentity.create (text value "worktreeIdentity")
          WorktreePath = WorktreePath.create (text value "worktreePath")
          TargetRef = TargetRef.create (text value "target")
          TargetBranchFrozen = text value "targetBranchFrozen"
          CandidateReady = optional (fun item -> {| CandidateCommit = commit item "commit" |}) value?candidate
          ConflictDetected = optional (fun item -> {| CandidateCommit = commit item "commit"; TargetHeadSnapshot = commit item "target"; ConflictFiles = unbox<string array> item?files |> Array.toList; DiagnosticsDigest = text item "digest" |}) value?conflict
          RebasedCandidateReady = optional (fun item -> {| RebasedCommit = commit item "commit"; TargetHeadSnapshot = commit item "target" |}) value?rebased
          PublishClaimed = optional (fun item -> {| TargetRef = TargetRef.create (text item "target"); RebasedCommit = commit item "commit"; ExpectedHead = commit item "expected" |}) value?claim
          Terminal = optional terminal value?terminal }

    let toJob (job: ManagerJobProjection) : obj =
        let option (mapping: 'a -> obj) (value: 'a option) : obj = value |> Option.map mapping |> Option.toObj
        let terminal value =
            match value with
            | TerminalOutcome.Published data -> createObj [ "kind" ==> "Published"; "candidate" ==> CommitHash.value data.CandidateCommit; "head" ==> CommitHash.value data.ResultingTargetHead ]
            | TerminalOutcome.Failed reason -> createObj [ "kind" ==> "Failed"; "reason" ==> reason ]
            | TerminalOutcome.Abandoned -> createObj [ "kind" ==> "Abandoned" ]
        createObj [
            "id" ==> ManagerJobId.value job.ManagerJobId; "session" ==> SessionId.value job.ManagerSessionId
            "agent" ==> job.ManagerAgent; "byname" ==> job.Byname
            "worktreeIdentity" ==> WorktreeIdentity.value job.WorktreeIdentity; "worktreePath" ==> WorktreePath.value job.WorktreePath
            "target" ==> TargetRef.value job.TargetRef; "targetBranchFrozen" ==> job.TargetBranchFrozen
            "candidate" ==> option (fun (data: {| CandidateCommit: CommitHash |}) -> createObj [ "commit" ==> CommitHash.value data.CandidateCommit ]) job.CandidateReady
            "conflict" ==> option (fun (data: {| CandidateCommit: CommitHash; TargetHeadSnapshot: CommitHash; ConflictFiles: string list; DiagnosticsDigest: string |}) -> createObj [ "commit" ==> CommitHash.value data.CandidateCommit; "target" ==> CommitHash.value data.TargetHeadSnapshot; "files" ==> List.toArray data.ConflictFiles; "digest" ==> data.DiagnosticsDigest ]) job.ConflictDetected
            "rebased" ==> option (fun (data: {| RebasedCommit: CommitHash; TargetHeadSnapshot: CommitHash |}) -> createObj [ "commit" ==> CommitHash.value data.RebasedCommit; "target" ==> CommitHash.value data.TargetHeadSnapshot ]) job.RebasedCandidateReady
            "claim" ==> option (fun (data: {| TargetRef: TargetRef; RebasedCommit: CommitHash; ExpectedHead: CommitHash |}) -> createObj [ "target" ==> TargetRef.value data.TargetRef; "commit" ==> CommitHash.value data.RebasedCommit; "expected" ==> CommitHash.value data.ExpectedHead ]) job.PublishClaimed
            "terminal" ==> option terminal job.Terminal ]

    let tryFind (id: ManagerJobId) projection = projection.History?job (ManagerJobId.value id) |> optional fromJob
    let tryFindByByname (name: string) projection = projection.History?jobByName name |> optional fromJob
    let tryFindByManagerSession (id: SessionId) projection = projection.History?jobBySession (SessionId.value id) |> optional fromJob
    let activeJobs projection = unbox<obj array> (projection.History?activeJobs ()) |> Array.map fromJob |> Array.toList
    let tryWorktreeEffect (identity: WorktreeIdentity) projection =
        projection.History?worktreeEffect (WorktreeIdentity.value identity)
        |> optional (fun value ->
            let data = {| ManagerJobId = ManagerJobId.create (text value "id"); WorktreePath = WorktreePath.create (text value "path") |}
            if text value "kind" = "Requested" then WorktreeEffectStatus.Requested data else WorktreeEffectStatus.Created data)

    let applyEvent (event: obj) projection : Result<OrchestratorProjection, OrchestratorFoldRejection> =
        let result: obj = projection.History?apply event
        if unbox<bool> result?ok then Ok { History = result?history }
        elif text result "error" = "PublishClaimedWithoutRebasedCandidate" then Error OrchestratorFoldRejection.PublishClaimedWithoutRebasedCandidate
        else Error OrchestratorFoldRejection.PublishClaimedCommitMismatch

    let private record kind id fields projection =
        let event = createObj ([ "kind" ==> kind; "id" ==> ManagerJobId.value id ] @ fields)
        match applyEvent event projection with
        | Ok next -> next
        | Error error -> invalidOp (OrchestratorFoldRejection.message error)

    let requestWorktree identity path id projection =
        record "WorktreeCreateRequested" id [ "identity" ==> WorktreeIdentity.value identity; "path" ==> WorktreePath.value path ] projection
    let acceptWorktree identity path id projection =
        record "WorktreeCreated" id [ "identity" ==> WorktreeIdentity.value identity; "path" ==> WorktreePath.value path ] projection
    let createJob (job: {| ManagerJobId: ManagerJobId; ManagerSessionId: SessionId; ManagerAgent: string; Byname: string; WorktreeIdentity: WorktreeIdentity; WorktreePath: WorktreePath; TargetRef: TargetRef; TargetBranchFrozen: string |}) projection =
        record "ManagerJobCreated" job.ManagerJobId [ "session" ==> SessionId.value job.ManagerSessionId; "agent" ==> job.ManagerAgent; "byname" ==> job.Byname; "worktreeIdentity" ==> WorktreeIdentity.value job.WorktreeIdentity; "worktreePath" ==> WorktreePath.value job.WorktreePath; "target" ==> TargetRef.value job.TargetRef; "targetBranchFrozen" ==> job.TargetBranchFrozen ] projection
    let recordCandidateReady id (payload: {| CandidateCommit: CommitHash |}) projection =
        record "CandidateReady" id [ "commit" ==> CommitHash.value payload.CandidateCommit ] projection
    let recordConflictDetected id (payload: {| CandidateCommit: CommitHash; TargetHeadSnapshot: CommitHash; ConflictFiles: string list; DiagnosticsDigest: string |}) projection =
        record "ConflictDetected" id [ "commit" ==> CommitHash.value payload.CandidateCommit; "target" ==> CommitHash.value payload.TargetHeadSnapshot; "files" ==> Array.ofList payload.ConflictFiles; "digest" ==> payload.DiagnosticsDigest ] projection
    let recordRebasedCandidateReady id (payload: {| RebasedCommit: CommitHash; TargetHeadSnapshot: CommitHash |}) projection =
        record "RebasedCandidateReady" id [ "commit" ==> CommitHash.value payload.RebasedCommit; "target" ==> CommitHash.value payload.TargetHeadSnapshot ] projection
    let recordPublishClaimed id (payload: {| TargetRef: TargetRef; RebasedCommit: CommitHash; ExpectedHead: CommitHash |}) projection =
        record "PublishClaimed" id [ "target" ==> TargetRef.value payload.TargetRef; "commit" ==> CommitHash.value payload.RebasedCommit; "expected" ==> CommitHash.value payload.ExpectedHead ] projection
    let recordTerminal id terminal projection =
        match terminal with
        | TerminalOutcome.Published payload -> record "Published" id [ "candidate" ==> CommitHash.value payload.CandidateCommit; "head" ==> CommitHash.value payload.ResultingTargetHead ] projection
        | TerminalOutcome.Failed reason -> record "JobFailed" id [ "reason" ==> reason ] projection
        | TerminalOutcome.Abandoned -> record "JobAbandoned" id [] projection
    /// PERSIST-009: decide from durable intent plus, only when Requested is
    /// ambiguous, one complete physical worktree observation.
    let decideWorktreeReconciliation (jobId: ManagerJobId) (identity: WorktreeIdentity) (path: WorktreePath) (observation: WorktreeReconciliationObservation) : WorktreeReconciliationDecision =
        let request = createObj [ "id" ==> ManagerJobId.value jobId; "identity" ==> WorktreeIdentity.value identity; "path" ==> WorktreePath.value path ]
        let observed =
            let fields kind id path = [ "kind" ==> kind; "id" ==> ManagerJobId.value id; "path" ==> WorktreePath.value path ]
            match observation with
            | WorktreeReconciliationObservation.NoDurableEffect -> createObj [ "kind" ==> "None" ]
            | WorktreeReconciliationObservation.CreatedReceipt(id, path) -> createObj (fields "Created" id path)
            | WorktreeReconciliationObservation.RequestedConflict(id, path) -> createObj (fields "Conflict" id path)
            | WorktreeReconciliationObservation.RequestedAmbiguity(id, path, physical) ->
                let physical = match physical with
                               | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
                               | Ok entries -> createObj [ "ok" ==> true; "value" ==> (entries |> List.map (fun (path, identity) -> createObj [ "path" ==> WorktreePath.value path; "identity" ==> (identity |> Option.map WorktreeIdentity.value |> Option.toObj) ]) |> List.toArray) ]
                createObj (fields "Requested" id path @ [ "physical" ==> physical ])
        let raw: obj = NativePublication.value?reconcile(request, observed)
        match unbox<string> raw?kind with
        | "RequestThenCreate" -> WorktreeReconciliationDecision.RequestThenCreate
        | "CreateAfterProvenMissing" -> WorktreeReconciliationDecision.CreateAfterProvenMissing
        | "AdoptThenRecordCreated" -> WorktreeReconciliationDecision.AdoptThenRecordCreated
        | "AdoptCreated" -> WorktreeReconciliationDecision.AdoptCreated
        | _ ->
            let reason = match unbox<string> raw?reason with
                         | "WorktreeQueryFailed" -> WorktreeReconciliationFailure.WorktreeQueryFailed raw?error
                         | "DurableOwnershipConflict" -> WorktreeReconciliationFailure.DurableOwnershipConflict
                         | _ -> WorktreeReconciliationFailure.PhysicalIdentityPathConflict
            WorktreeReconciliationDecision.Reject reason

    let classifyRebasedCandidate (currentHead: CommitHash option) (rebasedCommit: CommitHash) (targetHeadSnapshot: CommitHash) : RebasedCandidateReality =
        match unbox<string> (NativePublication.value?classifyRebased(NativePublication.optional CommitHash.value currentHead, CommitHash.value targetHeadSnapshot)) with
        | "HeadUnreadable" -> RebasedCandidateReality.HeadUnreadable | "PublishReady" -> RebasedCandidateReality.PublishReady | _ -> RebasedCandidateReality.NeedsRebase
    let classifyPublishClaim (currentHead: CommitHash option) (rebasedCommit: CommitHash) (expectedHead: CommitHash) : PublishClaimReality =
        match unbox<string> (NativePublication.value?classifyClaim(NativePublication.optional CommitHash.value currentHead, CommitHash.value rebasedCommit, CommitHash.value expectedHead)) with
        | "HeadUnreadable" -> PublishClaimReality.HeadUnreadable | "AlreadyFastForwarded" -> PublishClaimReality.AlreadyFastForwarded | "PublishReady" -> PublishClaimReality.PublishReady | _ -> PublishClaimReality.ClaimExpired
