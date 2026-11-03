namespace Wanxiangshu.Interaction.PairGuidance

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type PairProgrammingGuideline =
    { Ordinal: int64
      CallId: ToolCallId
      MarkerText: string
      CallGap: TranscriptGap
      ResultGap: TranscriptGap }

type GuidelineProjectionState

[<RequireQualifiedAccess>]
type GuidelineFoldRejection =
    | NonSequentialOrdinal of expected: int64 * actual: int64
    | DuplicateCallId of callId: string
    | DuplicatePlacement of callGap: TranscriptGap * resultGap: TranscriptGap

module GuidelineProjection =
    val empty: GuidelineProjectionState
    val pairs: GuidelineProjectionState -> PairProgrammingGuideline list
    val visiblePairs: GuidelineProjectionState -> PairProgrammingGuideline list
    val nextOrdinal: GuidelineProjectionState -> int64
    val visibleFromOrdinal: GuidelineProjectionState -> int64
    val callIds: GuidelineProjectionState -> string array
    val placementCount: GuidelineProjectionState -> int
    val applyReanchor: GuidelineProjectionState -> GuidelineProjectionState
    val restoreVisibilityFloor: int64 -> GuidelineProjectionState -> GuidelineProjectionState

    val apply:
        ordinal: int64 ->
        callId: ToolCallId ->
        markerText: string ->
        callGap: TranscriptGap ->
        resultGap: TranscriptGap ->
        state: GuidelineProjectionState ->
            Result<GuidelineProjectionState, GuidelineFoldRejection>
