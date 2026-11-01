namespace Wanxiangshu.Context.Trace

open System
open System.Threading.Tasks
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

    val createHistory: traceId: string -> History
    val rangeObject: range: XTraceRange -> obj
    val rangeFromNative: range: obj -> XTraceRange
    val cutFromNative: cut: obj -> XTraceCut
    val intervalFromNative: interval: obj -> XTraceCut * XTraceCut
    val semanticToNative: SemanticPart -> obj
    val semanticFromNative: part: obj -> SemanticPart
    val itemFromNative: provenanceOf: (obj -> string) -> item: obj -> XTraceItem
    val materializeItemsInRange: range: XTraceRange -> items: XTraceItem list -> XTraceItem list
    val inspectItemsBridge: traceId: string -> openingText: string option -> range: XTraceRange -> items: XTraceItem list -> obj

[<RequireQualifiedAccess>]
module XTrace =
    val sliceBetween: start: XTraceCursor -> endExclusive: XTraceCursor -> items: XTraceItem list -> XTraceItem list
    val sliceFrom: start: XTraceCursor -> items: XTraceItem list -> XTraceItem list
    val head: items: XTraceItem list -> XTraceCursor
    val flatten: messages: SemanticMessage list -> {| Part: SemanticPart; Role: string |} list
    val forOpening: items: XTraceItem list -> XTraceItem list
    val render: items: XTraceItem list -> string
