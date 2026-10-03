namespace Wanxiangshu.Interaction.Repair

open System.Threading.Tasks
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Persistence.Journal

/// Idle-derived interaction repair (missing-final-report / incomplete interaction).
/// HOST-004 admission: no idle permit, or a permit that no longer holds at send
/// time → zero physical prompt, zero claim, zero terminal.
module InteractionRepairWorkflow =

    /// Generic interaction nudges are gate reminders, gated on a fresh idle permit
    /// (HOST-004). The same terminal occasion is idempotent; a fresh terminal while
    /// the interaction gate remains unsatisfied earns another reminder.
    ///
    /// The task is awaited rather than discarded. `|> ignore` on the task also
    /// discarded the claim/abandon bookkeeping inside it, so a failed repair left
    /// a Claimed fact with nothing after it and no terminal for the caller.
    ///
    /// `Superseded` (stale permit) is not a failure: nothing was claimed, nothing
    /// was sent — the system is doing something fresher.
    let private sendRepair
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (turn: ReconciledTurn)
        (prompt: string)
        (repairKind: string)
        : Task =
        task {
            let! outcome =
                HostSessionNudge.trySendIdleGateRepair
                    quiescence
                    permit
                    sessionPort
                    rootWorkspace
                    turn.SessionId
                    prompt
                    turn.Directory
                    journal
                    repairKind
                    turn.ProviderRun

            match outcome with
            | HostSessionNudge.IdleContinuationOutcome.Sent _
            | HostSessionNudge.IdleContinuationOutcome.AdmissionRejected _
            | HostSessionNudge.IdleContinuationOutcome.AlreadyAdmitted
            | HostSessionNudge.IdleContinuationOutcome.Retired -> ()
            | HostSessionNudge.IdleContinuationOutcome.NotSent error ->
                Diagnostic.emit
                    "interaction-gate-nudge-not-sent"
                    [ "session_id", SessionId.value turn.SessionId; "result", error ]
            | HostSessionNudge.IdleContinuationOutcome.Failed error ->
                // Journal/authority/transport failures are Wanxiangshu invariant
                // failures, not model behavior. In production fatal kills the
                // process; the terminal signal keeps node:test fail-closed too.
                eventPort.NotifyTerminal
                    turn.SessionId
                    (TerminalOutcome.Failed(
                        TerminalStop.forAuthority turn.AuthorityRootUserMessageId ("WANXIANGSHU_FATAL: " + error)
                    ))
                |> ignore

                // sendRepair already staged rejection as the terminal effect:
                // fail-closed means neither a swallowed error nor a second
                // process exit. The emitted record keeps the full error for
                // wire-side forensics while the run survives for recovery.
                // (The Failed terminal on the own turn IS the settlement
                // receipt — abandoning the process after it would orphan any
                // intact retries and fabricate a transport-level crash.)
                Diagnostic.emit
                    "interaction-repair-infrastructure-failed"
                    [ "session_id", SessionId.value turn.SessionId; "result", error ]
        }
        :> Task

    /// HOST-004: idle-derived repair sends funnel through one admission point.
    let private trySendIdleRepair
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (prompt: string)
        (repairKind: string)
        : Task =
        match context.Quiescence with
        | None -> AsyncSupport.completedTask ()
        | Some permit ->
            sendRepair quiescence permit sessionPort rootWorkspace eventPort journal context.Turn prompt repairKind

    let private continuationKindOf (journal: AgentJournal option) (turn: ReconciledTurn) =
        journal
        |> Option.bind (fun durable ->
            AgentProjection.tryFind turn.SessionId (AgentJournal.snapshot durable).AgentProjections)
        |> Option.bind (fun session -> session.PromptAuthority)
        |> Option.bind (fun authority -> Map.tryFind turn.PhysicalUserMessageId authority.AcceptedContinuationIds)

    let private isInteractionRepairAttempt journal turn =
        continuationKindOf journal turn = Some PromptAuthority.ContinuationKind.InteractionRepair

    let private repairDefect
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (prompt: string)
        (repairKind: string)
        : Task =
        let turn = context.Turn

        match
            CompletedTurnClassifier.decideRepairDefect
                (isInteractionRepairAttempt journal turn)
                turn.Observation
                turn.Outcome
        with
        | CompletedTurnClassifier.RepairDefectDecision.RequestRepair ->
            trySendIdleRepair quiescence context sessionPort rootWorkspace eventPort journal prompt repairKind
        | CompletedTurnClassifier.RepairDefectDecision.AwaitRepairTerminal
        | CompletedTurnClassifier.RepairDefectDecision.NoRepair -> AsyncSupport.completedTask ()

    /// The `None` arm of `repairBloggerProtocol`: a reached-for journal that some
    /// composition paths never install is an assembly gap, not a semantic cut.
    /// The caller already showed the Failed terminal; kiling the process here
    /// erases the durable trace a future replay could use to explain destruction.
    let private notifyBloggerProtocolAudited
        (eventPort: IEventObservationPort)
        (turn: ReconciledTurn)
        (reason: string)
        =
        eventPort.NotifyTerminal
            turn.SessionId
            (TerminalOutcome.Failed(TerminalStop.forAuthority turn.AuthorityRootUserMessageId reason))
        |> ignore

        Diagnostic.emit
            "blogger-protocol-repair-failed"
            [ "session_id", SessionId.value turn.SessionId; "result", reason ]


    /// Blogger has a stricter terminal protocol than ordinary agents: prose-only
    /// completion is not a closing report. Idle is the only guaranteed wake after
    /// a zero-tool terminal, so idle forwards the exact observation to
    /// BloggerCoordinator.observeIdleRepair, which owns the missing-chronicle
    /// sequence instead of the generic MissingClosingReport
    /// continuation.
    /// Historical/unowned idle is observation only: without the exact live
    /// BloggerRequest there is no protocol budget to spend.
    let private consumeBloggerRepairOutcome (outcome: BloggerRepairOutcome) : unit =
        match outcome with
        | BloggerRepairOutcome.NudgeSent _
        | BloggerRepairOutcome.AabbSent _
        | BloggerRepairOutcome.RepairInjected _
        | BloggerRepairOutcome.PendingRepairWait
        | BloggerRepairOutcome.UnownedIdleIgnored
        | BloggerRepairOutcome.SupersededIgnored
        | BloggerRepairOutcome.UnprovenIgnored
        | BloggerRepairOutcome.AbandonedExhausted
        | BloggerRepairOutcome.Completed -> ()

    let private sendOwnedBloggerRepair
        (host: IBloggerRuntimeHost)
        (durable: AgentJournal)
        (request: BloggerRequestContext)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        : Task =
        task {
            let! outcome =
                BloggerCoordinator.observeIdleRepair
                    host
                    (Some durable)
                    request
                    quiescence
                    context
                    sessionPort
                    rootWorkspace
                    eventPort

            consumeBloggerRepairOutcome outcome
            return ()
        }
        :> Task

    let private forwardOwnedBloggerRepair
        (host: IBloggerRuntimeHost)
        (durable: AgentJournal)
        (request: BloggerRequestContext)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        : Task =
        sendOwnedBloggerRepair host durable request quiescence context sessionPort rootWorkspace eventPort

    let repairBloggerProtocol
        (host: IBloggerRuntimeHost)
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        : Task =
        let liveRequest = host.TryPeekCurrentRequest(SessionId.value context.Turn.SessionId)

        match journal, liveRequest with
        | None, _ ->
            notifyBloggerProtocolAudited eventPort context.Turn "blogger protocol repair requires an AgentJournal"
            AsyncSupport.completedTask ()
        | Some _, None ->
            Diagnostic.emit
                "blogger-protocol-repair-unowned-idle"
                [ "session_id", SessionId.value context.Turn.SessionId
                  "result", "no live BloggerRequest; idle cannot spend protocol repair budget" ]

            AsyncSupport.completedTask ()
        | Some durable, Some request ->
            forwardOwnedBloggerRepair host durable request quiescence context sessionPort rootWorkspace eventPort

    /// interaction-authority-023: a retry continuation suppresses idle repair only
    /// while its own attempt is unsettled. The race this guards is the interleaved
    /// idle of the retry attempt itself — finish=None (Unknown) or tool-calls
    /// while the retry response is still on the wire, where a fresh SessionIdle
    /// mints a valid quiescence permit and a repair nudge would hijack the
    /// response.
    ///
    /// Settlement is exact on both sides: the durable `ChatExecution` terminal or
    /// the stable observation terminal of the same turn. Either one releases the
    /// suppression, so a fresh unsatisfied terminal (length, unusable stop)
    /// regains its nudge qualification per interaction-authority-019.
    let private retryAttemptSuppressesRepair
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        (turn: ReconciledTurn)
        : bool =
        let isRetryContinuation =
            match observation with
            | Some port ->
                port.TryContinuationKind turn.SessionId turn.PhysicalUserMessageId = Some
                    PromptContinuationKind.ProviderRetryAttempt
            | None -> continuationKindOf journal turn = Some PromptAuthority.ContinuationKind.ProviderRetryAttempt

        let hasDurableTerminal =
            observation
            |> Option.exists (fun port -> port.HasExecutionTerminal turn.SessionId turn.PhysicalUserMessageId)

        CompletedTurnClassifier.retryContinuationSuppressesRepair
            isRetryContinuation
            turn.Observation
            turn.Outcome
            hasDurableTerminal

    let private isFissionReplaced (journal: AgentJournal option) (sessionId: SessionId) : bool =
        FissionRuntime.isSilentInterrupt sessionId
        || (journal
            |> Option.exists (fun durable ->
                FissionProjection.tryActiveForOwner sessionId (AgentJournal.snapshot durable).AgentProjections.Fission
                |> Option.isSome))

    /// GLORY-070 / HOST-004 rev.4: a stable idle that never produced a final
    /// report is reminded once per exact terminal occasion, and only when the
    /// pass carried idle evidence. If the reminder itself reaches another invalid
    /// terminal, that fresh occasion may remind again until the closing-report
    /// gate is satisfied. An unsettled ProviderRetryAttempt owns its own request
    /// until it settles; once settled, its unsatisfied terminal earns this
    /// reminder like any other (interaction-authority-023).
    let repairMissingFinalReport
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        : Task =
        let fissionReplaced =
            match observation with
            | Some obs ->
                FissionRuntime.isSilentInterrupt context.Turn.SessionId
                || obs.IsFissionActive context.Turn.SessionId
            | None -> isFissionReplaced journal context.Turn.SessionId

        if fissionReplaced || retryAttemptSuppressesRepair journal observation context.Turn then
            AsyncSupport.completedTask ()
        else
            repairDefect
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                (ProviderProse.documentFor context.Turn.SessionId RuntimeNudge.MissingClosingReport Map.empty)
                "missing-final-report"

    /// Incomplete in-progress interaction: classify then idle-repair, unless the
    /// exact request is still owned by an unsettled ProviderRetryAttempt.
    let repairIncompleteInteraction
        (quiescence: ISessionQuiescenceGate)
        (context: ReconciledTurnContext)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (observation: TurnObservationJournalPort option)
        : Task =
        let turn = context.Turn

        let fissionReplaced =
            match observation with
            | Some obs ->
                FissionRuntime.isSilentInterrupt turn.SessionId
                || obs.IsFissionActive turn.SessionId
            | None -> isFissionReplaced journal turn.SessionId

        if fissionReplaced || retryAttemptSuppressesRepair journal observation turn then
            AsyncSupport.completedTask ()
        elif CompletedTurnClassifier.needsInteractionRepair turn.Role (box turn.Outcome) turn.Parts then
            repairDefect
                quiescence
                context
                sessionPort
                rootWorkspace
                eventPort
                journal
                (ProviderProse.documentFor turn.SessionId RuntimeNudge.InteractionContinue Map.empty)
                "interaction-repair"
        else
            AsyncSupport.completedTask ()
