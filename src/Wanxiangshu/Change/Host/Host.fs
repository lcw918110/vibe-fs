namespace Wanxiangshu.Change.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Change
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

/// The durable loop signal one Manager work holds for Change.
///
/// An open tenure owns the road. `Deliver` commits a Relay together with its
/// successor incumbency in the same record, so a Relayed retirement whose
/// successor is already walking has no fresh signal to give: ManagerWorkflow owns
/// that hand-off's one physical prompt through the ManagerLoopGate occasion, and
/// Change's part is to wait on durable change (`awaitDispatchedWait`). Reporting
/// the consumed retirement again on every read instead drives the program through
/// `AwaitLoopSignal` → `ContinueLoop` without ever blocking; in the plugin build
/// that unbounded promise chain starves the Host's event loop, so the road freezes
/// exactly at its first relayed retirement.
///
/// Only a work with no open tenure can hold a signal: a Delivered retirement owes
/// the publication candidate, an unnamed confirmation fails closed, and no
/// retirement at all is not permission to continue.
[<RequireQualifiedAccess>]
module DurableLoopSignal =
    let ofWork (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
        match work.ActiveId, work.Retirements |> List.tryLast with
        | Some _, _ -> None
        | None, Some retirement when retirement.Outcome = "Delivered" -> Some ManagerLoopSignal.Candidate
        | None, Some retirement when retirement.Outcome = "Relayed" -> Some ManagerLoopSignal.Continue
        | None, Some retirement ->
            Some(
                ManagerLoopSignal.ExceptionalTerminal(
                    sprintf "Retirement %s has no durable Relayed or Delivered confirmation" retirement.RetirementId
                )
            )
        | None, None -> None

/// change-integration-005/013: the same Work's next tenure after a delivered
/// manager life.
///
/// A delivered tenure has no successor record of its own, so the work's
/// continuation is the pair `Reopened` + `IncumbencyOpened`: the one lifecycle
/// fact pair that leaves `Delivered` and opens a fresh, read-only, unreviewed
/// tenure of the same Work. Change records the machine conflict (or the rebase
/// that invalidated the old qualification) outside the gate and then hands the
/// same Work over; the physical Manager session, the worktree and the Work stay
/// exactly as they were (change-integration-009).
///
/// The hand-off is not a retirement attempt: the delivered retirement already
/// settled the live-resource obligation (work-009) with an empty blocker set,
/// and nothing can have started a resource since — the delivered attempt was
/// interrupted and its logical run closed. An open tenure means the hand-off
/// already happened, so a repeated read never opens a second one.
[<RequireQualifiedAccess>]
module DeliveredTenureHandOff =
    /// The continuation one committed retirement owes its work.
    ///
    /// A Relayed retirement already opened the successor tenure it hands to. A
    /// Delivered retirement has no successor record of its own, so the work's
    /// continuation is the pair `Reopened` + `IncumbencyOpened`: the one lifecycle
    /// fact pair that leaves `Delivered` and opens a fresh, read-only, unreviewed
    /// tenure of the same Work. Change records the machine conflict (or the rebase
    /// that invalidated the old qualification) outside the gate and then hands the
    /// same Work over; the physical Manager session, the worktree and the Work stay
    /// exactly as they were (change-integration-009).
    ///
    /// The hand-off is not a retirement attempt, so its blocker set is the durable
    /// fact the delivered retirement already settled (work-009): that retirement
    /// was written with no live resource, its attempt was interrupted and its
    /// logical run closed, and nothing else can have started a resource since. An
    /// open tenure means the hand-off already happened, so a repeated read never
    /// opens a second one. A missing observed cursor fails closed: an invented
    /// cursor would let the successor tenure inherit the predecessor's
    /// interactions.
    let continuation
        (gitCommonDir: string)
        (workId: string)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (retirement: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostRetirement)
        (observedCursor: int64 option)
        (requestId: string)
        : Result<IncumbencyId, string> =
        let openTenure () =
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir workId
            |> Option.bind (fun seen -> seen.ActiveId)

        let reopenDelivered (cursor: float) =
            let command =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.command
                    "Reopen"
                    [ "retirementId", box retirement.RetirementId
                      "ownerId", box workId
                      "openingCursor", box cursor
                      "blockers", box ([||]: string array) ]

            let receipt =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.execute gitCommonDir workId requestId command

            // A concurrent hand-off may have committed the same rotation
            // (Conflict/Unknown), so only a reread that proves the open tenure
            // settles the decision.
            let written () =
                openTenure () |> Option.map (fun activeId -> Ok(IncumbencyId.create activeId))

            let refusal () =
                if receipt.Status = "Committed" || receipt.Status = "Replayed" then
                    Error "hand-off committed without an open tenure"
                else
                    Error(
                        "manager loop hand-off failed: "
                        + (if receipt.Reason = "" then
                               receipt.Status
                           else
                               receipt.Reason)
                    )

            written () |> Option.defaultWith refusal

        match retirement.Outcome, work.ActiveId, observedCursor with
        | "Relayed", Some activeId, _ -> Ok(IncumbencyId.create activeId)
        | "Relayed", None, _ -> Error "relay continuation has no successor incumbency"
        | "Delivered", Some activeId, _ -> Ok(IncumbencyId.create activeId)
        | "Delivered", None, None -> Error "manager loop hand-off has no observed head cursor"
        | "Delivered", None, Some cursor -> reopenDelivered (float cursor)
        | _ -> Error(sprintf "retirement %s is neither a relay nor a delivery continuation" retirement.RetirementId)

/// The durable review evidence the publish gate reads for one Manager road.
///
/// An open tenure carries its own review. Publishing, however, happens *after*
/// the qualifying tenure delivered its candidate (change-integration-005/013:
/// the next tenure re-reviews and then delivers), so the open-tenure review is
/// absent exactly when the gate needs it. The qualifying review is then the
/// retired round's own — and only when that round was the delivery, because a
/// relayed round's review belongs to a tenure that never produced a publishable
/// candidate. Everything else stays a refusal with zero writes.
[<RequireQualifiedAccess>]
module PublishReviewEvidence =
    let private openTenureReview (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) : string option =
        match work.Reviewed, work.ReviewOutcome with
        | true, Some outcome -> Some outcome
        | _ -> None

    let private deliveringReview (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) : string option =
        match work.Retirements |> List.tryLast with
        | Some retirement when retirement.Outcome = "Delivered" -> work.LastRetiredReview
        | _ -> None

    let ofWork (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) : (string * string) option =
        // An open tenure is the road's current candidate authority: while it is
        // unreviewed nothing may publish, even though the predecessor's delivered
        // review is still in the history.
        let outcome =
            match work.ActiveId with
            | Some _ -> openTenureReview work
            | None -> deliveringReview work

        outcome |> Option.map (fun value -> value, work.Authority)

/// Host wiring for one Change manager session. One physical Manager session can host many
/// logical Relay incumbencies; Change never creates a second audit session.
type OrchestratorHost(deps: OrchestratorHostDeps, orchestratorId: SessionId) =
    // DSL-MUTABLE: resource — manager worktree path registry
    let worktrees = Dictionary<string, string>()
    let joinGate = obj ()
    // DSL-MUTABLE: single-flight — join-in-flight latch under joinGate
    let mutable joinInFlight = false
    let authorityUpdateGate = obj ()
    let authorityUpdatesInFlight = HashSet<string>()

    // ORCH-PORT: domain-owned sweep/relay/engine journal capabilities built once
    // from composition-handed journal.
    let sweepPortOpt = deps.Journal |> Option.map OrchestratorJournalAdapter.forSweep

    let relayPortOpt = deps.Journal |> Option.map OrchestratorJournalAdapter.forRelay

    let gitPort = GitOperations.createWithRepo deps.RepoPath OrchestratorGit.run

    let onChildCreated (agentId: string) (role: Role) (childId: SessionId) =
        deps.OnChildCreated agentId role childId

    let runtime =
        HostForkRuntime(
            orchestratorId,
            deps.Sessions,
            (fun _ _ -> Task.FromResult None),
            CompletionMailboxRuntime.create,
            NodeTiming.nodeClockPort (),
            NodeTiming.raceExit,
            ?journal = deps.Journal,
            onChildCreated = onChildCreated,
            onChildCreatedDir =
                (fun _ childId dirOpt -> dirOpt |> Option.iter (fun path -> deps.RegisterChildDirectory childId path)),
            directoryFor =
                (fun agentId ->
                    match worktrees.TryGetValue agentId with
                    // ORCH-006 defence: the worktree is removed at publish. A
                    // residual manager-family prompt must not keep pointing at the
                    // deleted path (ARCH-004 seal break); fall back to the root
                    // workspace once the worktree is gone.
                    | true, path when System.IO.Directory.Exists path -> Some path
                    | _ -> None),
            ?sessionSnapshot = deps.SessionSnapshot,
            onRunStarted = deps.OnRunStarted
        )

    let managerAgentId (jobId: ManagerJobId) = ManagerJobId.value jobId

    /// The durable job record. ORCH-003: the Manager's managed agent name lives here
    /// and nowhere else (PROMPT-008 forbids rebuilding it from the role).
    let jobRecord (jobId: ManagerJobId) =
        sweepPortOpt |> Option.bind (fun sweep -> sweep.TryJob jobId)

    let outcomeResult (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted _ -> Ok()
        | AgentFailed payload -> Error payload.Message
        | AgentAbandoned(_, reason) -> Error reason

    let outcomeOf (run: RunCompletion) = outcomeResult run.Outcome

    let childSessionOrError (agentId: string) =
        match runtime.TryChildSession agentId with
        | Some childId -> Ok childId
        | None -> Error(sprintf "Fork of '%s' produced no child session" agentId)

    /// Fork a child and hand back the Host session it created.
    ///
    /// The session comes from the runtime's own child map, not from the fork result:
    /// only the Host can issue a session id, and ORCH-006 requires the real one.
    let forkChild
        (agentId: string)
        (role: Role)
        (agent: string)
        (worktree: WorktreePath)
        (prompt: string)
        (deferSend: bool)
        (expectedToolCalls: int option)
        =
        taskResult {
            worktrees.[agentId] <- WorktreePath.value worktree

            let! _fork =
                runtime.Fork(
                    agentId,
                    role,
                    agent,
                    prompt,
                    None,
                    deferSend = deferSend,
                    ?expectedToolCalls = expectedToolCalls
                )

            return! childSessionOrError agentId
        }

    // ── RelayPort ───────────────────────────────────────────────────────────

    let createManagerSession (start: ManagerStart) : Task<Result<SessionId, string>> =
        forkChild
            (managerAgentId start.JobId)
            Role.Manager
            start.ManagerAgent
            start.Worktree
            start.RootRequest
            true
            start.ExpectedToolCalls

    let activateManager (jobId: ManagerJobId) : Task<Result<unit, string>> =
        runtime.SendDeferredFirstPrompt(managerAgentId jobId)


    let requireJobRecord (jobId: ManagerJobId) =
        match jobRecord jobId with
        | Some record -> Ok record
        | None -> Error(sprintf "No durable job record for '%s'" (ManagerJobId.value jobId))

    let lifecycleWorkOf
        (record: ManagerJobProjection)
        : Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option =
        let directory = WorktreePath.value record.WorktreePath
        let commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.forStateRoot directory
        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir (SessionId.value record.ManagerSessionId)

    let loopSignalOfWork = DurableLoopSignal.ofWork

    let requireOpenWork
        (record: ManagerJobProjection)
        : Result<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork, string> =
        lifecycleWorkOf record |> Result.requireSome "Manager session is not open"

    /// The last committed retirement on this work. The list is projected from
    /// committed IncumbencyRetired facts only; a missing or unnamed retirement
    /// is refused rather than treated as a relay.
    let requireCommittedRetirement
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        : Result<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostRetirement, string> =
        match work.Retirements |> List.tryLast with
        | Some retirement when not (String.IsNullOrWhiteSpace retirement.IncumbencyId) -> Ok retirement
        | _ -> Error "inspect has no retirement outcome"

    let requireRelayAndJob (relay: OrchestratorRelayPort option) (jobId: ManagerJobId) (relayError: string) =
        match relay, requireJobRecord jobId with
        | None, _ -> Error relayError
        | _, Error error -> Error error
        | Some relay, Ok record -> Ok(relay, record)

    let rec awaitLoopSignalFromRelay
        (jobId: ManagerJobId)
        (relay: OrchestratorRelayPort)
        (record: ManagerJobProjection)
        : Task<Result<ManagerLoopSignal, string>> =
        task {
            let revision =
                deps.Journal
                |> Option.map (fun durable -> (AgentJournal.snapshotWithRevision durable) |> snd)
                |> Option.defaultWith (fun () -> JournalRevision.create 0L)

            let workOpt = lifecycleWorkOf record

            match workOpt |> Option.bind loopSignalOfWork with
            | Some signal -> return Ok signal
            | None -> return! awaitDispatchedWait jobId relay record revision
        }

    and awaitDispatchedWait jobId relay record revision =
        task {
            // ManagerWorkflow owns the sole physical send for the
            // `ManagerLoopGate.gateKind retirement.Id` occasion; Change only awaits
            // the durable loop signal admitted through the PromptAuthority gate.
            match! deps.ContinueManagerLoop record.ManagerSessionId (WorktreePath.value record.WorktreePath) with
            | Error error -> return Error error
            | Ok() ->
                do! relay.AwaitChangeFrom revision
                return! awaitLoopSignal jobId
        }

    and awaitLoopSignal (jobId: ManagerJobId) : Task<Result<ManagerLoopSignal, string>> =
        match requireRelayAndJob relayPortOpt jobId "Manager session requires a durable journal" with
        | Error error -> Task.FromResult(Error error)
        | Ok(relay, record) -> awaitLoopSignalFromRelay jobId relay record

    /// The continuation the road owes after its last committed retirement.
    ///
    /// A Relayed tenure hands the road to the successor incumbency it opened, and
    /// that successor is the continuation. A Delivered tenure has no successor
    /// record, so the road's continuation is the same Work's next tenure: every
    /// continuation after a delivered hand-off means the candidate was rebased or
    /// lost to a machine conflict, and change-integration-005/013 require the next
    /// tenure to re-review before anything may publish. An already open tenure is
    /// that hand-off already made, so a repeated read never opens a second one.
    ///
    /// The cursor comes from the observed journal head only. A missing observation
    /// fails closed: an invented cursor would let the successor tenure inherit the
    /// predecessor's interactions.
    let continueLoop (jobId: ManagerJobId) : Task<Result<IncumbencyId, string>> =
        taskResult {
            let! relay, record =
                requireRelayAndJob relayPortOpt jobId "Manager loop continuation requires a durable journal"

            let! work = requireOpenWork record
            let! retirement = requireCommittedRetirement work

            let commonDir =
                Wanxiangshu.Persistence.Journal.RuntimePath.forStateRoot (WorktreePath.value record.WorktreePath)

            let requestId =
                "hand-off:" + ManagerJobId.value jobId + ":" + retirement.RetirementId

            return!
                DeliveredTenureHandOff.continuation
                    commonDir
                    (SessionId.value record.ManagerSessionId)
                    work
                    retirement
                    (deps.ObservedHeadCursor record.ManagerSessionId)
                    requestId
        }

    let finalizeRegisteredWorktree (agentId: string) =
        match worktrees.TryGetValue agentId with
        | true, path -> OrchestratorGit.finalizeWorktree OrchestratorGit.run agentId path
        | false, _ -> Task.FromResult(Error(sprintf "No worktree registered for manager job '%s'" agentId))

    let prepareCandidate (jobId: ManagerJobId) : Task<Result<CommitHash, string>> =
        taskResult {
            let! record = requireJobRecord jobId
            do! finalizeRegisteredWorktree (managerAgentId jobId)
            return! gitPort.ReadHead record.WorktreePath
        }

    let terminateWorkResources (jobId: ManagerJobId) : Task<unit> =
        task {
            let managerId = managerAgentId jobId

            let entry =
                lock runtime.Gate (fun () ->
                    match runtime.Children.TryGetValue managerId with
                    | true, sessionId -> Some sessionId
                    | false, _ -> None)

            match entry with
            | None -> return ()
            | Some sessionId ->
                let! _ = HostForkChildDispatch.teardownChildren runtime.Sessions [ sessionId ]
                lock runtime.Gate (fun () -> runtime.Children.Remove managerId |> ignore)
        }

    /// The durable unified review fact for a manager session: the recorded
    /// Promoted/Stayed outcome together with the work's authority count. None
    /// means no review is durable yet, and the publish gate refuses with zero
    /// writes.
    let lifecycleReviewed
        (managerSessionId: SessionId)
        (worktreePath: Wanxiangshu.Foundation.Identity.WorktreePath)
        : Task<(string * string) option> =
        task {
            let! commonDir =
                worktreePath
                |> Wanxiangshu.Foundation.Identity.WorktreePath.value
                |> Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot

            return
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir (SessionId.value managerSessionId)
                |> Option.bind PublishReviewEvidence.ofWork
        }

    let relayPort: RelayPort =
        { CreateManagerSession = createManagerSession
          ActivateManager = activateManager
          AwaitLoopSignal = awaitLoopSignal
          ContinueLoop = continueLoop
          PrepareCandidate = prepareCandidate
          TerminateWorkResources = terminateWorkResources
          LifecycleReviewed = lifecycleReviewed }

    // ── engine ──────────────────────────────────────────────────────────────

    // DSL-MUTABLE: resource — memoized orchestrator engine instance
    let mutable engineInstance: Orchestrator option = None
    let engineGate = obj ()
    // DSL-MUTABLE: single-flight — engine create task under engineGate
    let mutable engineTask: Task<Result<Orchestrator, string>> option = None

    /// ORCH-008: freeze the publish target by `symbolic-ref` once, at engine start.
    ///
    /// A configured branch is still resolved through the same verb rather than trusted
    /// as a string, so a configured name that does not exist fails here instead of at
    /// publish time.
    let frozenTarget () =
        task {
            match! gitPort.FreezeTargetBranch() with
            | Ok target when String.IsNullOrWhiteSpace deps.TargetBranch -> return Ok target
            | Ok _ -> return Ok(TargetRef.create deps.TargetBranch)
            | Error error -> return Error error
        }

    let recoverJobsIfPresent (value: Orchestrator) =
        match sweepPortOpt with
        | Some sweep ->
            OrchestratorManagerJob.recoverJobs sweep orchestratorId worktrees deps.RegisterChildDirectory value
        | None -> task { return () }

    let mapSweepError (pending: Task<Result<unit, string>>) : Task<Result<unit, string>> =
        task {
            match! pending with
            | Ok() -> return Ok()
            | Error error -> return Error(sprintf "orchestrator cleanup failed: %s" error)
        }

    let createEngine (target: TargetRef) : Task<Result<Orchestrator, string>> =
        taskResult {
            // Canonicalize the repo path via git common-dir so symlinked
            // spellings share one cross-process publish lock.
            let! lockRepoPath =
                Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot deps.RepoPath
                |> TaskResultCE.ofTask

            let sweepLockPath = IntegrationGate.lockPath lockRepoPath (TargetRef.value target)

            // Sweep orphaned manager artifacts before resuming jobs, so a
            // resumed job never adopts a worktree the sweep is about to remove.
            let sweepDescriptor =
                DiagnosticWait.create
                    "orchestrator-engine-sweep"
                    (CausalOwner.create "OrchestratorWorkflow" [ "session", SessionId.value orchestratorId ])
                    [ "lock", sweepLockPath; "target", TargetRef.value target ]
                    (ExternalProducer("integration-gate", [ "lock", sweepLockPath ]))
                    [ WaitEscape.ProcessLifetime ]
                    "OrchestratorHost.initializeEngine.sweepLocked"

            do!
                CausalAwait.awaitTask
                    deps.WaitObserver
                    sweepDescriptor
                    (OrchestratorSweep.sweepLocked sweepLockPath gitPort (fun () ->
                        sweepPortOpt
                        |> Option.map (fun sweep -> sweep.ActiveJobs())
                        |> Option.defaultValue []))
                |> mapSweepError

            let value =
                Orchestrator(
                    deps.WaitObserver,
                    gitPort,
                    relayPort,
                    deps.RepoPath,
                    target,
                    ?journal = (deps.Journal |> Option.map OrchestratorJournalAdapter.forEngine),
                    ?lockRepoPath = Some lockRepoPath
                )

            do! recoverJobsIfPresent value |> TaskResultCE.ofTask
            lock engineGate (fun () -> engineInstance <- Some value)
            return value
        }

    let initializeEngine () : Task<Result<Orchestrator, string>> =
        match engineInstance with
        | Some value -> Task.FromResult(Ok value)
        | None ->
            taskResult {
                let! target = frozenTarget ()
                return! createEngine target
            }

    let engine () : Task<Result<Orchestrator, string>> =
        lock engineGate (fun () ->
            match engineInstance, engineTask with
            | Some value, _ -> Task.FromResult(Ok value)
            | None, Some task -> task
            | None, None ->
                let task = initializeEngine ()
                engineTask <- Some task
                task)

    let mapForkManagerError
        (pending: Task<Result<OrchestratorHandle, OrchestratorVerdict>>)
        : Task<Result<OrchestratorHandle, string>> =
        task {
            match! pending with
            | Ok handle -> return Ok handle
            | Error verdict -> return Error(sprintf "%A" verdict)
        }

    let providerBynameOrAgent (byname: string option) (managerAgent: string) =
        match byname with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
        | _ -> managerAgent

    let replaceEstimateIfPresent (jobId: ManagerJobId) (expectedToolCalls: int option) =
        match expectedToolCalls, jobRecord jobId, deps.Journal with
        | Some expected, Some record, Some journal ->
            let port = AgentJournalPortAdapter.forDelegatedToolEstimate journal
            DelegatedToolEstimateLedger.replace port record.ManagerSessionId expected
        | _ -> task { return () }

    let authorityRevisionFor
        (record: ManagerJobProjection)
        (callerProviderRun: ProviderRunIdentity)
        (callerToolCallId: ToolCallId)
        (prompt: string)
        =
        String.concat
            "\n"
            // Durable identity tag: it feeds the AdvanceCommitted id, so it is a
            // stable machine vocabulary — renaming it would break replay of
            // existing records. Not a user-visible term.
            [ "relay-authority-revision-v1"
              SessionId.value record.ManagerSessionId
              ProviderRunIdentity.value callerProviderRun
              ToolCallId.value callerToolCallId
              HostDigest.sha256Hex prompt ]
        |> HostDigest.sha256Hex
        |> fun digest -> AuthorityRevision.create ("authority-revision:" + digest)

    let tryEnterAuthorityUpdate (jobId: ManagerJobId) =
        lock authorityUpdateGate (fun () -> authorityUpdatesInFlight.Add(ManagerJobId.value jobId))

    let leaveAuthorityUpdate (jobId: ManagerJobId) =
        lock authorityUpdateGate (fun () -> authorityUpdatesInFlight.Remove(ManagerJobId.value jobId) |> ignore)

    let joinPublishedBatchOnce
        (maxCount: int)
        (interrupt: Task<JoinInterruptReason>)
        : Task<Result<JoinWaitOutcome<OrchestratorVerdict>, string>> =
        taskResult {
            try
                let! engine = engine ()
                let! outcome = engine.JoinPublishedBatch(maxCount, interrupt) |> TaskResultCE.ofTask
                return outcome
            finally
                lock joinGate (fun () -> joinInFlight <- false)
        }

    /// The active Relay incumbency that owns the work's current authority. The
    /// last committed AdvanceCommitted must have produced exactly the authority
    /// the work now carries (or no advance has happened since the opening):
    /// otherwise the active tenure is not the one a new charge may advance.
    let requireActiveWorkOwnedIncumbent
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        : Result<string, string> =
        let lastAdvance = work.Advances |> List.tryLast

        match work.ActiveId, work.Reviewed, lastAdvance with
        | None, _, _ -> Error "inspect has no active incumbency"
        | Some _, false, _ -> Error "Relay incumbency cannot take new charge before assessment is recorded"
        | Some _, true, _ when String.IsNullOrWhiteSpace work.Authority -> Error "inspect has no authority to advance"
        | Some id, true, Some advance when advance.Next <> work.Authority ->
            Error(sprintf "active incumbency %s does not own the current authority" id)
        | Some id, true, _ -> Ok id

    let advanceReceiptOutcome
        (directory: string)
        (receipt: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostReceipt)
        : Result<string, string> =
        if receipt.Status = "Committed" || receipt.Status = "Replayed" then
            Ok directory
        else
            Error(
                "gate continuation was sent; lifecycle commit failed: "
                + (if receipt.Reason = "" then
                       receipt.Status
                   else
                       receipt.Reason)
            )

    let executeAuthorityAdvance
        (directory: string)
        (commonDir: string)
        (workId: string)
        (incumbent: string)
        (expected: string)
        (next: string)
        : Result<string, string> =
        match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir workId with
        | None -> Error "lifecycle work is missing"
        | Some seen when String.IsNullOrEmpty seen.Authority -> Error "expected advance is missing"
        | Some _seen ->
            let requestId = incumbent + ":" + expected + ":" + next

            let command =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.command
                    "Advance"
                    [ "incumbencyId", box incumbent; "expected", box expected; "next", box next ]

            let receipt =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.execute commonDir workId requestId command

            advanceReceiptOutcome directory receipt

    let commitAuthorityAdvance
        (record: ManagerJobProjection)
        (incumbent: string)
        (expected: string)
        (next: string)
        : Task<Result<string, string>> =
        task {
            let directory = WorktreePath.value record.WorktreePath
            let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot directory

            if String.IsNullOrWhiteSpace commonDir then
                return Error "lifecycle store is unavailable"
            else
                return
                    executeAuthorityAdvance
                        directory
                        commonDir
                        (SessionId.value record.ManagerSessionId)
                        incumbent
                        expected
                        next
        }

    let advanceAuthorityRevision
        (relay: OrchestratorRelayPort)
        (record: ManagerJobProjection)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (incumbent: string)
        (nextRevision: AuthorityRevision)
        (prompt: string)
        (callerProviderRun: ProviderRunIdentity)
        : Task<Result<string, string>> =
        taskResult {
            let expectedRevision = AuthorityRevision.create work.Authority
            // Durable gate identity (the nudge's payload digest binds it), so it
            // stays stable like the authority-revision tag above.
            let gateKind = "relay-authority-update:" + AuthorityRevision.value nextRevision

            let! _physicalAuthorityMessage =
                deps.SendGateContinuation
                    record.ManagerSessionId
                    prompt
                    PromptAuthority.ContinuationKind.ManagedDelegationAssignment
                    (Some(WorktreePath.value record.WorktreePath))
                    deps.Journal
                    gateKind
                    callerProviderRun

            return!
                commitAuthorityAdvance
                    record
                    incumbent
                    (AuthorityRevision.value expectedRevision)
                    (AuthorityRevision.value nextRevision)
        }

    let continueManagerJobCore
        (jobId: ManagerJobId)
        (prompt: string)
        (callerProviderRun: ProviderRunIdentity)
        (callerToolCallId: ToolCallId)
        (expectedToolCalls: int option)
        : Task<Result<string, string>> =
        taskResult {
            do! replaceEstimateIfPresent jobId expectedToolCalls |> TaskResultCE.ofTask
            let! record = requireJobRecord jobId

            let! relay =
                relayPortOpt
                |> Result.requireSome "Relay authority update requires a durable journal"

            let! work = requireOpenWork record

            let nextRevision =
                authorityRevisionFor record callerProviderRun callerToolCallId prompt

            // Stopped. inspect has no advance history. Absence is not an authority match.
            let! incumbent = requireActiveWorkOwnedIncumbent work
            return! advanceAuthorityRevision relay record work incumbent nextRevision prompt callerProviderRun
        }

    let runAuthorityUpdate (jobId: ManagerJobId) (action: unit -> Task<Result<string, string>>) =
        task {
            try
                return! action ()
            finally
                leaveAuthorityUpdate jobId
        }

    member _.HasChild(agentId: string) : bool = runtime.HasChild agentId

    member _.ForkManagerJob
        (jobId: ManagerJobId, managerAgent: string, prompt: string, ?byname: string, ?expectedToolCalls: int)
        : Task<Result<string, string>> =
        let providerByname = providerBynameOrAgent byname managerAgent

        let descriptor =
            DiagnosticWait.create
                "commission-manager-job"
                (CausalOwner.create "OrchestratorWorkflow" [ "session", SessionId.value orchestratorId ])
                [ "job", ManagerJobId.value jobId; "manager_agent", managerAgent ]
                (ExternalProducer("orchestrator-engine", [ "job", ManagerJobId.value jobId ]))
                [ WaitEscape.ProcessLifetime; WaitEscape.SessionLifetime ]
                "OrchestratorHost.CommissionManagerJob"

        let pending =
            taskResult {
                let! engine = engine ()

                let! handle =
                    engine.ForkManager(
                        jobId,
                        managerAgent,
                        prompt,
                        byname = providerByname,
                        ?expectedToolCalls = expectedToolCalls
                    )
                    |> mapForkManagerError

                return WorktreePath.value handle.WorktreePath
            }

        CausalAwait.awaitTask deps.WaitObserver descriptor pending

    /// Same-road charge update. The physical Session/worktree stay stable, but
    /// the requirement is a new durable Relay AuthorityRevision. Exact tool
    /// replay reuses the same gate occasion and therefore the same physical
    /// authority message; it never creates a second revision.
    member _.ContinueManagerJob
        (
            jobId: ManagerJobId,
            prompt: string,
            callerProviderRun: ProviderRunIdentity,
            callerToolCallId: ToolCallId,
            ?expectedToolCalls: int
        ) : Task<Result<string, string>> =
        if not (tryEnterAuthorityUpdate jobId) then
            Task.FromResult(Error "Relay authority update is already in flight for this Work")
        else
            runAuthorityUpdate jobId (fun () ->
                continueManagerJobCore jobId prompt callerProviderRun callerToolCallId expectedToolCalls)

    /// EXEC-019: FIFO batch + local interrupt (JoinTool renders wire).
    member _.JoinPublishedAvailable
        (maxCount: int, interrupt: Task<JoinInterruptReason>)
        : Task<Result<JoinWaitOutcome<OrchestratorVerdict>, string>> =
        let acquired =
            lock joinGate (fun () ->
                if joinInFlight then
                    false
                else
                    joinInFlight <- true
                    true)

        if not acquired then
            Task.FromResult(Error "JOIN_IN_PROGRESS: another join call is already waiting for this session")
        else
            joinPublishedBatchOnce maxCount interrupt

    member _.CancelAndDrain() : Task = runtime.CancelAndDrain()

    member _.DetachAndDrain() : Task = runtime.DetachAndDrain()

    member _.Cancel() = runtime.Cancel()
