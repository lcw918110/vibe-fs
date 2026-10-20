namespace Wanxiangshu.Execution.Fission

[<RequireQualifiedAccess>]
type FissionRejectReason =
    | AlreadyFissioned
    | TooFewLanes
    | EmptyLanePrompt of int
    | CapacityExceeded
    | InvalidOrigin
    | RuntimeUnavailable of string

[<CLIMutable>]
type FissionLanePrompt = { Index: int; Prompt: string }

[<CLIMutable>]
type ParsedFissionPrompts =
    { Count: int
      Lanes: FissionLanePrompt list }

module FissionPrompt =
    val parse: prompts: string list -> Result<ParsedFissionPrompts, FissionRejectReason>

[<RequireQualifiedAccess>]
type FissionCompletionAffinity =
    | PreFissionBroadcast
    | Lane of int

module FissionCompletionAffinity =
    val lane: index: int -> FissionCompletionAffinity

module FissionExternalId =
    val agent: handleId: string -> string
    val pty: ptyId: string -> string

module FissionCompletionRouting =
    val targets: laneCount: int -> affinity: FissionCompletionAffinity -> int list

[<RequireQualifiedAccess>]
type FissionDeliveryError = InvalidLane of int

[<CLIMutable>]
type FissionDelivery =
    { LaneCount: int
      Delivered: Map<string, Set<int>> }

module FissionDelivery =
    val empty: laneCount: int -> FissionDelivery

    val mark:
        completionId: string ->
        laneIndex: int ->
        delivery: FissionDelivery ->
            Result<FissionDelivery, FissionDeliveryError>

    val pendingTargets: completionId: string -> delivery: FissionDelivery -> int list

[<RequireQualifiedAccess>]
type FissionBundleError = ConflictingLaneRecord of laneIndex: int * existingRef: string * proposedRef: string

[<Struct>]
type FissionWorkBundle = private FissionWorkBundle of Map<int, string>

module FissionWorkBundle =
    val empty: FissionWorkBundle

    val add:
        laneIndex: int -> viewRef: string -> bundle: FissionWorkBundle -> Result<FissionWorkBundle, FissionBundleError>

    val merge: left: FissionWorkBundle -> right: FissionWorkBundle -> Result<FissionWorkBundle, FissionBundleError>
    val keys: bundle: FissionWorkBundle -> int list
    val entries: bundle: FissionWorkBundle -> (int * string) list
    val count: bundle: FissionWorkBundle -> int

module FissionRing =
    val mergeOrder: laneCount: int -> int list
    val finalLane: laneCount: int -> int option
    val successor: laneCount: int -> laneIndex: int -> closedLanes: int list -> int option

[<RequireQualifiedAccess>]
type FissionSettlementObservation =
    | OngoingExecution
    | NeedsContinuation
    | ProviderFailed
    | DegenerationInterrupted
    | ExternalAbort of string
    | Completed

[<RequireQualifiedAccess>]
type FissionLaneSettlementDecision =
    | YieldToTurnWorkflow
    | MaterializeLane
    | FailGroup of string

[<RequireQualifiedAccess>]
type FissionTakeoverSettlementDecision =
    | YieldToTurnWorkflow
    | CompleteOwner
    | FailGroup of string

module FissionSettlement =
    val decideLane: observation: FissionSettlementObservation -> FissionLaneSettlementDecision
    val decideTakeover: observation: FissionSettlementObservation -> FissionTakeoverSettlementDecision

module FissionConvergence =
    val ready:
        laneCount: int ->
        preFissionCompletionIds: string list ->
        bundle: FissionWorkBundle ->
        delivery: FissionDelivery ->
            bool

[<RequireQualifiedAccess>]
module FissionRequestProjection =
    val apply: hasPhysicalParent: bool -> bool
