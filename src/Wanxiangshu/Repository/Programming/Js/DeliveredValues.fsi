namespace Wanxiangshu.Repository.Programming.Js

/// Process-live cell holding the value and optional view a program handed over
/// through `js.deliver(value, view)`. The completion path reads the rendered
/// handoff (value, plus the prose the view selects, minus identities already
/// returned). An ordinary `return` leaves the cell untouched. The returned
/// cursor is keyed by anchor number, not by prose text.
module JsDeliveredValues =
    type ReturnedPiece = { Key: string; Text: string }

    val record: sessionId: string -> value: obj -> unit
    val recordView: sessionId: string -> value: obj -> view: obj option -> unit
    val take: sessionId: string -> obj
    val peek: sessionId: string -> obj
    val render: value: obj -> string option
    val alreadyReturned: sessionId: string -> Set<string>
    val select: view: obj option -> already: Set<string> -> removals: Map<int, string> -> ReturnedPiece list
    val tryTakeRenderedWith: sessionId: string -> assistant: string option -> string option
    val tryTakeRendered: sessionId: string -> string option
    val tryPeekRendered: sessionId: string -> string option
    val clear: sessionId: string -> unit
    val clearReturned: sessionId: string -> unit
    val clearAll: unit -> unit
