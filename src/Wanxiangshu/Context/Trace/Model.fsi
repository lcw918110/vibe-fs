namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Participant.Provider.Projection.ProviderProjection

type XTraceItem =
    { Cursor: XTraceCursor
      Provenance: string
      Role: string
      Part: SemanticPart }

[<RequireQualifiedAccess>]
module internal WanxiangliTrace =
    [<Literal>]
    val MaxPartChunkBytes: int = 524288

    type ProjectionBridge =
        { TraceId: string
          Records: string list
          OpeningCutCursor: int
          PartCuts: (int64 * int) list
          RecordCount: int }

    val emptyBridge: traceId: string -> ProjectionBridge

    /// Oldest-first record lines. `ProjectionBridge.Records` is stored
    /// newest-first (O(1) prepend on append); this restores chronological order
    /// for the trace constructor and the `traceRecords` surface.
    val recordsOldestFirst: bridge: ProjectionBridge -> string list

    val appendOpeningToBridge:
        assignment: string ->
        requirements: string list ->
        hasExistingEntries: bool ->
        rebuildWithOpening: (ProjectionBridge -> ProjectionBridge) ->
        bridge: ProjectionBridge ->
            ProjectionBridge

    val appendPartDescriptorToBridge:
        cursorSequence: int64 ->
        role: string ->
        provenance: string ->
        kind: string ->
        toolName: string option ->
        textRef: string ->
        textDigest: string ->
        bridge: ProjectionBridge ->
            ProjectionBridge

    val appendTerminalDescriptorToBridge:
        providerRun: string ->
        textRef: string ->
        textDigest: string ->
        frontierSequence: int64 ->
        bridge: ProjectionBridge ->
            ProjectionBridge

    val headCutOfBridge: bridge: ProjectionBridge -> XTraceCut
    val cutAtSequenceOfBridge: sequence: int64 -> bridge: ProjectionBridge -> XTraceCut
    val intervalOfRangeOnBridge: range: XTraceRange -> bridge: ProjectionBridge -> XTraceCut * XTraceCut
    val intervalAfterCoverageOnBridge: coverage: RecordCoverage -> bridge: ProjectionBridge -> XTraceCut * XTraceCut
    val verifyAndReadIntervalOnBridge: range: XTraceRange -> bridge: ProjectionBridge -> obj array
    val materializeItemsInRange: range: XTraceRange -> items: XTraceItem list -> XTraceItem list

    val inspectItemsBridge:
        traceId: string -> openingText: string option -> range: XTraceRange -> items: XTraceItem list -> obj

[<RequireQualifiedAccess>]
module XTrace =
    val sliceBetween: start: XTraceCursor -> endExclusive: XTraceCursor -> items: XTraceItem list -> XTraceItem list
    val sliceFrom: start: XTraceCursor -> items: XTraceItem list -> XTraceItem list
    val head: items: XTraceItem list -> XTraceCursor
    val flatten: messages: SemanticMessage list -> {| Part: SemanticPart; Role: string |} list
    val forOpening: items: XTraceItem list -> XTraceItem list
    val renderItem: item: XTraceItem -> string
    val render: items: XTraceItem list -> string
