namespace Wanxiangshu.Change

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

module OrchestratorFactFold =
    let fold (orchestrator: OrchestratorProjection) (fact: OrchestratorFactCases) : Result<OrchestratorProjection, OrchestratorFoldRejection> =
        let event kind id fields = createObj ([ "kind" ==> kind; "id" ==> ManagerJobId.value id ] @ fields)
        let wire =
            match fact with
            | OrchestratorFactCases.ManagerJobCreated value ->
                event "ManagerJobCreated" value.ManagerJobId [ "session" ==> SessionId.value value.ManagerSessionId; "agent" ==> value.ManagerAgent; "byname" ==> value.Byname; "worktreeIdentity" ==> WorktreeIdentity.value value.WorktreeIdentity; "worktreePath" ==> WorktreePath.value value.WorktreePath; "target" ==> TargetRef.value value.TargetRef; "targetBranchFrozen" ==> value.TargetBranchFrozen ]
            | OrchestratorFactCases.CandidateReady value ->
                event "CandidateReady" value.ManagerJobId [ "commit" ==> CommitHash.value value.CandidateCommit ]
            | OrchestratorFactCases.ConflictDetected value ->
                event "ConflictDetected" value.ManagerJobId [ "commit" ==> CommitHash.value value.CandidateCommit; "target" ==> CommitHash.value value.TargetHeadSnapshot; "files" ==> List.toArray value.ConflictFiles; "digest" ==> value.DiagnosticsDigest ]
            | OrchestratorFactCases.RebasedCandidateReady value ->
                event "RebasedCandidateReady" value.ManagerJobId [ "commit" ==> CommitHash.value value.RebasedCommit; "target" ==> CommitHash.value value.TargetHeadSnapshot ]
            | OrchestratorFactCases.PublishClaimed value ->
                event "PublishClaimed" value.ManagerJobId [ "target" ==> TargetRef.value value.TargetRef; "commit" ==> CommitHash.value value.RebasedCommit; "expected" ==> CommitHash.value value.ExpectedHead ]
            | OrchestratorFactCases.Published value ->
                event "Published" value.ManagerJobId [ "candidate" ==> CommitHash.value value.CandidateCommit; "head" ==> CommitHash.value value.ResultingTargetHead ]
            | OrchestratorFactCases.JobFailed value -> event "JobFailed" value.ManagerJobId [ "reason" ==> value.Reason ]
            | OrchestratorFactCases.JobAbandoned value -> event "JobAbandoned" value.ManagerJobId []
            | OrchestratorFactCases.WorktreeCreateRequested value ->
                event "WorktreeCreateRequested" value.ManagerJobId [ "identity" ==> WorktreeIdentity.value value.WorktreeIdentity; "path" ==> WorktreePath.value value.WorktreePath ]
            | OrchestratorFactCases.WorktreeCreated value ->
                event "WorktreeCreated" value.ManagerJobId [ "identity" ==> WorktreeIdentity.value value.WorktreeIdentity; "path" ==> WorktreePath.value value.WorktreePath ]
        OrchestratorProjection.applyEvent wire orchestrator
