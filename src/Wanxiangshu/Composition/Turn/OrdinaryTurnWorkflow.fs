namespace Wanxiangshu.Composition.Turn

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Attention
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

    let private bloggerReceiptKind (port: TurnObservationJournalPort) (turn: ReconciledTurn) =
        port.TryBloggerReceiptKind turn.SessionId turn.ProviderRun

    let private requestKindWithoutBloggerReceipt (port: TurnObservationJournalPort) (turn: ReconciledTurn) =
        let continuationKind =
            port.TryContinuationKind turn.SessionId turn.PhysicalUserMessageId

        match continuationKind, turn.Role with
        | Some PromptContinuationKind.InteractionRepair, _ -> Some ProviderRequestKind.InteractionRepair
        // A Blogger terminal without a durable cycle receipt did not prove a
        // business Main or maintenance Squash success. Never clear the failure budget
        // from Role alone.
        | _, Some Role.Blogger -> None
        | _ -> Some ProviderRequestKind.WorkMain

    let private requestKindOfCompleted (port: TurnObservationJournalPort) (turn: ReconciledTurn) =
        match bloggerReceiptKind port turn with
        | Some BlogFrameKind.Squash -> Some ProviderRequestKind.BloggerSquash
        | Some BlogFrameKind.Entry -> Some ProviderRequestKind.BloggerMain
        | None -> requestKindWithoutBloggerReceipt port turn

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

        match isFissionReplaced, context.Turn.Observation, context.Turn.Outcome with
        | true, _, _ -> AsyncSupport.completedTask ()
        | false, Some ReconcileProgram.TurnUnknown, _ ->
            InteractionRepairWorkflow.repairMissingFinalReport
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | false, None, ReconcileProgram.TurnInProgress ->
            InteractionRepairWorkflow.repairIncompleteInteraction
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | false, None, ReconcileProgram.TurnNeedsContinuation _ ->
            InteractionRepairWorkflow.repairMissingFinalReport
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | false, None, (ReconcileProgram.TurnCompleted | ReconcileProgram.TurnAborted _ | ReconcileProgram.TurnFailed _) ->
            AsyncSupport.completedTask ()

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
        =
        task {
            let turn = context.Turn

            match context.Quiescence with
            | None -> ()
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
                | _ -> ()
        }

    /// ATTENTION-005: after an Engineer, DevOps or Orchestrator run reaches
    /// its natural terminal, pending deferred work is presented once through a
    /// same-run continuation and then consumed durably. Presentation and
    /// receipt are idempotent: a replayed terminal finds nothing pending, and a
    /// failed send leaves the entries for the next terminal.
    let private presentPendingDeferredWork
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal)
        (turn: ReconciledTurn)
        (items: DeferredWorkItem list)
        =
        task {
            let attention = AttentionConcernJournalAdapter.forAttention journal

            let guidance =
                ProviderProse.render
                    (ProviderProse.languageOf turn.SessionId)
                    "attention-regulation/defer-presentation"
                    Map.empty

            let entries = items |> List.map (fun item -> "- " + item.Text) |> String.concat "\n"

            let prompt = guidance + "\n\n" + entries

            let! sent =
                HostSessionNudge.sendContinuation
                    sessionPort
                    rootWorkspace
                    turn.SessionId
                    prompt
                    PromptAuthority.ContinuationKind.DeferredWorkPresentation
                    turn.Directory
                    (Some journal)

            match sent with
            | Error _ -> return ()
            | Ok _ ->
                let fact =
                    AttentionFactCases.DeferredWorkConsumed
                        {| SessionId = turn.SessionId
                           OccurrenceIds = (items |> List.map (fun item -> item.OccurrenceId)) |}

                let! _ = attention.Append turn.SessionId (Some turn.ProviderRun) fact
                return ()
        }

    let private presentDeferredWork
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal)
        (turn: ReconciledTurn)
        =
        task {
            let attention = AttentionConcernJournalAdapter.forAttention journal
            let pending = AttentionProjection.pending turn.SessionId (attention.Read())

            match pending with
            | [] -> return ()
            | items -> return! presentPendingDeferredWork sessionPort rootWorkspace journal turn items
        }

    let private presentDeferredWorkForRole
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (turn: ReconciledTurn)
        =
        match journal, turn.Role with
        | Some durable, Some(Role.Engineer | Role.DevOps | Role.Orchestrator) ->
            presentDeferredWork sessionPort rootWorkspace durable turn
        | _ -> Task.FromResult(())

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

            let! completion =
                if joinOutstanding then
                    Task.FromResult XTraceTerminalCompletion.RejectedEmptyOutput
                else
                    completeAgent ()

            let terminalValid =
                match completion with
                | XTraceTerminalCompletion.Published _ -> true
                | XTraceTerminalCompletion.CaptureFailed _
                | XTraceTerminalCompletion.RejectedMissingRole
                | XTraceTerminalCompletion.RejectedEmptyOutput -> false

            if terminalValid then
                do! recordSuccessIfValid journal observation turn

            if TerminalPolicy.sessionDead terminalPolicyPort turn.SessionId then
                return ()
            elif joinOutstanding then
                return!
                    applyJoinGuardNudge sessionPort rootWorkspace eventPort journal joinGuardNudges quiescence context
            elif terminalValid then
                return! presentDeferredWorkForRole sessionPort rootWorkspace journal turn
            else
                return ()
        }
        :> Task

    let private handleFailedTurn
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (recoveryScope: IBloggerRuntimeHost)
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
                recoveryScope
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
        (recoveryScope: IBloggerRuntimeHost)
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
            handleFailedTurn sessionPort rootWorkspace eventPort journal recoveryScope context error
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

    let observe
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (recoveryScope: IBloggerRuntimeHost)
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

        match isFissionReplaced, turn.Observation with
        | true, _ -> AsyncSupport.completedTask ()
        | false, Some ReconcileProgram.TurnUnknown ->
            InteractionRepairWorkflow.repairMissingFinalReport
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                observer
        | false, None ->
            let completeAgent () =
                let tracePort = journal |> Option.map TerminalTracePort.forJournal
                TerminalReporter.completeWithEvidence eventPort tracePort turn

            handleOutcome
                sessionPort
                rootWorkspace
                eventPort
                journal
                observation
                recoveryScope
                joinGuardNudges
                hasLivePty
                abortCause
                quiescence
                context
                completeAgent
                observer
