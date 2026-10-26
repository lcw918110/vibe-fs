namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

[<RequireQualifiedAccess>]
type DiagnosticCapacityState =
    | NotAcquired
    | Queued
    | Active
    | Releasing
    | Released

[<RequireQualifiedAccess>]
type DiagnosticRecoveryDecision =
    | ObserveOnly
    | ResumeAdmission
    | ReconcileStartedProvider
    | MarkTerminal
    | FailClosed
    | ManualIntervention

[<Struct>]
type DiagnosticStateTransition =
    { From: DurableExecutionLifecycle option
      To: DurableExecutionLifecycle }

type CausalDiagnosticRecord =
    { Operation: string
      LogicalRunId: LogicalRunId option
      SessionId: SessionId option
      AuthorityRootUserMessageId: AuthorityRootUserMessageId option
      PhysicalUserMessageId: PhysicalUserMessageId option
      PromptKey: PromptKey option
      ProviderRunIdentity: ProviderRunIdentity option
      Participant: string option
      Role: Role option
      ProviderRequestKind: ProviderRequestKind option
      Transition: DiagnosticStateTransition
      FailureClass: ExecutionFailure option
      Resolution: ExecutionFailureResolution option
      CapacityState: DiagnosticCapacityState option
      CapacityFence: string option
      Hook: string option
      PolicyClass: HookCriticality option
      RecoveryDecision: DiagnosticRecoveryDecision option
      PersistenceCommitment: PersistenceCommitment option }

[<RequireQualifiedAccess>]
type ReliabilityObservation =
    | IdentityConflict
    | QueueFull
    | FatalSettlement
    | Recovery of DiagnosticRecoveryDecision
    | HookFailure
    | FallbackAdvanced
    | StreamAbort

[<Struct>]
type ReliabilityCounterSnapshot =
    { IdentityConflicts: int64
      QueueFull: int64
      FatalSettlements: int64
      RecoveryObserveOnly: int64
      RecoveryResumeAdmission: int64
      RecoveryReconcileStartedProvider: int64
      RecoveryMarkTerminal: int64
      RecoveryFailClosed: int64
      RecoveryManualIntervention: int64
      HookFailures: int64
      FallbackAdvances: int64
      StreamAborts: int64 }

module private ReliabilityInterop =
    [<Import("createReliabilityDiagnosticsStore", "wanxiangli/degeneration")>]
    let createStore: unit -> obj = jsNative

    let private recoveryKey (decision: DiagnosticRecoveryDecision) : string =
        match decision with
        | DiagnosticRecoveryDecision.ObserveOnly -> "recovery:observeOnly"
        | DiagnosticRecoveryDecision.ResumeAdmission -> "recovery:resumeAdmission"
        | DiagnosticRecoveryDecision.ReconcileStartedProvider -> "recovery:reconcileStartedProvider"
        | DiagnosticRecoveryDecision.MarkTerminal -> "recovery:markTerminal"
        | DiagnosticRecoveryDecision.FailClosed -> "recovery:failClosed"
        | DiagnosticRecoveryDecision.ManualIntervention -> "recovery:manualIntervention"

    let observationKey (observation: ReliabilityObservation) : string =
        match observation with
        | ReliabilityObservation.IdentityConflict -> "identityConflict"
        | ReliabilityObservation.QueueFull -> "queueFull"
        | ReliabilityObservation.FatalSettlement -> "fatalSettlement"
        | ReliabilityObservation.Recovery decision -> recoveryKey decision
        | ReliabilityObservation.HookFailure -> "hookFailure"
        | ReliabilityObservation.FallbackAdvanced -> "fallbackAdvanced"
        | ReliabilityObservation.StreamAbort -> "streamAbort"

type ReliabilityCounters() =
    let store: obj = ReliabilityInterop.createStore ()

    member _.Record(observation: ReliabilityObservation) : unit =
        store?Record(ReliabilityInterop.observationKey observation)

    member _.Snapshot() : ReliabilityCounterSnapshot =
        let raw: obj = store?Snapshot()
        { IdentityConflicts = unbox<int64> raw?identityConflicts
          QueueFull = unbox<int64> raw?queueFull
          FatalSettlements = unbox<int64> raw?fatalSettlements
          RecoveryObserveOnly = unbox<int64> raw?recoveryObserveOnly
          RecoveryResumeAdmission = unbox<int64> raw?recoveryResumeAdmission
          RecoveryReconcileStartedProvider = unbox<int64> raw?recoveryReconcileStartedProvider
          RecoveryMarkTerminal = unbox<int64> raw?recoveryMarkTerminal
          RecoveryFailClosed = unbox<int64> raw?recoveryFailClosed
          RecoveryManualIntervention = unbox<int64> raw?recoveryManualIntervention
          HookFailures = unbox<int64> raw?hookFailures
          FallbackAdvances = unbox<int64> raw?fallbackAdvances
          StreamAborts = unbox<int64> raw?streamAborts }

[<Struct>]
type LogicalRunAttemptSnapshot =
    { LogicalRunId: LogicalRunId
      PhysicalAttempts: int }

[<Struct>]
type ExecutionReliabilitySnapshot =
    { AcceptedWithoutTerminal: int
      ProviderStartedWithoutTerminal: int
      PhysicalAttemptsByLogicalRun: LogicalRunAttemptSnapshot array }

[<Struct>]
type ExecutionReliabilitySource =
    { LogicalRunId: LogicalRunId
      Lifecycle: DurableExecutionLifecycle }

[<Struct>]
type CapacityReliabilitySnapshot =
    { QueueDepth: int
      ActiveLeases: int
      DuplicateFences: int64
      StaleFences: int64
      ConflictingFences: int64 }

[<Struct>]
type CapacityReliabilitySource =
    { QueueDepth: int
      ActiveLeases: int
      DuplicateFences: int64
      StaleFences: int64
      ConflictingFences: int64 }

[<Struct>]
type RecoveryOwnershipDiagnosticSource =
    { Pending: int
      ManualInterventionCount: int }

[<Struct>]
type ReliabilitySnapshot =
    { Counters: ReliabilityCounterSnapshot
      Execution: ExecutionReliabilitySnapshot
      Capacity: CapacityReliabilitySnapshot
      Recovery: RecoveryOwnershipDiagnosticSource }

[<RequireQualifiedAccess>]
module ReliabilityDiagnostics =

    let private isAcceptedWithoutTerminal (source: ExecutionReliabilitySource) =
        if source.Lifecycle = DurableExecutionLifecycle.AcceptedBeforeProvider then 1 else 0

    let private isProviderStartedWithoutTerminal (source: ExecutionReliabilitySource) =
        if source.Lifecycle = DurableExecutionLifecycle.ProviderStarted then 1 else 0

    let private logicalRunAttempt (logicalRunId: LogicalRunId, attempts: int) =
        { LogicalRunId = logicalRunId
          PhysicalAttempts = attempts }

    let querySources
        (counters: ReliabilityCounters)
        (executions: ExecutionReliabilitySource seq)
        (capacity: CapacityReliabilitySource)
        (recovery: RecoveryOwnershipDiagnosticSource)
        : ReliabilitySnapshot =
        let projected = executions |> Seq.toArray

        { Counters = counters.Snapshot()
          Execution =
            { AcceptedWithoutTerminal =
                projected |> Array.sumBy isAcceptedWithoutTerminal
              ProviderStartedWithoutTerminal =
                projected |> Array.sumBy isProviderStartedWithoutTerminal
              PhysicalAttemptsByLogicalRun =
                projected
                |> Array.countBy _.LogicalRunId
                |> Array.sortBy (fst >> LogicalRunId.value)
                |> Array.map logicalRunAttempt }
          Capacity =
            { QueueDepth = capacity.QueueDepth
              ActiveLeases = capacity.ActiveLeases
              DuplicateFences = capacity.DuplicateFences
              StaleFences = capacity.StaleFences
              ConflictingFences = capacity.ConflictingFences }
          Recovery = recovery }

    let validateOperation (operation: string) = Diagnostic.validateOperation operation
