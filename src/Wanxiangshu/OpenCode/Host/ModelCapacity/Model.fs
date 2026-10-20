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
    let initial = CapacityCreditId 0L
    let next (CapacityCreditId value) = CapacityCreditId(value + 1L)
    let first = next initial
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

[<Struct; StructuralEquality; StructuralComparison>]
type internal QueueFence = private QueueFence of int64

module internal QueueFence =
    let initial = QueueFence 0L
    let next (QueueFence value) = QueueFence(value + 1L)

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

type internal CapacityTransitionCounters() =
    let gate = obj ()
    // DSL-MUTABLE: resource
    let mutable duplicate = 0L
    let mutable stale = 0L
    // DSL-MUTABLE: resource
    let mutable conflict = 0L

    member _.Record(outcome: CapacityTransitionOutcome) =
        lock gate (fun () ->
            match outcome with
            | CapacityTransitionOutcome.Applied -> ()
            | CapacityTransitionOutcome.AlreadyApplied -> duplicate <- duplicate + 1L
            | CapacityTransitionOutcome.StaleFence -> stale <- stale + 1L
            | CapacityTransitionOutcome.Conflict -> conflict <- conflict + 1L)

        outcome

    member _.Snapshot() =
        lock gate (fun () ->
            { Duplicate = duplicate
              Stale = stale
              Conflict = conflict })

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
    let private ownerKey owner =
        owner.SessionId, owner.PhysicalUserMessageId, owner.Role, owner.Participant

    let decide (evidence: CapacityInvariantEvidence) =
        let owners = evidence.Owners |> Array.map ownerKey |> Set.ofArray

        let custodies =
            evidence.Custodies
            |> Array.map (fun custody -> ownerKey custody.Owner)
            |> Set.ofArray

        let ledgerCredits = evidence.LedgerEntries |> Array.map _.Credit |> Set.ofArray
        let tokenCredits = evidence.Tokens |> Array.map _.Credit |> Set.ofArray

        let idle =
            evidence.Tokens
            |> Array.sumBy (fun token -> if token.State = "Idle" then 1 else 0)

        let inFlight =
            evidence.Tokens
            |> Array.sumBy (fun token -> if token.State = "InFlight" then 1 else 0)

        let retiring =
            evidence.Tokens
            |> Array.sumBy (fun token -> if token.State = "Retiring" then 1 else 0)

        [| if
               evidence.ActiveCount < 0
               || evidence.ActiveCount > evidence.LedgerEntries.Length
               || evidence.ActiveCount <> inFlight + retiring
           then
               CapacityReconciliationFailure.ActiveOutsideLedgerBounds
           if
               evidence.IdleCount <> idle
               || evidence.InFlightCount <> inFlight
               || evidence.RetiringCount <> retiring
               || idle + inFlight + retiring <> evidence.Tokens.Length
           then
               CapacityReconciliationFailure.TokenStateCountMismatch
           if
               ledgerCredits <> tokenCredits
               || evidence.LedgerEntries.Length <> evidence.Tokens.Length
           then
               CapacityReconciliationFailure.MapLedgerDivergence
           if
               evidence.Tokens
               |> Array.exists (fun token -> not (Set.contains (ownerKey token.Owner) owners))
           then
               CapacityReconciliationFailure.UntraceableTokenOwner
           if
               evidence.Waiters
               |> Array.exists (fun waiter -> not (Set.contains (ownerKey waiter.Owner) owners))
           then
               CapacityReconciliationFailure.UntraceableWaiterOwner
           if
               evidence.Executions
               |> Array.exists (fun execution -> not (Set.contains (ownerKey execution) custodies))
           then
               CapacityReconciliationFailure.UntraceableExecutionCustody
           if
               evidence.Counters.Duplicate < 0L
               || evidence.Counters.Stale < 0L
               || evidence.Counters.Conflict < 0L
           then
               CapacityReconciliationFailure.CounterRegression |]
        |> function
            | [||] -> CapacityReconciliationDecision.NoOp
            | failures -> CapacityReconciliationDecision.FailClosed failures

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

    let leaseOf =
        function
        | ExecutionCapacityLifecycle.Pending lease
        | ExecutionCapacityLifecycle.Committed lease
        | ExecutionCapacityLifecycle.Releasing(lease, _)
        | ExecutionCapacityLifecycle.Released(lease, _) -> lease

    let private validateObserved (lease: ExecutionAdmissionLease) (observed: ExecutionAdmissionExactIdentity) =
        if observed.SessionId <> lease.Identity.SessionId then
            Error ExecutionAdmissionRejection.WrongSession
        elif observed.PhysicalUserMessageId <> lease.Identity.PhysicalUserMessageId then
            Error ExecutionAdmissionRejection.WrongPhysicalUserMessage
        elif observed.Role <> lease.Identity.Role then
            Error ExecutionAdmissionRejection.WrongRole
        elif observed.Participant <> lease.Identity.Participant then
            Error ExecutionAdmissionRejection.WrongParticipant
        elif observed.Target <> lease.Identity.Target then
            Error ExecutionAdmissionRejection.WrongTarget
        else
            Ok()

    let private evidenceLease =
        function
        | ExecutionCapacityEvidence.Acquire lease
        | ExecutionCapacityEvidence.BeginPhysicalCompletion lease
        | ExecutionCapacityEvidence.Commit(lease, _)
        | ExecutionCapacityEvidence.BeginReleaseBeforeProvider(lease, _)
        | ExecutionCapacityEvidence.CompleteRelease(lease, _) -> lease

    let private observedEvidence =
        function
        | ExecutionCapacityEvidence.Commit(lease, observed)
        | ExecutionCapacityEvidence.BeginReleaseBeforeProvider(lease, observed) -> validateObserved lease observed
        | ExecutionCapacityEvidence.Acquire _
        | ExecutionCapacityEvidence.BeginPhysicalCompletion _
        | ExecutionCapacityEvidence.CompleteRelease _ -> Ok()

    let private sameRelease expected actual =
        if expected = actual then
            ExecutionCapacityDecision.Idempotent
        else
            ExecutionCapacityDecision.Rejected ExecutionAdmissionRejection.OppositeTerminalConflict

    let private transition lifecycle evidence =
        match lifecycle, evidence with
        | ExecutionCapacityLifecycle.Pending _, ExecutionCapacityEvidence.Acquire _ ->
            ExecutionCapacityDecision.Idempotent
        | ExecutionCapacityLifecycle.Pending lease, ExecutionCapacityEvidence.Commit _ ->
            ExecutionCapacityDecision.Transitioned(ExecutionCapacityLifecycle.Committed lease)
        | ExecutionCapacityLifecycle.Pending lease, ExecutionCapacityEvidence.BeginReleaseBeforeProvider _ ->
            ExecutionCapacityDecision.Transitioned(
                ExecutionCapacityLifecycle.Releasing(lease, ExecutionCapacityRelease.BeforeProvider)
            )
        | ExecutionCapacityLifecycle.Pending lease, ExecutionCapacityEvidence.BeginPhysicalCompletion _ ->
            ExecutionCapacityDecision.Transitioned(
                ExecutionCapacityLifecycle.Releasing(lease, ExecutionCapacityRelease.PhysicalCompletion)
            )
        | ExecutionCapacityLifecycle.Committed _, ExecutionCapacityEvidence.Commit _ ->
            ExecutionCapacityDecision.Idempotent
        | ExecutionCapacityLifecycle.Committed lease, ExecutionCapacityEvidence.BeginPhysicalCompletion _ ->
            ExecutionCapacityDecision.Transitioned(
                ExecutionCapacityLifecycle.Releasing(lease, ExecutionCapacityRelease.PhysicalCompletion)
            )
        | ExecutionCapacityLifecycle.Releasing(lease, release), ExecutionCapacityEvidence.CompleteRelease(_, requested) when
            release = requested
            ->
            ExecutionCapacityDecision.Transitioned(ExecutionCapacityLifecycle.Released(lease, release))
        | ExecutionCapacityLifecycle.Releasing(_, release), ExecutionCapacityEvidence.BeginReleaseBeforeProvider _ ->
            sameRelease release ExecutionCapacityRelease.BeforeProvider
        | ExecutionCapacityLifecycle.Releasing(_, release), ExecutionCapacityEvidence.BeginPhysicalCompletion _ ->
            sameRelease release ExecutionCapacityRelease.PhysicalCompletion
        | ExecutionCapacityLifecycle.Releasing(_, release), ExecutionCapacityEvidence.CompleteRelease(_, requested) ->
            sameRelease release requested
        | ExecutionCapacityLifecycle.Released(_, release), ExecutionCapacityEvidence.BeginReleaseBeforeProvider _ ->
            sameRelease release ExecutionCapacityRelease.BeforeProvider
        | ExecutionCapacityLifecycle.Released(_, release), ExecutionCapacityEvidence.BeginPhysicalCompletion _ ->
            sameRelease release ExecutionCapacityRelease.PhysicalCompletion
        | ExecutionCapacityLifecycle.Released(_, release), ExecutionCapacityEvidence.CompleteRelease(_, requested) ->
            sameRelease release requested
        | ExecutionCapacityLifecycle.Committed _, ExecutionCapacityEvidence.BeginReleaseBeforeProvider _
        | ExecutionCapacityLifecycle.Releasing _, ExecutionCapacityEvidence.Commit _
        | ExecutionCapacityLifecycle.Released _, ExecutionCapacityEvidence.Commit _ ->
            ExecutionCapacityDecision.Rejected ExecutionAdmissionRejection.OppositeTerminalConflict
        | ExecutionCapacityLifecycle.Pending _, ExecutionCapacityEvidence.CompleteRelease _
        | ExecutionCapacityLifecycle.Committed _, ExecutionCapacityEvidence.Acquire _
        | ExecutionCapacityLifecycle.Committed _, ExecutionCapacityEvidence.CompleteRelease _
        | ExecutionCapacityLifecycle.Releasing _, ExecutionCapacityEvidence.Acquire _
        | ExecutionCapacityLifecycle.Released _, ExecutionCapacityEvidence.Acquire _ ->
            ExecutionCapacityDecision.Rejected ExecutionAdmissionRejection.IllegalTransition

    let decide current evidence =
        match current, evidence with
        | None, ExecutionCapacityEvidence.Acquire lease ->
            ExecutionCapacityDecision.Transitioned(ExecutionCapacityLifecycle.Pending lease)
        | None, ExecutionCapacityEvidence.Commit _
        | None, ExecutionCapacityEvidence.BeginReleaseBeforeProvider _
        | None, ExecutionCapacityEvidence.BeginPhysicalCompletion _
        | None, ExecutionCapacityEvidence.CompleteRelease _ ->
            ExecutionCapacityDecision.Rejected ExecutionAdmissionRejection.UnknownLease
        | Some lifecycle, _ when not (obj.ReferenceEquals(leaseOf lifecycle, evidenceLease evidence)) ->
            ExecutionCapacityDecision.Rejected ExecutionAdmissionRejection.StaleLease
        | Some lifecycle, _ ->
            observedEvidence evidence
            |> Result.bind (fun () -> Ok(transition lifecycle evidence))
            |> Result.defaultWith ExecutionCapacityDecision.Rejected
