namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Change
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Process

/// Delegation-owned join wire surface. Inputs and outputs are plain JavaScript
/// data; completion and error unions remain inside the renderer owner.
[<RequireQualifiedAccess>]
module JoinSurface =
    let private text (value: obj) =
        if isNull value then "" else string value

    let private language (value: string) = ProviderLanguage.parse value

    let private role (value: obj) : Role option =
        match text value with
        | "Manager" -> Some Role.Manager
        | "Orchestrator" -> Some Role.Orchestrator
        | "Engineer" -> Some Role.Engineer
        | "Coder" -> Some Role.Coder
        | "Inspector" -> Some Role.Inspector
        | "DevOps" -> Some Role.DevOps
        | "Browser" -> Some Role.Browser
        | "Inquiry" -> Some Role.Inquiry
        | "Distiller" -> Some Role.Distiller
        | _ -> None

    let private requiredRunId (value: obj) : string option =
        let runId = text value
        if String.IsNullOrWhiteSpace runId then None else Some runId

    let private agentItem (value: obj) : JoinItem option =
        let agentId = text (value?agentId)
        let agentName = text (value?agentName)
        let kind = text (value?kind)
        let rawRole = value?role
        let canonicalRole = role rawRole
        let hasRole = not (isNull rawRole)

        match kind, canonicalRole, requiredRunId (value?runId) with
        | "failed", Some role, Some runId ->
            Some(
                AgentItem(
                    AgentFailedItem
                        { AgentId = agentId
                          ChildSessionId = None
                          RunId = runId
                          Role = Some role
                          Code = text (value?code)
                          Message = text (value?message) }
                )
            )
        | "completed", Some role, Some runId ->
            Some(
                AgentItem(
                    AgentCompletedItem
                        { AgentId = agentId
                          ChildSessionId = None
                          RunId = runId
                          Role = role
                          AuthorityRoot = None
                          ProviderRun = None
                          View = text (value?view)
                          Directory = None }
                )
            )
        | "abandoned", _, _ when hasRole && Option.isNone canonicalRole -> None
        | "abandoned", _, _ -> Some(AgentItem(AgentAbandonedItem(agentId, text (value?reason))))
        | _ -> None

    let private ptyItem (value: obj) : JoinItem option =
        let ptyId = text (value?ptyId)
        let outcome = text (value?outcome)
        let code = text (value?code)
        let message = text (value?message)

        match text (value?kind) with
        | "pty-failed" ->
            Some(
                PtyItem(
                    PtyFailed
                        { PtyId = ptyId
                          Outcome = outcome
                          Closed = true
                          Code = code
                          Message = message }
                )
            )
        | "pty-aborted" ->
            Some(
                PtyItem(
                    PtyAborted
                        { PtyId = ptyId
                          Outcome = outcome
                          Closed = true
                          Code = code
                          Message = message }
                )
            )
        | "pty-exited" ->
            Some(
                PtyItem(
                    PtyExited
                        { PtyId = ptyId
                          Outcome = outcome
                          Closed = true }
                )
            )
        | _ -> None

    let private itemOf (value: obj) : JoinItem option =
        if (text (value?kind)).StartsWith("pty-", StringComparison.Ordinal) then
            ptyItem value
        else
            agentItem value

    let private itemName (value: obj) =
        text (value?agentId), text (value?agentName)

    let renderBatch (languageName: string) (items: obj array) : string =
        if isNull items || items.Length = 0 then
            ""
        else
            let converted = items |> Array.map itemOf

            if converted |> Array.exists Option.isNone then
                ""
            else
                let converted = converted |> Array.choose id
                let names = items |> Array.map itemName |> Map.ofArray

                let terminals =
                    items
                    |> Array.choose (fun value ->
                        if (text (value?kind)).StartsWith("pty-", StringComparison.Ordinal) then
                            Some(text (value?ptyId), text (value?terminalLabel))
                        else
                            None)
                    |> Map.ofArray

                let resolveAgentName agentId =
                    match Map.tryFind agentId names with
                    | Some name when not (String.IsNullOrWhiteSpace name) -> name
                    | _ -> ""

                let resolveTerminalLabel ptyId =
                    match Map.tryFind ptyId terminals with
                    | Some label when not (String.IsNullOrWhiteSpace label) -> label
                    | _ -> ptyId

                JoinResultRenderer.renderJoinItemBatch
                    (language languageName)
                    resolveAgentName
                    (NonEmptyBatch.ofHeadTail converted[0] (converted |> Array.skip 1 |> Array.toList))
                    resolveTerminalLabel

    let renderInterrupted (languageName: string) (reason: string) : string =
        let interrupt =
            match reason with
            | "UserMessageArrived" -> JoinInterruptReason.UserMessageArrived
            | "DeadlineExpired" -> JoinInterruptReason.DeadlineExpired
            | _ -> JoinInterruptReason.OperatorAbort

        JoinResultRenderer.renderInterrupted (language languageName) interrupt

    let renderForkError (languageName: string) (error: string) : string =
        let forkError =
            match error with
            | "Cancelled" -> ForkError.Cancelled
            | "JoinInProgress" -> ForkError.JoinInProgress
            | "TimedOut" -> ForkError.TimedOut
            | "NotFound" -> ForkError.NotFound "unknown"
            | "Abandoned" -> ForkError.Abandoned("unknown", "abandoned")
            | "TerminalMaterializationFailed" -> ForkError.TerminalMaterializationFailed "unknown"
            | "Empty" -> ForkError.Empty
            | _ -> ForkError.NothingToJoin

        JoinResultRenderer.renderForkError (language languageName) forkError (fun _ -> "")

    let renderOrchestratorBatch (languageName: string) (verdictNames: string array) : string =
        let verdicts =
            verdictNames
            |> Array.map (fun name ->
                match name with
                | "Published" -> OrchestratorVerdict.Published(ManagerJobId.create "job", CommitHash.create "head")
                | "PublishedPendingCleanup" ->
                    OrchestratorVerdict.PublishedPendingCleanup(
                        ManagerJobId.create "job",
                        CommitHash.create "head",
                        "cleanup-failed"
                    )
                | "Cancelled" -> OrchestratorVerdict.Cancelled(ManagerJobId.create "job")
                | "RejectedDirty" -> OrchestratorVerdict.RejectedDirty "dirty"
                | "IntegrationFailed" -> OrchestratorVerdict.IntegrationFailed(ManagerJobId.create "job", "failed")
                | _ -> OrchestratorVerdict.Empty)
            |> Array.toList

        match verdicts with
        | [] -> ""
        | head :: tail ->
            JoinResultRenderer.renderOrchestratorBatch (language languageName) (NonEmptyBatch.ofHeadTail head tail)
    // R17 join bound probe: dist-backed resource evidence over the real
    // permit-gated join loop. Journal-less PTY arm (agent join stays
    // fail-closed); physical ports only — stub session port, in-memory PtyPort
    // handler, production CompletionMailbox. No timers, no modelled Join:
    // every outcome below is the compiled HostForkJoin/Join decision.
    type private JoinProbeSessions() =
        interface ISessionHostPort with
            member _.SubscribeTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SubscribeFutureTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SendPrompt(_, _, _) =
                Task.FromResult(SendOutcome.AcceptanceUnknown "join probe never sends")

            member _.AbortSession _ = Task.FromResult(Ok())
            member _.InterruptAttempt _ = Task.FromResult(Ok())
            member _.IsManagedChild _ = true
            member _.AbortChildren _ = Task.FromResult(()) :> Task

            member _.CreateSiblingSession(_, _, _) =
                Task.FromResult(Error "join probe has no sibling sessions")

            member _.TryGetParentSession _ = Task.FromResult(Ok None)

            member _.CreateChildSession(_, _) =
                Task.FromResult(Error "join probe has no child sessions")

            member _.ListChildren _ = Task.FromResult(Ok [])
            member _.FamilyRootOf sessionId = sessionId

    type private JoinProbeHandle(parentId: SessionId, runtime: HostForkRuntime) =
        member _.ParentId = parentId
        member _.Runtime = runtime

    type private JoinInterruptHandle(tcs: TaskCompletionSource<JoinInterruptReason>) =
        member _.Source = tcs

    let private joinInterruptOf (reason: string) : JoinInterruptReason =
        match reason with
        | "UserMessageArrived" -> JoinInterruptReason.UserMessageArrived
        | "DeadlineExpired" -> JoinInterruptReason.DeadlineExpired
        | _ -> JoinInterruptReason.OperatorAbort

    let private joinOutcomeObject (outcome: Result<JoinWaitOutcome<JoinItem>, ForkError>) : obj =
        match outcome with
        | Ok(ResultsAvailable batch) ->
            let items = NonEmptyBatch.toList batch

            let ids =
                items
                |> List.map (function
                    | AgentItem _ -> "agent"
                    | PtyItem pty -> PtyJoinItem.ptyId pty)
                |> List.toArray

            box
                {| kind = "ResultsAvailable"
                   count = items.Length
                   ptyIds = ids |}
        | Ok(Interrupted reason) ->
            let name =
                match reason with
                | JoinInterruptReason.UserMessageArrived -> "UserMessageArrived"
                | JoinInterruptReason.DeadlineExpired -> "DeadlineExpired"
                | JoinInterruptReason.OperatorAbort -> "OperatorAbort"

            box
                {| kind = "Interrupted"
                   reason = name |}
        | Error error ->
            let name =
                match error with
                | ForkError.Empty -> "Empty"
                | ForkError.NothingToJoin -> "NothingToJoin"
                | ForkError.Cancelled -> "Cancelled"
                | ForkError.JoinInProgress -> "JoinInProgress"
                | ForkError.Abandoned _ -> "Abandoned"
                | ForkError.NotFound _ -> "NotFound"
                | ForkError.TimedOut -> "TimedOut"
                | ForkError.TerminalMaterializationFailed _ -> "TerminalMaterializationFailed"

            box {| kind = "Error"; error = name |}

    let createVisibleInputRegistry () : obj =
        let registry = JoinAttemptRegistry() :> IJoinAttemptRegistry

        createObj
            [ "begin"
              ==> (fun (sessionId: string) ->
                  let lease = registry.Begin(SessionId.create sessionId, None)

                  createObj
                      [ "result"
                        ==> task {
                            let! reason = lease.Wait
                            return joinOutcomeObject (Ok(JoinWaitOutcome.Interrupted reason))
                        }
                        "deadline" ==> (fun () -> lease.SignalDeadline())
                        "abort" ==> (fun () -> lease.SignalOperatorAbort())
                        "dispose" ==> (fun () -> (lease :> IDisposable).Dispose()) ])
              "signal"
              ==> (fun (sessionId: string, physicalId: string) ->
                  registry.SignalVisibleUserMessage(
                      SessionId.create sessionId,
                      PhysicalUserMessageId.create physicalId
                  )) ]

    // Production JoinTool.renderJoined releases PTY ownership tracks once a
    // batch is delivered; the probe keeps the same discipline so PtyRuns
    // returns to quiescence and a later empty join stays NothingToJoin.
    let private releaseDeliveredTracks (runtime: HostForkRuntime) outcome =
        match outcome with
        | Ok(ResultsAvailable batch) ->
            NonEmptyBatch.toList batch
            |> List.iter (function
                | PtyItem pty -> runtime.UntrackPtyRun(PtyJoinItem.ptyId pty)
                | AgentItem _ -> ())
        | _ -> ()

    let createJoinProbe () : obj =
        let parentId =
            SessionId.create (sprintf "join-probe-%s" (Guid.NewGuid().ToString("N")))

        let sessions = JoinProbeSessions() :> ISessionHostPort

        let port =
            PtyPort(handler = (fun _ _ -> Task.FromResult(Ok(): Result<unit, string>)))

        let runtime =
            HostForkRuntime(
                parentId,
                sessions,
                (fun _ _ -> Task.FromResult None),
                CompletionMailboxRuntime.create,
                NodeTiming.nodeClockPort (),
                NodeTiming.raceExit,
                ptyPort = port
            )

        box (JoinProbeHandle(parentId, runtime))

    let joinProbeForkPty (probe: obj) (command: string) : Task<obj> =
        task {
            let runtime = (unbox<JoinProbeHandle> probe).Runtime
            let! result = runtime.ForkPty(command, ManagedAgent.make Role.DevOps)

            match result with
            | Ok id ->
                return
                    box
                        {| ok = true
                           ptyId = id.Value
                           error = "" |}
            | Error error ->
                return
                    box
                        {| ok = false
                           ptyId = ""
                           error = error |}
        }

    let joinProbeCompletePty (probe: obj) (ptyId: string) : unit =
        let runtime = (unbox<JoinProbeHandle> probe).Runtime
        runtime.PtyPort.Complete(PtyId.Create ptyId)

    let joinProbePulseWake (probe: obj) : unit =
        let runtime = (unbox<JoinProbeHandle> probe).Runtime
        runtime.Runtime.PulseWake()

    let joinProbeCancel (probe: obj) : unit =
        (unbox<JoinProbeHandle> probe).Runtime.Cancel()

    let joinProbeCounts (probe: obj) : obj =
        let runtime = (unbox<JoinProbeHandle> probe).Runtime

        box
            {| pendingCompletions = runtime.Runtime.PendingCompletionCount
               pendingPtys = runtime.Runtime.PendingPtyCount
               ptyRuns = runtime.SnapshotOutstandingPtyRuns() |> List.length
               pendingRuns = runtime.PendingRunCount |}

    let createJoinInterrupt () : obj =
        box (
            JoinInterruptHandle(
                TaskCompletionSource<JoinInterruptReason>(TaskCreationOptions.RunContinuationsAsynchronously)
            )
        )

    let fireJoinInterrupt (handle: obj) (reason: string) : unit =
        AsyncSupport.trySetResult (unbox<JoinInterruptHandle> handle).Source (joinInterruptOf reason)
        |> ignore

    let joinAvailable (probe: obj) (maxCount: int) (interrupt: obj) : Task<obj> =
        task {
            let runtime = (unbox<JoinProbeHandle> probe).Runtime
            let wait = (unbox<JoinInterruptHandle> interrupt).Source.Task
            let! outcome = HostForkJoin.joinAvailable runtime maxCount wait
            let rendered = joinOutcomeObject outcome
            releaseDeliveredTracks runtime outcome
            return rendered
        }

    let joinAvailableWithPermit (probe: obj) (permitSequence: int) (maxCount: int) (interrupt: obj) : Task<obj> =
        task {
            let handle = unbox<JoinProbeHandle> probe

            let permit =
                Wanxiangshu.Execution.Session.Recovery.SessionRecovery.FamilyRecoveryPermit.currentProcess
                    handle.ParentId
                    (int64 permitSequence)

            let! outcome =
                Wanxiangshu.Execution.Delegation.Join.joinAvailable
                    handle.Runtime
                    permit
                    maxCount
                    (unbox<JoinInterruptHandle> interrupt).Source.Task

            let rendered = joinOutcomeObject outcome
            releaseDeliveredTracks handle.Runtime outcome
            return rendered
        }

    let joinMaxBatch () : int = JoinBatch.MaxJoinBatch
