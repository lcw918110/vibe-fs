namespace Wanxiangshu.Execution.Routing

open Wanxiangshu.Foundation.HostSession

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
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
    /// Credits are minted by wanxiangli/borrowing (CUT-10); the host only wraps the library's number.
    let ofValue (value: int64) = CapacityCreditId value
    let first = ofValue 1L
    let value (CapacityCreditId value) = value

[<Struct; StructuralEquality; StructuralComparison>]
type internal CapacityLeaseId = private CapacityLeaseId of int64

module internal CapacityLeaseId =
    let initial = CapacityLeaseId 0L
    let next (CapacityLeaseId value) = CapacityLeaseId(value + 1L)
    let first = next initial
    let value (CapacityLeaseId value) = value

[<Struct; StructuralEquality; StructuralComparison>]
type internal CapacityFence = private CapacityFence of int64

module internal CapacityFence =
    let initial = CapacityFence 0L
    let next (CapacityFence value) = CapacityFence(value + 1L)
    let first = next initial
    let value (CapacityFence value) = value

type internal ExecutionAdmissionExactIdentity =
    { SessionId: string
      PhysicalUserMessageId: string
      Role: Role
      Participant: string
      Target: ModelRoutingTarget }

type internal ExecutionAdmissionLease
    private
    (
        owner: obj,
        capacityCredit: CapacityCreditId,
        leaseId: CapacityLeaseId,
        fence: CapacityFence,
        identity: ExecutionAdmissionExactIdentity
    ) =
    member internal _.Owner = owner
    member internal _.CapacityCredit = capacityCredit
    member internal _.LeaseId = leaseId
    member internal _.Fence = fence
    member internal _.Identity = identity

    static member internal Create(owner: obj, capacityCredit, leaseId, fence, identity) =
        ExecutionAdmissionLease(owner, capacityCredit, leaseId, fence, identity)

[<Struct; StructuralEquality; StructuralComparison>]
type internal QueueNodeId = private QueueNodeId of int64

module internal QueueNodeId =
    let initial = QueueNodeId 0L
    let next (QueueNodeId value) = QueueNodeId(value + 1L)
    let value (QueueNodeId value) = value

[<Struct; StructuralEquality; StructuralComparison>]
type internal QueueFence = private QueueFence of int64

module internal QueueFence =
    let initial = QueueFence 0L
    let next (QueueFence value) = QueueFence(value + 1L)
    let value (QueueFence value) = value

type internal ExecutionAdmissionQueueNode
    private
    (
        owner: obj,
        nodeId: QueueNodeId,
        fence: QueueFence,
        sessionId: string,
        physicalUserMessageId: string,
        completion: TaskCompletionSource<ExecutionAdmissionAcquisition>
    ) =
    member internal _.Owner = owner
    member internal _.NodeId = nodeId
    member internal _.Fence = fence
    member internal _.SessionId = sessionId
    member internal _.PhysicalUserMessageId = physicalUserMessageId
    member internal _.Completion = completion

    static member internal Create(owner: obj, nodeId, fence, sessionId, physicalUserMessageId, completion) =
        ExecutionAdmissionQueueNode(owner, nodeId, fence, sessionId, physicalUserMessageId, completion)

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

type internal CapacityTransitionCountersLibrary =
    abstract recordDuplicate: unit -> float
    abstract recordStale: unit -> float
    abstract recordConflict: unit -> float
    abstract snapshot: unit -> obj
    abstract reset: unit -> unit

module internal CapacityTransitionCountersBinding =
    [<Import("CapacityTransitionCounters", "wanxiangli/permit")>]
    let private countersClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : CapacityTransitionCountersLibrary = jsNative

    let create () = construct countersClass

type internal CapacityTransitionCounters() =
    let library = CapacityTransitionCountersBinding.create ()

    member _.Record(outcome: CapacityTransitionOutcome) =
        match outcome with
        | CapacityTransitionOutcome.Applied -> ()
        | CapacityTransitionOutcome.AlreadyApplied -> library.recordDuplicate () |> ignore
        | CapacityTransitionOutcome.StaleFence -> library.recordStale () |> ignore
        | CapacityTransitionOutcome.Conflict -> library.recordConflict () |> ignore

        outcome

    member _.Snapshot() =
        let snap = library.snapshot ()

        { Duplicate = int64 (unbox<float> snap?duplicate)
          Stale = int64 (unbox<float> snap?stale)
          Conflict = int64 (unbox<float> snap?conflict) }

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
