namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
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
            match turn.Directory with
            | None -> return false
            | Some directory ->
                let! commonDir = RuntimePath.stateRoot directory

                let workOpt =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir (SessionId.value turn.SessionId)

                return
                    workOpt
                    |> Option.exists (fun work ->
                        work.Delivered
                        || List.contains (ProviderRunIdentity.value turn.ProviderRun) work.RetiredRuns)
        }

    let private classifyManagerOrPlanTurn (context: ReconciledTurnContext) : LongWorkGate =
        let turn = context.Turn

        match context.Failure, turn.Observation, turn.Outcome with
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

    let private longWorkGate (journal: AgentJournal option) (context: ReconciledTurnContext) : Task<LongWorkGate> =
        task {
            let turn = context.Turn

            let isManaged =
                match turn.Role with
                | Some(Role.Manager | Role.Plan) -> true
                | _ -> false

            let authority () =
                journal
                |> Option.bind (fun durable ->
                    (AgentJournal.snapshot durable).AgentProjections.Sessions
                    |> Map.tryFind turn.SessionId
                    |> Option.bind (fun session -> session.PromptAuthority))

            if not isManaged then
                return Ordinary
            elif isExplicitWaitOrOldTurn (authority ()) turn then
                return Wait
            else
                let! settled = isTurnWorkSettled turn
                return classifyManagedTurnGate settled context
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
    /// `abortCause` is the Host boundary typed outcome consumed exactly once (structured-workflow-017 ①).
    /// Guard armed state is not exposed; CE branches on the typed abort outcome.
    let private handleAborted
        (eventPort: IEventObservationPort)
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        (reason: string)
        =
        // degeneration-guard-009: degeneration-guard already owns its successor. Application must
        // not become a second recovery owner. External aborts retain normal cleanup.
        match abortCause with
        | AbortCause.DegenerationGuard _ -> AsyncSupport.completedTask ()
        | AbortCause.External ->
            task {
                // managed-session-lifecycle-018: TurnAborted is an attempt observation, not
                // proof that the logical parent/session ceased to exist. Do not
                // escalate ambiguous AbortError into ParentCancelled and do not
                // physically destroy background children. SessionDeleted or an
                // explicit successor-less termination owns that irreversible act.

                eventPort.NotifyTerminal
                    turn.SessionId
                    (TerminalOutcome.Aborted(TerminalStop.forAuthority turn.AuthorityRootUserMessageId reason))
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
                | XTraceTerminalCompletion.RejectedEmptyOutput -> false

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
        (abortCause: AbortCause)
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
        | ReconcileProgram.TurnAborted reason -> handleAborted eventPort abortCause turn reason
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
        (abortCause: AbortCause)
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
                    TerminalReporter.completeWithEvidence eventPort tracePort turn

                return!
                    handleOutcome
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        observation
                        joinGuardNudges
                        hasLivePty
                        abortCause
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
        (abortCause: AbortCause)
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
                abortCause
                quiescence
                context
                observer
