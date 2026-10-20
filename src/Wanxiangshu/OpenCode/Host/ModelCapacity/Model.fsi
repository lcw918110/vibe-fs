namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation

type ModelRoutingTarget = { Model: string; Reasoning: string }

/// Scheduler execution purpose. It travels with physical admission and reaches
/// the MJS scheduler as the fourth argument; it is derived from the Host
/// request type / authorization binding, never from tool arguments, user text,
/// model self-description or a role name.
[<RequireQualifiedAccess>]
type ModelExecutionPurpose = | Normal

[<Struct; StructuralEquality; StructuralComparison>]
type internal CapacityCreditId = private CapacityCreditId of int64

module internal CapacityCreditId =
    val initial: CapacityCreditId
    val next: CapacityCreditId -> CapacityCreditId
    val first: CapacityCreditId
    val value: CapacityCreditId -> int64

[<Struct; StructuralEquality; StructuralComparison>]
type internal CapacityLeaseId = private CapacityLeaseId of int64

module internal CapacityLeaseId =
    val initial: CapacityLeaseId
    val next: CapacityLeaseId -> CapacityLeaseId
    val first: CapacityLeaseId
    val value: CapacityLeaseId -> int64

[<Struct; StructuralEquality; StructuralComparison>]
type internal CapacityFence = private CapacityFence of int64

module internal CapacityFence =
    val initial: CapacityFence
    val next: CapacityFence -> CapacityFence
    val first: CapacityFence
    val value: CapacityFence -> int64

type internal ExecutionAdmissionExactIdentity =
    { SessionId: string
      PhysicalUserMessageId: string
      Role: Role
      Participant: string
      Target: ModelRoutingTarget }

type internal ExecutionAdmissionLease =
    private new:
        owner: obj *
        capacityCredit: CapacityCreditId *
        leaseId: CapacityLeaseId *
        fence: CapacityFence *
        identity: ExecutionAdmissionExactIdentity ->
            ExecutionAdmissionLease

    member internal Owner: obj
    member internal CapacityCredit: CapacityCreditId
    member internal LeaseId: CapacityLeaseId
    member internal Fence: CapacityFence
    member internal Identity: ExecutionAdmissionExactIdentity

    static member internal Create:
        owner: obj *
        capacityCredit: CapacityCreditId *
        leaseId: CapacityLeaseId *
        fence: CapacityFence *
        identity: ExecutionAdmissionExactIdentity ->
            ExecutionAdmissionLease

[<Struct; StructuralEquality; StructuralComparison>]
type internal QueueNodeId = private QueueNodeId of int64

module internal QueueNodeId =
    val initial: QueueNodeId
    val next: QueueNodeId -> QueueNodeId

[<Struct; StructuralEquality; StructuralComparison>]
type internal QueueFence = private QueueFence of int64

module internal QueueFence =
    val initial: QueueFence
    val next: QueueFence -> QueueFence

type internal ExecutionAdmissionQueueNode =
    private new:
        owner: obj *
        nodeId: QueueNodeId *
        fence: QueueFence *
        sessionId: string *
        physicalUserMessageId: string *
        completion: TaskCompletionSource<ExecutionAdmissionAcquisition> ->
            ExecutionAdmissionQueueNode

    member internal Owner: obj
    member internal NodeId: QueueNodeId
    member internal Fence: QueueFence
    member internal SessionId: string
    member internal PhysicalUserMessageId: string
    member internal Completion: TaskCompletionSource<ExecutionAdmissionAcquisition>

    static member internal Create:
        owner: obj *
        nodeId: QueueNodeId *
        fence: QueueFence *
        sessionId: string *
        physicalUserMessageId: string *
        completion: TaskCompletionSource<ExecutionAdmissionAcquisition> ->
            ExecutionAdmissionQueueNode

and [<RequireQualifiedAccess>] internal ExecutionAdmissionAcquisition =
    | Admitted of ExecutionAdmissionLease
    | Queued of ExecutionAdmissionQueueNode
    | QueueFull
    | Cancelled
    | Superseded

[<RequireQualifiedAccess>]
type internal ExecutionAdmissionRejection =
    | UnknownLease
    | WrongFence
    | StaleLease
    | WrongSession
    | WrongPhysicalUserMessage
    | WrongRole
    | WrongParticipant
    | WrongTarget
    | IllegalTransition
    | OppositeTerminalConflict

[<RequireQualifiedAccess>]
type internal CapacityTransitionOutcome =
    | Applied
    | AlreadyApplied
    | StaleFence
    | Conflict

type internal CapacityTransitionCountersSnapshot =
    { Duplicate: int64
      Stale: int64
      Conflict: int64 }

type internal CapacityTransitionCounters =
    new: unit -> CapacityTransitionCounters
    member Record: outcome: CapacityTransitionOutcome -> CapacityTransitionOutcome
    member Snapshot: unit -> CapacityTransitionCountersSnapshot

type internal CapacityExactOwnerSnapshot =
    { SessionId: string
      PhysicalUserMessageId: string
      Role: Role option
      Participant: string option }

type internal CapacityLedgerEntrySnapshot<'target> = { Credit: int64; Target: 'target }

type internal CapacityTokenSnapshot<'target> =
    { Credit: int64
      State: string
      Owner: CapacityExactOwnerSnapshot
      Target: 'target }

type internal CapacityCustodySnapshot =
    { Credit: int64
      Owner: CapacityExactOwnerSnapshot }

type internal CapacityWaiterSnapshot =
    { Owner: CapacityExactOwnerSnapshot
      Sequence: int64
      Kind: string }

type internal CapacityLineageSnapshot =
    { ParentSessionId: string
      ChildSessionId: string }

type internal CapacityInvariantEvidence =
    { LedgerEntries: CapacityLedgerEntrySnapshot<ModelRoutingTarget> array
      Tokens: CapacityTokenSnapshot<ModelRoutingTarget> array
      Custodies: CapacityCustodySnapshot array
      Executions: CapacityExactOwnerSnapshot array
      Waiters: CapacityWaiterSnapshot array
      Owners: CapacityExactOwnerSnapshot array
      Lineage: CapacityLineageSnapshot array
      IdleCount: int
      InFlightCount: int
      RetiringCount: int
      ActiveCount: int
      Counters: CapacityTransitionCountersSnapshot }

type internal BorrowingCapacitySnapshot<'target> =
    { LedgerEntries: CapacityLedgerEntrySnapshot<'target> array
      Tokens: CapacityTokenSnapshot<'target> array
      Custodies: CapacityCustodySnapshot array
      Waiters: CapacityWaiterSnapshot array
      Lineage: CapacityLineageSnapshot array
      IdleCount: int
      InFlightCount: int
      RetiringCount: int }

[<RequireQualifiedAccess>]
type internal CapacityReconciliationFailure =
    | ActiveOutsideLedgerBounds
    | TokenStateCountMismatch
    | MapLedgerDivergence
    | UntraceableTokenOwner
    | UntraceableWaiterOwner
    | UntraceableExecutionCustody
    | CounterRegression

[<RequireQualifiedAccess>]
type internal CapacityReconciliationDecision =
    | NoOp
    | FailClosed of CapacityReconciliationFailure array

module internal CapacityReconciliation =
    val decide: evidence: CapacityInvariantEvidence -> CapacityReconciliationDecision

[<RequireQualifiedAccess>]
type internal ExecutionCapacityRelease =
    | BeforeProvider
    | PhysicalCompletion

[<RequireQualifiedAccess>]
type internal ExecutionCapacityLifecycle =
    | Pending of ExecutionAdmissionLease
    | Committed of ExecutionAdmissionLease
    | Releasing of ExecutionAdmissionLease * ExecutionCapacityRelease
    | Released of ExecutionAdmissionLease * ExecutionCapacityRelease

[<RequireQualifiedAccess>]
type internal ExecutionCapacityEvidence =
    | Acquire of ExecutionAdmissionLease
    | Commit of ExecutionAdmissionLease * ExecutionAdmissionExactIdentity
    | BeginReleaseBeforeProvider of ExecutionAdmissionLease * ExecutionAdmissionExactIdentity
    | BeginPhysicalCompletion of ExecutionAdmissionLease
    | CompleteRelease of ExecutionAdmissionLease * ExecutionCapacityRelease

[<RequireQualifiedAccess>]
type internal ExecutionCapacityDecision =
    | Transitioned of ExecutionCapacityLifecycle
    | Idempotent
    | Rejected of ExecutionAdmissionRejection

module internal ExecutionCapacityLifecycle =
    val leaseOf: ExecutionCapacityLifecycle -> ExecutionAdmissionLease

    val decide:
        current: ExecutionCapacityLifecycle option -> evidence: ExecutionCapacityEvidence -> ExecutionCapacityDecision
