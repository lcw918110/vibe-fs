namespace Wanxiangshu.Execution.Fission

open System

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

    let private validateLanePrompts prompts =
        match prompts |> List.tryFindIndex String.IsNullOrWhiteSpace with
        | Some index -> Error(FissionRejectReason.EmptyLanePrompt index)
        | None -> Ok()

    let parse (prompts: string list) : Result<ParsedFissionPrompts, FissionRejectReason> =
        if List.length prompts < 2 then
            Error FissionRejectReason.TooFewLanes
        else
            validateLanePrompts prompts
            |> Result.map (fun () ->
                { Count = List.length prompts
                  Lanes = prompts |> List.mapi (fun index prompt -> { Index = index; Prompt = prompt }) })

[<RequireQualifiedAccess>]
type FissionCompletionAffinity =
    | PreFissionBroadcast
    | Lane of int

module FissionCompletionAffinity =
    let lane index = FissionCompletionAffinity.Lane index

module FissionExternalId =
    let agent handleId = "agent:" + handleId
    let pty ptyId = "pty:" + ptyId

module FissionCompletionRouting =

    let targets laneCount affinity =
        match affinity with
        | FissionCompletionAffinity.PreFissionBroadcast -> [ 0 .. laneCount - 1 ]
        | FissionCompletionAffinity.Lane index when index >= 0 && index < laneCount -> [ index ]
        | FissionCompletionAffinity.Lane _ -> []

[<RequireQualifiedAccess>]
type FissionDeliveryError = InvalidLane of int

[<CLIMutable>]
type FissionDelivery =
    { LaneCount: int
      Delivered: Map<string, Set<int>> }

module FissionDelivery =

    let empty laneCount =
        { LaneCount = max 0 laneCount
          Delivered = Map.empty }

    let mark completionId laneIndex delivery =
        if laneIndex < 0 || laneIndex >= delivery.LaneCount then
            Error(FissionDeliveryError.InvalidLane laneIndex)
        else
            let current =
                delivery.Delivered |> Map.tryFind completionId |> Option.defaultValue Set.empty

            Ok
                { delivery with
                    Delivered = Map.add completionId (Set.add laneIndex current) delivery.Delivered }

    let pendingTargets completionId delivery =
        let delivered =
            delivery.Delivered |> Map.tryFind completionId |> Option.defaultValue Set.empty

        [ 0 .. delivery.LaneCount - 1 ]
        |> List.filter (fun lane -> not (Set.contains lane delivered))

[<RequireQualifiedAccess>]
type FissionBundleError = ConflictingLaneRecord of laneIndex: int * existingRef: string * proposedRef: string

[<Struct>]
type FissionWorkBundle = private FissionWorkBundle of Map<int, string>

module FissionWorkBundle =

    let empty = FissionWorkBundle Map.empty

    let private value (FissionWorkBundle records) = records

    let add laneIndex viewRef bundle =
        let records = value bundle

        match Map.tryFind laneIndex records with
        | None -> Ok(FissionWorkBundle(Map.add laneIndex viewRef records))
        | Some existing when existing = viewRef -> Ok bundle
        | Some existing -> Error(FissionBundleError.ConflictingLaneRecord(laneIndex, existing, viewRef))

    let merge left right =
        let folder state (laneIndex, viewRef) =
            state |> Result.bind (add laneIndex viewRef)

        value right |> Map.toList |> List.fold folder (Ok left)

    let keys bundle =
        value bundle |> Map.toList |> List.map fst

    let entries bundle = value bundle |> Map.toList

    let count bundle = value bundle |> Map.count

module FissionRing =

    /// Canonical ring fold. The order is a property of the topology, not of
    /// callback arrival. Keeping this tiny makes arrival-order convergence
    /// literally unrepresentable in the domain model.
    let mergeOrder laneCount =
        if laneCount < 2 then [] else [ 0 .. laneCount - 1 ]

    /// V1 closes the canonical ring at N-1. Every valid group therefore has one
    /// deterministic physical present that may receive the final takeover.
    let finalLane laneCount = mergeOrder laneCount |> List.tryLast

    /// Ring transport is derived from lane index/count. Closed successors are
    /// skipped mechanically until the next live present; when every lane is
    /// closed the durable group finalizer owns the bundle.
    let successor laneCount laneIndex closedLanes =
        if laneCount < 2 || laneIndex < 0 || laneIndex >= laneCount then
            None
        else
            let closed = Set.ofList closedLanes

            [ 1..laneCount ]
            |> List.map (fun offset -> (laneIndex + offset) % laneCount)
            |> List.tryFind (fun candidate -> not (Set.contains candidate closed))

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

/// Pure ownership law at the Fission/Turn boundary. Fission does not implement
/// nudge, assistance, fallback, or degeneration recovery. Those control-plane
/// successors must settle first; Fission only consumes the stable completion.
module FissionSettlement =

    let decideLane observation =
        match observation with
        | FissionSettlementObservation.OngoingExecution
        | FissionSettlementObservation.NeedsContinuation
        | FissionSettlementObservation.ProviderFailed
        | FissionSettlementObservation.DegenerationInterrupted -> FissionLaneSettlementDecision.YieldToTurnWorkflow
        | FissionSettlementObservation.ExternalAbort reason -> FissionLaneSettlementDecision.FailGroup reason
        | FissionSettlementObservation.Completed -> FissionLaneSettlementDecision.MaterializeLane

    let decideTakeover observation =
        match observation with
        | FissionSettlementObservation.OngoingExecution
        | FissionSettlementObservation.NeedsContinuation
        | FissionSettlementObservation.ProviderFailed
        | FissionSettlementObservation.DegenerationInterrupted -> FissionTakeoverSettlementDecision.YieldToTurnWorkflow
        | FissionSettlementObservation.ExternalAbort reason -> FissionTakeoverSettlementDecision.FailGroup reason
        | FissionSettlementObservation.Completed -> FissionTakeoverSettlementDecision.CompleteOwner

module FissionConvergence =

    let ready laneCount preFissionCompletionIds bundle delivery =
        let completeLaneSet = FissionWorkBundle.keys bundle = [ 0 .. laneCount - 1 ]

        let allBroadcastsDelivered =
            preFissionCompletionIds
            |> List.forall (fun completionId -> FissionDelivery.pendingTargets completionId delivery |> List.isEmpty)

        laneCount >= 2 && completeLaneSet && allBroadcastsDelivered

[<RequireQualifiedAccess>]
module FissionRequestProjection =

    /// intra-participant-parallelism-013: physical origin may only narrow the
    /// office entitlement. `true` means the provider request must carry
    /// `fission=false`.
    let apply (hasPhysicalParent: bool) : bool = not hasPhysicalParent
