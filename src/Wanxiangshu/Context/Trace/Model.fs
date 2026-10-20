namespace Wanxiangshu.Context.Trace

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection

/// 一个 XTrace 语义事实。`Provenance` 保存 session/run/message/part 归属供
/// 证明与恢复使用，renderer 永不输出它（HOST-005）。
type XTraceItem =
    { Cursor: XTraceCursor
      Provenance: string
      Role: string
      Part: SemanticPart }

[<RequireQualifiedAccess>]
module internal WanxiangliTrace =

    [<Literal>]
    let MaxPartChunkBytes = 524288

    [<Import("Trace", "wanxiangli/trace")>]
    let private traceCtor: obj = jsNative

    [<Emit("new $0($1, $2)")>]
    let private createTraceWithRecords (_ctor: obj) (_traceId: string) (_records: string array) : obj = jsNative

    [<Emit("new $0($1)")>]
    let private createEmptyTrace (_ctor: obj) (_traceId: string) : obj = jsNative

    // DSL-MUTABLE: resource — 私有缓存只创建一次。
    // 模块值上的 Emit 属性会在每个引用点展开，不能用它声明有身份的状态。
    let private traceCache: obj =
        emitJsExpr
            ()
            "(() => { const wm = new WeakMap(); return { attach: (b, t) => { wm.set(b, t); return b; }, take: (b, c) => { const t = wm.get(b); if (t && t.inspect().cursor === c) { wm.delete(b); return t; } return null; }, peek: (b, c) => { const t = wm.get(b); return (t && t.inspect().cursor === c) ? t : null; } }; })()"

    [<Emit("$0.attach($1, $2)")>]
    let private attachCachedTrace (_cache: obj) (_bridge: 'a) (_trace: obj) : 'a = jsNative

    [<Emit("$0.take($1, $2)")>]
    let private takeCachedTrace (_cache: obj) (_bridge: 'a) (_expectedCursor: int) : obj = jsNative

    [<Emit("$0.peek($1, $2)")>]
    let private peekCachedTrace (_cache: obj) (_bridge: 'a) (_expectedCursor: int) : obj = jsNative

    [<Emit("new TextEncoder().encode($0)")>]
    let private utf8ToBytes (_value: string) : obj = jsNative

    [<Emit("new Uint8Array(0)")>]
    let private emptyBytes () : obj = jsNative

    [<Emit("$0.byteLength")>]
    let private bytesLength (_bytes: obj) : int = jsNative

    [<Emit("$0.slice($1, $2)")>]
    let private sliceBytes (_bytes: obj) (_startIdx: int) (_endIdx: int) : obj = jsNative

    [<Emit("Buffer.concat($0.map(c => Buffer.from(c))).toString('utf8')")>]
    let private concatBytesToUtf8 (_chunks: obj array) : string = jsNative

    [<Emit("$0.capture($1)")>]
    let private captureObs (_trace: obj) (_observation: obj) : obj = jsNative

    [<Emit("$0.head()")>]
    let private rawHead (_trace: obj) : obj = jsNative

    [<Emit("$0.cut($1)")>]
    let private rawCut (_trace: obj) (_cursor: int) : obj = jsNative

    [<Emit("$0.records()")>]
    let private rawRecords (_trace: obj) : string array = jsNative

    [<Emit("$0.inspect().cursor")>]
    let private rawInspectCursor (_trace: obj) : int = jsNative

    [<Emit("$0.read($1, $2)")>]
    let private rawRead (_trace: obj) (_afterCut: obj) (_throughCut: obj) : obj array = jsNative

    [<Emit("$0.materialize($1, $2)")>]
    let private rawMaterialize (_trace: obj) (_afterCut: obj) (_throughCut: obj) : obj = jsNative

    [<Emit("$0[$1]")>]
    let private getField (_target: obj) (_name: string) : obj = jsNative

    type ProjectionBridge =
        {
            TraceId: string
            /// Trace record lines, stored NEWEST-first so each append is an O(1)
            /// prepend. `recordsOldestFirst` reverses to chronological order for
            /// the wanxiangli constructor and the `traceRecords` surface.
            Records: string list
            OpeningCutCursor: int
            /// (sequence, cutCursor) pairs, stored newest-first.
            PartCuts: (int64 * int) list
            RecordCount: int
        }

    /// Oldest-first record lines for the trace constructor / inspection.
    let recordsOldestFirst (bridge: ProjectionBridge) : string list =
        // `Records` is stored newest-first (see append sites); reverse to the
        // chronological order the wanxiangli constructor and inspect surface need.
        List.rev bridge.Records

    let private instantiateTrace (traceId: string) (records: string list) : obj =
        if List.isEmpty records then
            createEmptyTrace traceCtor traceId
        else
            createTraceWithRecords traceCtor traceId (List.toArray records)

    let private acquireWritableTrace (bridge: ProjectionBridge) : obj =
        let cached = takeCachedTrace traceCache bridge bridge.RecordCount

        if not (isNull cached) then
            cached
        else
            instantiateTrace bridge.TraceId (recordsOldestFirst bridge)

    let private acquireReadableTrace (bridge: ProjectionBridge) : obj =
        let cached = peekCachedTrace traceCache bridge bridge.RecordCount

        if not (isNull cached) then
            cached
        else
            let created = instantiateTrace bridge.TraceId (recordsOldestFirst bridge)
            attachCachedTrace traceCache bridge created |> ignore
            created

    let private cutFromJs (raw: obj) : XTraceCut =
        { TraceId = string (getField raw "traceId")
          Cursor = unbox<int> (getField raw "cursor")
          Digest = string (getField raw "digest") }

    let private cutToJs (cut: XTraceCut) : obj =
        createObj
            [ "traceId", box cut.TraceId
              "cursor", box cut.Cursor
              "digest", box cut.Digest ]

    let private chunkBytes (raw: string) : obj array =
        let encoded = utf8ToBytes raw
        let total = bytesLength encoded

        if total = 0 then
            [| emptyBytes () |]
        else
            let count = (total + MaxPartChunkBytes - 1) / MaxPartChunkBytes

            Array.init count (fun index ->
                let startOffset = index * MaxPartChunkBytes
                let endOffset = min total (startOffset + MaxPartChunkBytes)
                sliceBytes encoded startOffset endOffset)

    let private boundedOpeningBytes (raw: string) : obj =
        let encoded = utf8ToBytes raw
        let total = bytesLength encoded

        if total <= MaxPartChunkBytes then
            encoded
        else
            sliceBytes encoded 0 MaxPartChunkBytes

    let private obsOpening (raw: string) : obj =
        createObj [ "type", box "Opening"; "bytes", boundedOpeningBytes raw ]

    let private obsRunStarted (runId: string) : obj =
        createObj [ "type", box "RunStarted"; "runId", box runId ]

    let private obsMessageObserved (messageId: string) (role: string) (origin: string) (runIdOpt: string option) : obj =
        let runField =
            match runIdOpt with
            | Some id -> box id
            | None -> null

        createObj
            [ "type", box "MessageObserved"
              "messageId", box messageId
              "role", box role
              "origin", box origin
              "runId", runField ]

    let private obsPartConfirmed (messageId: string) (partId: string) (index: int) (kind: string) (bytes: obj) : obj =
        createObj
            [ "type", box "PartConfirmed"
              "messageId", box messageId
              "partId", box partId
              "index", box index
              "kind", box kind
              "bytes", bytes ]

    let private obsMessageFinished (messageId: string) (partCount: int) : obj =
        createObj
            [ "type", box "MessageFinished"
              "messageId", box messageId
              "partCount", box partCount ]

    let private obsRunFinished (runId: string) (outcome: string) : obj =
        createObj [ "type", box "RunFinished"; "runId", box runId; "outcome", box outcome ]

    let emptyBridge (traceId: string) : ProjectionBridge =
        let trace = createEmptyTrace traceCtor traceId

        let bridge =
            { TraceId = traceId
              Records = []
              OpeningCutCursor = 0
              PartCuts = []
              RecordCount = 0 }

        attachCachedTrace traceCache bridge trace

    let private openingEnvelope (assignment: string) (requirements: string list) : string =
        let reqJoined = String.concat "\n" requirements
        sprintf "assignment:%d:%s\n%s" assignment.Length assignment reqJoined

    let private appendOpeningToEmptyOrReplayedBridge (payload: string) (bridge: ProjectionBridge) : ProjectionBridge =
        let trace = acquireWritableTrace bridge
        let result = captureObs trace (obsOpening payload)
        let record = string (getField result "record")
        let cursor = rawInspectCursor trace

        let nextRecords =
            if bridge.RecordCount = 0 then
                [ record ]
            else
                bridge.Records

        let updated =
            { bridge with
                Records = nextRecords
                OpeningCutCursor = cursor
                RecordCount = cursor }

        attachCachedTrace traceCache updated trace

    let appendOpeningToBridge
        (assignment: string)
        (requirements: string list)
        (hasExistingEntries: bool)
        (rebuildWithOpening: ProjectionBridge -> ProjectionBridge)
        (bridge: ProjectionBridge)
        : ProjectionBridge =
        let payload = openingEnvelope assignment requirements

        if hasExistingEntries then
            let freshTrace = createEmptyTrace traceCtor bridge.TraceId
            let result = captureObs freshTrace (obsOpening payload)
            let record = string (getField result "record")
            let cursor = rawInspectCursor freshTrace

            let seeded =
                { TraceId = bridge.TraceId
                  Records = [ record ]
                  OpeningCutCursor = cursor
                  PartCuts = []
                  RecordCount = cursor }

            let attached = attachCachedTrace traceCache seeded freshTrace
            rebuildWithOpening attached
        else
            appendOpeningToEmptyOrReplayedBridge payload bridge

    let private wireRoleAndOrigin (role: string) (kind: string) (runId: string) : string * string * string option =
        match kind, role with
        | "tool_call", _ -> "assistant", "provider", Some runId
        | "tool-call", _ -> "assistant", "provider", Some runId
        | "tool_result", _ -> "tool", "provider", Some runId
        | "tool-result", _ -> "tool", "provider", Some runId
        | _, "user" -> "user", "human", None
        | _, "system" -> "system", "host-internal", None
        | _ -> "assistant", "provider", Some runId

    let private wirePartKind (kind: string) : string =
        match kind with
        | "text" -> "text"
        | "tool_call"
        | "tool-call" -> "tool-call"
        | "tool_result"
        | "tool-result" -> "tool-result"
        | _ -> "binary"

    let private captureObsRecord (trace: obj) (obs: obj) : string =
        let res = captureObs trace obs
        string (getField res "record")

    let private captureChunkedMessage
        (trace: obj)
        (messageId: string)
        (role: string)
        (origin: string)
        (runIdOpt: string option)
        (partIdPrefix: string)
        (wireKind: string)
        (payload: string)
        : string list =
        let started =
            match runIdOpt with
            | Some runId -> [ captureObsRecord trace (obsRunStarted runId) ]
            | None -> []

        let observed =
            captureObsRecord trace (obsMessageObserved messageId role origin runIdOpt)

        let chunks = chunkBytes payload

        let partRecords =
            chunks
            |> Array.mapi (fun index chunk ->
                let partId = sprintf "%s/c:%d" partIdPrefix index
                captureObsRecord trace (obsPartConfirmed messageId partId index wireKind chunk))
            |> Array.toList

        let finished = captureObsRecord trace (obsMessageFinished messageId chunks.Length)

        let runEnded =
            match runIdOpt with
            | Some runId -> [ captureObsRecord trace (obsRunFinished runId "Completed") ]
            | None -> []

        started @ (observed :: partRecords) @ (finished :: runEnded)

    let appendPartDescriptorToBridge
        (cursorSequence: int64)
        (role: string)
        (provenance: string)
        (kind: string)
        (toolName: string option)
        (textRef: string)
        (textDigest: string)
        (bridge: ProjectionBridge)
        : ProjectionBridge =
        let trace = acquireWritableTrace bridge
        let runId = sprintf "xrun-%d" cursorSequence
        let messageId = sprintf "xmsg-%d" cursorSequence
        let wireRole, wireOrigin, wireRunOpt = wireRoleAndOrigin role kind runId
        let mappedKind = wirePartKind kind
        let toolLabel = Option.defaultValue "" toolName

        let payload =
            sprintf "%s|%s|%s|%s|%s|%s" role provenance kind toolLabel textRef textDigest

        let partPrefix = sprintf "p:%d" cursorSequence

        let addedRecords =
            captureChunkedMessage trace messageId wireRole wireOrigin wireRunOpt partPrefix mappedKind payload

        let afterCursor = rawInspectCursor trace

        let updated =
            { bridge with
                Records = List.fold (fun records record -> record :: records) bridge.Records addedRecords
                PartCuts = (cursorSequence, afterCursor) :: bridge.PartCuts
                RecordCount = afterCursor }

        attachCachedTrace traceCache updated trace

    let appendTerminalDescriptorToBridge
        (providerRun: string)
        (textRef: string)
        (textDigest: string)
        (frontierSequence: int64)
        (bridge: ProjectionBridge)
        : ProjectionBridge =
        let trace = acquireWritableTrace bridge
        let termOrd = bridge.RecordCount + 1
        let runId = sprintf "xterm-run-%d" termOrd
        let messageId = sprintf "xterm-msg-%d" termOrd

        let payload =
            sprintf "terminal|%s|%s|%s|%d" providerRun textRef textDigest frontierSequence

        let addedRecords =
            captureChunkedMessage trace messageId "assistant" "provider" (Some runId) "term" "binary" payload

        let afterCursor = rawInspectCursor trace

        let updated =
            { bridge with
                Records = List.fold (fun records record -> record :: records) bridge.Records addedRecords
                RecordCount = afterCursor }

        attachCachedTrace traceCache updated trace

    let headCutOfBridge (bridge: ProjectionBridge) : XTraceCut =
        let trace = acquireReadableTrace bridge
        cutFromJs (rawHead trace)

    let private anchorCutCursorBeforeSequence (sequence: int64) (bridge: ProjectionBridge) : int =
        bridge.PartCuts
        |> List.tryFind (fun (seq, _) -> seq < sequence)
        |> Option.map snd
        |> Option.defaultValue bridge.OpeningCutCursor

    let cutAtSequenceOfBridge (sequence: int64) (bridge: ProjectionBridge) : XTraceCut =
        let trace = acquireReadableTrace bridge

        let targetCursor =
            if sequence <= 0L then
                bridge.OpeningCutCursor
            else
                bridge.PartCuts
                |> List.tryFind (fun (seq, _) -> seq <= sequence)
                |> Option.map snd
                |> Option.defaultValue bridge.OpeningCutCursor

        cutFromJs (rawCut trace targetCursor)

    let private intervalCursorsForRange (range: XTraceRange) (bridge: ProjectionBridge) : int * int =
        let startSeq = range |> XTraceRange.startInclusive |> XTraceCursor.sequence
        let endSeq = range |> XTraceRange.endExclusive |> XTraceCursor.sequence

        anchorCutCursorBeforeSequence startSeq bridge, anchorCutCursorBeforeSequence endSeq bridge

    let intervalOfRangeOnBridge (range: XTraceRange) (bridge: ProjectionBridge) : XTraceCut * XTraceCut =
        let trace = acquireReadableTrace bridge
        let afterCursor, throughCursor = intervalCursorsForRange range bridge
        cutFromJs (rawCut trace afterCursor), cutFromJs (rawCut trace throughCursor)

    let intervalAfterCoverageOnBridge (coverage: RecordCoverage) (bridge: ProjectionBridge) : XTraceCut * XTraceCut =
        let trace = acquireReadableTrace bridge

        let ingestedSeq =
            coverage |> RecordCoverage.ingestedThrough |> XTraceCursor.sequence

        let afterCut = cutAtSequenceOfBridge ingestedSeq bridge

        let throughCursor =
            bridge.PartCuts
            |> List.tryHead
            |> Option.map snd
            |> Option.defaultValue bridge.OpeningCutCursor

        let throughCut = cutFromJs (rawCut trace throughCursor)

        let effectiveAfter =
            if afterCut.Cursor > throughCut.Cursor then
                throughCut
            else
                afterCut

        effectiveAfter, throughCut

    let verifyAndReadIntervalOnBridge (range: XTraceRange) (bridge: ProjectionBridge) : obj array =
        let trace = acquireReadableTrace bridge
        let afterCut, throughCut = intervalOfRangeOnBridge range bridge
        rawRead trace (cutToJs afterCut) (cutToJs throughCut)

    let private encodeItemPayload (part: SemanticPart) : string * string * string =
        match part with
        | SemanticText text -> "text", "text", text
        | SemanticReasoning text -> "binary", "reasoning", text
        | SemanticToolCall(name, args) -> "tool-call", "tool_call", sprintf "%d:%s\n%s" name.Length name args
        | SemanticToolResult result -> "tool-result", "tool_result", result
        | SemanticMedia(Some mediaType, digest) ->
            "binary", "media", sprintf "1:%d:%s\n%s" mediaType.Length mediaType digest
        | SemanticMedia(None, digest) -> "binary", "media", sprintf "0:0:\n%s" digest

    let private decodeToolCallPayload (raw: string) : SemanticPart =
        let colon = raw.IndexOf(':')
        let len = Int32.Parse(raw.Substring(0, colon))
        let name = raw.Substring(colon + 1, len)
        let args = raw.Substring(colon + 1 + len + 1)
        SemanticToolCall(name, args)

    let private decodeMediaPayload (raw: string) : SemanticPart =
        let firstColon = raw.IndexOf(':')
        let hasMedia = raw.Substring(0, firstColon) = "1"
        let rest = raw.Substring(firstColon + 1)
        let secondColon = rest.IndexOf(':')
        let len = Int32.Parse(rest.Substring(0, secondColon))
        let mediaType = rest.Substring(secondColon + 1, len)
        let digest = rest.Substring(secondColon + 1 + len + 1)
        SemanticMedia((if hasMedia then Some mediaType else None), digest)

    let private decodeItemPart (tag: string) (raw: string) : SemanticPart =
        match tag with
        | "text" -> SemanticText raw
        | "reasoning" -> SemanticReasoning raw
        | "tool_call" -> decodeToolCallPayload raw
        | "tool_result" -> SemanticToolResult raw
        | "media" -> decodeMediaPayload raw
        | _ -> SemanticText raw

    let private parsePartTag (partId: string) : string =
        if partId.StartsWith("k:", StringComparison.Ordinal) then
            let slash = partId.IndexOf('/')
            if slash > 2 then partId.Substring(2, slash - 2) else "text"
        else
            "text"

    let private parseMessageOrdinal (messageId: string) : int option =
        let parsed, value =
            if messageId.StartsWith("xmsg-", StringComparison.Ordinal) then
                Int32.TryParse(messageId.Substring(5))
            else
                false, 0

        if parsed then Some value else None

    let private firstPartTag (sortedParts: obj array) : string =
        if sortedParts.Length = 0 then
            "text"
        else
            parsePartTag (string (getField sortedParts.[0] "partId"))

    let private reconstructOrdinalItem (original: XTraceItem) (messageView: obj) : XTraceItem =
        let rawParts = unbox<obj array> (getField messageView "parts")

        let sortedParts =
            rawParts |> Array.sortBy (fun part -> unbox<int> (getField part "index"))

        let chunks =
            sortedParts
            |> Array.map (fun part -> getField (getField part "content") "bytes")

        let fullText = concatBytesToUtf8 chunks
        let tag = firstPartTag sortedParts

        { Cursor = original.Cursor
          Provenance = original.Provenance
          Role = original.Role
          Part = decodeItemPart tag fullText }

    let private reconstructMessageItem (itemsByOrdinal: XTraceItem array) (messageView: obj) : XTraceItem option =
        let messageId = string (getField messageView "messageId")

        parseMessageOrdinal messageId
        |> Option.bind (fun ordinal ->
            if ordinal < 0 || ordinal >= itemsByOrdinal.Length then
                None
            else
                Some(reconstructOrdinalItem itemsByOrdinal.[ordinal] messageView))

    let private populateTraceFromItems (traceId: string) (openingText: string option) (items: XTraceItem list) =
        let trace = createEmptyTrace traceCtor traceId

        let openingRecords, openingCutCursor =
            match openingText with
            | Some text ->
                let recStr = captureObsRecord trace (obsOpening text)
                [ recStr ], rawInspectCursor trace
            | None -> [], 0

        let itemsArray = List.toArray items

        let partPairs =
            itemsArray
            |> Array.mapi (fun ordinal item ->
                let seq = XTraceCursor.sequence item.Cursor
                let wireKind, tag, payload = encodeItemPayload item.Part
                let runId = sprintf "xrun-%d" ordinal
                let messageId = sprintf "xmsg-%d" ordinal
                let wireRole, wireOrigin, wireRunOpt = wireRoleAndOrigin item.Role tag runId
                let prefix = sprintf "k:%s" tag

                let added =
                    captureChunkedMessage trace messageId wireRole wireOrigin wireRunOpt prefix wireKind payload

                added, (seq, rawInspectCursor trace))

        let allRecords =
            partPairs
            |> Array.fold
                (fun records (added, _) -> List.fold (fun acc record -> record :: acc) records added)
                (List.rev openingRecords)

        let partCuts = partPairs |> Array.fold (fun cuts (_, cut) -> cut :: cuts) []
        let finalCursor = rawInspectCursor trace

        let bridge =
            { TraceId = traceId
              Records = allRecords
              OpeningCutCursor = openingCutCursor
              PartCuts = partCuts
              RecordCount = finalCursor }

        let attached = attachCachedTrace traceCache bridge trace
        attached, trace, itemsArray

    let materializeItemsInRange (range: XTraceRange) (items: XTraceItem list) : XTraceItem list =
        if List.isEmpty items || XTraceRange.isEmpty range then
            []
        else
            let bridge, trace, itemsArray = populateTraceFromItems "xtrace-items" None items
            let afterCut, throughCut = intervalOfRangeOnBridge range bridge
            let window = rawMaterialize trace (cutToJs afterCut) (cutToJs throughCut)
            let messageViews = unbox<obj array> (getField window "messages")

            messageViews
            |> Array.choose (reconstructMessageItem itemsArray)
            |> Array.filter (fun item -> XTraceRange.contains item.Cursor range)
            |> Array.toList

    let inspectItemsBridge
        (traceId: string)
        (openingText: string option)
        (range: XTraceRange)
        (items: XTraceItem list)
        : obj =
        let bridge, trace, itemsArray = populateTraceFromItems traceId openingText items
        let afterCut, throughCut = intervalOfRangeOnBridge range bridge
        let window = rawMaterialize trace (cutToJs afterCut) (cutToJs throughCut)
        let messageViews = unbox<obj array> (getField window "messages")

        let materialized =
            messageViews
            |> Array.choose (reconstructMessageItem itemsArray)
            |> Array.filter (fun item -> XTraceRange.contains item.Cursor range)

        createObj
            [ "traceId", box bridge.TraceId
              "head", cutToJs (headCutOfBridge bridge)
              "after", cutToJs afterCut
              "through", cutToJs throughCut
              "records", box (recordsOldestFirst bridge |> List.toArray)
              "window", window
              "materializedCount", box materialized.Length ]

[<RequireQualifiedAccess>]
module XTrace =

    /// `cursor >= start` 且 `cursor < endExclusive` 的 items。
    /// 经由组合层 C 的 WanxiangliTrace 执行 (after, through] 固定区间切片与字节无损还原。
    let sliceBetween (start: XTraceCursor) (endExclusive: XTraceCursor) (items: XTraceItem list) =
        let range = XTraceRange.create start endExclusive
        WanxiangliTrace.materializeItemsInRange range items

    /// 当前 head：最后一条 item 之后的位置。空轨迹为 origin。
    let head (items: XTraceItem list) : XTraceCursor =
        match items |> List.tryLast with
        | Some item -> XTraceCursor.nextCursor item.Cursor
        | None -> XTraceCursor.originCursor

    /// `cursor >= start` 的全部 items（到 XTrace head）。
    let sliceFrom (start: XTraceCursor) (items: XTraceItem list) =
        let traceHead = head items

        if XTraceCursor.isAfter start traceHead then
            []
        else
            sliceBetween start traceHead items

    /// 从 SemanticMessage 平铺为带 role 的语义 part 序列。
    ///
    /// XTrace 是语义投影、覆盖区间与 terminal capture 的共同唯一 source
    /// （COMPANION-007、COMPANION-012）。同一 segment 的语义解析不得分叉。
    let flatten (messages: SemanticMessage list) : {| Role: string; Part: SemanticPart |} list =
        messages
        |> List.collect (fun message -> message.Parts |> List.map (fun part -> {| Role = message.Role; Part = part |}))

    /// semantic-trace-010 Opening 区间投影：区间本身已是 OpeningMaterial，
    /// 不过滤 raw tool call/result；Opening 边界由 trace 自身捕获的 Opening
    /// 决定，不由 checkpoint 决定。
    let forOpening (items: XTraceItem list) : XTraceItem list = items

    let private rolePrefix (role: string) =
        match role with
        | "user" -> "user"
        | "assistant" -> "assistant"
        | other -> other

    let private renderMedia mediaType =
        match mediaType with
        | Some mediaValue -> sprintf "[media omitted: %s]" mediaValue
        | None -> "[media omitted]"

    let private renderPart (part: SemanticPart) : string =
        match part with
        | SemanticText text -> text
        | SemanticReasoning text -> text
        | SemanticToolCall(name, args) -> sprintf "[tool call] %s %s" name args
        | SemanticToolResult result -> sprintf "[tool result] %s" result
        | SemanticMedia(mediaType, _digest) -> renderMedia mediaType

    /// 一个 item 的单行文本（role 只对 prompt 有意义；assistant 正文不带
    /// role 前缀，避免渲染文本中每行重复身份）。
    let renderItem (item: XTraceItem) : string =
        match item.Part with
        | SemanticText _ -> sprintf "%s: %s" (rolePrefix item.Role) (renderPart item.Part)
        | _ -> renderPart item.Part

    let render (items: XTraceItem list) : string =
        items |> List.map renderItem |> String.concat "\n"
