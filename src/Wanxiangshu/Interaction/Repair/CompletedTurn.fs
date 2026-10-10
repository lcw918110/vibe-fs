namespace Wanxiangshu.Interaction.Repair

open System
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Composition.Turn
open Wanxiangshu.OpenCode

/// Pure classification of a fully-loaded assistant message.
/// Empty formal text or formal text that contains XML markup (including broken
/// tags) is interaction repair (continuation), never durable fallback.
module CompletedTurnClassifier =

    [<RequireQualifiedAccess>]
    type RepairDefectDecision =
        | RequestRepair
        | AwaitRepairTerminal
        | NoRepair

    /// capability-enforcement-021: how one idle Blogger turn reaches the
    /// protocol repair owner.
    [<RequireQualifiedAccess>]
    type BloggerIdleRoute =
        /// Ordinary turn observation only.
        | Observe
        /// Prose-only terminal: protocol repair alone owns the turn.
        | Repair
        /// Aborted turn: repair drives its live request, then the ordinary
        /// abort observation still runs.
        | RepairThenObserve

    let private supportsInteractionRepair =
        function
        | Some Role.Manager
        | Some Role.Orchestrator
        | Some Role.Engineer
        | Some Role.Coder
        | Some Role.Inspector
        | Some Role.DevOps
        | Some Role.Browser
        | Some Role.Inquiry
        | Some Role.Plan -> true
        | Some Role.Distiller
        | Some Role.Blogger
        | None -> false

    /// Formal visible assistant text only (no reasoning/thinking).
    /// Used for empty/XML-only repair classification and finish=stop emptiness.
    let partsText (parts: MessagePart array) : string =
        if isNull parts then
            ""
        else
            parts
            |> Array.choose (function
                | MessagePart.Text text -> Some text
                | _ -> None)
            |> String.concat ""

    /// Session terminal material: formal text + host-visible reasoning/thinking.
    /// COMPANION-003: TerminalOutputRaw 与 LWR 禁止 raw tool call/result——
    /// 末回合工具可能极大且不经 transform。This is the XTrace terminal segment's
    /// text, not a parallel A channel.
    let partsSessionText (parts: MessagePart array) : string =
        if isNull parts then
            ""
        else
            parts
            |> Array.choose (function
                | MessagePart.Text text
                | MessagePart.Reasoning text -> Some text
                | _ -> None)
            |> String.concat "\n\n"

    let hasToolCallPart (parts: MessagePart array) : bool =
        if isNull parts then
            false
        else
            parts
            |> Array.exists (function
                | MessagePart.ToolCall _ -> true
                | MessagePart.Activity kind -> kind = "patch" || kind = "step-start" || kind = "step-finish"
                | _ -> false)

    let isAbortErrorName (name: string option) =
        match name with
        | Some value ->
            let lower = value.ToLowerInvariant()
            let upstreamAbort = lower.Contains("upstream_error")
            let plainAbort = lower.Contains("abort")

            upstreamAbort |> not && plainAbort
        | None -> false

    /// CTX-004: stop is completed only when formal text passes the shared
    /// terminal validity gate; otherwise the turn earns interaction repair.
    let private classifyStopped (parts: MessagePart array) : obj =
        match TerminalValidity.check (partsText parts) with
        | Ok() -> box ReconcileProgram.TurnCompleted
        | Error rejection ->
            box (
                ReconcileProgram.TurnNeedsContinuation(
                    sprintf "assistant stop with %s" (TerminalValidity.describe rejection)
                )
            )

    /// Content validity alone cannot prove a provider failure.
    let formalContentUnusable (parts: MessagePart array) : bool =
        TerminalValidity.check (partsText parts) |> Result.isError

    /// Returns either a publishable `TurnOutcome` or a private `SnapshotObservation`.
    /// Heterogeneous `obj` so finish=None stays instanceof SnapshotObservation in JS
    /// (HOST-004 Clean Break — must not mint TurnOutcome.TurnUnknown).
    let classifyOutcome
        (completed: bool)
        (finish: string option)
        (errorName: string option)
        (parts: MessagePart array)
        : obj =
        match isAbortErrorName errorName, completed && Option.isSome errorName, finish with
        | true, _, _ -> box (ReconcileProgram.TurnAborted(defaultArg errorName "aborted"))
        | false, true, _ -> box (ReconcileProgram.TurnFailed(defaultArg errorName "assistant completed with error"))
        | false, false, Some value when value.Equals("aborted", StringComparison.OrdinalIgnoreCase) ->
            box (ReconcileProgram.TurnAborted("finish=aborted"))
        | false, false, Some value when value.Equals("error", StringComparison.OrdinalIgnoreCase) ->
            box (ReconcileProgram.TurnFailed(defaultArg errorName "assistant finish=error"))
        | false, false, Some value when value.Equals("stop", StringComparison.OrdinalIgnoreCase) ->
            classifyStopped parts
        | false, false, Some value when value.Equals("tool-calls", StringComparison.OrdinalIgnoreCase) ->
            box ReconcileProgram.TurnInProgress
        | false, false, Some value when value.Equals("length", StringComparison.OrdinalIgnoreCase) ->
            box (ReconcileProgram.TurnNeedsContinuation "assistant finish=length")
        | false, false, Some value -> box (ReconcileProgram.TurnFailed(sprintf "assistant finish=%s" value))
        // No finish yet: private SnapshotObservation. Never invent Completed
        // from parts alone — abort/error may still be racing the idle wake-up.
        | false, false, None -> box ReconcileProgram.TurnUnknown

    /// ARCH-011: named for the typed occasion (unfinished interaction), not for any
    /// character feature of the repair payload. A normal `finish=tool-calls` turn is
    /// still owned by the Host provider/tool loop; its concrete tool/activity part is
    /// proof that continuation is already in flight and must never be pre-empted by a
    /// synthetic InteractionRepair. Only an in-progress turn with no such Host work,
    /// or an explicit NeedsContinuation, earns repair.
    /// Accepts `obj` because classifyOutcome may return SnapshotObservation.
    let needsInteractionRepair (role: Role option) (classified: obj) (parts: MessagePart array) =
        supportsInteractionRepair role
        && (match classified with
            | :? ReconcileProgram.TurnOutcome as outcome ->
                match outcome with
                | ReconcileProgram.TurnInProgress -> not (hasToolCallPart parts)
                | ReconcileProgram.TurnNeedsContinuation _ -> true
                | ReconcileProgram.TurnCompleted
                | ReconcileProgram.TurnAborted _
                | ReconcileProgram.TurnFailed _ -> false
            | _ -> false)

    /// interaction-authority-023: an attempt is unsettled while the observation
    /// carries no stable terminal — finish=None (Unknown) or tool-calls
    /// (InProgress). Every other observation is a settled terminal, including
    /// the unsatisfied ones (length, unusable stop, completed-with-error).
    let private observationUnsettled
        (observation: ReconcileProgram.SnapshotObservation option)
        (outcome: ReconcileProgram.TurnOutcome)
        =
        match observation, outcome with
        | Some ReconcileProgram.TurnUnknown, _ -> true
        | None, ReconcileProgram.TurnInProgress -> true
        | None, _ -> false

    /// interaction-authority-023: a `ProviderRetryAttempt` continuation
    /// suppresses idle repair only while its own attempt is unsettled. A stable
    /// observation terminal or a durable `ChatExecution` terminal settles it,
    /// and either settlement restores the gate-nudge qualification that
    /// interaction-authority-019 grants to a fresh terminal. Keying suppression on the
    /// durable physical identity instead parks every later silent stop forever.
    let retryContinuationSuppressesRepair
        (isRetryContinuation: bool)
        (observation: ReconcileProgram.SnapshotObservation option)
        (outcome: ReconcileProgram.TurnOutcome)
        (hasDurableTerminal: bool)
        : bool =
        isRetryContinuation
        && not hasDurableTerminal
        && observationUnsettled observation outcome

    let decideRepairDefect
        (currentAttemptIsRepair: bool)
        (observation: ReconcileProgram.SnapshotObservation option)
        (outcome: ReconcileProgram.TurnOutcome)
        : RepairDefectDecision =
        match currentAttemptIsRepair, observation, outcome with
        | false, _, _ -> RepairDefectDecision.RequestRepair
        | true, Some ReconcileProgram.TurnUnknown, _ -> RepairDefectDecision.AwaitRepairTerminal
        | true, None, ReconcileProgram.TurnInProgress -> RepairDefectDecision.AwaitRepairTerminal
        // A gate reminder that itself reaches another stable invalid terminal is
        // not "exhausted". The gate is still unsatisfied, so the fresh terminal
        // earns another reminder; exact-terminal admission dedupes replay.
        | true, None, ReconcileProgram.TurnNeedsContinuation _ -> RepairDefectDecision.RequestRepair
        | true, None, (ReconcileProgram.TurnCompleted | ReconcileProgram.TurnAborted _ | ReconcileProgram.TurnFailed _) ->
            RepairDefectDecision.NoRepair

    let private hasBloggerToolEvidence (parts: MessagePart array) =
        not (isNull parts)
        && parts
           |> Array.exists (function
               | MessagePart.ToolCall _
               | MessagePart.ToolResult _ -> true
               | _ -> false)

    /// capability-enforcement-021: a Blogger turn that no Host tool loop
    /// follows (prose-only completion, or a run stopped by the continuation
    /// itself or aborted from outside) leaves its live request with idle as the
    /// only wake that can still nudge, AABB or abandon it. Ignoring an aborted
    /// turn would keep that request's flight claimed forever. The repair owner
    /// itself proves the turn belongs to the live request. Provider failures
    /// belong to provider-attempt recovery, and a degeneration-guard abort
    /// already owns its successor.
    let bloggerIdleRoute
        (bloggerQuiescent: bool)
        (guardOwnsAbort: bool)
        (outcome: ReconcileProgram.TurnOutcome)
        (parts: MessagePart array)
        : BloggerIdleRoute =
        match bloggerQuiescent, outcome with
        | false, _
        | true, ReconcileProgram.TurnFailed _ -> BloggerIdleRoute.Observe
        | true, ReconcileProgram.TurnAborted _ when guardOwnsAbort -> BloggerIdleRoute.Observe
        | true, ReconcileProgram.TurnAborted _ -> BloggerIdleRoute.RepairThenObserve
        | true, _ when hasBloggerToolEvidence parts -> BloggerIdleRoute.Observe
        | true, _ -> BloggerIdleRoute.Repair

    let roleOfAgent (agent: string option) (fallback: Role option) =
        match agent with
        | Some value -> HostSessionContext.roleOf value |> Option.orElse fallback
        | None -> fallback

    let buildTurn
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (authorityRoot: AuthorityRootUserMessageId)
        (assistant: SessionMessage)
        (roleFallback: Role option)
        (directory: string option)
        : ReconciledTurn =
        let role = roleOfAgent assistant.Agent roleFallback

        let classified =
            classifyOutcome assistant.Completed assistant.Finish assistant.ErrorName assistant.Parts

        // When Observation is Some, Outcome is an unreachable placeholder for
        // callers that only match publishable TurnOutcome cases (evidence and
        // missing-final-report consult Observation first).
        let outcome, observation =
            match classified with
            | :? ReconcileProgram.SnapshotObservation as obs ->
                ReconcileProgram.TurnNeedsContinuation "private-snapshot-observation", Some obs
            | :? ReconcileProgram.TurnOutcome as o -> o, None
            | _ -> failwith "classifyOutcome returned neither TurnOutcome nor SnapshotObservation"

        { SessionId = sessionId
          PhysicalUserMessageId = physicalUserMessageId
          AuthorityRootUserMessageId = authorityRoot
          // HOST-010: the assistant message IS the provider run.
          ProviderRun = ProviderRunIdentity.create assistant.Id
          Role = role
          Directory = directory
          Parts = assistant.Parts
          Finish = assistant.Finish
          ErrorName = assistant.ErrorName
          Model = assistant.Model
          Outcome = outcome
          Observation = observation }
