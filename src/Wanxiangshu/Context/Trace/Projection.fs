namespace Wanxiangshu.Context.Trace

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

/// COMPANION-003 / HOST-005: the XTrace's durable projection.
///
/// Append-only within one lifecycle: `Opening` is captured once and never
/// overwritten, `Parts` grow strictly by cursor, terminal occurrences grow by
/// ProviderRun as a reusable managed session completes successive work units.
/// `RecordCoverage` (how far the trace has been consumed) is supplied by the
/// caller, NOT advanced here — this module holds the record itself. The
/// durable `ViewCheckpointCommitted` window (context-compression-028) records
/// which view declarations were committed; what history is kept or replaced is
/// decided by the model's own ANCHOR `view` declaration (prefixUntil /
/// thenRemove), not by a second copy of the record.
///
/// Bodies live in blobs (PERSIST-007): the journal line carries cursor, kind,
/// role, digest and reference; the text is resolved at the read boundary, the
/// same way blob readers resolve text references.
type XTraceProjectionState =
    private
        {
            /// InitialCharge, inline: first task prompt, bounded and
            /// human-sized.
            Opening: XTraceOpeningEvidence option
            /// Semantic parts. Stored newest-first so replay cons is O(1);
            /// `XTraceProjection.parts` restores oldest-first cursor order.
            /// `Kind` is one of text / reasoning / tool_call / tool_result / media.
            Parts: XTracePartRef list
            /// Terminal occurrences, newest first. Reusable managed sessions may
            /// complete many work units; each occurrence keeps the ProviderRun and
            /// the XTrace part frontier at which that completion happened so a
            Terminals: XTraceTerminalRef list
            /// Composition-layer bridge to the underlying wanxiangli/trace instance.
            Bridge: WanxiangliTrace.ProjectionBridge
        }

/// DSL-state-combination: domain — optional tool/provider/host identities are
/// provenance facets of one immutable trace part, not independent execution
/// stages or mutable continuation state.
and internal XTracePartRef =
    {
        Cursor: XTraceCursor
        Provenance: string
        /// Parsed once at fold from Provenance (`g:N/...`; legacy → 0).
        Generation: int
        Role: string
        /// Host semantic coordinates, kept so the writer can map a Host
        /// message's turn/part back to the XTrace cursor sequence it
        /// corresponds to. The XTrace cursor itself is independent of them.
        Turn: int
        PartIndex: int
        Kind: string
        ToolName: string option
        ProviderRun: ProviderRunIdentity option
        ToolCallId: ToolCallId option
        HostToolPartId: HostToolPartId option
        TextRef: BlobRef
        TextDigest: BlobDigest
    }

and internal XTraceTerminalRef =
    { TextRef: BlobRef
      TextDigest: BlobDigest
      ProviderRun: ProviderRunIdentity
      Frontier: XTraceCursor }

/// Stable query result for one semantic part. This is a copied view, never the
/// projection's append storage, so callers cannot mutate or retain owner state.
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

/// Why an XTrace line was refused (PERSIST-010).
[<RequireQualifiedAccess>]
type XTraceFoldRejection =
    /// The opening was already captured with different text. A replay must carry
    /// the SAME text; a different one means two writers disagreed about the
    /// session's opening task.
    | OpeningAlreadyCaptured
    /// A part arrived with a cursor not after the projection's head. Either a
    /// duplicate (same cursor) or a reordering (older cursor after newer).
    | CursorNotAfterHead of expected: int64 * actual: int64
    /// This ProviderRun already captured a different terminal. Idempotent replay
    /// carries the same digest; another ProviderRun owns another occurrence.
    | TerminalAlreadyCaptured

module XTraceProjection =

    let empty =
        { Opening = None
          Parts = []
          Terminals = []
          Bridge = WanxiangliTrace.emptyBridge "xtrace-projection" }

    /// Oldest-first cursor order. The stored field is newest-first.
    let internal parts (state: XTraceProjectionState) : XTracePartRef list = List.rev state.Parts

    let internal partCount (state: XTraceProjectionState) = List.length state.Parts

    let internal terminalCount (state: XTraceProjectionState) = List.length state.Terminals

    let internal openingCaptured (state: XTraceProjectionState) = Option.isSome state.Opening

    let private semanticPartView (part: XTracePartRef) : XTraceSemanticPartView =
        { Cursor = part.Cursor
          Provenance = part.Provenance
          Generation = part.Generation
          Role = part.Role
          Turn = part.Turn
          PartIndex = part.PartIndex
          Kind = part.Kind
          ToolName = part.ToolName
          ProviderRun = part.ProviderRun
          ToolCallId = part.ToolCallId
          HostToolPartId = part.HostToolPartId
          TextRef = part.TextRef
          TextDigest = part.TextDigest }

    let private terminalEvidence (terminal: XTraceTerminalRef) : XTraceTerminalEvidence =
        { TextRef = terminal.TextRef
          TextDigest = terminal.TextDigest
          ProviderRun = terminal.ProviderRun
          Frontier = terminal.Frontier }

    /// Opening proof copied out of the opaque projection.
    let openingEvidence (state: XTraceProjectionState) : XTraceOpeningEvidence option =
        state.Opening
        |> Option.map (fun opening ->
            { AssignmentText = opening.AssignmentText
              AuthoritativeRequirements = opening.AuthoritativeRequirements
              ConstitutiveBody = opening.ConstitutiveBody })

    let hasOpening (state: XTraceProjectionState) = Option.isSome state.Opening

    let hasSemanticParts (state: XTraceProjectionState option) =
        state |> Option.exists (fun trace -> not (List.isEmpty trace.Parts))

    /// Stable oldest-first semantic query view across the whole lifecycle.
    let orderedSemanticParts (state: XTraceProjectionState) : XTraceSemanticPartView list =
        parts state |> List.map semanticPartView

    let internal headSequence (state: XTraceProjectionState) : int64 =
        match state.Parts with
        | part :: _ -> XTraceCursor.sequence part.Cursor
        | [] -> 0L

    /// Latest assigned part cursor, without conflating an empty trace with
    /// sequence zero.
    let latestPartCursor (state: XTraceProjectionState) : XTraceCursor option =
        state.Parts |> List.tryHead |> Option.map (fun part -> part.Cursor)

    /// One-past last part (XTrace.head semantics). Empty projection is 0.
    /// Distinct from headSequence, which is the last assigned part (or 0).
    let internal head (state: XTraceProjectionState) : int64 =
        match state.Parts with
        | part :: _ -> XTraceCursor.sequence part.Cursor + 1L
        | [] -> 0L

    /// One-past the latest part as an opaque trace cursor.
    let headCursor (state: XTraceProjectionState) : XTraceCursor = XTraceCursor.create (head state)

    let rangeFrom (startInclusive: XTraceCursor) (state: XTraceProjectionState) : XTraceRange =
        let traceHead = headCursor state

        let effectiveStart =
            if XTraceCursor.isAfter startInclusive traceHead then
                traceHead
            else
                startInclusive

        XTraceRange.create effectiveStart traceHead

    let slice (range: XTraceRange) (state: XTraceProjectionState) : XTraceSemanticPartView list =
        WanxiangliTrace.verifyAndReadIntervalOnBridge range state.Bridge |> ignore

        parts state
        |> List.filter (fun part -> XTraceRange.contains part.Cursor range)
        |> List.map semanticPartView

    let frontierAfter (part: XTraceSemanticPartView) : XTraceCursor = XTraceCursor.nextCursor part.Cursor

    let rangeOfPart (part: XTraceSemanticPartView) : XTraceRange =
        XTraceRange.create part.Cursor (frontierAfter part)

    let partKinds (state: XTraceProjectionState) : string list =
        parts state |> List.map (fun part -> part.Kind)

    let internal latestTerminal (state: XTraceProjectionState) : XTraceTerminalRef option = List.tryHead state.Terminals

    let latestTerminalEvidence (state: XTraceProjectionState) : XTraceTerminalEvidence option =
        latestTerminal state |> Option.map terminalEvidence

    let internal terminalForProviderRun (providerRun: ProviderRunIdentity) (state: XTraceProjectionState) =
        state.Terminals
        |> List.tryFind (fun terminal -> terminal.ProviderRun = providerRun)

    let terminalEvidenceForProviderRun
        (providerRun: ProviderRunIdentity)
        (state: XTraceProjectionState)
        : XTraceTerminalEvidence option =
        terminalForProviderRun providerRun state |> Option.map terminalEvidence

    let private parseGeneration (token: string) =
        match System.Int32.TryParse token with
        | true, n when n >= 0 -> n
        | _ -> 0

    let rec private collectCurrentGeneration
        (maxGen: int)
        (acc: XTracePartRef list)
        (remaining: XTracePartRef list)
        : XTracePartRef list =
        match remaining with
        | [] -> List.rev acc
        | part :: tail when part.Generation > maxGen -> collectCurrentGeneration part.Generation [ part ] tail
        | part :: tail when part.Generation = maxGen -> collectCurrentGeneration maxGen (part :: acc) tail
        | _ :: tail -> collectCurrentGeneration maxGen acc tail

    let private cursorAfterSearch (searchable: XTracePartRef list) : XTraceCursor =
        match List.tryLast searchable with
        | Some last -> XTraceCursor.nextCursor last.Cursor
        | None -> XTraceCursor.create 0L

    let private appendPartRefToBridge (part: XTracePartRef) (bridge: WanxiangliTrace.ProjectionBridge) =
        WanxiangliTrace.appendPartDescriptorToBridge
            (XTraceCursor.sequence part.Cursor)
            part.Role
            part.Provenance
            part.Kind
            part.ToolName
            (BlobRef.value part.TextRef)
            (BlobDigest.value part.TextDigest)
            bridge

    let private appendTerminalRefToBridge (terminal: XTraceTerminalRef) (bridge: WanxiangliTrace.ProjectionBridge) =
        WanxiangliTrace.appendTerminalDescriptorToBridge
            (ProviderRunIdentity.value terminal.ProviderRun)
            (BlobRef.value terminal.TextRef)
            (BlobDigest.value terminal.TextDigest)
            (XTraceCursor.sequence terminal.Frontier)
            bridge

    let private rebuildBridgeEntries (state: XTraceProjectionState) (seeded: WanxiangliTrace.ProjectionBridge) =
        let withParts =
            parts state
            |> List.fold (fun current part -> appendPartRefToBridge part current) seeded

        List.rev state.Terminals
        |> List.fold (fun current terminal -> appendTerminalRefToBridge terminal current) withParts

    /// COMPANION-003: capture the opening task verbatim. Idempotent: replaying
    /// the same text changes nothing; a DIFFERENT text is refused (PERSIST-010).
    let applyOpening
        (assignment: string)
        (requirements: string list)
        (state: XTraceProjectionState)
        : Result<XTraceProjectionState, XTraceFoldRejection> =
        match state.Opening with
        | Some existing when
            existing.AssignmentText = assignment
            && existing.AuthoritativeRequirements = requirements
            ->
            Ok state
        | Some _ -> Error XTraceFoldRejection.OpeningAlreadyCaptured
        | None ->
            let hasExisting =
                not (List.isEmpty state.Parts) || not (List.isEmpty state.Terminals)

            let nextBridge =
                WanxiangliTrace.appendOpeningToBridge
                    assignment
                    requirements
                    hasExisting
                    (rebuildBridgeEntries state)
                    state.Bridge

            Ok
                { state with
                    Opening =
                        Some
                            { AssignmentText = assignment
                              AuthoritativeRequirements = requirements
                              ConstitutiveBody = "" }
                    Bridge = nextBridge }

    /// Provenance generation: `g:N/...` after HOST-006 reanchor; legacy `turn:N/part:M` → 0.
    let provenanceGeneration (provenance: string) : int =
        if provenance.StartsWith("g:") then
            let rest = provenance.Substring(2)
            let slash = rest.IndexOf('/')
            let token = if slash < 0 then rest else rest.Substring(0, slash)
            parseGeneration token
        else
            0

    /// COMPANION-003 / HOST-005: append one semantic part reference. Strictly
    /// monotonic; a duplicate cursor or a retreat is refused (PERSIST-010).
    let applyPart
        (cursorSequence: int64)
        (role: string)
        (provenance: string)
        (turn: int)
        (partIndex: int)
        (kind: string)
        (toolName: string option)
        (providerRun: ProviderRunIdentity option)
        (toolCallId: ToolCallId option)
        (hostToolPartId: HostToolPartId option)
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (state: XTraceProjectionState)
        : Result<XTraceProjectionState, XTraceFoldRejection> =
        let head = headSequence state

        if cursorSequence <= head then
            Error(XTraceFoldRejection.CursorNotAfterHead(head, cursorSequence))
        else
            let partRef: XTracePartRef =
                { Cursor = XTraceCursor.create cursorSequence
                  Provenance = provenance
                  Generation = provenanceGeneration provenance
                  Role = role
                  Turn = turn
                  PartIndex = partIndex
                  Kind = kind
                  ToolName = toolName
                  ProviderRun = providerRun
                  ToolCallId = toolCallId
                  HostToolPartId = hostToolPartId
                  TextRef = textRef
                  TextDigest = textDigest }

            let nextBridge = appendPartRefToBridge partRef state.Bridge

            Ok
                { state with
                    Parts = partRef :: state.Parts
                    Bridge = nextBridge }

    /// COMPANION-003 / EXEC-009: capture one terminal occurrence. Idempotency is
    /// scoped to ProviderRun, not terminal bytes: two reused work units may
    /// legitimately return identical prose and still need distinct bounded
    /// completion evidence.
    let applyTerminal
        (textRef: BlobRef)
        (textDigest: BlobDigest)
        (providerRun: ProviderRunIdentity)
        (state: XTraceProjectionState)
        : Result<XTraceProjectionState, XTraceFoldRejection> =
        match terminalForProviderRun providerRun state with
        | Some existing when existing.TextRef = textRef && existing.TextDigest = textDigest -> Ok state
        | Some _ -> Error XTraceFoldRejection.TerminalAlreadyCaptured
        | None ->
            let terminalRef: XTraceTerminalRef =
                { TextRef = textRef
                  TextDigest = textDigest
                  ProviderRun = providerRun
                  Frontier = XTraceCursor.create (head state) }

            let nextBridge = appendTerminalRefToBridge terminalRef state.Bridge

            Ok
                { state with
                    Terminals = terminalRef :: state.Terminals
                    Bridge = nextBridge }

    let traceHeadCut (state: XTraceProjectionState) : XTraceCut =
        WanxiangliTrace.headCutOfBridge state.Bridge

    let traceCutAt (cursor: XTraceCursor) (state: XTraceProjectionState) : XTraceCut =
        WanxiangliTrace.cutAtSequenceOfBridge (XTraceCursor.sequence cursor) state.Bridge

    let traceIntervalOfRange (range: XTraceRange) (state: XTraceProjectionState) : XTraceCut * XTraceCut =
        WanxiangliTrace.intervalOfRangeOnBridge range state.Bridge

    let traceIntervalAfterCoverage (coverage: RecordCoverage) (state: XTraceProjectionState) : XTraceCut * XTraceCut =
        WanxiangliTrace.intervalAfterCoverageOnBridge coverage state.Bridge

    let traceRecords (state: XTraceProjectionState) : string list =
        WanxiangliTrace.recordsOldestFirst state.Bridge

    /// Host turn indices restart per reanchor generation; XTrace Sequence does not.
    /// Turn/Part labels are only comparable within one generation.
    let internal currentGenerationParts (parts: XTracePartRef list) : XTracePartRef list =
        match parts with
        | [] -> []
        | first :: rest -> collectCurrentGeneration first.Generation [ first ] rest

    /// Oldest-first copied views from the latest reanchor generation only.
    let currentGenerationSemanticParts (state: XTraceProjectionState) : XTraceSemanticPartView list =
        state |> parts |> currentGenerationParts |> List.map semanticPartView

    let providerRunParts
        (providerRun: ProviderRunIdentity)
        (state: XTraceProjectionState)
        : XTraceSemanticPartView list =
        parts state
        |> List.filter (fun part -> part.ProviderRun = Some providerRun)
        |> List.map semanticPartView

    let toolResultParts
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        (state: XTraceProjectionState)
        : XTraceSemanticPartView list =
        parts state
        |> List.filter (fun part ->
            part.Kind = "tool_result"
            && part.ProviderRun = Some providerRun
            && part.ToolCallId = Some toolCallId)
        |> List.map semanticPartView

    let toolPartsForHostIdentity
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        (hostToolPartId: HostToolPartId)
        (state: XTraceProjectionState)
        : XTraceSemanticPartView list =
        parts state
        |> List.filter (fun part ->
            part.ProviderRun = Some providerRun
            && part.ToolCallId = Some toolCallId
            && part.HostToolPartId = Some hostToolPartId)
        |> List.map semanticPartView

    /// Stable Host message identity encoded by the capture provenance.
    /// Positional legacy provenance intentionally returns None: callers may use a
    /// bounded legacy fallback, but new prefix proof/writeback must not mistake a
    /// request-local array index for historical identity.
    let private nonBlankHostMessageId (messageId: string) : string option =
        if System.String.IsNullOrWhiteSpace messageId then
            None
        else
            Some messageId

    let private hostMessageIdFrom (part: XTracePartRef) (idStart: int) : string option =
        let slash = part.Provenance.IndexOf('/', idStart)
        let idEnd = if slash < 0 then part.Provenance.Length else slash
        let length = idEnd - idStart

        if length <= 0 then
            None
        else
            part.Provenance.Substring(idStart, length) |> nonBlankHostMessageId

    let internal tryHostMessageId (part: XTracePartRef) : string option =
        let marker = "/msg:"
        let start = part.Provenance.IndexOf(marker, System.StringComparison.Ordinal)

        if start < 0 then
            None
        else
            hostMessageIdFrom part (start + marker.Length)

    let tryHostMessageIdAt (cursor: XTraceCursor) (state: XTraceProjectionState) : string option =
        parts state
        |> List.tryFind (fun part -> part.Cursor = cursor)
        |> Option.bind tryHostMessageId

    let hostMessageFirstCursors (state: XTraceProjectionState) : Map<string, int64> =
        parts state
        |> List.fold
            (fun cursors part ->
                match tryHostMessageId part with
                | Some messageId when not (Map.containsKey messageId cursors) ->
                    Map.add messageId (XTraceCursor.sequence part.Cursor) cursors
                | _ -> cursors)
            Map.empty

    let partsForHostMessageIds (messageIds: Set<string>) (state: XTraceProjectionState) : XTraceSemanticPartView list =
        parts state
        |> List.filter (fun part -> tryHostMessageId part |> Option.exists messageIds.Contains)
        |> List.map semanticPartView

    let tryContiguousHostRange (messageIds: Set<string>) (state: XTraceProjectionState) : XTraceRange option =
        let matched =
            parts state
            |> List.filter (fun part -> tryHostMessageId part |> Option.exists messageIds.Contains)

        let rec contiguous (previous: int64) (remaining: XTracePartRef list) =
            match remaining with
            | [] -> true
            | part :: tail when XTraceCursor.sequence part.Cursor = previous + 1L ->
                contiguous (XTraceCursor.sequence part.Cursor) tail
            | _ -> false

        let observedIds = matched |> List.choose tryHostMessageId |> Set.ofList
        let exactRequestedIds = not (Set.isEmpty messageIds) && observedIds = messageIds

        match matched, exactRequestedIds with
        | first :: tail, true when contiguous (XTraceCursor.sequence first.Cursor) tail ->
            let last = List.last matched
            Some(XTraceRange.create first.Cursor (XTraceCursor.nextCursor last.Cursor))
        | _ -> None

    /// Canonical semantic turn occupied by one physical Host message in the
    /// current reanchor generation.
    let tryTurnOfHostMessageId (messageId: string) (state: XTraceProjectionState) : int option =
        state
        |> parts
        |> currentGenerationParts
        |> List.tryPick (fun part ->
            match tryHostMessageId part with
            | Some stable when stable = messageId -> Some part.Turn
            | _ -> None)

    /// context-compression-028: the turn a committed tool call started in.
    ///
    /// `Bi` is the start of `Ai`'s own complete semantic turn, so the answer must be
    /// read from the CURRENT generation: a phase committed in a voided numbering
    /// cannot name a boundary the prefix may fold at. `None` therefore means "this
    /// phase has no addressable turn here", which the window treats as no desire.
    let tryTurnOfToolCallId (callId: ToolCallId) (state: XTraceProjectionState) : int option =
        state
        |> parts
        |> currentGenerationParts
        |> List.tryPick (fun part ->
            match part.ToolCallId with
            | Some owned when owned = callId -> Some part.Turn
            | _ -> None)

    /// context-compression-028/029: the turn of the newest captured message in the
    /// current generation — the message this request answers.
    ///
    /// A loop's later provider steps answer their own assistant/tool history, so this
    /// is NOT the trailing user message: one user message can drive many provider
    /// steps, and bounding a fold by the message that opened the loop would forbid
    /// every fold inside it. A candidate cutoff may not pass this turn, or the fold
    /// would replace the very message being answered.
    let frontierTurn (state: XTraceProjectionState) : int option =
        state
        |> parts
        |> currentGenerationParts
        |> List.tryLast
        |> Option.map (fun part -> part.Turn)

    /// The first stable user message in the current Host generation is the raw
    /// session Opening. Same-session prefix replacement never removes it from
    /// the trace (semantic-trace-010); what the provider is shown is trimmed
    /// only by the model's own ANCHOR `view` declaration (context-compression-031).
    let tryOpeningHostMessageId (state: XTraceProjectionState) : string option =
        state
        |> parts
        |> currentGenerationParts
        |> List.tryPick (fun part ->
            if part.Role.Equals("user", System.StringComparison.OrdinalIgnoreCase) then
                tryHostMessageId part
            else
                None)

    /// Stable physical messages whose canonical X semantic turn is strictly
    /// before cutoff. Distinct preserves encounter order, so prefix writeback can
    /// delete exactly the historical rows proved by XTrace without moving an
    /// unrelated request-local presentation row across the boundary.
    let hostMessageIdsBeforeTurn (cutoffExclusive: int) (state: XTraceProjectionState) : string list =
        state
        |> parts
        |> currentGenerationParts
        |> List.filter (fun part -> part.Turn < cutoffExclusive)
        |> List.choose tryHostMessageId
        |> List.distinct

    /// Map an ingest cursor (sequence of the last COVERED part) to the semantic
    /// cursor of the first UNCOVERED part. `>` not `>=`: the coverage sequence
    /// names a part already consumed, so the delta starts strictly after it.
    /// A sequence past the head means "nothing left" (one turn past the last part).
    ///
    /// After HOST-006 reanchor, Host renumbering voids old turn indices. Resolve the
    /// cursor against the current generation only so nextChunk / lastCoveredSequence
    /// share one numbering. Pre-reanchor sequences still advance coverage via
    /// absolute Sequence, but their Turn labels are never mixed with post-reanchor ones.
    let internal semanticCursorFor (sequence: int64) (state: XTraceProjectionState) : XTraceCursor =
        let searchable =
            let ordered = parts state
            let current = currentGenerationParts ordered

            if List.isEmpty current then ordered else current

        match
            searchable
            |> List.tryFind (fun part -> XTraceCursor.sequence part.Cursor > sequence)
        with
        | Some part -> part.Cursor
        | None -> cursorAfterSearch searchable

    let semanticCursorAfter (cursor: XTraceCursor) (state: XTraceProjectionState) : XTraceCursor =
        semanticCursorFor (XTraceCursor.sequence cursor) state

    let semanticCursorAfterCoverage (coverage: RecordCoverage) (state: XTraceProjectionState) : XTraceCursor =
        semanticCursorAfter (RecordCoverage.ingestedThrough coverage) state
