namespace Wanxiangshu.Execution.Routing

open System.Threading.Tasks
open Wanxiangshu.Foundation

type ModelRoutingTarget = { Model: string; Reasoning: string }

[<RequireQualifiedAccess>]
type ModelExecutionPurpose = | Normal

type internal ExecutionAdmissionExactIdentity =
    { SessionId: string
      PhysicalUserMessageId: string
      Role: Role
      Participant: string
      Target: ModelRoutingTarget }

/// 库签发的原许可；接口不包装或复制其对象身份。
[<AllowNullLiteral>]
type internal ExecutionAdmissionLease =
    abstract identity: obj
    abstract credit: obj
    abstract leaseId: obj
    abstract fence: obj

/// 等待者与 completion 由同一个库对象持有。
[<AllowNullLiteral>]
type internal ExecutionAdmissionQueueNode =
    abstract nodeId: float
    abstract fence: float
    abstract sequence: float
    abstract sessionId: string
    abstract physicalUserMessageId: string
    abstract completion: Task<obj>

[<RequireQualifiedAccess>]
type internal ExecutionAdmissionAcquisition =
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
