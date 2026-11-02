namespace Wanxiangshu.Context.Trace

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection

type XTraceItem =
    { Cursor: XTraceCursor
      Provenance: string
      Role: string
      Part: SemanticPart }

[<RequireQualifiedAccess>]
module internal WanxiangliTrace =

    type History =
        abstract appendOpening: opening: obj -> obj
        abstract appendPart: part: obj * prepareMetadata: Func<obj> -> obj
        abstract appendTerminal: terminal: obj -> obj
        abstract entries: unit -> obj array
        abstract terminals: unit -> obj array
        abstract opening: unit -> obj
        abstract latestTerminal: unit -> obj
        abstract terminalFor: providerRun: string -> obj
        abstract headSequence: unit -> int64
        abstract head: unit -> int64
        abstract latestSequence: unit -> obj
        abstract rangeFrom: start: int64 -> obj
        abstract slice: range: obj -> obj array
        abstract interval: range: obj -> obj
        abstract afterCoverage: sequence: int64 -> obj
        abstract cutAt: sequence: int64 -> obj
        abstract headCut: unit -> obj
        abstract records: unit -> string array
        abstract read: entries: obj array * readBody: Func<string, Task<obj>> -> Task<obj>
        abstract materialize: range: obj * readBody: Func<string, Task<obj>> -> Task<obj>

    [<Import("TraceHistory", "wanxiangli/trace")>]
    let private historyClass: obj = jsNative

    [<Import("materializeTraceItems", "wanxiangli/trace")>]
    let private materializeItems: obj = jsNative

    [<Import("inspectTraceItems", "wanxiangli/trace")>]
    let private inspectItems: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (_cls: obj) (_traceId: string) : History = jsNative

    let createHistory traceId = construct historyClass traceId

    let rangeObject (range: XTraceRange) : obj =
        createObj
            [ "start" ==> (range |> XTraceRange.startInclusive |> XTraceCursor.sequence)
              "end" ==> (range |> XTraceRange.endExclusive |> XTraceCursor.sequence) ]

    let rangeFromNative (range: obj) =
        XTraceRange.create
            (XTraceCursor.create (unbox<int64> range?start))
            (XTraceCursor.create (unbox<int64> range?``end``))

    let cutFromNative (cut: obj) : XTraceCut =
        { TraceId = unbox<string> cut?traceId
          Cursor = unbox<int> cut?cursor
          Digest = unbox<string> cut?digest }

    let intervalFromNative (interval: obj) =
        cutFromNative interval?after, cutFromNative interval?through

    let semanticToNative =
        function
        | SemanticText text -> createObj [ "kind" ==> "text"; "text" ==> text ]
        | SemanticReasoning text -> createObj [ "kind" ==> "reasoning"; "text" ==> text ]
        | SemanticToolCall(name, args) -> createObj [ "kind" ==> "tool_call"; "name" ==> name; "args" ==> args ]
        | SemanticToolResult text -> createObj [ "kind" ==> "tool_result"; "text" ==> text ]
        | SemanticMedia(mediaType, digest) ->
            createObj
                [ "kind" ==> "media"
                  "mediaType" ==> (mediaType |> Option.map box |> Option.defaultValue null)
                  "digest" ==> digest ]

    let semanticFromNative (part: obj) : SemanticPart =
        match unbox<string> part?kind with
        | "text" -> SemanticText(unbox<string> part?text)
        | "reasoning" -> SemanticReasoning(unbox<string> part?text)
        | "tool_call" -> SemanticToolCall(unbox<string> part?name, unbox<string> part?args)
        | "tool_result" -> SemanticToolResult(unbox<string> part?text)
        | "media" ->
            let mediaType: obj = part?mediaType

            SemanticMedia(
                (if isNull mediaType then
                     None
                 else
                     Some(unbox<string> mediaType)),
                unbox<string> part?digest
            )
        | kind -> invalidOp ("wanxiangli/trace returned an unknown semantic part: " + kind)

    let itemFromNative (provenanceOf: obj -> string) (item: obj) : XTraceItem =
        { Cursor = XTraceCursor.create (unbox<int64> item?sequence)
          Provenance = provenanceOf item?metadata
          Role = unbox<string> item?role
          Part = semanticFromNative item?part }

    let private itemToNative (item: XTraceItem) : obj =
        createObj
            [ "sequence" ==> XTraceCursor.sequence item.Cursor
              "role" ==> item.Role
              "part" ==> semanticToNative item.Part
              "metadata" ==> item.Provenance ]

    let materializeItemsInRange (range: XTraceRange) (items: XTraceItem list) : XTraceItem list =
        let native = items |> List.map itemToNative |> List.toArray

        let values: obj array =
            emitJsExpr (materializeItems, rangeObject range, native) "$0($1, $2)"

        values |> Array.map (itemFromNative unbox<string>) |> Array.toList

    let inspectItemsBridge
        (traceId: string)
        (openingText: string option)
        (range: XTraceRange)
        (items: XTraceItem list)
        : obj =
        let native = items |> List.map itemToNative |> List.toArray
        let opening = openingText |> Option.map box |> Option.defaultValue null
        emitJsExpr (inspectItems, traceId, opening, rangeObject range, native) "$0($1, $2, $3, $4)"

[<RequireQualifiedAccess>]
module XTrace =

    let sliceBetween (start: XTraceCursor) (endExclusive: XTraceCursor) (items: XTraceItem list) =
        WanxiangliTrace.materializeItemsInRange (XTraceRange.create start endExclusive) items

    let head (items: XTraceItem list) : XTraceCursor =
        match List.tryLast items with
        | Some item -> XTraceCursor.nextCursor item.Cursor
        | None -> XTraceCursor.originCursor

    let sliceFrom (start: XTraceCursor) (items: XTraceItem list) =
        let traceHead = head items

        if XTraceCursor.isAfter start traceHead then
            []
        else
            sliceBetween start traceHead items

    let flatten (messages: SemanticMessage list) : {| Role: string; Part: SemanticPart |} list =
        messages
        |> List.collect (fun message -> message.Parts |> List.map (fun part -> {| Role = message.Role; Part = part |}))

    let forOpening (items: XTraceItem list) : XTraceItem list = items

    let private renderMedia =
        function
        | Some mediaType -> sprintf "[media omitted: %s]" mediaType
        | None -> "[media omitted]"

    let private renderPart =
        function
        | SemanticText text
        | SemanticReasoning text -> text
        | SemanticToolCall(name, args) -> sprintf "[tool call] %s %s" name args
        | SemanticToolResult result -> sprintf "[tool result] %s" result
        | SemanticMedia(mediaType, _) -> renderMedia mediaType

    let renderItem (item: XTraceItem) =
        match item.Part with
        | SemanticText _ -> sprintf "%s: %s" item.Role (renderPart item.Part)
        | _ -> renderPart item.Part

    let render (items: XTraceItem list) =
        items |> List.map renderItem |> String.concat "\n"
