namespace Wanxiangshu.Change

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal

module internal ChangeInterop =
    let result value =
        match value with
        | Ok value -> createObj [ "ok" ==> true; "value" ==> value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

    let mapTask mapping operation =
        task {
            let! value = operation
            return value |> Result.map mapping |> result
        }

    let verdict (raw: obj) =
        let job = ManagerJobId.create raw?id

        match unbox<string> raw?kind with
        | "Published" -> OrchestratorVerdict.Published(job, CommitHash.create raw?head)
        | "PublishedPendingCleanup" ->
            OrchestratorVerdict.PublishedPendingCleanup(job, CommitHash.create raw?head, raw?error)
        | "Cancelled" -> OrchestratorVerdict.Cancelled job
        | "RejectedDirty" -> OrchestratorVerdict.RejectedDirty raw?error
        | "Empty" -> OrchestratorVerdict.Empty
        | _ -> OrchestratorVerdict.IntegrationFailed(job, raw?error)

    let fact (raw: obj) =
        let id = ManagerJobId.create raw?id
        let commit () = CommitHash.create raw?commit

        match unbox<string> raw?kind with
        | "CandidateReady" ->
            OrchestratorFact.CandidateReady
                {| ManagerJobId = id
                   CandidateCommit = commit () |}
        | "RebasedCandidateReady" ->
            OrchestratorFact.RebasedCandidateReady
                {| ManagerJobId = id
                   RebasedCommit = commit ()
                   TargetHeadSnapshot = CommitHash.create raw?target |}
        | "ConflictDetected" ->
            OrchestratorFact.ConflictDetected
                {| ManagerJobId = id
                   CandidateCommit = commit ()
                   TargetHeadSnapshot = CommitHash.create raw?target
                   ConflictFiles = unbox<string array> raw?files |> Array.toList
                   DiagnosticsDigest = raw?digest |}
        | "PublishClaimed" ->
            OrchestratorFact.PublishClaimed
                {| ManagerJobId = id
                   TargetRef = TargetRef.create raw?target
                   RebasedCommit = commit ()
                   ExpectedHead = CommitHash.create raw?expected |}
        | "Published" ->
            OrchestratorFact.Published
                {| ManagerJobId = id
                   CandidateCommit = CommitHash.create raw?candidate
                   ResultingTargetHead = CommitHash.create raw?head |}
        | "WorktreeCreateRequested" ->
            OrchestratorFact.WorktreeCreateRequested
                {| ManagerJobId = id
                   WorktreeIdentity = WorktreeIdentity.create raw?identity
                   WorktreePath = WorktreePath.create raw?path |}
        | "WorktreeCreated" ->
            OrchestratorFact.WorktreeCreated
                {| ManagerJobId = id
                   WorktreeIdentity = WorktreeIdentity.create raw?identity
                   WorktreePath = WorktreePath.create raw?path |}
        | "ManagerJobCreated" ->
            OrchestratorFact.ManagerJobCreated
                {| ManagerJobId = id
                   ManagerSessionId = SessionId.create raw?session
                   ManagerAgent = raw?agent
                   Byname = raw?byname
                   WorktreeIdentity = WorktreeIdentity.create raw?worktreeIdentity
                   WorktreePath = WorktreePath.create raw?worktreePath
                   TargetRef = TargetRef.create raw?target
                   TargetBranchFrozen = raw?targetBranchFrozen |}
        | other -> invalidOp ("Unknown change event: " + other)

    let collaborators (deps: OrchestratorProgramDeps) =
        let git =
            createObj
                [ "readHead"
                  ==> (fun path -> deps.Git.ReadHead(WorktreePath.create path) |> mapTask CommitHash.value)
                  "targetHead"
                  ==> (fun target -> deps.Git.GetTargetHead(TargetRef.create target) |> mapTask CommitHash.value)
                  "conflictedFiles"
                  ==> (fun path -> deps.Git.ConflictedFiles(WorktreePath.create path) |> mapTask List.toArray)
                  "rebase"
                  ==> (fun path target ->
                      deps.Git.Rebase (WorktreePath.create path) (TargetRef.create target)
                      |> mapTask id)
                  "ffMerge"
                  ==> (fun path target expected candidate ->
                      deps.Git.FfMerge
                          (WorktreePath.create path)
                          (TargetRef.create target)
                          (CommitHash.create expected)
                          (CommitHash.create candidate)
                      |> mapTask CommitHash.value) ]

        let relay =
            createObj
                [ "prepareCandidate"
                  ==> (fun job -> deps.Relay.PrepareCandidate(ManagerJobId.create job) |> mapTask CommitHash.value)
                  "continueLoop"
                  ==> (fun job -> deps.Relay.ContinueLoop(ManagerJobId.create job) |> mapTask (fun _ -> null))
                  "awaitSignal"
                  ==> (fun job ->
                      deps.Relay.AwaitLoopSignal(ManagerJobId.create job)
                      |> mapTask (function
                          | ManagerLoopSignal.Continue -> createObj [ "kind" ==> "Continue" ]
                          | ManagerLoopSignal.Candidate -> createObj [ "kind" ==> "Candidate" ]
                          | ManagerLoopSignal.ExceptionalTerminal reason ->
                              createObj [ "kind" ==> "ExceptionalTerminal"; "reason" ==> reason ]))
                  "terminateResources"
                  ==> (fun job -> deps.Relay.TerminateWorkResources(ManagerJobId.create job)) ]

        createObj
            [ "git" ==> git
              "relay" ==> relay
              "history"
              ==> (fun () -> (deps.Snapshot()).AgentProjections.Orchestrator |> OrchestratorProjection.native)
              "append"
              ==> (fun raw -> deps.AppendFact StreamId.Workspace (fact raw) |> mapTask id)
              "acquireGate"
              ==> (fun () ->
                  task {
                      let! lease = deps.AcquirePublishGate()
                      return createObj [ "release" ==> (fun () -> lease.Release()) ]
                  })
              "sha256" ==> (fun body -> HostDigest.sha256Hex body)
              "isCancellation" ==> (fun (error: exn) -> error :? OperationCanceledException) ]

    let job (job: ManagerJob) =
        createObj
            [ "id" ==> ManagerJobId.value job.JobId
              "target" ==> TargetRef.value job.TargetRef
              "path" ==> WorktreePath.value job.Worktree.Path
              "release" ==> (fun () -> job.Worktree.Release() |> mapTask id) ]

module OrchestratorProgram =
    [<Import("ChangeJob", "wanxiangli/change")>]
    let private jobClass: obj = jsNative

    let run (deps: OrchestratorProgramDeps) (job: ManagerJob) =
        task {
            try
                let runner = createNew jobClass ()
                let! outcome = unbox<Task<obj>> (runner?run (ChangeInterop.job job, ChangeInterop.collaborators deps))
                return ChangeInterop.verdict outcome
            with
            | :? OperationCanceledException -> return OrchestratorVerdict.Cancelled job.JobId
            | error -> return OrchestratorVerdict.IntegrationFailed(job.JobId, sprintf "%A" error)
        }
