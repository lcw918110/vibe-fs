namespace Wanxiangshu.Interaction.PairGuidance

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type PairProgrammingGuideline =
    { Ordinal: int64
      CallId: ToolCallId
      MarkerText: string
      CallGap: TranscriptGap
      ResultGap: TranscriptGap }

type GuidelineProjectionState = private { History: obj }

[<RequireQualifiedAccess>]
type GuidelineFoldRejection =
    | NonSequentialOrdinal of expected: int64 * actual: int64
    | DuplicateCallId of callId: string
    | DuplicatePlacement of callGap: TranscriptGap * resultGap: TranscriptGap

module GuidelineProjection =
    [<Import("PairGuidanceHistory", "wanxiangli/pair-hint")>]
    let private historyClass: obj = jsNative

    let empty = { History = createNew historyClass () }

    let private pairOf (entry: obj) : PairProgrammingGuideline =
        let callGap, resultGap = unbox<TranscriptGap * TranscriptGap> entry?metadata

        { Ordinal = entry?ordinal
          CallId = ToolCallId.create entry?callId
          MarkerText = entry?markerText
          CallGap = callGap
          ResultGap = resultGap }

    let pairs state =
        unbox<obj array> (state.History?history()) |> Array.map pairOf |> Array.toList

    let visiblePairs state =
        unbox<obj array> (state.History?visible()) |> Array.map pairOf |> Array.toList

    let nextOrdinal state : int64 = state.History?nextOrdinal()
    let visibleFromOrdinal state : int64 = state.History?visibleFromOrdinal()
    let callIds state : string array = state.History?callIds()
    let placementCount state : int = state.History?placementCount()
    let applyReanchor state = { History = state.History?reanchor() }

    let restoreVisibilityFloor (floor: int64) state =
        { History = state.History?restoreVisibilityFloor(floor) }

    let private gapKey =
        function
        | TranscriptGap.Start -> "s"
        | TranscriptGap.Before address -> "b:" + TranscriptMessageAddress.value address
        | TranscriptGap.After address -> "a:" + TranscriptMessageAddress.value address

    let private rejection callGap resultGap (error: obj) =
        match unbox<string> error?kind with
        | "NonSequentialOrdinal" -> Error(GuidelineFoldRejection.NonSequentialOrdinal(error?expected, error?actual))
        | "DuplicateCallId" -> Error(GuidelineFoldRejection.DuplicateCallId error?callId)
        | "DuplicatePlacement" -> Error(GuidelineFoldRejection.DuplicatePlacement(callGap, resultGap))
        | kind -> invalidOp ("wanxiangli/pair-hint returned an unknown history rejection: " + kind)

    let apply (ordinal: int64) callId (markerText: string) callGap resultGap state =
        let entry =
            createObj
                [ "ordinal" ==> ordinal
                  "callId" ==> ToolCallId.value callId
                  "markerText" ==> markerText
                  "placementKey" ==> (gapKey callGap + "|" + gapKey resultGap)
                  "metadata" ==> box (callGap, resultGap) ]

        let result: obj = state.History?append(entry)

        if unbox<bool> result?ok then
            Ok { History = result?value }
        else
            rejection callGap resultGap result?error
