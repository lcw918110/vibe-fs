namespace Wanxiangshu.Change

open System.IO
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Persistence.Journal

module private OrchestratorRuntimeDecisions =
    /// Durable terminal fact closes the job permanently (GLORY-068).
    let requireActiveJob (record: ManagerJobProjection) : Result<ManagerJobProjection, string> =
        match record.Terminal with
        | Some _ -> Error(sprintf "Manager job is no longer active: %s" (ManagerJobId.value record.ManagerJobId))
        | None -> Ok record

    let mapTaskError (mapper: 'error -> 'mapped) (operation: Task<Result<'value, 'error>>) =
        task {
            let! result = operation
            return Result.mapError mapper result
        }

    let releaseWorktreeOnError (worktree: WorktreeResource) (operation: Task<Result<'value, 'error>>) =
        task {
            match! operation with
            | Ok value -> return Ok value
            | Error error ->
                let! _ = worktree.Release()
                return Error error
        }

    /// `AdoptCreated` re-enters an admission whose `WorktreeCreated` is already
    /// durable, so it owes no second fact. Every other reconciliation still owes
    /// the fact for the physical worktree it just acquired.
    let pendingWorktreeCreatedFact (decision: WorktreeReconciliationDecision) fact =
        match decision with
        | WorktreeReconciliationDecision.AdoptCreated -> None
        | _ -> Some fact

    /// A journaled admission has durable worktree evidence, so a failed manager
    /// start leaves the physical worktree for recovery. Without a journal nothing
    /// records the worktree, so failure must release it.
    let releaseWorktreeUnlessJournaled journalPort worktree operation =
        journalPort
        |> Option.map (fun _ -> operation)
        |> Option.defaultWith (fun () -> operation |> releaseWorktreeOnError worktree)

/// Runtime owner for ManagerJob resources. Every job runs the sequential
/// direct-CE OrchestratorProgram workflow; the mailbox contains only final post-FF verdicts.
///
/// ORCH-006's `ManagerJobCreated` is the only writer here, and it is written after
/// the Manager fork returns a `SessionId` but before the program awaits it. That
/// ordering is what makes a crash recoverable in either direction: no fact means no
/// job, and a fact means a live Manager whose session is known.
type Orchestrator
    (
        observer: IWaitObserver,
        git: GitPort,
        relay: RelayPort,
        repoPath: string,
        targetRef: TargetRef,
        ?journal: OrchestratorJournalPort,
        ?lockRepoPath: string
    ) =

    let mailbox = VerdictMailbox(observer)
    let journalPort = journal

    let gatePath =
        IntegrationGate.lockPath (defaultArg lockRepoPath repoPath) (TargetRef.value targetRef)

    let appendFact stream fact =
        taskResult {
            match journalPort with
            | None -> return ()
            | Some port ->
                let! _ = port.AppendFact stream fact
                return ()
        }

    let snapshot () =
        journalPort
        |> Option.map (fun port -> port.Snapshot())
        |> Option.defaultValue Fold.empty

    let acquirePublishGate () =
        task {
            let! gate = IntegrationGate.acquire gatePath
            return { Release = fun () -> gate.Release() }
        }

    let programDeps: OrchestratorProgramDeps =
        { Git = git
          Relay = relay
          AppendFact = appendFact
          Snapshot = snapshot
          AcquirePublishGate = acquirePublishGate }

    let startPublication (job: ManagerJob) =
        mailbox.StartJob()

        task {
            let! verdict = OrchestratorProgram.run programDeps job

            mailbox.Publish verdict
        }
        |> ignore

    let defaultWorktreePath (jobId: ManagerJobId) =
        WorktreePath.create (Path.Combine(Path.GetTempPath(), sprintf "wanxiangshu-%s" (ManagerJobId.value jobId)))

    /// ORCH-004: a new ManagerJob.
    ///
    /// `managerAgent` is required and never defaulted. ORCH-003 keeps one Manager per
    /// job for its whole life, and PROMPT-008 forbids rebuilding a managed name from
    /// a role — the managed agent name lives here and nowhere else.
    let forkManagerCore
        (jobId: ManagerJobId)
        (managerAgent: string)
        (byname: string)
        (prompt: string)
        (expectedToolCalls: int option)
        (worktreePath: WorktreePath option)
        : Task<Result<OrchestratorHandle, OrchestratorVerdict>> =
        taskResult {
            let integration failurePrefix error =
                OrchestratorVerdict.IntegrationFailed(jobId, sprintf "%s: %s" failurePrefix error)

            let! blocking =
                git.IsDirty(WorktreePath.create repoPath)
                |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to inspect target cleanliness")

            do!
                match blocking with
                | [] -> Ok()
                | paths ->
                    Error(OrchestratorVerdict.RejectedDirty(sprintf "Worktree is dirty: %s" (String.concat ", " paths)))

            let path = defaultArg worktreePath (defaultWorktreePath jobId)
            // PERSIST-009: effect identity is deterministic before git runs.
            let identity =
                GitPorcelain.managerBranchOf (ManagerJobId.value jobId)
                |> WorktreeIdentity.create

            let requestFact =
                OrchestratorFact.WorktreeCreateRequested
                    {| ManagerJobId = jobId
                       WorktreeIdentity = identity
                       WorktreePath = path |}

            let durableEffect =
                (snapshot ()).AgentProjections.Orchestrator
                |> OrchestratorProjection.tryWorktreeEffect identity

            let! observation =
                match durableEffect with
                | None ->
                    Task.FromResult WorktreeReconciliationObservation.NoDurableEffect
                    |> TaskResultCE.ofTask
                | Some(WorktreeEffectStatus.Created receipt) ->
                    Task.FromResult(
                        WorktreeReconciliationObservation.CreatedReceipt(receipt.ManagerJobId, receipt.WorktreePath)
                    )
                    |> TaskResultCE.ofTask
                | Some(WorktreeEffectStatus.Requested request) when
                    request.ManagerJobId <> jobId || request.WorktreePath <> path
                    ->
                    Task.FromResult(
                        WorktreeReconciliationObservation.RequestedConflict(request.ManagerJobId, request.WorktreePath)
                    )
                    |> TaskResultCE.ofTask
                | Some(WorktreeEffectStatus.Requested request) ->
                    task {
                        let! physical = git.Worktrees.List()

                        return
                            WorktreeReconciliationObservation.RequestedAmbiguity(
                                request.ManagerJobId,
                                request.WorktreePath,
                                physical
                            )
                    }
                    |> TaskResultCE.ofTask

            let decision =
                OrchestratorProjection.decideWorktreeReconciliation jobId identity path observation

            let reconciliationFailure failure =
                match failure with
                | WorktreeReconciliationFailure.DurableOwnershipConflict ->
                    "Durable worktree intent conflicts with the requested job or path"
                | WorktreeReconciliationFailure.WorktreeQueryFailed error ->
                    sprintf "Failed to query requested worktree ambiguity: %s" error
                | WorktreeReconciliationFailure.PhysicalIdentityPathConflict ->
                    "Physical worktree identity/path evidence conflicts with the durable request"

            do!
                match decision with
                | WorktreeReconciliationDecision.RequestThenCreate ->
                    appendFact StreamId.Workspace requestFact
                    |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to persist worktree request")
                | WorktreeReconciliationDecision.Reject failure ->
                    Error(integration "Worktree reconciliation rejected" (reconciliationFailure failure))
                    |> Task.FromResult
                | _ -> Ok() |> Task.FromResult

            let acquireWorktree () =
                match decision with
                | WorktreeReconciliationDecision.RequestThenCreate
                | WorktreeReconciliationDecision.CreateAfterProvenMissing ->
                    // The isolated worktree is created from the frozen target
                    // ref: it sees that ref's committed content, and the main
                    // worktree's unrelated drafts stay where they are.
                    git.Worktrees.Create(identity, path, targetRef)
                    |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to create worktree")
                | WorktreeReconciliationDecision.AdoptThenRecordCreated
                | WorktreeReconciliationDecision.AdoptCreated ->
                    Ok(git.Worktrees.Adopt(identity, path)) |> Task.FromResult
                | WorktreeReconciliationDecision.Reject failure ->
                    Error(integration "Worktree reconciliation rejected" (reconciliationFailure failure))
                    |> Task.FromResult

            let persistWorktreeCreated createdFact =
                appendFact StreamId.Workspace createdFact
                |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to persist worktree created")

            let recordWorktreeCreated (worktree: WorktreeResource) =
                OrchestratorFact.WorktreeCreated
                    {| ManagerJobId = jobId
                       WorktreeIdentity = worktree.Identity
                       WorktreePath = path |}
                |> OrchestratorRuntimeDecisions.pendingWorktreeCreatedFact decision
                |> Option.map persistWorktreeCreated
                |> Option.defaultWith (fun () -> Ok() |> Task.FromResult)
                |> OrchestratorRuntimeDecisions.releaseWorktreeOnError worktree

            let createManagerSession (worktree: WorktreeResource) =
                taskResult {
                    // The Manager session is created before the job fact because
                    // ORCH-006 persists its SessionId. Its first prompt remains
                    // deferred until ManagerJobCreated is durable.
                    let! managerSessionId =
                        relay.CreateManagerSession
                            { JobId = jobId
                              ManagerAgent = managerAgent
                              Worktree = path
                              RootRequest = prompt
                              ExpectedToolCalls = expectedToolCalls }
                        |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to create manager session")

                    let fact =
                        OrchestratorFact.ManagerJobCreated
                            {| ManagerJobId = jobId
                               ManagerSessionId = managerSessionId
                               ManagerAgent = managerAgent
                               Byname = byname
                               WorktreeIdentity = worktree.Identity
                               WorktreePath = path
                               TargetRef = targetRef
                               TargetBranchFrozen = TargetRef.value targetRef |}

                    do!
                        appendFact StreamId.Workspace fact
                        |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to persist manager job")

                    let job =
                        { JobId = jobId
                          ManagerSessionId = managerSessionId
                          ManagerAgent = managerAgent
                          TargetRef = targetRef
                          Worktree = worktree }

                    return job
                }

            return!
                task {
                    let! admissionGate = IntegrationGate.acquire gatePath

                    let! outcome =
                        task {
                            try
                                return!
                                    taskResult {
                                        let! worktree = acquireWorktree ()
                                        do! recordWorktreeCreated worktree
                                        journalPort |> Option.iter (fun _ -> worktree.MarkDurable())

                                        return!
                                            createManagerSession worktree
                                            |> OrchestratorRuntimeDecisions.releaseWorktreeUnlessJournaled
                                                journalPort
                                                worktree
                                    }
                            with ex ->
                                return Error(integration "Manager admission failed" ex.Message)
                        }

                    do! admissionGate.Release()

                    return!
                        taskResult {
                            let! job = outcome

                            do!
                                relay.ActivateManager jobId
                                |> OrchestratorRuntimeDecisions.mapTaskError (integration "Failed to activate manager")

                            startPublication job
                            return job.Handle
                        }
                }

        }

    member _.ForkManager
        (
            jobId: ManagerJobId,
            managerAgent: string,
            prompt: string,
            ?worktreePath: WorktreePath,
            ?byname: string,
            ?expectedToolCalls: int
        ) =
        let providerByname =
            match byname with
            | Some value when not (System.String.IsNullOrWhiteSpace value) -> value.Trim()
            | _ -> managerAgent

        forkManagerCore jobId managerAgent providerByname prompt expectedToolCalls worktreePath

    /// ORCH-007: resume a persisted job. The worktree is adopted by its durable
    /// identity, never recreated, and the Manager is the one the fact names.
    member _.RecoverManagerJob(record: ManagerJobProjection) =
        let worktree = git.Worktrees.Adopt(record.WorktreeIdentity, record.WorktreePath)

        startPublication
            { JobId = record.ManagerJobId
              ManagerSessionId = record.ManagerSessionId
              ManagerAgent = record.ManagerAgent
              TargetRef = record.TargetRef
              Worktree = worktree }

    /// EXEC-019: bounded FIFO batch with local interrupt (≠ lifecycle Cancel).
    member _.JoinPublishedBatch(maxCount: int, interrupt: Task<JoinInterruptReason>) =
        mailbox.JoinAvailable(maxCount, interrupt)
