namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Resources

/// Ordinary turn observation policy (INTERACTION-REPAIR / PROVIDER-RECOVERY / TERMINAL-REPORT).
module OrdinaryTurnWorkflow =

    type private LongWorkGate =
        | Ordinary
        | Wait
        | Continue

    let private isExplicitWaitOrOldTurn
        (authority: PromptAuthority.PromptAuthorityProjection option)
        (turn: ReconciledTurn)
        : bool =
        authority
        |> Option.exists (fun state ->
            let obsolete =
                match state.ActiveLogicalRun with
                | Some active -> active.AuthorityRootUserMessageId <> turn.AuthorityRootUserMessageId
                | None -> state.LastAuthorityProfile.IsSome

            obsolete
            || (state.UserQuestions
                |> Map.exists (fun _ question ->
                    question.Request.AuthorityRootUserMessageId = turn.AuthorityRootUserMessageId
                    && (question.Answer.IsNone || question.Request.ProviderRunId = turn.ProviderRun))))

    let private isTurnWorkSettled (turn: ReconciledTurn) : Task<bool> =
        task {
            // Manager missing projection must not be treated as settled/completed
            match turn.Directory with
            | None -> return false
            | Some directory ->
                let! commonDir = RuntimePath.stateRoot directory

                let workOpt =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir (SessionId.value turn.SessionId)

                return
                    workOpt
                    |> Option.exists (fun work ->
                        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.owesNoContinuation work
                        || List.contains (ProviderRunIdentity.value turn.ProviderRun) work.RetiredRuns)
        }

    /// Delivery evidence for one turn (deliver clause 3). A turn counts as
    /// delivered only when a durable witness exists for this session and this
    /// provider run, and its fence names this authority term. The witness is
    /// read through the fence-aware entry: siblings of the same run with a
    /// different delivery material are not picked at random, and a fence that
    /// does not name this term is not this turn's evidence. The witness
    /// itself carries the view declaration, the pre-call prose and the
    /// materialized view, so a view declaration, a prose body, a tool call or
    /// a provider stop can never stand in for it.
    /// The process-live cell is a material cache, not proof: it can show what
    /// was handed over, not that it was handed over. Material readers keep
    /// taking the cell for rendering; this decision point never takes it as
    /// evidence, so a turn without a durable witness is not a delivered turn.
    let private hasDeliveryEvidence (journal: AgentJournal option) (turn: ReconciledTurn) : bool =
        match journal with
        | Some durable ->
            let witnesses = (AgentJournal.snapshot durable).AgentProjections.Deliveries

            TaskDelivery.tryFindByFenceFields
                turn.SessionId
                turn.ProviderRun
                None
                (Some turn.AuthorityRootUserMessageId)
                (Some turn.PhysicalUserMessageId)
                // 本次只有子集围栏：turn 不携带 LogicalRunId 也不携带交付任务，
                // 缺失的那两位（逻辑运行 / 交付任务）由交付见证的写入方
                // （settle 侧按 activeProfile 与 deliveryTaskOf 登记）另行核对。
                None
                None
                witnesses
            |> Option.isSome
        | None -> false

    /// Roles that stop naturally deliver their outcome in prose and session views, so
    /// their completed turn is an ordinary terminal without requiring an explicit delivery
    /// witness or nudging unfinished work (an empty answer still fails through AgentRunResult.IsValid).
    let private deliversByProse (turn: ReconciledTurn) : bool =
        turn.Role = Some Role.Advisor
        || turn.Role = Some Role.Engineer
        || turn.Role = Some Role.DevOps

    let private classifyManagerOrPlanTurn (context: ReconciledTurnContext) : LongWorkGate =
        let turn = context.Turn

        match context.Failure, turn.Observation, turn.Outcome with
        | None, None, ReconcileProgram.TurnCompleted when turn.Role = Some Role.Manager && turn.Directory.IsNone ->
            // Manager 缺投影当未完成，继续推进
            Continue
        | None, None, ReconcileProgram.TurnCompleted -> Continue
        | None, None, ReconcileProgram.TurnNeedsContinuation _ when
            turn.Finish
            |> Option.exists (fun finish ->
                System.String.Equals(finish, "stop", System.StringComparison.OrdinalIgnoreCase))
            ->
            Continue
        | _ -> Ordinary

    let private classifyManagedTurnGate settled context =
        if settled then Wait else classifyManagerOrPlanTurn context

    /// A turn whose delivery is proven by a durable witness is this turn's own
    /// terminal: the owner of that delivery waits for a terminal fact, so the
    /// terminal must be published. A turn without such a witness keeps the
    /// Continue/nudge path (the obligation is still open), and Manager / Plan
    /// turns keep their own lifecycle ownership.
    let private classifyDeliveredTurn (turn: ReconciledTurn) =
        match turn.Role, turn.Outcome with
        | (Some Role.Manager | Some Role.Plan), _ -> Wait
        | _, ReconcileProgram.TurnCompleted -> Ordinary
        | _, _ -> Wait

    let private settledTurnGate
        (journal: AgentJournal option)
        (context: ReconciledTurnContext)
        (settled: bool)
        : LongWorkGate =
        if settled then
            Wait
        elif hasDeliveryEvidence journal context.Turn then
            classifyDeliveredTurn context.Turn
        else
            classifyManagerOrPlanTurn context

    let private longWorkGate (journal: AgentJournal option) (context: ReconciledTurnContext) : Task<LongWorkGate> =
        task {
            let turn = context.Turn

            let hasRole = turn.Role.IsSome

            let authority () =
                journal
                |> Option.bind (fun durable ->
                    (AgentJournal.snapshot durable).AgentProjections.Sessions
                    |> Map.tryFind turn.SessionId
                    |> Option.bind (fun session -> session.PromptAuthority))

            if not hasRole then
                return Ordinary
            elif isExplicitWaitOrOldTurn (authority ()) turn then
                return Wait
            elif deliversByProse turn then
                return Ordinary
            else
                let! settled = isTurnWorkSettled turn
                return settledTurnGate journal context settled
        }

    let private requestKindOfCompleted (port: TurnObservationJournalPort) (turn: ReconciledTurn) =
        let continuationKind =
            port.TryContinuationKind turn.SessionId turn.PhysicalUserMessageId

        match continuationKind with
        | Some PromptContinuationKind.InteractionRepair -> Some ProviderRequestKind.InteractionRepair
        | _ -> Some ProviderRequestKind.WorkMain

    let private recordSuccessIfValid
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (turn: ReconciledTurn)
        =
        task {
            let clearingKind =
                observation
                |> Option.bind (fun port ->
                    requestKindOfCompleted port turn
                    |> Option.filter ProviderRequestKind.clearsFailureCountOnSuccess)

            match journal, clearingKind with
            | Some durable, Some _ ->
                let port = AgentJournalPortAdapter.forProviderFailure durable
                let! _ = ProviderFailureLedger.recordConfirmedSuccess port turn.SessionId turn.ProviderRun
                return ()
            | _ -> return ()
        }

    let private dispatchIdleTurn
        (quiescence: ISessionQuiescenceGate)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (context: ReconciledTurnContext)
        (observer: ContinuationAcceptanceObserver option)
        : Task =
        task {
            let! workGate = longWorkGate journal context

            match workGate, context.Turn.Observation, context.Turn.Outcome with
            | Wait, _, _ -> return ()
            | Continue, _, _ ->
                return!
                    InteractionRepairWorkflow.repairUnfinishedWork
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observer
            | _, Some ReconcileProgram.TurnUnknown, _ ->
                return!
                    InteractionRepairWorkflow.repairMissingFinalReport
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        observer
            | _, None, ReconcileProgram.TurnInProgress ->
                return!
                    InteractionRepairWorkflow.repairIncompleteInteraction
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        observer
            | _, None, ReconcileProgram.TurnNeedsContinuation _ ->
                return!
                    InteractionRepairWorkflow.repairMissingFinalReport
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        observer
            | _, None, (ReconcileProgram.TurnCompleted | ReconcileProgram.TurnAborted _ | ReconcileProgram.TurnFailed _) ->
                return ()
        }

    /// Revisit a previously delivered turn only for work whose authority comes
    /// from a fresh idle observation. Terminal plumbing remains first-delivery only.
    let observeIdle
        (quiescence: ISessionQuiescenceGate)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (context: ReconciledTurnContext)
        (observer: ContinuationAcceptanceObserver option)
        : Task =
        let isFissionReplaced =
            FissionRuntime.isSilentInterrupt context.Turn.SessionId
            || (observation
                |> Option.exists (fun port -> port.IsFissionActive context.Turn.SessionId))

        if isFissionReplaced then
            AsyncSupport.completedTask ()
        else
            dispatchIdleTurn quiescence sessionPort rootWorkspace eventPort journal observation context observer

    /// Own the reconciled ordinary-turn outcome match.
    let private handleAborted (eventPort: IEventObservationPort) (turn: ReconciledTurn) (reason: string) =
        task {
            // managed-session-lifecycle-018: TurnAborted is an attempt observation, not
            // proof that the logical parent/session ceased to exist. Do not
            // escalate ambiguous AbortError into ParentCancelled and do not
            // physically destroy background children. SessionDeleted or an
            // explicit successor-less termination owns that irreversible act.

            // 027 (HNI interruption payload, ruling 2026-10-22): the stop
            // carries the identity of the interrupted round itself —
            // `turn.ProviderRun` is a fact about this turn, never inferred
            // from anywhere else. Settlement (`RunLifecycle.complete`) may
            // match a durable delivery witness by exact fence with it; this
            // is NOT a terminal fact source and decides no closure.
            eventPort.NotifyTerminal
                turn.SessionId
                (TerminalOutcome.Aborted(
                    TerminalStop.forAuthorityRun turn.AuthorityRootUserMessageId turn.ProviderRun reason
                ))
            |> ignore

            return ()
        }
        :> Task

    let private applyJoinGuardNudge
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        : Task<bool> =
        task {
            let turn = context.Turn

            match context.Quiescence with
            // No idle permit: no reminder can be claimed on this occasion, and
            // the reconcile pass dedupes the published turn — this terminal will
            // not be re-offered. Only publishing now can still reach the parent.
            | None -> return false
            | Some permit ->
                match!
                    HostJoinGuard.nudge
                        sessionPort
                        rootWorkspace
                        (journal |> Option.map AgentJournalPortAdapter.forHostJoinGuard)
                        journal
                        joinGuardNudges
                        (fun () -> quiescence.TryConsume permit)
                        (fun () -> quiescence.TryRelease permit)
                        turn.SessionId
                        turn.ProviderRun
                        turn.Directory
                with
                | HostJoinGuard.JoinGuardNudgeOutcome.Failed reason ->
                    eventPort.NotifyTerminal
                        turn.SessionId
                        (TerminalOutcome.Failed(TerminalStop.forAuthority turn.AuthorityRootUserMessageId reason))
                    |> ignore
                    // The Failed terminal was published: the parent already
                    // learns this occasion resolved. Do not publish again.
                    return true
                | HostJoinGuard.JoinGuardNudgeOutcome.Sent _
                | HostJoinGuard.JoinGuardNudgeOutcome.AlreadyOutstanding ->
                    // A durable continuation claim exists on this session: the
                    // woken run will report a fresh terminal occasion.
                    return true
                | HostJoinGuard.JoinGuardNudgeOutcome.NotSent
                | HostJoinGuard.JoinGuardNudgeOutcome.Superseded
                | HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected _ ->
                    // No durable claim exists for the reminder, and the reconcile
                    // pass already consumed this published turn: nothing will
                    // re-offer this terminal. Withholding it would strand every
                    // parent waiting on this run.
                    return false
        }

    let private handleCompleted
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (joinGuardNudges: HashSet<string>)
        (hasLivePty: string -> bool)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (completeAgent: unit -> Task<XTraceTerminalCompletion>)
        =
        task {
            let turn = context.Turn

            let terminalPolicyPort =
                Option.map AgentJournalPortAdapter.forTerminalPolicy journal

            let joinOutstanding =
                TerminalPolicy.outstandingBackground terminalPolicyPort hasLivePty turn.Role turn.SessionId

            // A withheld completion is only safe when a later pass is promised
            // to re-offer this same terminal. When JoinGuard cannot make that
            // promise (nudge unsent or superseded), the completed terminal is
            // published instead — deferring would strand every parent waiting
            // on a fact that already happened. (managed-session-lifecycle:
            // terminal admission deferral)
            let resolveOutstandingJoinCompletion () =
                task {
                    let! deferred =
                        applyJoinGuardNudge
                            sessionPort
                            rootWorkspace
                            eventPort
                            journal
                            joinGuardNudges
                            quiescence
                            context

                    if deferred then
                        return XTraceTerminalCompletion.RejectedEmptyOutput
                    else
                        return! completeAgent ()
                }

            let! completion =
                if not joinOutstanding then
                    completeAgent ()
                else
                    resolveOutstandingJoinCompletion ()

            let terminalValid =
                match completion with
                | XTraceTerminalCompletion.Published _ -> true
                | XTraceTerminalCompletion.CaptureFailed _
                | XTraceTerminalCompletion.RejectedMissingRole
                | XTraceTerminalCompletion.RejectedEmptyOutput
                | XTraceTerminalCompletion.RejectedUndelivered -> false

            if terminalValid then
                do! recordSuccessIfValid journal observation turn

            return ()
        }
        :> Task

    let private handleFailedTurn
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (context: ReconciledTurnContext)
        (error: string)
        =
        match context.Failure with
        | Some failure ->
            ProviderRecoveryWorkflow.continueAfterConfirmedFailure
                sessionPort
                rootWorkspace
                eventPort
                journal
                context.Turn
                failure
                error
                (ProviderProse.documentFor context.Turn.SessionId RuntimeNudge.ProviderRetry Map.empty)
        | None ->
            eventPort.NotifyTerminal
                context.Turn.SessionId
                (TerminalOutcome.Failed(TerminalStop.forAuthority context.Turn.AuthorityRootUserMessageId error))
            |> ignore

            Task.FromResult(()) :> Task

    let private handleOutcome
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (joinGuardNudges: HashSet<string>)
        (hasLivePty: string -> bool)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (completeAgent: unit -> Task<XTraceTerminalCompletion>)
        (observer: ContinuationAcceptanceObserver option)
        =
        let turn = context.Turn

        match turn.Outcome with
        | ReconcileProgram.TurnInProgress ->
            InteractionRepairWorkflow.repairIncompleteInteraction
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | ReconcileProgram.TurnNeedsContinuation _ ->
            // Absorb text and reasoning into the XTrace even though this turn is
            // not completable, then ask for the missing report. Still not provider recovery.
            // (The XTrace parts are captured at the transform boundary.)
            InteractionRepairWorkflow.repairMissingFinalReport
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | ReconcileProgram.TurnAborted reason -> handleAborted eventPort turn reason
        | ReconcileProgram.TurnFailed error ->
            handleFailedTurn sessionPort rootWorkspace eventPort journal context error
        | ReconcileProgram.TurnCompleted ->
            handleCompleted
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                joinGuardNudges
                hasLivePty
                quiescence
                context
                completeAgent

    /// degenerate attempt from entering the ordinary completion path and
    /// re-triggers execution on that branch.
    /// If execution failed (e.g. timeout), report the error and do not record
    /// completion.
    /// If tool execution had an error, complete the turn anyway (the LLM can see
    /// the tool error in the next turn).
    let private dispatchTurnObservation
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (joinGuardNudges: HashSet<string>)
        (hasLivePty: string -> bool)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (observer: ContinuationAcceptanceObserver option)
        : Task =
        let turn = context.Turn

        task {
            let! workGate = longWorkGate journal context

            match workGate, turn.Observation with
            | Wait, _ -> return ()
            | Continue, _ ->
                return!
                    InteractionRepairWorkflow.repairUnfinishedWork
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observer
            | _, Some ReconcileProgram.TurnUnknown ->
                return!
                    InteractionRepairWorkflow.repairMissingFinalReport
                        quiescence
                        context
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        observer
            | _, None ->
                let completeAgent () =
                    let tracePort = journal |> Option.map TerminalTracePort.forJournal

                    TerminalReporter.completeWithEvidence
                        eventPort
                        tracePort
                        turn
                        (deliversByProse turn || hasDeliveryEvidence journal turn)

                return!
                    handleOutcome
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        joinGuardNudges
                        hasLivePty
                        quiescence
                        context
                        completeAgent
                        observer
        }

    let observe
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (joinGuardNudges: HashSet<string>)
        (hasLivePty: string -> bool)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (observer: ContinuationAcceptanceObserver option)
        : Task =
        let turn = context.Turn

        let isFissionReplaced =
            FissionRuntime.isSilentInterrupt turn.SessionId
            || (observation |> Option.exists (fun port -> port.IsFissionActive turn.SessionId))

        if isFissionReplaced then
            AsyncSupport.completedTask ()
        else
            dispatchTurnObservation
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                joinGuardNudges
                hasLivePty
                quiescence
                context
                observer
