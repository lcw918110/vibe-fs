namespace Wanxiangshu.Mission.Manager

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Work
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

module ManagerWorkflow =
    [<Literal>]
    let private assessPath = "runtime/manager-assess"

    [<Literal>]
    let private workPath = "runtime/manager-work"

    [<Literal>]
    let private finishPath = "runtime/manager-finish"

    let private lifecycleWork (directory: string option) (sessionId: string) =
        task {
            match directory with
            | Some workspace ->
                let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                return Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir sessionId
            | None -> return None
        }

    /// The iteration ordinal as the provider reads it: "1" for the first
    /// Manager on this road, then one more per durable opening. A road with no
    /// view yet (first opening not yet committed) is still the first.
    let private iterationOrdinalText (directory: string option) (sessionId: SessionId) =
        task {
            let! workOpt = lifecycleWork directory (SessionId.value sessionId)

            let ordinal =
                workOpt
                |> Option.map (fun work ->
                    let retired = work.RoundCount
                    max 1 (retired + if work.ActiveId.IsSome then 1 else 0))
                |> Option.defaultValue 1

            return string ordinal
        }

    /// True when this provider run already produced one of the work's
    /// retirements: the run is retired and its later observations are stale.
    let private isRetiredObservation
        (directory: string option)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        =
        task {
            let! workOpt = lifecycleWork directory (SessionId.value sessionId)

            return
                workOpt
                |> Option.exists (fun work -> List.contains (ProviderRunIdentity.value providerRun) work.RetiredRuns)
        }

    // Algorithm H reads the opening sequence plus tenure, review state, and obligations.
    // S1–S3 do not enter the S4 resource match. Certificates are not an input.
    let private resourceForCurrentAction (directory: string option) (sessionId: SessionId) =
        task {
            let! workOpt = lifecycleWork directory (SessionId.value sessionId)

            return
                match workOpt with
                | None -> None
                | Some work when work.Sequence <> [ "S4" ] -> None
                | Some work when work.ActiveId.IsNone -> None
                | Some work when not work.Reviewed -> Some assessPath
                | Some _ -> Some workPath
        }


    let private activeResourcePath (directory: string option) (sessionId: SessionId) =
        resourceForCurrentAction directory sessionId

    let private sendNudge
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal)
        (turn: ReconciledTurn)
        (resourcePath: string)
        =
        task {
            let! substitutions =
                if resourcePath = assessPath then
                    task {
                        let! ordinal = iterationOrdinalText turn.Directory turn.SessionId
                        return Map [ "ordinal", ordinal ]
                    }
                else
                    Task.FromResult Map.empty

            return!
                HostSessionNudge.trySendIdleGateContinuation
                    quiescence
                    permit
                    sessionPort
                    rootWorkspace
                    turn.SessionId
                    (ProviderProse.documentFor turn.SessionId "work/manager-continue" Map.empty
                     + "\n\n"
                     + ProviderProse.documentFor turn.SessionId resourcePath substitutions)
                    PromptAuthority.ContinuationKind.ManagerGuard
                    turn.Directory
                    (Some journal)
                    (resourcePath + ":" + ProviderRunIdentity.value turn.ProviderRun)
                    turn.ProviderRun
                    PromptDispatcher.AwaitMode.Detached
        }

    let private scheduleNudge
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal)
        (turn: ReconciledTurn)
        =
        task {
            // 没读到工作不是完成证据；提醒继续核对任务，而不是悄悄放行自然结束。
            let! resourceOpt = activeResourcePath turn.Directory turn.SessionId
            let resourcePath = resourceOpt |> Option.defaultValue workPath

            let! outcome = sendNudge quiescence permit sessionPort rootWorkspace journal turn resourcePath

            match outcome with
            | HostSessionNudge.IdleContinuationOutcome.Failed error
            | HostSessionNudge.IdleContinuationOutcome.NotSent error ->
                return invalidOp (sprintf "Manager continuation could not be delivered: %s" error)
            | _ -> return ()
        }
        :> Task

    /// Algorithm H8 entry two: an S4 work whose open tenure has not been reviewed
    /// yet starts from the successor prompt, and the last committed retirement is
    /// the occasion: a Relayed retirement opened the successor itself, a Delivered
    /// retirement is the cut Change handed the same Work over from after a machine
    /// conflict or a rebase that invalidated the candidate
    /// (change-integration-005/013). Either way the retirement id names the cut
    /// and the committed retirement fence carries the provider run that made it;
    /// no run identity is invented, and a work with no retirement (the first
    /// tenure) gets its authority from the job's own prompt, not from here.
    let private unreviewedS4Retirement (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
        match work.Sequence, work.ActiveId, List.tryLast work.Retirements with
        | [ "S4" ], Some _, Some retirement when not work.Reviewed ->
            Some(retirement.RetirementId, retirement.Fence.ProviderRunId)
        | _ -> None

    let private successorPromptContext
        (workspaceDirectory: string option)
        (sessionIdText: string)
        : Task<(string * string) option> =
        task {
            let! workOpt = lifecycleWork workspaceDirectory sessionIdText
            return workOpt |> Option.bind unreviewedS4Retirement
        }

    /// Read-only probe (the `loopSignalOf` / `publishReviewOf` pattern): the
    /// successor-prompt occasion this Manager session's work currently owes,
    /// read from the same fold the dispatch reads and dispatching nothing.
    /// `present = false` is the fail-closed answer for a work that owes no
    /// successor prompt: a first tenure with no retirement, a work whose open
    /// tenure is already reviewed, and every non-S4 sequence.
    let successorPromptContextOf (workspaceDirectory: string) (sessionIdText: string) : Task<obj> =
        task {
            let! contextOpt = successorPromptContext (Some workspaceDirectory) sessionIdText

            return
                match contextOpt with
                | Some(retirementId, providerRunId) ->
                    box
                        {| present = true
                           retirementId = retirementId
                           providerRunId = providerRunId |}
                | None ->
                    box
                        {| present = false
                           retirementId = ""
                           providerRunId = "" |}
        }

    /// 第一次 review 开立 Manager 工作。变换不发 Open，也不发 Reopen。
    let ensureManagerWorkOpened
        (workspaceDirectory: string option)
        (observedHead: int64 option)
        (sessionIdTextOpt: string option)
        : Task<unit> =
        ignore workspaceDirectory
        ignore observedHead
        ignore sessionIdTextOpt
        Task.FromResult()

    let private deliverLoopPrompt
        sessionPort
        rootWorkspace
        workspaceDirectory
        (durable: AgentJournal)
        sessionId
        (retirementId: string)
        (terminalProviderRunId: string)
        =
        task {
            let! ordinal = iterationOrdinalText workspaceDirectory sessionId

            let loopPromptText =
                ProviderProse.documentFor sessionId "runtime/manager-assess" (Map [ "ordinal", ordinal ])

            let terminalRun = ProviderRunIdentity.create terminalProviderRunId

            match!
                HostSessionNudge.trySendGateContinuation
                    sessionPort
                    rootWorkspace
                    sessionId
                    loopPromptText
                    PromptAuthority.ContinuationKind.ManagerGuard
                    workspaceDirectory
                    (Some durable)
                    (ManagerLoopGate.gateKind (RetirementId.create retirementId))
                    terminalRun
            with
            | HostSessionNudge.GateContinuationOutcome.Sent _
            | HostSessionNudge.GateContinuationOutcome.AlreadyAdmitted -> return ()
            | HostSessionNudge.GateContinuationOutcome.Retired ->
                return invalidOp (sprintf "MANAGER-LOOP-003: loop gate retired for retirement %s" retirementId)
            | HostSessionNudge.GateContinuationOutcome.Failed error ->
                return invalidOp (sprintf "MANAGER-LOOP-003: loop gate nudge failed: %s" error)
        }

    let private deliverSuccessorPrompt
        sessionPort
        rootWorkspace
        workspaceDirectory
        (durable: AgentJournal)
        (sessionId: SessionId)
        =
        task {
            match! successorPromptContext workspaceDirectory (SessionId.value sessionId) with
            | Some(retirementId, providerRunId) ->
                do!
                    deliverLoopPrompt
                        sessionPort
                        rootWorkspace
                        workspaceDirectory
                        durable
                        sessionId
                        retirementId
                        providerRunId
            | None -> return ()
        }

    let maybeDeliverLoop
        sessionPort
        rootWorkspace
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (sessionIdTextOpt: string option)
        : Task<unit> =
        task {
            match journal, sessionIdTextOpt with
            | Some durable, Some sidText when not (System.String.IsNullOrWhiteSpace sidText) ->
                do!
                    deliverSuccessorPrompt
                        sessionPort
                        rootWorkspace
                        workspaceDirectory
                        durable
                        (SessionId.create sidText)
            | _ -> return ()
        }

    let continueAfterRetiredAttempt
        sessionPort
        rootWorkspace
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (stopRetiredAttempt: SessionId -> Task<unit>)
        (sessionId: SessionId)
        : Task<unit> =
        task {
            let sendPrompt =
                journal
                |> Option.map (fun durable ->
                    fun () -> deliverSuccessorPrompt sessionPort rootWorkspace workspaceDirectory durable sessionId)

            do! stopRetiredAttempt sessionId

            match sendPrompt with
            | Some send -> do! send ()
            | None -> return ()
        }

    let observeIdle
        (quiescence: ISessionQuiescenceGate)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let! retired = isRetiredObservation context.Turn.Directory context.Turn.SessionId context.Turn.ProviderRun

            match retired, journal, context.Failure, context.Turn.Outcome, context.Quiescence with
            | true, _, _, _, _ -> return ()
            | false, Some durable, _, _, _ when
                UserQuestionJournal.suppressesTurn
                    durable
                    context.Turn.SessionId
                    context.Turn.AuthorityRootUserMessageId
                    context.Turn.ProviderRun
                ->
                return ()
            | false, Some durable, None, ReconcileProgram.TurnCompleted, Some permit ->
                return! scheduleNudge quiescence permit sessionPort rootWorkspace durable context.Turn
            | _ -> return ()
        }

    let observe
        (quiescence: ISessionQuiescenceGate)
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (observeOrdinary: ReconciledTurnContext -> Task)
        (context: ReconciledTurnContext)
        : Task =
        task {
            let! retired = isRetiredObservation context.Turn.Directory context.Turn.SessionId context.Turn.ProviderRun

            match retired, context.Failure, context.Turn.Outcome with
            | true, _, _ -> return ()
            | false, Some _, _ -> return! observeOrdinary context
            | false, None, ReconcileProgram.TurnInProgress -> return ()
            // provider-attempt-recovery-008: an unfinished manager turn is content damage, not a
            // reason to stop. It earns the same bounded Interaction Repair as every
            // other repairable role instead of being silently dropped.
            | false, None, ReconcileProgram.TurnNeedsContinuation _ -> return! observeOrdinary context
            | false, None, ReconcileProgram.TurnCompleted ->
                return! observeIdle quiescence sessionPort rootWorkspace journal context
            | false, None, _ -> return! observeOrdinary context
        }
