namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type XTraceProjectionState =
    private
        { Opening: XTraceOpeningEvidence option
          Parts: XTracePartRef list
          Terminals: XTraceTerminalRef list
          Bridge: WanxiangliTrace.ProjectionBridge }

and internal XTracePartRef =
    { Cursor: XTraceCursor
      Provenance: string
      Generation: int
      Role: string
      Turn: int
      PartIndex: int
      Kind: string
      ToolName: string option
      ProviderRun: ProviderRunIdentity option
      ToolCallId: ToolCallId option
      HostToolPartId: HostToolPartId option
      TextRef: BlobRef
      TextDigest: BlobDigest }

and internal XTraceTerminalRef =
    { TextRef: BlobRef
      TextDigest: BlobDigest
      ProviderRun: ProviderRunIdentity
      Frontier: XTraceCursor }

type XTraceSemanticPartView =
    { Cursor: XTraceCursor
      Provenance: string
      Generation: int
      Role: string
      Turn: int
      PartIndex: int
      Kind: string
      ToolName: string option
      ProviderRun: ProviderRunIdentity option
      ToolCallId: ToolCallId option
      HostToolPartId: HostToolPartId option
      TextRef: BlobRef
      TextDigest: BlobDigest }

type XTraceTerminalEvidence =
    { TextRef: BlobRef
      TextDigest: BlobDigest
      ProviderRun: ProviderRunIdentity
      Frontier: XTraceCursor }

[<RequireQualifiedAccess>]
type XTraceFoldRejection =
    | OpeningAlreadyCaptured
    | CursorNotAfterHead of expected: int64 * actual: int64
    | TerminalAlreadyCaptured

module XTraceProjection =
    val empty: XTraceProjectionState
    val internal parts: state: XTraceProjectionState -> XTracePartRef list
    val internal partCount: state: XTraceProjectionState -> int
    val internal terminalCount: state: XTraceProjectionState -> int
    val internal openingCaptured: state: XTraceProjectionState -> bool
    val openingEvidence: state: XTraceProjectionState -> XTraceOpeningEvidence option
    val hasOpening: state: XTraceProjectionState -> bool
    val hasSemanticParts: state: XTraceProjectionState option -> bool
    val orderedSemanticParts: state: XTraceProjectionState -> XTraceSemanticPartView list
    val internal headSequence: state: XTraceProjectionState -> int64
    val latestPartCursor: state: XTraceProjectionState -> XTraceCursor option
    val internal head: state: XTraceProjectionState -> int64
    val headCursor: state: XTraceProjectionState -> XTraceCursor
    val rangeFrom: startInclusive: XTraceCursor -> state: XTraceProjectionState -> XTraceRange
    val slice: range: XTraceRange -> state: XTraceProjectionState -> XTraceSemanticPartView list
    val frontierAfter: part: XTraceSemanticPartView -> XTraceCursor
    val rangeOfPart: part: XTraceSemanticPartView -> XTraceRange
    val partKinds: state: XTraceProjectionState -> string list
    val internal latestTerminal: state: XTraceProjectionState -> XTraceTerminalRef option
    val latestTerminalEvidence: state: XTraceProjectionState -> XTraceTerminalEvidence option

    val internal terminalForProviderRun:
        providerRun: ProviderRunIdentity -> state: XTraceProjectionState -> XTraceTerminalRef option

    val terminalEvidenceForProviderRun:
        providerRun: ProviderRunIdentity -> state: XTraceProjectionState -> XTraceTerminalEvidence option

    val applyOpening:
        assignment: string ->
        requirements: string list ->
        state: XTraceProjectionState ->
            Result<XTraceProjectionState, XTraceFoldRejection>

    val provenanceGeneration: provenance: string -> int

    val applyPart:
        cursorSequence: int64 ->
        role: string ->
        provenance: string ->
        turn: int ->
        partIndex: int ->
        kind: string ->
        toolName: string option ->
        providerRun: ProviderRunIdentity option ->
        toolCallId: ToolCallId option ->
        hostToolPartId: HostToolPartId option ->
        textRef: BlobRef ->
        textDigest: BlobDigest ->
        state: XTraceProjectionState ->
            Result<XTraceProjectionState, XTraceFoldRejection>

    val applyTerminal:
        textRef: BlobRef ->
        textDigest: BlobDigest ->
        providerRun: ProviderRunIdentity ->
        state: XTraceProjectionState ->
            Result<XTraceProjectionState, XTraceFoldRejection>

    val traceHeadCut: state: XTraceProjectionState -> XTraceCut
    val traceCutAt: cursor: XTraceCursor -> state: XTraceProjectionState -> XTraceCut
    val traceIntervalOfRange: range: XTraceRange -> state: XTraceProjectionState -> XTraceCut * XTraceCut
    val traceIntervalAfterCoverage: coverage: RecordCoverage -> state: XTraceProjectionState -> XTraceCut * XTraceCut
    val traceRecords: state: XTraceProjectionState -> string list

    val internal currentGenerationParts: parts: XTracePartRef list -> XTracePartRef list
    val currentGenerationSemanticParts: state: XTraceProjectionState -> XTraceSemanticPartView list

    val providerRunParts:
        providerRun: ProviderRunIdentity -> state: XTraceProjectionState -> XTraceSemanticPartView list

    val toolResultParts:
        providerRun: ProviderRunIdentity ->
        toolCallId: ToolCallId ->
        state: XTraceProjectionState ->
            XTraceSemanticPartView list

    val toolPartsForHostIdentity:
        providerRun: ProviderRunIdentity ->
        toolCallId: ToolCallId ->
        hostToolPartId: HostToolPartId ->
        state: XTraceProjectionState ->
            XTraceSemanticPartView list

    val internal tryHostMessageId: part: XTracePartRef -> string option
    val tryHostMessageIdAt: cursor: XTraceCursor -> state: XTraceProjectionState -> string option
    val hostMessageFirstCursors: state: XTraceProjectionState -> Map<string, int64>
    val partsForHostMessageIds: messageIds: Set<string> -> state: XTraceProjectionState -> XTraceSemanticPartView list
    val tryContiguousHostRange: messageIds: Set<string> -> state: XTraceProjectionState -> XTraceRange option
    val tryTurnOfHostMessageId: messageId: string -> state: XTraceProjectionState -> int option
    val tryTurnOfToolCallId: callId: ToolCallId -> state: XTraceProjectionState -> int option
    val frontierTurn: state: XTraceProjectionState -> int option
    val tryOpeningHostMessageId: state: XTraceProjectionState -> string option
    val hostMessageIdsBeforeTurn: cutoffExclusive: int -> state: XTraceProjectionState -> string list
    val internal semanticCursorFor: sequence: int64 -> state: XTraceProjectionState -> XTraceCursor
    val semanticCursorAfter: cursor: XTraceCursor -> state: XTraceProjectionState -> XTraceCursor
    val semanticCursorAfterCoverage: coverage: RecordCoverage -> state: XTraceProjectionState -> XTraceCursor
