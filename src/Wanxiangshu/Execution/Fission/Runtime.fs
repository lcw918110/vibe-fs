namespace Wanxiangshu.Execution.Fission

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

[<CLIMutable>]
type FissionLaneBinding =
    { GroupId: string
      OwnerSessionId: SessionId
      LaneIndex: int
      LaneCount: int }

/// Process-local resource indexes for Fission. Durable truth lives in
/// Journal.FissionProjection; these indexes are disposable accelerators / abort
/// causes and may be rebuilt after restart.
module FissionRuntime =

    [<Import("fissionLaneRegistry", "wanxiangli/fission-group")>]
    let private laneRegistry: obj = jsNative
    [<Emit("$0 === undefined")>]
    let private isUndefined (_v: obj) : bool = jsNative


    let private gate = obj ()
    // DSL-MUTABLE: resource — silent interrupt owner set
    let private silentInterrupts = HashSet<string>()

    // DSL-MUTABLE: resource — group disposable resource registry
    let private groupResources = Dictionary<string, ResizeArray<IDisposable>>()
    // DSL-MUTABLE: single-flight — delivery claim latch per group+completion+lane
    let private deliveryClaims = HashSet<string>()
    // DSL-MUTABLE: single-flight — takeover claim latch per group
    let private takeoverClaims = HashSet<string>()

    let private deliveryKey groupId completionId (laneIndex: int) =
        groupId + "\u001f" + completionId + "\u001f" + string laneIndex

    let private disposeSafely (resource: IDisposable) =
        try
            resource.Dispose()
        with _ ->
            ()

    let private disposeGroupResources
        (groupResources: Dictionary<string, ResizeArray<IDisposable>>)
        (groupId: string)
        : unit =
        match groupResources.TryGetValue groupId with
        | true, resources ->
            resources |> Seq.iter disposeSafely
            groupResources.Remove groupId |> ignore
        | false, _ -> ()

    let bindLane (groupId: string) (ownerSessionId: SessionId) (laneIndex: int) (laneCount: int) (laneSessionId: SessionId) : unit =
        laneRegistry?bindLane (
            groupId,
            SessionId.value ownerSessionId,
            laneIndex,
            laneCount,
            SessionId.value laneSessionId
        )

    let unbindLane laneSessionId =
        laneRegistry?unbindLane (SessionId.value laneSessionId) |> ignore

    let installDurableLaneEvidence (resolve: SessionId -> FissionLaneBinding option) : unit =
        laneRegistry?hydrateFromDurable (fun (laneSessionIdStr: string) ->
            resolve (SessionId.create laneSessionIdStr)
            |> Option.map (fun binding ->
                {| GroupId = binding.GroupId
                   OwnerSessionId = SessionId.value binding.OwnerSessionId
                   LaneIndex = binding.LaneIndex
                   LaneCount = binding.LaneCount |}
                |> box)
            |> Option.toObj)

    let tryLane laneSessionId : FissionLaneBinding option =
        let recordOpt: obj = laneRegistry?tryLane (SessionId.value laneSessionId)

        if isNull recordOpt || isUndefined recordOpt then
            None
        else
            Some
                { GroupId = unbox<string> recordOpt?GroupId
                  OwnerSessionId = SessionId.create (unbox<string> recordOpt?OwnerSessionId)
                  LaneIndex = unbox<int> recordOpt?LaneIndex
                  LaneCount = unbox<int> recordOpt?LaneCount }

    let tryOwner laneSessionId =
        tryLane laneSessionId |> Option.map (fun binding -> binding.OwnerSessionId)

    let logicalOwner sessionId =
        let ownerStr: string = laneRegistry?logicalOwner (SessionId.value sessionId)

        if String.IsNullOrEmpty ownerStr then
            sessionId
        else
            SessionId.create ownerStr

    let markSilentInterrupt ownerSessionId =
        lock gate (fun () -> silentInterrupts.Add(SessionId.value ownerSessionId) |> ignore)

    let clearSilentInterrupt ownerSessionId =
        lock gate (fun () -> silentInterrupts.Remove(SessionId.value ownerSessionId) |> ignore)

    let isSilentInterrupt ownerSessionId =
        lock gate (fun () -> silentInterrupts.Contains(SessionId.value ownerSessionId))

    let tryConsumeSilentInterrupt ownerSessionId =
        lock gate (fun () -> silentInterrupts.Contains(SessionId.value ownerSessionId))

    let trackGroupResource groupId (resource: IDisposable) =
        lock gate (fun () ->
            let resources =
                match groupResources.TryGetValue groupId with
                | true, current -> current
                | false, _ ->
                    // DSL-MUTABLE: algorithm-scratch — new resource list for dictionary insert
                    let created = ResizeArray<IDisposable>()
                    groupResources.[groupId] <- created
                    created

            resources.Add resource)

    let tryBeginDelivery groupId completionId (laneIndex: int) =
        lock gate (fun () -> deliveryClaims.Add(deliveryKey groupId completionId laneIndex))

    let endDelivery groupId completionId (laneIndex: int) =
        lock gate (fun () -> deliveryClaims.Remove(deliveryKey groupId completionId laneIndex) |> ignore)

    let tryBeginTakeover groupId =
        lock gate (fun () -> takeoverClaims.Add groupId)

    let endTakeover groupId =
        lock gate (fun () -> takeoverClaims.Remove groupId |> ignore)

    let clearOwner ownerSessionId =
        let ownerKey = SessionId.value ownerSessionId
        let cleanup: obj = laneRegistry?clearOwner (ownerKey)

        let groupIds: string array =
            if isNull cleanup || isUndefined cleanup then
                [||]
            else
                unbox<string array> cleanup?RemovedGroupIds

        lock gate (fun () ->
            groupIds |> Array.iter (fun groupId -> takeoverClaims.Remove groupId |> ignore)
            groupIds |> Array.iter (disposeGroupResources groupResources)
            silentInterrupts.Remove ownerKey |> ignore)
