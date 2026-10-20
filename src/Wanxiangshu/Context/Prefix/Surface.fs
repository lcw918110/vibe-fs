namespace Wanxiangshu.Context.Prefix

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt

/// Prefix-stability owner surface. Prefix epoch state and reanchor
/// facts cross as JSON; the production epoch and identity representations stay
/// behind this boundary.
[<RequireQualifiedAccess>]
module PrefixSurface =

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    let private int64Value (value: obj) : int64 = int64 (text value)
    let private intValue (value: obj) : int = int (text value)


    let private tenuresOfJs (value: obj) : Set<string> =
        if isNullish value then
            Set.empty
        else
            value |> unbox<obj array> |> Array.map string |> Set.ofArray

    let private runsOfJs (value: obj) : Set<ProviderRunIdentity> =
        if isNullish value then
            Set.empty
        else
            value
            |> unbox<obj array>
            |> Array.map (fun run -> ProviderRunIdentity.create (text run))
            |> Set.ofArray

    let private stateOfJs (value: obj) : ActivePrefixEpoch =
        { EpochId = PrefixEpochId.create (int64Value value?epoch)
          ReanchoredRuns = runsOfJs value?reanchoredRuns
          ReanchoredTenures = tenuresOfJs value?reanchoredTenures }

    let private stateToJs (state: ActivePrefixEpoch) : obj =
        box
            {| epoch = PrefixEpochId.value state.EpochId
               reanchoredRuns = state.ReanchoredRuns |> Set.toArray |> Array.map ProviderRunIdentity.value
               reanchoredTenures = state.ReanchoredTenures |> Set.toArray |}

    let private rejectionName (rejection: PrefixFoldRejection) : string =
        match rejection with
        | PrefixFoldRejection.StalePrefixEpoch _ -> "StalePrefixEpoch"
        | PrefixFoldRejection.NonSequentialPrefixEpoch -> "NonSequentialPrefixEpoch"
        | PrefixFoldRejection.CompactionAlreadyReanchored _ -> "CompactionAlreadyReanchored"
        | PrefixFoldRejection.TenureAlreadyReanchored _ -> "TenureAlreadyReanchored"

    let private resultToJs (result: Result<ActivePrefixEpoch, PrefixFoldRejection>) : obj =
        match result with
        | Ok value -> box {| ok = true; value = stateToJs value |}
        | Error rejection ->
            box
                {| ok = false
                   error = rejectionName rejection |}

    let empty: obj =
        box
            {| epoch = 0L
               reanchoredRuns = [||]
               reanchoredTenures = [||] |}

    let private requestKindOf (value: obj) : ProviderRequestKind option =
        match text value |> fun item -> item.ToLowerInvariant() with
        | "workmain"
        | "work-main" -> Some ProviderRequestKind.WorkMain
        | "interactionrepair"
        | "interaction-repair" -> Some ProviderRequestKind.InteractionRepair
        | _ -> None


    let requestKindLabels: string array =
        [| ProviderRequestKind.WorkMain; ProviderRequestKind.InteractionRepair |]
        |> Array.map ProviderRequestKind.label

    let requestKindLabel (kind: string) : string =
        requestKindOf kind
        |> Option.map ProviderRequestKind.label
        |> Option.defaultValue ""

    let requestKind =
        box
            {| workMain = "work-main"
               interactionRepair = "interaction-repair"
               all = requestKindLabels
               label = (fun kind -> requestKindLabel kind) |}

    let applyTenureReanchor (request: obj) (state: obj) : obj =
        PrefixEpochProjection.applyTenureReanchor
            (PrefixEpochId.create (int64Value request?previousEpoch))
            (PrefixEpochId.create (int64Value request?nextEpoch))
            (string request?incumbencyId)
            (stateOfJs state)
        |> resultToJs

    let applyReanchor (request: obj) (state: obj) : obj =
        PrefixEpochProjection.applyReanchor
            (PrefixEpochId.create (int64Value request?previousEpoch))
            (PrefixEpochId.create (int64Value request?nextEpoch))
            (ProviderRunIdentity.create (text request?observedRun))
            (stateOfJs state)
        |> resultToJs

    let epochOf (state: obj) : int64 =
        PrefixEpochId.value (stateOfJs state).EpochId

    let isTenureReanchored (incumbencyId: string) (state: obj) : bool =
        PrefixEpochProjection.isTenureReanchored incumbencyId (stateOfJs state)

    let reanchoredTenures (state: obj) : string array =
        (stateOfJs state).ReanchoredTenures |> Set.toArray

    let reanchoredRuns (state: obj) : string array =
        (stateOfJs state).ReanchoredRuns
        |> Set.toArray
        |> Array.map ProviderRunIdentity.value

    let isReanchored (run: string) (state: obj) : bool =
        PrefixEpochProjection.isReanchored (ProviderRunIdentity.create (text run)) (stateOfJs state)


    let private checkpointOfJs (item: obj) : Wanxiangshu.Context.Prefix.PhaseWindow.PhaseCheckpoint =
        { ToolCallId = ToolCallId.create (string item?callId) }

    let private checkpointToJs (checkpoint: Wanxiangshu.Context.Prefix.PhaseWindow.PhaseCheckpoint) : obj =
        box {| callId = ToolCallId.value checkpoint.ToolCallId |}

    /// Append one successful view declaration checkpoint. The window is not trimmed by K;
    /// only a committed prefix rebase can make older checkpoints unrecoverable.
    let appendCheckpoint (callId: string) (window: obj array) : obj array =
        let committed =
            { Wanxiangshu.Context.Prefix.PhaseWindow.Checkpoints =
                (if isNull window then [] else Array.toList window) |> List.map checkpointOfJs }

        Wanxiangshu.Context.Prefix.PhaseWindow.appendCheckpoint (ToolCallId.create callId) committed
        |> fun next -> next.Checkpoints |> List.map checkpointToJs |> List.toArray
