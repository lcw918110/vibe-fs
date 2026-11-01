namespace Wanxiangshu.Context.Trace

open System
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type XTraceProjectionState = private { History: WanxiangliTrace.History }

type internal XTracePartRef =
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

type internal XTraceTerminalRef =
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

type private HostPartMetadata =
    { Provenance: string
      Generation: int
      Turn: int
      PartIndex: int
      ProviderRun: ProviderRunIdentity option
      ToolCallId: ToolCallId option
      HostToolPartId: HostToolPartId option }

module XTraceProjection =

    let empty = { History = WanxiangliTrace.createHistory "xtrace-projection" }
    let internal history (state: XTraceProjectionState) = state.History

    let internal provenanceFromMetadata (metadata: obj) = (unbox<HostPartMetadata> metadata).Provenance

    let private descriptor role provenance kind toolName textRef textDigest =
        sprintf "%s|%s|%s|%s|%s|%s" role provenance kind (Option.defaultValue "" toolName) (BlobRef.value textRef) (BlobDigest.value textDigest)

    let private partFromNative (entry: obj) : XTracePartRef =
        let metadata = unbox<HostPartMetadata> entry?metadata
        let toolName: obj = entry?toolName

        { Cursor = XTraceCursor.create (unbox<int64> entry?sequence)
          Provenance = metadata.Provenance
          Generation = metadata.Generation
          Role = unbox<string> entry?role
          Turn = metadata.Turn
          PartIndex = metadata.PartIndex
          Kind = unbox<string> entry?kind
          ToolName = if isNull toolName then None else Some(unbox<string> toolName)
          ProviderRun = metadata.ProviderRun
          ToolCallId = metadata.ToolCallId
          HostToolPartId = metadata.HostToolPartId
          TextRef = BlobRef.create (unbox<string> entry?bodyRef)
          TextDigest = BlobDigest.create (unbox<string> entry?bodyDigest) }

    let internal nativePart (part: XTracePartRef) : obj =
        let metadata: HostPartMetadata =
            { Provenance = part.Provenance
              Generation = part.Generation
              Turn = part.Turn
              PartIndex = part.PartIndex
              ProviderRun = part.ProviderRun
              ToolCallId = part.ToolCallId
              HostToolPartId = part.HostToolPartId }

        createObj
            [ "sequence" ==> XTraceCursor.sequence part.Cursor
              "role" ==> part.Role
              "kind" ==> part.Kind
              "toolName" ==> (part.ToolName |> Option.map box |> Option.defaultValue null)
              "bodyRef" ==> BlobRef.value part.TextRef
              "bodyDigest" ==> BlobDigest.value part.TextDigest
              "descriptor" ==> descriptor part.Role part.Provenance part.Kind part.ToolName part.TextRef part.TextDigest
              "metadata" ==> metadata ]

    let private terminalFromNative (entry: obj) : XTraceTerminalRef =
        { TextRef = BlobRef.create (unbox<string> entry?bodyRef)
          TextDigest = BlobDigest.create (unbox<string> entry?bodyDigest)
          ProviderRun = ProviderRunIdentity.create (unbox<string> entry?providerRun)
          Frontier = XTraceCursor.create (unbox<int64> entry?frontier) }

    let private terminalOption (value: obj) =
        if isNull value then None else Some(terminalFromNative value)

    let internal parts (state: XTraceProjectionState) = state.History.entries () |> Array.map partFromNative |> Array.toList
    let internal partCount (state: XTraceProjectionState) = state.History.entries().Length
    let internal terminalCount (state: XTraceProjectionState) = state.History.terminals().Length
    let internal openingCaptured (state: XTraceProjectionState) = not (isNull (state.History.opening()))

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

    let openingEvidence (state: XTraceProjectionState) : XTraceOpeningEvidence option =
        let opening = state.History.opening ()

        if isNull opening then None
        else
            Some
                { AssignmentText = unbox<string> opening?assignment
                  AuthoritativeRequirements = unbox<string array> opening?requirements |> Array.toList
                  ConstitutiveBody = unbox<string> opening?constitutiveBody }

    let hasOpening (state: XTraceProjectionState) = openingCaptured state
    let hasSemanticParts (state: XTraceProjectionState option) = state |> Option.exists (fun trace -> partCount trace > 0)
    let orderedSemanticParts state = parts state |> List.map semanticPartView
    let internal headSequence (state: XTraceProjectionState) = state.History.headSequence ()

    let latestPartCursor (state: XTraceProjectionState) =
        let value = state.History.latestSequence ()
        if isNull value then None else Some(XTraceCursor.create (unbox<int64> value))

    let internal head (state: XTraceProjectionState) = state.History.head ()
    let headCursor state = XTraceCursor.create (head state)

    let rangeFrom (startInclusive: XTraceCursor) (state: XTraceProjectionState) =
        state.History.rangeFrom (XTraceCursor.sequence startInclusive) |> WanxiangliTrace.rangeFromNative

    let slice (range: XTraceRange) (state: XTraceProjectionState) =
        state.History.slice (WanxiangliTrace.rangeObject range)
        |> Array.map (partFromNative >> semanticPartView)
        |> Array.toList

    let frontierAfter (part: XTraceSemanticPartView) = XTraceCursor.nextCursor part.Cursor
    let rangeOfPart (part: XTraceSemanticPartView) = XTraceRange.create part.Cursor (frontierAfter part)
    let partKinds state = parts state |> List.map (fun part -> part.Kind)
    let internal latestTerminal (state: XTraceProjectionState) = state.History.latestTerminal () |> terminalOption
    let latestTerminalEvidence state = latestTerminal state |> Option.map terminalEvidence

    let internal terminalForProviderRun (providerRun: ProviderRunIdentity) (state: XTraceProjectionState) =
        state.History.terminalFor (ProviderRunIdentity.value providerRun) |> terminalOption

    let terminalEvidenceForProviderRun providerRun state = terminalForProviderRun providerRun state |> Option.map terminalEvidence

    let private appendResult (state: XTraceProjectionState) (result: obj) =
        if unbox<bool> result?ok then
            let next = unbox<WanxiangliTrace.History> result?value
            Ok(if obj.ReferenceEquals(state.History, next) then state else { History = next })
        else
            let error: obj = result?error
            match unbox<string> error?kind with
            | "OpeningAlreadyCaptured" -> Error XTraceFoldRejection.OpeningAlreadyCaptured
            | "CursorNotAfterHead" -> Error(XTraceFoldRejection.CursorNotAfterHead(unbox<int64> error?expected, unbox<int64> error?actual))
            | "TerminalAlreadyCaptured" -> Error XTraceFoldRejection.TerminalAlreadyCaptured
            | kind -> invalidOp ("wanxiangli/trace returned an unknown append rejection: " + kind)

    let applyOpening (assignment: string) (requirements: string list) (state: XTraceProjectionState) =
        createObj [ "assignment" ==> assignment; "requirements" ==> List.toArray requirements ]
        |> state.History.appendOpening
        |> appendResult state

    let private parseGeneration (token: string) =
        match Int32.TryParse token with
        | true, value when value >= 0 -> value
        | _ -> 0

    let provenanceGeneration (provenance: string) =
        if provenance.StartsWith("g:") then
            let rest = provenance.Substring(2)
            let slash = rest.IndexOf('/')
            parseGeneration (if slash < 0 then rest else rest.Substring(0, slash))
        else 0

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
        =
        let prepareMetadata =
            Func<obj>(fun () ->
                box
                    { Provenance = provenance
                      Generation = provenanceGeneration provenance
                      Turn = turn
                      PartIndex = partIndex
                      ProviderRun = providerRun
                      ToolCallId = toolCallId
                      HostToolPartId = hostToolPartId })

        let part =
            createObj
                [ "sequence" ==> cursorSequence
                  "role" ==> role
                  "kind" ==> kind
                  "toolName" ==> (toolName |> Option.map box |> Option.defaultValue null)
                  "bodyRef" ==> BlobRef.value textRef
                  "bodyDigest" ==> BlobDigest.value textDigest
                  "descriptor" ==> descriptor role provenance kind toolName textRef textDigest ]

        state.History.appendPart(part, prepareMetadata) |> appendResult state

    let applyTerminal (textRef: BlobRef) (textDigest: BlobDigest) (providerRun: ProviderRunIdentity) (state: XTraceProjectionState) =
        createObj
            [ "providerRun" ==> ProviderRunIdentity.value providerRun
              "bodyRef" ==> BlobRef.value textRef
              "bodyDigest" ==> BlobDigest.value textDigest ]
        |> state.History.appendTerminal
        |> appendResult state

    let traceHeadCut (state: XTraceProjectionState) = state.History.headCut () |> WanxiangliTrace.cutFromNative
    let traceCutAt (cursor: XTraceCursor) (state: XTraceProjectionState) = state.History.cutAt (XTraceCursor.sequence cursor) |> WanxiangliTrace.cutFromNative
    let traceIntervalOfRange (range: XTraceRange) (state: XTraceProjectionState) = state.History.interval (WanxiangliTrace.rangeObject range) |> WanxiangliTrace.intervalFromNative
    let traceIntervalAfterCoverage (coverage: RecordCoverage) (state: XTraceProjectionState) = state.History.afterCoverage (coverage |> RecordCoverage.ingestedThrough |> XTraceCursor.sequence) |> WanxiangliTrace.intervalFromNative
    let traceRecords (state: XTraceProjectionState) = state.History.records () |> Array.toList

    let rec private collectCurrentGeneration (maxGen: int) (acc: XTracePartRef list) (remaining: XTracePartRef list) =
        match remaining with
        | [] -> List.rev acc
        | part :: tail when part.Generation > maxGen -> collectCurrentGeneration part.Generation [ part ] tail
        | part :: tail when part.Generation = maxGen -> collectCurrentGeneration maxGen (part :: acc) tail
        | _ :: tail -> collectCurrentGeneration maxGen acc tail

    let internal currentGenerationParts (parts: XTracePartRef list) =
        match parts with
        | [] -> []
        | first :: rest -> collectCurrentGeneration first.Generation [ first ] rest

    let currentGenerationSemanticParts state = parts state |> currentGenerationParts |> List.map semanticPartView

    let providerRunParts (providerRun: ProviderRunIdentity) state =
        parts state |> List.filter (fun part -> part.ProviderRun = Some providerRun) |> List.map semanticPartView

    let toolResultParts (providerRun: ProviderRunIdentity) (toolCallId: ToolCallId) state =
        parts state
        |> List.filter (fun part -> part.Kind = "tool_result" && part.ProviderRun = Some providerRun && part.ToolCallId = Some toolCallId)
        |> List.map semanticPartView

    let toolPartsForHostIdentity (providerRun: ProviderRunIdentity) (toolCallId: ToolCallId) (hostToolPartId: HostToolPartId) state =
        parts state
        |> List.filter (fun part -> part.ProviderRun = Some providerRun && part.ToolCallId = Some toolCallId && part.HostToolPartId = Some hostToolPartId)
        |> List.map semanticPartView

    let private nonBlankHostMessageId (messageId: string) =
        if String.IsNullOrWhiteSpace messageId then None else Some messageId

    let private hostMessageIdFrom (part: XTracePartRef) (idStart: int) =
        let slash = part.Provenance.IndexOf('/', idStart)
        let idEnd = if slash < 0 then part.Provenance.Length else slash
        let length = idEnd - idStart
        if length <= 0 then None else part.Provenance.Substring(idStart, length) |> nonBlankHostMessageId

    let internal tryHostMessageId (part: XTracePartRef) =
        let marker = "/msg:"
        let start = part.Provenance.IndexOf(marker, StringComparison.Ordinal)
        if start < 0 then None else hostMessageIdFrom part (start + marker.Length)

    let tryHostMessageIdAt (cursor: XTraceCursor) state =
        parts state |> List.tryFind (fun part -> part.Cursor = cursor) |> Option.bind tryHostMessageId

    let hostMessageFirstCursors state =
        parts state
        |> List.fold (fun cursors part ->
            match tryHostMessageId part with
            | Some messageId when not (Map.containsKey messageId cursors) -> Map.add messageId (XTraceCursor.sequence part.Cursor) cursors
            | _ -> cursors) Map.empty

    let partsForHostMessageIds (messageIds: Set<string>) state =
        parts state
        |> List.filter (fun part -> tryHostMessageId part |> Option.exists messageIds.Contains)
        |> List.map semanticPartView

    let tryContiguousHostRange (messageIds: Set<string>) state =
        let matched = parts state |> List.filter (fun part -> tryHostMessageId part |> Option.exists messageIds.Contains)
        let rec contiguous (previous: int64) (remaining: XTracePartRef list) =
            match remaining with
            | [] -> true
            | part :: tail when XTraceCursor.sequence part.Cursor = previous + 1L -> contiguous (XTraceCursor.sequence part.Cursor) tail
            | _ -> false

        let observedIds = matched |> List.choose tryHostMessageId |> Set.ofList
        let exactRequestedIds = not (Set.isEmpty messageIds) && observedIds = messageIds

        match matched, exactRequestedIds with
        | first :: tail, true when contiguous (XTraceCursor.sequence first.Cursor) tail ->
            Some(XTraceRange.create first.Cursor (XTraceCursor.nextCursor (List.last matched).Cursor))
        | _ -> None

    let tryTurnOfHostMessageId (messageId: string) state =
        parts state |> currentGenerationParts
        |> List.tryPick (fun part ->
            match tryHostMessageId part with
            | Some stable when stable = messageId -> Some part.Turn
            | _ -> None)

    let tryOpeningHostMessageId state =
        parts state |> currentGenerationParts
        |> List.tryPick (fun part ->
            if part.Role.Equals("user", StringComparison.OrdinalIgnoreCase) then tryHostMessageId part else None)

    let hostMessageIdsBeforeTurn (cutoffExclusive: int) state =
        parts state |> currentGenerationParts
        |> List.filter (fun part -> part.Turn < cutoffExclusive)
        |> List.choose tryHostMessageId
        |> List.distinct

    let internal semanticCursorFor (sequence: int64) state =
        let ordered = parts state
        let current = currentGenerationParts ordered
        let searchable = if List.isEmpty current then ordered else current

        match searchable |> List.tryFind (fun part -> XTraceCursor.sequence part.Cursor > sequence) with
        | Some part -> part.Cursor
        | None ->
            match List.tryLast searchable with
            | Some last -> XTraceCursor.nextCursor last.Cursor
            | None -> XTraceCursor.create 0L

    let semanticCursorAfter (cursor: XTraceCursor) state = semanticCursorFor (XTraceCursor.sequence cursor) state
