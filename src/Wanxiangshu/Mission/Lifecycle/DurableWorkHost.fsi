namespace Wanxiangshu.Mission.Lifecycle

module DurableWorkHost =
    val journalPath: stateRoot: string -> string

    type HostReceipt =
        { RequestId: string
          Status: string
          Kind: string
          Reason: string
          Revision: float
          Result: obj }

    val observe: requestId: string -> durableResult: obj -> HostReceipt
    val admitted: receipt: HostReceipt -> bool
    val resultText: receipt: HostReceipt -> name: string -> string
    val mayStart: durableResult: obj -> bool
    val command: kind: string -> fields: (string * obj) list -> obj
    val planSequence: unit -> obj
    val executionSequence: unit -> obj
    val useWork: gitCommonDir: string -> workId: string -> body: (obj -> 'a) -> 'a
    val useEphemeralWork: workId: string -> records: string array -> body: (obj -> 'a) -> 'a
    val execute: gitCommonDir: string -> workId: string -> requestId: string -> command: obj -> HostReceipt

    val submitReview:
        gitCommonDir: string ->
        workId: string ->
        requestId: string ->
        office: string ->
        providerRunId: string ->
        toolCallId: string ->
        updates: obj ->
        blockers: obj ->
        digest: string ->
        effects: obj ->
            obj

    /// Builds the DurableWork effect ports from park, send, open, draft and stop.
    val reviewEffects: ports: obj -> obj

    val inspect: gitCommonDir: string -> workId: string -> obj

    type HostRunFence =
        { ProviderRunId: string
          ToolCallId: string }

    type HostRetirement =
        { RetirementId: string
          IncumbencyId: string
          Outcome: string
          Expected: string
          Fence: HostRunFence
          ProjectionCut: bool }

    type HostAdvance =
        { IncumbencyId: string
          Expected: string
          Next: string }

    type HostObligation = { Key: string; Value: string }

    type InspectedWork =
        { Opened: bool
          Sequence: string list
          Stage: string
          Wait: string
          ActiveId: string option
          OpeningStage: string option
          Reviewed: bool
          ReviewOutcome: string option
          Obligations: obj array
          OpeningCursor: float option
          PredecessorRange: (float * float) option
          LastRetirementCursor: float option
          DeliveryAuthorized: bool
          Delivered: bool
          PendingDelivery: bool
          PendingOperationId: string option
          PendingExecSessionId: string option
          PendingDigest: string option
          PendingPath: string option
          RoundCount: int
          Launches: int
          RetiredRuns: string list
          DevOpsId: string option
          DevOpsTarget: string option
          Authority: string
          BlockerDigest: string option
          Root: string option
          Retirements: HostRetirement list
          ProjectionCut: HostRunFence option
          Advances: HostAdvance list
          BarrierToolCallId: string option
          SuccessorTenureId: string option }

    val inspected: gitCommonDir: string -> workId: string -> InspectedWork option

    /// A completed work closes its authority run. The milestone 「已完成」 and Delivered are the same fact.
    val closesAuthority: work: InspectedWork -> bool

    /// No continuation is owed: the work is complete, or a Plan in 「待执行」 has launched a Manager.
    val owesNoContinuation: work: InspectedWork -> bool

    /// The retirement id that closes a completed work, when that work has no active tenure.
    val completedClosureId: work: InspectedWork -> string option

    val inspectedMany:
        gitCommonDir: string -> workIds: string array -> (string * Result<InspectedWork option, string>) array

    val ensureOpened:
        gitCommonDir: string ->
        workId: string ->
        requestId: string ->
        root: string ->
        sequence: obj ->
        ownerId: string ->
        openingCursor: float ->
            Result<InspectedWork, string>

    val recover: gitCommonDir: string -> workId: string -> obj

    type OwnershipLease =
        { RegistryId: string
          ResourceId: string
          Token: string }

    type OwnershipBlocker =
        { Lease: OwnershipLease
          ScopeId: string
          Kind: string
          Phase: string
          ExternalId: string option
          IncarnationId: string option
          Outcome: string option
          Reason: string option
          CancellationRequested: bool }

    val ownershipMessage: kind: string -> fields: (string * obj) list -> obj
    val leaseObject: lease: OwnershipLease -> obj
    val bindingObject: externalId: string -> incarnationId: string -> obj
    val createOwnership: registryId: string -> records: string array -> obj
    val executeOwnershipInMemory: ownership: obj -> requestId: string -> message: obj -> HostReceipt
    val ownershipBlockersInMemory: ownership: obj -> scopeId: string -> OwnershipBlocker list
    val ownershipScopePhaseInMemory: ownership: obj -> scopeId: string -> string option

    val executeOwnershipDurable:
        gitCommonDir: string -> registryId: string -> requestId: string -> message: obj -> HostReceipt

    val mayStartResource: receipt: HostReceipt -> OwnershipLease option
    val ownershipBlockersDurable: gitCommonDir: string -> registryId: string -> scopeId: string -> OwnershipBlocker list
