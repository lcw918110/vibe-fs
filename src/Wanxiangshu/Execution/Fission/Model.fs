namespace Wanxiangshu.Execution.Fission

open System
open Fable.Core
open Fable.Core.JsInterop

/// CUT-28: the Fission topology and settlement laws are wanxiangli/fission-group's.
/// This file keeps the typed vocabulary and the container values; the rules
/// below only translate to and from the library.
module internal FissionGroupLaw =
    [<Import("FissionGroupLaw", "wanxiangli/fission-group")>]
    let private lawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (_cls: obj) : obj = jsNative

    let law: obj = construct lawClass

    let ints (value: obj) : int list = unbox<int array> value |> Array.toList

open Wanxiangshu.Foundation.Identity

[<CLIMutable>]
type FissionLaneBinding =
    { GroupId: string
      OwnerSessionId: SessionId
      LaneIndex: int
      LaneCount: int }

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

    let parse (prompts: string list) : Result<ParsedFissionPrompts, FissionRejectReason> =
        let verdict: obj = FissionGroupLaw.law?parsePrompts (List.toArray prompts)

        match unbox<string option> verdict?rejection with
        | Some "TooFewLanes" -> Error FissionRejectReason.TooFewLanes
        | Some "EmptyLanePrompt" -> Error(FissionRejectReason.EmptyLanePrompt(unbox<int> verdict?index))
        | Some other -> invalidOp ("wanxiangli/fission-group returned an unknown lane request rejection: " + other)
        | None ->
            Ok
                { Count = unbox<int> verdict?lanes
                  Lanes = prompts |> List.mapi (fun index prompt -> { Index = index; Prompt = prompt }) }

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

    let targets (laneCount: int) affinity =
        let lane: obj =
            match affinity with
            | FissionCompletionAffinity.PreFissionBroadcast -> null
            | FissionCompletionAffinity.Lane index -> box index

        FissionGroupLaw.ints (
            FissionGroupLaw.law?routeCompletion (createObj [ "laneCount" ==> laneCount; "lane" ==> lane ])
        )

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
    let mergeOrder (laneCount: int) =
        FissionGroupLaw.ints (FissionGroupLaw.law?mergeOrder laneCount)

    /// V1 closes the canonical ring at N-1. Every valid group therefore has one
    /// deterministic physical present that may receive the final takeover.
    let finalLane (laneCount: int) : int option =
        let lane: obj = FissionGroupLaw.law?finalLane laneCount
        if isNull lane then None else Some(unbox<int> lane)

    /// Ring transport is derived from lane index/count. Closed successors are
    /// skipped mechanically until the next live present; when every lane is
    /// closed the durable group finalizer owns the bundle.
    let successor (laneCount: int) (laneIndex: int) (closedLanes: int list) : int option =
        let lane: obj =
            FissionGroupLaw.law?successor (
                createObj
                    [ "laneCount" ==> laneCount
                      "laneIndex" ==> laneIndex
                      "closed" ==> List.toArray closedLanes ]
            )

        if isNull lane then None else Some(unbox<int> lane)

[<RequireQualifiedAccess>]
type FissionSettlementObservation =
    | OngoingExecution
    | NeedsContinuation
    | ProviderFailed
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

    let private decide (stage: string) observation =
        let name, reason =
            match observation with
            | FissionSettlementObservation.OngoingExecution -> "OngoingExecution", ""
            | FissionSettlementObservation.NeedsContinuation -> "NeedsContinuation", ""
            | FissionSettlementObservation.ProviderFailed -> "ProviderFailed", ""
            | FissionSettlementObservation.ExternalAbort reason -> "ExternalAbort", reason
            | FissionSettlementObservation.Completed -> "Completed", ""

        let verdict: obj =
            FissionGroupLaw.law?settle (createObj [ "stage" ==> stage; "observation" ==> name ])

        unbox<string> verdict?decision, reason

    let decideLane observation =
        match decide "lane" observation with
        | "yield", _ -> FissionLaneSettlementDecision.YieldToTurnWorkflow
        | "fail", reason -> FissionLaneSettlementDecision.FailGroup reason
        | "materialize", _ -> FissionLaneSettlementDecision.MaterializeLane
        | other, _ -> invalidOp ("wanxiangli/fission-group returned an unknown lane settlement: " + other)

    let decideTakeover observation =
        match decide "takeover" observation with
        | "yield", _ -> FissionTakeoverSettlementDecision.YieldToTurnWorkflow
        | "fail", reason -> FissionTakeoverSettlementDecision.FailGroup reason
        | "complete", _ -> FissionTakeoverSettlementDecision.CompleteOwner
        | other, _ -> invalidOp ("wanxiangli/fission-group returned an unknown takeover settlement: " + other)

module FissionConvergence =

    /// The same convergence precondition the durable projection uses
    /// (wanxiangli/fission-group `convergenceReady`), read off the live bundle
    /// and delivery. Only in-range lanes count as delivered, and a group needs
    /// at least two lanes.
    let ready (laneCount: int) (preFissionCompletionIds: string list) bundle (delivery: FissionDelivery) =
        let delivered completionId =
            delivery.Delivered
            |> Map.tryFind completionId
            |> Option.defaultValue Set.empty
            |> Set.filter (fun lane -> lane >= 0 && lane < delivery.LaneCount)
            |> Set.toArray

        laneCount >= 2
        && laneCount = delivery.LaneCount
        && unbox<bool> (
            FissionGroupLaw.law?convergenceReady (
                createObj
                    [ "laneCount" ==> laneCount
                      "progress"
                      ==> createObj
                              [ "workedLanes" ==> (FissionWorkBundle.keys bundle |> List.toArray)
                                "deliveries" ==> (preFissionCompletionIds |> List.map delivered |> List.toArray) ] ]
            )
        )

[<RequireQualifiedAccess>]
module FissionRequestProjection =

    /// intra-participant-parallelism-013: physical origin may only narrow the
    /// office entitlement. `true` means the provider request must carry
    /// `fission=false`.
    let apply (hasPhysicalParent: bool) : bool = not hasPhysicalParent
