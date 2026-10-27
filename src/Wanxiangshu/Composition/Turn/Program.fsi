namespace Wanxiangshu.Composition.Turn

open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

module ReconcileProgram =
    type TurnOutcome =
        | TurnInProgress
        | TurnNeedsContinuation of reason: string
        | TurnCompleted
        | TurnAborted of reason: string
        | TurnFailed of error: string

    type SnapshotObservation = | TurnUnknown

    type PublishTurn =
        { SessionId: SessionId
          PhysicalUserMessageId: PhysicalUserMessageId
          ProviderRun: ProviderRunIdentity
          Outcome: TurnOutcome }

    type ObservedTurn =
        { Outcome: TurnOutcome
          PublishTurn: PublishTurn option }

    [<RequireQualifiedAccess>]
    type FailureWakeSource =
        | CoarseHostSignal
        | ExactAssistantProjection

    [<RequireQualifiedAccess>]
    type ReconcileWake =
        | IdleWake of QuiescencePermit
        | RetryWake
        | FailureWake of
            physicalUserMessageId: PhysicalUserMessageId option *
            failure: ExecutionFailure *
            diagnostic: string *
            source: FailureWakeSource
        | IdleFailureWake of QuiescencePermit * PhysicalUserMessageId * ExecutionFailure * string * FailureWakeSource
        | AbortWake

    val mergeWake:
        currentPhysicalUserMessageId: PhysicalUserMessageId option ->
        previous: ReconcileWake ->
        incoming: ReconcileWake ->
            ReconcileWake

    [<RequireQualifiedAccess>]
    type ReconcileEvidence =
        | SnapshotError of reason: string
        | NoTurn
        | Provisional of ObservedTurn
        | Unknown of ObservedTurn option
        | Terminal of ObservedTurn
        | SessionCleared

    [<RequireQualifiedAccess>]
    type ReconcileDecision =
        | Publish
        | StopPass

    val outcomeOf: name: string -> TurnOutcome
    val isTerminalOutcome: outcome: TurnOutcome -> bool
    val tryFailureWitness: wake: ReconcileWake -> turn: PublishTurn -> (ExecutionFailure * string) option

    val failureWitnessMintsTerminal: failure: ExecutionFailure -> contentUsable: bool -> bool
    val decideStep: wake: ReconcileWake -> evidence: ReconcileEvidence -> ReconcileDecision
    val decisionName: decision: ReconcileDecision -> string
    val consumeKey: turn: PublishTurn -> string

    type PublishMaps =
        new: consumed: Map<string, string> * provisional: Map<string, string> -> PublishMaps
        member Consumed: Map<string, string>
        member Provisional: Map<string, string>
        member provisionalHas: turn: PublishTurn -> bool
        member consumedHas: turn: PublishTurn -> bool

    val publishMapsEmpty: unit -> PublishMaps

    val publishDecision:
        maps: PublishMaps ->
        turn: PublishTurn ->
            {| shouldPublish: bool
               maps: PublishMaps |}

    val clearProvisional: maps: PublishMaps -> sessionKey: string -> PublishMaps
    val turnFixture: session: string -> physical: string -> providerRun: string -> outcome: TurnOutcome -> PublishTurn
    val evidenceSnapshotError: reason: string -> ReconcileEvidence
    val evidenceNoTurn: unit -> ReconcileEvidence
    val observedTurn: turn: PublishTurn -> ObservedTurn
    val evidenceProvisional: outcome: TurnOutcome -> ReconcileEvidence
    val evidenceUnknown: unit -> ReconcileEvidence
    val evidenceTerminal: outcome: TurnOutcome -> ReconcileEvidence
    val evidenceSessionCleared: unit -> ReconcileEvidence
