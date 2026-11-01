namespace Wanxiangshu.Interaction.PairGuidance

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

/// JS-native semantic owner for the durable pair-guideline projection. The
/// typed GuidelineProjection fold remains private; states, gaps and rejection
/// reasons cross as plain objects/arrays/strings only.
[<RequireQualifiedAccess>]
module GuidelineSurface =

    let private isNullish (value: obj) : bool =
        isNull value || emitJsExpr value "$0 === undefined"

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    let private int64Value (value: obj) : int64 =
        if isNullish value then 0L else int64 (text value)

    let private gapOf (value: obj) : TranscriptGap =
        let raw = text value

        if raw = "" || raw.Equals("start", System.StringComparison.OrdinalIgnoreCase) then
            TranscriptGap.Start
        elif raw.StartsWith("before:", System.StringComparison.OrdinalIgnoreCase) then
            TranscriptGap.Before(TranscriptMessageAddress.create (raw.Substring(7)))
        elif raw.StartsWith("after:", System.StringComparison.OrdinalIgnoreCase) then
            TranscriptGap.After(TranscriptMessageAddress.create (raw.Substring(6)))
        else
            TranscriptGap.After(TranscriptMessageAddress.create raw)

    let private gapToJs (gap: TranscriptGap) : string =
        match gap with
        | TranscriptGap.Start -> "start"
        | TranscriptGap.Before address -> "before:" + TranscriptMessageAddress.value address
        | TranscriptGap.After address -> "after:" + TranscriptMessageAddress.value address

    let private pairToJs (pair: PairProgrammingGuideline) : obj =
        box
            {| ordinal = pair.Ordinal
               callId = ToolCallId.value pair.CallId
               markerText = pair.MarkerText
               callGap = gapToJs pair.CallGap
               resultGap = gapToJs pair.ResultGap |}

    let private stateToJs (state: GuidelineProjectionState) : obj =
        box
            {| pairs = GuidelineProjection.pairs state |> List.map pairToJs |> List.toArray
               visibleFromOrdinal = GuidelineProjection.visibleFromOrdinal state |}

    let private pairOfJs (value: obj) : PairProgrammingGuideline =
        { Ordinal = int64Value value?ordinal
          CallId = ToolCallId.create (text value?callId)
          MarkerText = text value?markerText
          CallGap = gapOf value?callGap
          ResultGap = gapOf value?resultGap }

    let private stateOfJs (value: obj) : GuidelineProjectionState =
        let pairs =
            if isNullish value?pairs then
                [||]
            else
                unbox<obj array> value?pairs

        let visibleFromOrdinal =
            if isNullish value?visibleFromOrdinal then
                1L
            else
                int64Value value?visibleFromOrdinal

        // The JSON state intentionally carries only semantic pairs. Replaying
        // them through the owner rebuilds the private set indexes and keeps
        // collection representation out of the JS contract.
        let rebuilt, crossedVisibilityFloor =
            ((GuidelineProjection.empty, false), pairs)
            ||> Array.fold (fun (state, crossed) value ->
                let pair = pairOfJs value

                let current =
                    if not crossed && visibleFromOrdinal > 1L && pair.Ordinal >= visibleFromOrdinal then
                        GuidelineProjection.applyReanchor state
                    else
                        state

                match
                    GuidelineProjection.apply
                        pair.Ordinal
                        pair.CallId
                        pair.MarkerText
                        pair.CallGap
                        pair.ResultGap
                        current
                with
                | Ok next -> next, crossed || pair.Ordinal >= visibleFromOrdinal
                | Error rejection -> failwithf "GuidelineSurface: invalid state (%A)" rejection)

        let rebuilt =
            if visibleFromOrdinal > 1L && not crossedVisibilityFloor then
                GuidelineProjection.applyReanchor rebuilt
            else
                rebuilt

        GuidelineProjection.restoreVisibilityFloor visibleFromOrdinal rebuilt

    let private rejectionToJs (rejection: GuidelineFoldRejection) : obj =
        match rejection with
        | GuidelineFoldRejection.NonSequentialOrdinal(expected, actual) ->
            box
                {| name = "NonSequentialOrdinal"
                   expected = expected
                   actual = actual |}
        | GuidelineFoldRejection.DuplicateCallId callId ->
            box
                {| name = "DuplicateCallId"
                   callId = callId |}
        | GuidelineFoldRejection.DuplicatePlacement(callGap, resultGap) ->
            box
                {| name = "DuplicatePlacement"
                   callGap = gapToJs callGap
                   resultGap = gapToJs resultGap |}

    let private resultToJs (result: Result<GuidelineProjectionState, GuidelineFoldRejection>) : obj =
        match result with
        | Ok state -> box {| ok = true; value = stateToJs state |}
        | Error rejection ->
            box
                {| ok = false
                   error = rejectionToJs rejection |}

    /// Empty projection state.
    let empty: obj = stateToJs GuidelineProjection.empty

    let nextOrdinal (state: obj) : int64 =
        GuidelineProjection.nextOrdinal (stateOfJs state)

    let pairs (state: obj) : obj array =
        GuidelineProjection.pairs (stateOfJs state) |> List.map pairToJs |> List.toArray

    let visiblePairs (state: obj) : obj array =
        GuidelineProjection.visiblePairs (stateOfJs state)
        |> List.map pairToJs
        |> List.toArray

    let applyReanchor (state: obj) : obj =
        stateOfJs state |> GuidelineProjection.applyReanchor |> stateToJs

    /// Apply one semantic pair. Ordinals may be JavaScript numbers or BigInts;
    /// the owner normalizes them to the production int64 identity.
    let apply (request: obj) (state: obj) : obj =
        GuidelineProjection.apply
            (int64Value request?ordinal)
            (ToolCallId.create (text request?callId))
            (text request?markerText)
            (gapOf request?callGap)
            (gapOf request?resultGap)
            (stateOfJs state)
        |> resultToJs
