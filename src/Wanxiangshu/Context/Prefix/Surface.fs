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

    let private scopesOfJs (value: obj) : Set<string> =
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
        PrefixEpochProjection.ofGeneration (
            createObj
                [ "epoch" ==> int64Value value?epoch
                  "compactionRuns"
                  ==> (runsOfJs value?reanchoredRuns
                       |> Set.toArray
                       |> Array.map ProviderRunIdentity.value)
                  "tenures" ==> (tenuresOfJs value?reanchoredTenures |> Set.toArray)
                  "scopes" ==> (scopesOfJs value?reanchoredScopes |> Set.toArray) ]
        )

    let private stateToJs (state: ActivePrefixEpoch) : obj =
        box
            {| epoch = PrefixEpochId.value (PrefixEpochProjection.epochId state)
               reanchoredRuns =
                PrefixEpochProjection.reanchoredRuns state
                |> Array.map ProviderRunIdentity.value
               reanchoredTenures = PrefixEpochProjection.reanchoredTenures state
               reanchoredScopes = PrefixEpochProjection.reanchoredScopes state |}

    let private rejectionName (rejection: PrefixFoldRejection) : string =
        match rejection with
        | PrefixFoldRejection.StalePrefixEpoch _ -> "StalePrefixEpoch"
        | PrefixFoldRejection.NonSequentialPrefixEpoch -> "NonSequentialPrefixEpoch"
        | PrefixFoldRejection.CompactionAlreadyReanchored _ -> "CompactionAlreadyReanchored"
        | PrefixFoldRejection.TenureAlreadyReanchored _ -> "TenureAlreadyReanchored"
        | PrefixFoldRejection.ScopeAlreadyReanchored _ -> "ScopeAlreadyReanchored"

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
               reanchoredTenures = [||]
               reanchoredScopes = [||] |}

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
            (text request?incumbencyId)
            (stateOfJs state)
        |> resultToJs

    let applyScopeReanchor (request: obj) (state: obj) : obj =
        PrefixEpochProjection.applyScopeReanchor
            (PrefixEpochId.create (int64Value request?previousEpoch))
            (PrefixEpochId.create (int64Value request?nextEpoch))
            (text request?callId)
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
        stateOfJs state |> PrefixEpochProjection.epochId |> PrefixEpochId.value

    let isTenureReanchored (incumbencyId: string) (state: obj) : bool =
        PrefixEpochProjection.isTenureReanchored incumbencyId (stateOfJs state)

    let isScopeReanchored (callId: string) (state: obj) : bool =
        PrefixEpochProjection.isScopeReanchored callId (stateOfJs state)

    let reanchoredTenures (state: obj) : string array =
        stateOfJs state |> PrefixEpochProjection.reanchoredTenures

    let reanchoredScopes (state: obj) : string array =
        stateOfJs state |> PrefixEpochProjection.reanchoredScopes

    let reanchoredRuns (state: obj) : string array =
        stateOfJs state
        |> PrefixEpochProjection.reanchoredRuns
        |> Array.map ProviderRunIdentity.value

    let isReanchored (run: string) (state: obj) : bool =
        PrefixEpochProjection.isReanchored (ProviderRunIdentity.create (text run)) (stateOfJs state)


    let appendCheckpoint (callId: string) (window: obj array) : obj array = if isNull window then [||] else window
