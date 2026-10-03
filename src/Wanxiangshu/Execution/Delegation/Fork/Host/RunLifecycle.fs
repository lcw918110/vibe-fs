namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

/// Per-run terminal lifecycle for HostForkRuntime: install, complete, fail.
module HostForkRunLifecycle =

    [<RequireQualifiedAccess>]
    type AgentOwnerDispatchOutcome =
        | Accepted of physicalUserMessageId: PhysicalUserMessageId * authorityRoot: AuthorityRootUserMessageId
        | AcceptanceUncertain of string
        | Rejected of string

    let private admittedWork durable parentId agentId childId authorityRoot =
        HandleProjection.tryAdmittedWork
            { Handle = HandleController.agentHandle agentId
              ChildSessionId = childId
              AuthorityRoot = authorityRoot }
            (AgentJournal.handleProjection durable parentId)

    let private acceptedOutcome
        (_durable: AgentJournal)
        (_childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (onAccepted: PhysicalUserMessageId -> unit)
        (evidence: PromptAuthority.AcceptedDispatch)
        =
        onAccepted evidence.PhysicalUserMessageId

        let root =
            PhysicalUserMessageId.promoteToAuthorityRoot evidence.PhysicalUserMessageId

        // Physical acceptance is the durable dispatch fact. Work admission may lag
        // until landing evidence arrives; do not downgrade a confirmed Host receipt
        // to AcceptanceUncertain solely for a missing Works entry.
        match PromptAuthority.identitySeedOwner identitySeed with
        | Some _ -> AgentOwnerDispatchOutcome.Accepted(evidence.PhysicalUserMessageId, root)
        | None -> AgentOwnerDispatchOutcome.Rejected "child assignment has no exact owner identity"

    let issueCurrentOwnerIdentitySeed
        (journal: AgentJournal option)
        (ownerSessionId: SessionId)
        (childAgent: string)
        : Result<PromptAuthority.IdentitySeed, string> =
        match journal with
        | None -> Error "No journal: an AgentOwnerRoot identity seed cannot be issued"
        | Some durable ->
            PromptAuthorityProjectionQueries.issueCurrentOwnerIdentitySeed
                (AgentJournal.snapshot durable).AgentProjections
                ownerSessionId
                childAgent

    [<RequireQualifiedAccess>]
    type private DurableDispatchObservation =
        | Accepted of PromptAuthority.AcceptedDispatch
        | Pending of PromptAuthority.PromptClaim
        | IdentityMismatch
        | Dispatchable

    let private pendingDispatchObservation childId payloadDigest identitySeed projections =
        match PromptAuthorityProjectionQueries.pendingDispatchClaim childId payloadDigest projections with
        | Some claim when claim.IdentitySeed = identitySeed -> DurableDispatchObservation.Pending claim
        | Some _ -> DurableDispatchObservation.IdentityMismatch
        | None ->
            raise (
                InvalidOperationException(
                    "PromptAuthority projection reported Pending without the matching durable claim"
                )
            )

    let private durableDispatchObservation
        (durable: AgentJournal)
        (childId: SessionId)
        (payloadDigest: string)
        (identitySeed: PromptAuthority.IdentitySeed)
        =
        let projections = (AgentJournal.snapshot durable).AgentProjections

        match PromptAuthorityProjectionQueries.dispatchStatusFor childId payloadDigest projections with
        | PromptAuthorityProjectionQueries.DispatchStatus.Accepted evidence when evidence.IdentitySeed = identitySeed ->
            DurableDispatchObservation.Accepted evidence
        | PromptAuthorityProjectionQueries.DispatchStatus.Accepted _ -> DurableDispatchObservation.IdentityMismatch
        | PromptAuthorityProjectionQueries.DispatchStatus.Pending ->
            pendingDispatchObservation childId payloadDigest identitySeed projections
        | PromptAuthorityProjectionQueries.DispatchStatus.Dispatchable -> DurableDispatchObservation.Dispatchable

    let private classifyPendingSend
        (durable: AgentJournal)
        (childId: SessionId)
        (payloadDigest: string)
        (identitySeed: PromptAuthority.IdentitySeed)
        (claim: PromptAuthority.PromptClaim)
        (onAccepted: PhysicalUserMessageId -> unit)
        (accepted: PromptAuthority.AcceptedDispatch -> AgentOwnerDispatchOutcome)
        (error: string)
        =
        // AcceptanceUnknown intentionally leaves the claim Pending. Restore the
        // process-local callback cancelled by the synchronous dispatcher error,
        // then re-read durable truth to close the accepted-between-read race.
        PromptPhysicalAcceptance.register claim.PromptKey onAccepted

        match durableDispatchObservation durable childId payloadDigest identitySeed with
        | DurableDispatchObservation.Accepted evidence ->
            PromptPhysicalAcceptance.cancel claim.PromptKey
            accepted evidence
        | DurableDispatchObservation.Pending _ -> AgentOwnerDispatchOutcome.AcceptanceUncertain error
        | DurableDispatchObservation.IdentityMismatch ->
            PromptPhysicalAcceptance.cancel claim.PromptKey
            AgentOwnerDispatchOutcome.Rejected "Durable child dispatch identity witness does not match this owner run"
        | DurableDispatchObservation.Dispatchable ->
            PromptPhysicalAcceptance.cancel claim.PromptKey
            AgentOwnerDispatchOutcome.Rejected error

    let private classifySendError
        (durable: AgentJournal)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (prompt: string)
        (onAccepted: PhysicalUserMessageId -> unit)
        (error: string)
        =
        let payloadDigest = HostDigest.sha256Hex prompt

        let accepted = acceptedOutcome durable childId identitySeed onAccepted

        match durableDispatchObservation durable childId payloadDigest identitySeed with
        | DurableDispatchObservation.Accepted evidence -> accepted evidence
        | DurableDispatchObservation.IdentityMismatch ->
            AgentOwnerDispatchOutcome.Rejected "Durable child dispatch identity witness does not match this owner run"
        | DurableDispatchObservation.Dispatchable -> AgentOwnerDispatchOutcome.Rejected error
        | DurableDispatchObservation.Pending claim ->
            classifyPendingSend durable childId payloadDigest identitySeed claim onAccepted accepted error

    let private classifySendSuccess durable childId identitySeed prompt onAccepted =
        match durableDispatchObservation durable childId (HostDigest.sha256Hex prompt) identitySeed with
        | DurableDispatchObservation.Accepted evidence ->
            acceptedOutcome durable childId identitySeed onAccepted evidence
        | DurableDispatchObservation.Pending _ ->
            AgentOwnerDispatchOutcome.AcceptanceUncertain "Prompt submitted but physical acceptance unconfirmed"
        | DurableDispatchObservation.IdentityMismatch ->
            AgentOwnerDispatchOutcome.Rejected "Durable child dispatch identity witness does not match this owner run"
        | DurableDispatchObservation.Dispatchable ->
            AgentOwnerDispatchOutcome.Rejected "Host reported admission without a durable prompt claim"

    let private interpretDispatchResult durable childId identitySeed prompt onAccepted =
        function
        | Ok _ -> classifySendSuccess durable childId identitySeed prompt onAccepted
        | Error error -> classifySendError durable childId identitySeed prompt onAccepted error

    let private sendAgentOwnerRootWithJournal
        (sessions: ISessionHostPort)
        (durable: AgentJournal)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (directory: string option)
        (prompt: string)
        (onAccepted: PhysicalUserMessageId -> unit)
        : Task<AgentOwnerDispatchOutcome> =
        task {
            let svc = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            let! sent =
                svc.SendAgentOwnerRoot
                    (DispatchSessionPort.ofSessionPort sessions)
                    childId
                    prompt
                    identitySeed
                    directory
                    PromptDispatcher.AwaitMode.Await
                    (Some onAccepted)

            return interpretDispatchResult durable childId identitySeed prompt onAccepted sent
        }

    let workRecordForOutcome
        (childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>)
        (xTraceHead: SessionId -> XTraceCursor)
        (run: PendingHostRun)
        (outcome: TerminalOutcome)
        =
        match outcome with
        | TerminalOutcome.Completed result ->
            childWorkRecordForRun
                run.ChildId
                (DelegationHandoff.childRange run.StartCursor (xTraceHead run.ChildId))
                result.ProviderRun
        | _ -> Task.FromResult None

    /// Idle existing child / first prompt for an AgentOwnerRoot work unit.
    ///
    /// PROMPT-005: the journal is required. A journal-less dispatcher would report
    /// success for a claim it never wrote, which is the one failure mode the
    /// four-fact protocol exists to prevent.
    let sendAgentOwnerRootObserved
        (sessions: ISessionHostPort)
        (journal: AgentJournal option)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (directory: string option)
        (prompt: string)
        (onAccepted: PhysicalUserMessageId -> unit)
        : Task<AgentOwnerDispatchOutcome> =
        match journal with
        | None ->
            Task.FromResult(AgentOwnerDispatchOutcome.Rejected "No journal: an AgentOwnerRoot prompt cannot be claimed")
        | Some durable ->
            sendAgentOwnerRootWithJournal sessions durable childId identitySeed directory prompt onAccepted

    /// PROMPT-006: every child prompt is an AgentOwnerRoot through the Dispatcher.
    ///
    /// The previous version fell back to `SendChildPromptFireAndForget` with
    /// `Metadata = None` whenever no journal was present. That path sent a real
    /// prompt with no PromptKey, so PROMPT-011 had no anchor to recover it by and
    /// PromptIngress could only classify the reply as UnknownOrigin.
    let sendChildPrompt
        (sessions: ISessionHostPort)
        (_parentId: SessionId)
        (journal: AgentJournal option)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (directory: string option)
        (prompt: string)
        (onAccepted: PhysicalUserMessageId -> unit)
        =
        sendAgentOwnerRootObserved sessions journal childId identitySeed directory prompt onAccepted

    let childPromptSender sessions parentId journal (directoryOf: string -> string option) =
        fun (agentId: string) childId (_role: Role) identitySeed prompt onAccepted ->
            sendChildPrompt sessions parentId journal childId identitySeed (directoryOf agentId) prompt onAccepted

    /// Surface journal: a real canonical journal in a fresh temporary directory,
    /// opened exactly the way production composition does (EventStore + journal
    /// writer + projection). Surfaces use it to construct pending runs with
    /// durable admitted work instead of journal-less fakes.
    let openTemporaryJournal () : Task<AgentJournal> =
        task {
            let directory =
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "wanxiangshu-surface-journal-" + Guid.NewGuid().ToString("N")
                )

            System.IO.Directory.CreateDirectory directory |> ignore

            let store =
                EventStore.createLocal
                    directory
                    (Guid.NewGuid().ToString("N"))
                    (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

            match!
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create (sprintf "fork-host-surface-%s" directory),
                    1,
                    DateTimeOffset.UtcNow,
                    store
                )
            with
            | Ok(writer, _, projection) ->
                match AgentJournal.createFromProjection writer projection with
                | Ok journal -> return journal
                | Error rejection ->
                    return failwithf "surface journal open failed: %s: %s" rejection.Fact rejection.Reason
            | Error rejection -> return failwithf "surface journal open failed: %s: %s" rejection.Fact rejection.Reason
        }

    let private dispatchSurfaceOwnerRoot
        (sessions: ISessionHostPort)
        (durable: AgentJournal)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (agentId: string)
        : Task<Result<AuthorityRootUserMessageId, string>> =
        task {
            let! sent =
                sendAgentOwnerRootObserved
                    sessions
                    (Some durable)
                    childId
                    identitySeed
                    None
                    (sprintf "surface admission: first work unit for %s" agentId)
                    ignore

            match sent with
            | AgentOwnerDispatchOutcome.Accepted(_, authorityRoot) -> return Ok authorityRoot
            | AgentOwnerDispatchOutcome.AcceptanceUncertain reason -> return Error reason
            | AgentOwnerDispatchOutcome.Rejected reason -> return Error reason
        }

    let private admitAfterLinkage
        (durable: AgentJournal)
        (sessions: ISessionHostPort)
        (parentId: SessionId)
        (agentId: string)
        (childId: SessionId)
        : Task<Result<AuthorityRootUserMessageId, string>> =
        task {
            match issueCurrentOwnerIdentitySeed (Some durable) parentId agentId with
            | Error reason -> return Error(sprintf "surface admission: child identity seed failed: %s" reason)
            | Ok identitySeed -> return! dispatchSurfaceOwnerRoot sessions durable childId identitySeed agentId
        }

    let private admitAfterOwnerRoot
        (durable: AgentJournal)
        (sessions: ISessionHostPort)
        (parentId: SessionId)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        : Task<Result<AuthorityRootUserMessageId, string>> =
        task {
            let journalPort = AgentJournalPortAdapter.fromAgentJournal durable

            let! linkage =
                HandleController.linkNamed
                    (Some journalPort)
                    parentId
                    agentId
                    childId
                    agentId
                    agentId
                    role
                    HandleOwnership.DurableParentHandle

            match linkage with
            | Error reason -> return Error(sprintf "surface admission: handle link failed: %s" reason)
            | Ok() -> return! admitAfterLinkage durable sessions parentId agentId childId
        }

    let private acceptOwnerRootThenAdmit
        (dispatcher: PromptDispatcher.Runtime)
        (durable: AgentJournal)
        (sessions: ISessionHostPort)
        (parentId: SessionId)
        (ownerPhysical: PhysicalUserMessageId)
        (seed: PromptAuthority.IdentitySeed)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        : Task<Result<AuthorityRootUserMessageId, string>> =
        task {
            match! dispatcher.AcceptHumanRoot parentId ownerPhysical (Some seed) with
            | Error failure ->
                return
                    Error(
                        sprintf
                            "surface admission: owner root rejected: %s"
                            (PromptDispatcher.describeHumanRootAcceptanceFailure failure)
                    )
            | Ok _ -> return! admitAfterOwnerRoot durable sessions parentId agentId childId role
        }

    /// Surface admission for a pending agent run (delegation-026 effect truth):
    /// durably link the child handle, admit the owner human root, and dispatch
    /// the child's first AgentOwnerRoot prompt through the real dispatcher, so
    /// the following installRun finds the exact admitted work instead of the
    /// fail-fast rejections. Returns the accepted authority root InstallRun
    /// must reuse so the run's terminal keeps its causal identity.
    let admitPendingAgentWork
        (durable: AgentJournal)
        (sessions: ISessionHostPort)
        (parentId: SessionId)
        (ownerAgent: string)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        : Task<Result<AuthorityRootUserMessageId, string>> =
        task {
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            let ownerSeed =
                ParticipantIdentity.resolveAtRoot ownerAgent
                |> Result.map PromptAuthority.IdentitySeed.RootSelection

            match ownerSeed with
            | Error reason -> return Error(sprintf "surface admission: owner identity unresolved: %A" reason)
            | Ok seed ->
                let ownerPhysical =
                    PhysicalUserMessageId.create (sprintf "surface-owner-root:%s" (SessionId.value parentId))

                return!
                    acceptOwnerRootThenAdmit
                        dispatcher
                        durable
                        sessions
                        parentId
                        ownerPhysical
                        seed
                        agentId
                        childId
                        role
        }

    let private completionBelongsToRun (run: PendingHostRun) (result: AgentRunResult) =
        run.ChildId = result.SessionId
        && run.AuthorityRoot = result.AuthorityRootUserMessageId

    let private stopBelongsToRun (run: PendingHostRun) (stop: TerminalStop) =
        stop.AuthorityRootUserMessageId.IsNone
        || TerminalStop.belongsTo run.AuthorityRoot stop

    /// delegation-031: the handoff port travels with the prepared handoff as one slot
    /// (`Some slot` proves the capability was present at PrepareHandoff time).
    /// A run whose `Handoff` slot is `None` has no completion checkpoint to
    /// write — capability absence is therefore decided here structurally, not by
    /// a late fatal, and `complete` never re-checks a construction-time fact.
    /// Prepared-handoff capability arbitration: `Some port`/`Some handoff`
    /// pair married, capability-absent prepared handoff → structural
    /// invariant violation. Kept at module level so `checkpointSlot` stays
    /// single-level — the nested match is delegated here, not inlined.
    let private checkpointSlotFor
        (handoffPort: ReusableHandoffPort option)
        (handoff: PreparedDelegationHandoff)
        : (ReusableHandoffPort * PreparedDelegationHandoff) option =
        match handoffPort with
        | Some port -> Some(port, handoff)
        | None ->
            raise (
                InvalidOperationException
                    "reusable fork run has no handoff capability: prepared handoff without a handoff port"
            )

    let private checkpointSlot (handoffPort: ReusableHandoffPort option) (run: PendingHostRun) =
        match run.Handoff with
        | None -> None
        | Some handoff -> checkpointSlotFor handoffPort handoff

    /// Commitment switchboard for a CheckpointCompleted settlement. Non-conflict
    /// outcomes are sunk to unit; PhaseConflict is the retained inventor's
    /// authority — it trips the process because no settlement path exists at
    /// that level.
    let private checkpointOutcomePort (settled: HandoffCheckpointSettlement) : Task =
        task {
            match settled.Commitment with
            | HandoffCheckpointCommitment.Committed
            | HandoffCheckpointCommitment.NotCommitted _
            | HandoffCheckpointCommitment.Unknown _ -> ()
            | HandoffCheckpointCommitment.PhaseConflict reason ->
                let detail =
                    sprintf
                        "delegation completed-handoff invariant cut at route %s: %s"
                        (DelegationHandoffRoute.value settled.Identity.Route)
                        reason

                FatalProcess.trip "HostForkRunLifecycle.checkpointCompletedHandoff" detail
        }
        :> Task

    let private settleCompletedHandoff
        (handoffPort: ReusableHandoffPort option)
        (parentId: SessionId)
        (run: PendingHostRun)
        : Task =
        match checkpointSlot handoffPort run with
        | None -> Task.FromResult(()) :> Task
        | Some(port, handoff) ->
            task {
                // delegation-031: the checkpoint returns its own settlement — a pending
                // completion is delivered whatever the commitment says. The child
                // already finished; NotCommitted/Unknown preserve pending-evidence
                // for the next invocation's durable re-read and never re-execute
                // the child. Only the PhaseConflict invariant cut escalates.
                let! settled = port.CheckpointCompleted parentId handoff
                return! checkpointOutcomePort settled
            }
            :> Task

    /// P0-RECOVERY-JOIN-001: only proven terminals may claim the cell.
    /// Aborted is observation — never recordCompletion / SetResult / mailbox.
    /// Claim runs only after JoinableCompletion proof succeeds (fail closed).
    let private claimPendingRun (gate: obj) (pendingRuns: Dictionary<string, PendingHostRun>) (run: PendingHostRun) =
        lock gate (fun () ->
            match pendingRuns.TryGetValue run.AgentId with
            | true, current when obj.ReferenceEquals(current.Token, run.Token) && not run.Finished ->
                run.Finished <- true
                true, run.Subscription
            | _ -> false, None)

    let private lateAdmittedWorkFromJournal (journal: AgentJournal option) (parentId: SessionId) (run: PendingHostRun) =
        // Admission may land after installRun snapped Work = None; re-read
        // Current before failing closed so deferred fold can still settle.
        match journal with
        | None -> Error "durable pending run has no admitted work proof"
        | Some durable ->
            admittedWork durable parentId run.AgentId run.ChildId run.AuthorityRoot
            |> Result.mapError (fun _ -> "durable pending run has no admitted work proof")

    let private resolveAdmittedWorkForCommit
        (journal: AgentJournal option)
        (parentId: SessionId)
        (run: PendingHostRun)
        =
        match run.Work with
        | Some admitted -> Ok admitted
        | None -> lateAdmittedWorkFromJournal journal parentId run

    let private committedOutcome
        (journal: AgentJournal option)
        (parentId: SessionId)
        (proof: JoinableCompletion)
        (run: PendingHostRun)
        (agentOutcome: AgentCompletionOutcome)
        =
        task {
            let journalPort = journal |> Option.map AgentJournalPortAdapter.fromAgentJournal

            let commitment =
                match resolveAdmittedWorkForCommit journal parentId run with
                | Ok admitted -> HandleController.recordWorkCompletion journalPort parentId admitted proof
                | Error error -> Task.FromResult(Error error)

            match! commitment with
            | Ok() -> return agentOutcome
            | Error error ->
                return
                    AgentCompletion.failed
                        run.AgentId
                        ("run-" + run.AgentId)
                        (Some run.Role)
                        (Some run.ChildId)
                        "PERSIST"
                        (sprintf "EXEC-009/PERSIST-002 HandleCompleted append failed: %s" error)
        }

    let private releaseMatchingPendingRun
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (gate: obj)
        (run: PendingHostRun)
        =
        lock gate (fun () ->
            match pendingRuns.TryGetValue run.AgentId with
            | true, current when obj.ReferenceEquals(current.Token, run.Token) ->
                pendingRuns.Remove run.AgentId |> ignore
            | _ -> ())

    let private startClaimDelivery
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (run: PendingHostRun)
        (proof: JoinableCompletion)
        (agentOutcome: AgentCompletionOutcome)
        : Task =
        task {
            let! finalOutcome =
                match journal with
                | None -> Task.FromResult agentOutcome
                | Some j -> committedOutcome (Some j) parentId proof run agentOutcome

            // Do not release the road while the exact completion commit is unknown.
            let workIsSettled durable admitted =
                HandleProjection.tryWork (AdmittedWork.id admitted) (AgentJournal.handleProjection durable parentId)
                |> Option.exists (fun work ->
                    match work.Lifecycle with
                    | CompletedAwaitingJoin _
                    | Retired -> true
                    | _ -> false)

            let committed =
                match resolveAdmittedWorkForCommit journal parentId run, journal with
                | Ok admitted, Some durable -> workIsSettled durable admitted
                | _ -> false

            if journal.IsNone || committed then
                releaseMatchingPendingRun pendingRuns gate run

            run.Source.SetResult finalOutcome
        }
        :> Task

    let private deliverClaimedCompletion
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (gate: obj)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (run: PendingHostRun)
        (proof: JoinableCompletion)
        (agentOutcome: AgentCompletionOutcome)
        : Task =
        let claimed, subscriptionToDispose = claimPendingRun gate pendingRuns run

        if claimed then
            subscriptionToDispose
            |> Option.iter (fun subscription -> subscription.Dispose())

            startClaimDelivery gate pendingRuns journal parentId run proof agentOutcome
        else
            Task.FromResult(())

    let private deliverProvenCompletion
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (run: PendingHostRun)
        (evidence: TerminalEvidence)
        (agentOutcome: AgentCompletionOutcome)
        : Task =
        match JoinableCompletion.tryFromProvenTerminal evidence with
        | Error _ ->
            // Fail closed: leave run Active / pending for a later proven terminal.
            Task.FromResult(())
        | Ok proof -> deliverClaimedCompletion pendingRuns gate journal parentId run proof agentOutcome

    let private deliverFailedCompletion
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (run: PendingHostRun)
        (error: string)
        : Task =
        let childId = run.ChildId
        let runId = "run-" + run.AgentId
        let handle = HandleController.agentHandle run.AgentId
        let code = if error = "cancelled" then "CANCELLED" else "ERROR"

        let agentOutcome =
            AgentCompletion.failed run.AgentId runId (Some run.Role) (Some childId) code error

        let body = HandleCompletionCodec.encodeOutcome runId agentOutcome

        deliverProvenCompletion
            gate
            pendingRuns
            journal
            parentId
            run
            (TerminalEvidence.failed run.AgentId handle childId body)
            agentOutcome

    let complete
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (handoffPort: ReusableHandoffPort option)
        (run: PendingHostRun)
        (outcome: TerminalOutcome)
        (workRecord: string option)
        : Task =
        // EXEC-006 / COMPANION-003: the completion's work record is the child's
        // final LifecycleWorkRecord only — Opening + frames + gap + terminal,
        // materialised by the same port as parent background. No TerminalText
        // or session-A fallback: a missing LWR is an empty work_record, not a
        // second channel.
        let completedWorkRecord = workRecord
        let runId = "run-" + run.AgentId
        let childId = run.ChildId
        let handle = HandleController.agentHandle run.AgentId

        match outcome with
        | Aborted _ ->
            // Observation only. Keep pending run Active for a later proven terminal.
            Task.FromResult(())
        | Completed result when not result.IsValid ->
            // provider-attempt-recovery-008 / P0-RECOVERY-JOIN-001: an empty / XML-only terminal is
            // not a proven failure. The subagent auto-retries and continues — its
            // reconcile loop performs the bounded missing-final-report repair.
            // Concluding MISSING_FINAL_REPORT
            // here would fail the run before the last effort. Observation only.
            Task.FromResult(())
        | Completed result when not (completionBelongsToRun run result) -> Task.FromResult(())
        | Completed result ->
            task {
                // delegation-031: settlement is attempted before delivery, but the
                // proven completion is delivered whatever the commitment says —
                // neither announced early (this await precedes the SetResult
                // below) nor forgotten. Only PhaseConflict escalates, after
                // which no delivery can follow.
                do! settleCompletedHandoff handoffPort parentId run

                let agentOutcome =
                    AgentCompletion.completed
                        run.AgentId
                        childId
                        runId
                        run.Role
                        result.AuthorityRootUserMessageId
                        result.ProviderRun
                        (completedWorkRecord |> Option.defaultValue "")
                        result.Directory

                let body = HandleCompletionCodec.encodeOutcome runId agentOutcome

                do!
                    deliverProvenCompletion
                        gate
                        pendingRuns
                        journal
                        parentId
                        run
                        (TerminalEvidence.completed run.AgentId handle childId body)
                        agentOutcome
            }
            :> Task
        | Failed stop when not (stopBelongsToRun run stop) -> Task.FromResult(())
        | Failed stop when
            stop.Reason = "MISSING_FINAL_REPORT"
            || stop.Reason.Contains("MISSING_FINAL_REPORT")
            ->
            // provider-attempt-recovery-008 / P0-RECOVERY-JOIN-001: a missing final report is not a
            // proven terminal failure. The subagent auto-retries and continues (its
            // reconcile loop keeps repairing the empty terminal); delivering a
            // proven MISSING_FINAL_REPORT failure here concludes the run before the
            // last effort (the same reason the `Aborted` branch observes only).
            // Observation only — keep pending run Active for a later proven terminal.
            Task.FromResult(())
        | Failed stop -> deliverFailedCompletion gate pendingRuns journal parentId run stop.Reason

    let installRun
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (childWorkRecordForRun: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>)
        (xTraceHead: SessionId -> XTraceCursor)
        (trackOwnedWork: (unit -> Task) -> unit)
        (handoffPort: ReusableHandoffPort option)
        (handoff: PreparedDelegationHandoff option)
        (agentId: string)
        (childId: SessionId)
        (role: Role)
        (authorityRoot: AuthorityRootUserMessageId)
        =
        let tryInstalledWork durable =
            // Missing or retired work must not block installing a pending run after
            // physical acceptance; completion paths tolerate Work = None.
            match admittedWork durable parentId agentId childId authorityRoot with
            | Ok admitted -> Some admitted
            | Error _ -> None

        let work =
            match journal with
            | None -> None
            | Some durable -> tryInstalledWork durable

        let run =
            { Token = obj ()
              AgentId = agentId
              ChildId = childId
              Role = role
              StartCursor = xTraceHead childId
              Handoff = handoff
              AuthorityRoot = authorityRoot
              Work = work
              Source = HostPendingRun.completionSource ()
              Subscription = None
              Finished = false }

        lock gate (fun () -> pendingRuns.[agentId] <- run)

        let subscription =
            sessions.SubscribeFutureTerminal(
                childId,
                (fun _ outcome ->
                    trackOwnedWork (fun () ->
                        task {
                            let! workRecord = workRecordForOutcome childWorkRecordForRun xTraceHead run outcome
                            do! complete gate pendingRuns journal parentId sessions handoffPort run outcome workRecord
                        }
                        :> Task))
            )

        let disposeImmediately =
            lock gate (fun () ->
                run.Subscription <- Some subscription
                run.Finished)

        if disposeImmediately then
            subscription.Dispose()

        run

    /// Parent cancellation has already committed durable HandleAbandoned before
    /// this runs. Settle only the in-memory waiter/subscription; routing the same
    /// run through `complete(Failed "cancelled")` would incorrectly compete with
    /// Abandoned by attempting a HandleCompleted(CANCELLED) commit.
    let settleParentCancelled (gate: obj) (pendingRuns: Dictionary<string, PendingHostRun>) (run: PendingHostRun) =
        let claimed, subscriptionToDispose =
            lock gate (fun () ->
                match pendingRuns.TryGetValue run.AgentId with
                | true, current when obj.ReferenceEquals(current.Token, run.Token) && not run.Finished ->
                    run.Finished <- true
                    pendingRuns.Remove run.AgentId |> ignore
                    true, run.Subscription
                | _ -> false, None)

        if claimed then
            subscriptionToDispose
            |> Option.iter (fun subscription -> subscription.Dispose())

            run.Source.SetResult(AgentCompletion.abandoned run.AgentId "ParentCancelled")

    let failRun
        (gate: obj)
        (pendingRuns: Dictionary<string, PendingHostRun>)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (sessions: ISessionHostPort)
        (handoffPort: ReusableHandoffPort option)
        (run: PendingHostRun)
        (error: string)
        =
        deliverFailedCompletion gate pendingRuns journal parentId run error

    /// Terminal outcomes are always accepted by complete. Call sites keep
    /// MarkReady for API shape; body is intentionally a no-op.
    let markReady
        (_gate: obj)
        (_pendingRuns: Dictionary<string, PendingHostRun>)
        (_journal: AgentJournal option)
        (_parentId: SessionId)
        (_sessions: ISessionHostPort)
        (_run: PendingHostRun)
        (_workRecord: string option)
        =
        ()
