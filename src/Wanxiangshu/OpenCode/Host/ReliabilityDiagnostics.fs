namespace Wanxiangshu.OpenCode.Host

open System
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

type ReliabilityCounters() =
    let gate = obj ()
    // DSL-MUTABLE: resource
    let mutable identityConflicts = 0L
    let mutable queueFull = 0L
    // DSL-MUTABLE: resource
    let mutable fatalSettlements = 0L
    let mutable recoveryObserveOnly = 0L
    // DSL-MUTABLE: resource
    let mutable recoveryResumeAdmission = 0L
    let mutable recoveryReconcileStartedProvider = 0L
    // DSL-MUTABLE: resource
    let mutable recoveryMarkTerminal = 0L
    let mutable recoveryFailClosed = 0L
    // DSL-MUTABLE: resource
    let mutable recoveryManualIntervention = 0L
    let mutable hookFailures = 0L
    // DSL-MUTABLE: resource
    let mutable fallbackAdvances = 0L
    let mutable streamAborts = 0L

    member _.Record(observation: ReliabilityObservation) =
        lock gate (fun () ->
            match observation with
            | ReliabilityObservation.IdentityConflict -> identityConflicts <- identityConflicts + 1L
            | ReliabilityObservation.QueueFull -> queueFull <- queueFull + 1L
            | ReliabilityObservation.FatalSettlement -> fatalSettlements <- fatalSettlements + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ObserveOnly ->
                recoveryObserveOnly <- recoveryObserveOnly + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ResumeAdmission ->
                recoveryResumeAdmission <- recoveryResumeAdmission + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ReconcileStartedProvider ->
                recoveryReconcileStartedProvider <- recoveryReconcileStartedProvider + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.MarkTerminal ->
                recoveryMarkTerminal <- recoveryMarkTerminal + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.FailClosed ->
                recoveryFailClosed <- recoveryFailClosed + 1L
            | ReliabilityObservation.Recovery DiagnosticRecoveryDecision.ManualIntervention ->
                recoveryManualIntervention <- recoveryManualIntervention + 1L
            | ReliabilityObservation.HookFailure -> hookFailures <- hookFailures + 1L
            | ReliabilityObservation.FallbackAdvanced -> fallbackAdvances <- fallbackAdvances + 1L
            | ReliabilityObservation.StreamAbort -> streamAborts <- streamAborts + 1L)

    member _.Snapshot() =
        lock gate (fun () ->
            { IdentityConflicts = identityConflicts
              QueueFull = queueFull
              FatalSettlements = fatalSettlements
              RecoveryObserveOnly = recoveryObserveOnly
              RecoveryResumeAdmission = recoveryResumeAdmission
              RecoveryReconcileStartedProvider = recoveryReconcileStartedProvider
              RecoveryMarkTerminal = recoveryMarkTerminal
              RecoveryFailClosed = recoveryFailClosed
              RecoveryManualIntervention = recoveryManualIntervention
              HookFailures = hookFailures
              FallbackAdvances = fallbackAdvances
              StreamAborts = streamAborts })

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
                projected
                |> Array.sumBy (fun source ->
                    if source.Lifecycle = DurableExecutionLifecycle.AcceptedBeforeProvider then
                        1
                    else
                        0)
              ProviderStartedWithoutTerminal =
                projected
                |> Array.sumBy (fun source ->
                    if source.Lifecycle = DurableExecutionLifecycle.ProviderStarted then
                        1
                    else
                        0)
              PhysicalAttemptsByLogicalRun =
                projected
                |> Array.countBy _.LogicalRunId
                |> Array.sortBy (fst >> LogicalRunId.value)
                |> Array.map (fun (logicalRunId, attempts) ->
                    { LogicalRunId = logicalRunId
                      PhysicalAttempts = attempts }) }
          Capacity =
            { QueueDepth = capacity.QueueDepth
              ActiveLeases = capacity.ActiveLeases
              DuplicateFences = capacity.DuplicateFences
              StaleFences = capacity.StaleFences
              ConflictingFences = capacity.ConflictingFences }
          Recovery = recovery }

    let validateOperation (operation: string) =
        not (String.IsNullOrWhiteSpace operation)
        && not (operation.Contains '\n')
        && not (operation.Contains '\r')
