namespace Wanxiangshu.Context.Trace

open System
open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

/// JS proof surface for semantic-trace owner operations.
/// Typed owner state never crosses this boundary; queries return copied plain evidence.
[<RequireQualifiedAccess>]
module SemanticTraceSurface =

    /// Opaque proof capability for the semantic-trace owner.
    type SemanticTraceProjection =
        private new: state: XTraceProjectionState -> SemanticTraceProjection
        member internal State: XTraceProjectionState
        static member internal Create: state: XTraceProjectionState -> SemanticTraceProjection

    val textPart: value: 'a -> obj
    val reasoningPart: value: 'a -> obj
    val toolCallPart: callId: 'a -> name: 'b -> args: 'c -> obj
    val toolResultPart: callId: 'a -> result: 'b -> obj
    val activityPart: kind: 'a -> obj
    val mapPart: value: 'a -> obj option
    val semanticText: value: string -> obj
    val semanticReasoning: value: string -> obj
    val semanticToolCall: name: string -> args: string -> obj
    val semanticToolResult: value: string -> obj
    val semanticMedia: mediaType: 'a -> digest: string -> obj
    val emptyProjection: unit -> SemanticTraceProjection

    val appendOpening:
        projection: SemanticTraceProjection -> assignmentText: string -> requirements: string array -> obj

    val appendPart: projection: SemanticTraceProjection -> value: 'a -> obj
    val appendTerminal: projection: SemanticTraceProjection -> value: 'a -> obj
    val openingEvidence: projection: SemanticTraceProjection -> obj option
    val hasOpening: projection: SemanticTraceProjection -> bool
    val hasSemanticParts: projection: SemanticTraceProjection -> bool
    val orderedSemanticParts: projection: SemanticTraceProjection -> obj array
    val currentGenerationSemanticParts: projection: SemanticTraceProjection -> obj array
    val partKinds: projection: SemanticTraceProjection -> string array
    val latestPartCursor: projection: SemanticTraceProjection -> obj option
    val headCursor: projection: SemanticTraceProjection -> obj
    val frontierAfter: value: 'a -> obj
    val rangeFrom: start: 'a -> projection: SemanticTraceProjection -> obj
    val rangeOfPart: value: 'a -> obj
    val slice: range: 'a -> projection: SemanticTraceProjection -> obj array
    val traceHeadCut: projection: SemanticTraceProjection -> obj
    val traceCutAt: cursor: 'a -> projection: SemanticTraceProjection -> obj
    val traceIntervalOfRange: range: 'a -> projection: SemanticTraceProjection -> obj
    val traceIntervalAfterCoverage: cursor: 'a -> projection: SemanticTraceProjection -> obj
    val traceRecords: projection: SemanticTraceProjection -> string array
    val latestTerminalEvidence: projection: SemanticTraceProjection -> obj option
    val terminalEvidenceForProviderRun: providerRun: string -> projection: SemanticTraceProjection -> obj option
    val providerRunParts: providerRun: string -> projection: SemanticTraceProjection -> obj array
    val toolResultParts: providerRun: string -> toolCallId: string -> projection: SemanticTraceProjection -> obj array

    val toolPartsForHostIdentity:
        providerRun: string ->
        toolCallId: string ->
        hostToolPartId: string ->
        projection: SemanticTraceProjection ->
            obj array

    val tryHostMessageIdAt: cursor: 'a -> projection: SemanticTraceProjection -> string option
    val hostMessageFirstCursors: projection: SemanticTraceProjection -> obj
    val partsForHostMessageIds: ids: string array -> projection: SemanticTraceProjection -> obj array
    val tryContiguousHostRange: ids: string array -> projection: SemanticTraceProjection -> obj option
    val tryTurnOfHostMessageId: id: string -> projection: SemanticTraceProjection -> int option
    val tryOpeningHostMessageId: projection: SemanticTraceProjection -> string option
    val hostMessageIdsBeforeTurn: cutoff: int -> projection: SemanticTraceProjection -> string array
    val semanticCursorAfter: cursor: 'a -> projection: SemanticTraceProjection -> obj
    val originCursor: obj
    val cursor: sequence: int -> obj
    val next: value: 'a -> obj
    val isAfter: value: 'a -> previous: 'b -> bool
    val isAtOrAfter: value: 'a -> previous: 'b -> bool
    val isBefore: value: 'a -> nextValue: 'b -> bool
    val createRange: start: 'a -> endExclusive: 'b -> obj
    val rangeContains: value: 'a -> range: 'b -> bool
    val rangeIsEmpty: range: 'a -> bool
    val item: value: 'a -> obj
    val sliceBetween: start: 'a -> endExclusive: 'b -> values: 'c -> obj array
    val sliceFrom: start: 'a -> values: 'b -> obj array
    val inspectBridge: traceId: string -> openingText: 'a -> range: 'b -> values: 'c -> obj
    val forOpening: values: 'a -> obj array
    val render: values: 'a -> string
    val flatten: messages: 'a -> obj array
    val snapshot: handle: JournalHandle -> sessionId: string -> SemanticTraceProjection
    val captureProjection: handle: JournalHandle -> sessionId: string -> projection: obj -> Task<obj>

    val captureOpening:
        handle: JournalHandle -> sessionId: string -> assignment: string -> requirements: string array -> Task<obj>

    val captureTerminalText:
        handle: JournalHandle -> sessionId: string -> value: string -> providerRun: string -> Task<obj>

    val captureLastWords:
        handle: JournalHandle ->
        sessionId: string ->
        textRef: string ->
        textDigest: string ->
        providerRun: string ->
            Task<obj>

    val captureMessageView: handle: JournalHandle -> sessionId: string -> messages: obj -> Task<obj>
    val captureObservedMessages: handle: JournalHandle -> sessionId: string -> observations: obj -> Task<obj>
    val currentProjection: handle: JournalHandle -> sessionId: string -> Task<obj>
    val currentProjectionBetween: handle: JournalHandle -> sessionId: string -> range: obj -> Task<obj>
    val renderRange: handle: JournalHandle -> sessionId: string -> range: obj -> Task<string>
