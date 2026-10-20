namespace Wanxiangshu.OpenCode.Host

open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Attempt

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

type ReliabilityCounters =
    new: unit -> ReliabilityCounters
    member Record: observation: ReliabilityObservation -> unit
    member Snapshot: unit -> ReliabilityCounterSnapshot

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
    val querySources:
        counters: ReliabilityCounters ->
        executions: ExecutionReliabilitySource seq ->
        capacity: CapacityReliabilitySource ->
        recovery: RecoveryOwnershipDiagnosticSource ->
            ReliabilitySnapshot

    val validateOperation: operation: string -> bool
