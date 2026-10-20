namespace Wanxiangshu.Interaction.Repair

open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

module CompletedTurnClassifier =
    [<RequireQualifiedAccess>]
    type RepairDefectDecision =
        | RequestRepair
        | AwaitRepairTerminal
        | NoRepair

    val partsText: parts: MessagePart array -> string
    val partsSessionText: parts: MessagePart array -> string
    val hasToolCallPart: parts: MessagePart array -> bool
    val isAbortErrorName: name: string option -> bool

    val formalContentUnusable: parts: MessagePart array -> bool

    val classifyOutcome:
        completed: bool -> finish: string option -> errorName: string option -> parts: MessagePart array -> obj

    /// 空停没有角色豁免；显式 ask 等待由 durable 事实门单独判定。
    val needsInteractionRepair: role: Role option -> classified: obj -> parts: MessagePart array -> bool

    val retryContinuationSuppressesRepair:
        isRetryContinuation: bool ->
        observation: ReconcileProgram.SnapshotObservation option ->
        outcome: ReconcileProgram.TurnOutcome ->
        hasDurableTerminal: bool ->
            bool

    val decideRepairDefect:
        currentAttemptIsRepair: bool ->
        observation: ReconcileProgram.SnapshotObservation option ->
        outcome: ReconcileProgram.TurnOutcome ->
            RepairDefectDecision

    val roleOfAgent: agent: string option -> fallback: Role option -> Role option

    val buildTurn:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        authorityRoot: AuthorityRootUserMessageId ->
        assistant: SessionMessage ->
        roleFallback: Role option ->
        directory: string option ->
            ReconciledTurn
