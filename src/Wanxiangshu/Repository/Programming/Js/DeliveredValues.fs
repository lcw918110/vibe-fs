namespace Wanxiangshu.Repository.Programming.Js

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation

/// Process-live delivered-value cell, keyed by session. The cell holds the value
/// and the optional view `js.deliver` handed over. A separate returned cursor
/// records the identity of every prose item already handed back, so a later
/// deliver in the same session does not return it again. Identity is the host
/// message id or the anchor number — never the prose text. Restart drops both;
/// the durable terminal record is written when the call settles.
module JsDeliveredValues =

    [<Emit("$0 === undefined || $0 === null")>]
    let private isMissing (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Emit("Object.keys($0)")>]
    let private objectKeys (value: obj) : string[] = jsNative

    [<Emit("$0[$1]")>]
    let private getProp (value: obj) (key: string) : obj = jsNative

    type private DeliveredCell = { Value: obj; View: obj option }

    type ReturnedPiece = { Key: string; Text: string }

    // DSL-MUTABLE: resource — sessionId → delivered value and view
    let private delivered = Dictionary<string, DeliveredCell>()

    // DSL-MUTABLE: resource — sessionId → identities already handed back
    let private returned = Dictionary<string, HashSet<string>>()

    let private returnedSet (sessionId: string) =
        match returned.TryGetValue sessionId with
        | true, existing -> existing
        | false, _ ->
            let fresh = HashSet<string>()
            returned.[sessionId] <- fresh
            fresh

    let alreadyReturned (sessionId: string) : Set<string> =
        match returned.TryGetValue sessionId with
        | true, existing -> existing |> Seq.toList |> Set.ofList
        | false, _ -> Set.empty

    let private markReturned (sessionId: string) (keys: string list) =
        let set = returnedSet sessionId
        keys |> List.iter (fun key -> set.Add key |> ignore)

    let record (sessionId: string) (value: obj) : unit =
        delivered.[sessionId] <- { Value = value; View = None }

    /// `view` is the already-validated deliver declaration, or None when the
    /// program handed over a value without choosing prose to return.
    let recordView (sessionId: string) (value: obj) (view: obj option) : unit =
        delivered.[sessionId] <- { Value = value; View = view }

    let take (sessionId: string) : obj =
        match delivered.TryGetValue sessionId with
        | true, cell ->
            delivered.Remove sessionId |> ignore
            cell.Value
        | false, _ -> null

    let peek (sessionId: string) : obj =
        match delivered.TryGetValue sessionId with
        | true, cell -> cell.Value
        | false, _ -> null

    let private renderNonBlank (text: string) : string option =
        if String.IsNullOrWhiteSpace text then None else Some text

    let private renderStructured (structured: LlmFacing.Data.Value) : string option =
        (LlmFacing.empty
         |> LlmFacing.withData (LlmFacing.Data.structuredValue structured)
         |> LlmFacing.render)
            .TrimEnd('\n')
        |> renderNonBlank

    let private renderPresentValue (value: obj) : string option =
        match LlmFacing.Data.ofJs value with
        | LlmFacing.Data.Value.Null -> None
        | LlmFacing.Data.Value.String text -> renderNonBlank text
        | structured -> renderStructured structured

    let render (value: obj) : string option =
        if isMissing value then None else renderPresentValue value

    let private unboxInteger (value: obj) : int option =
        try
            Some(int (unbox<float> value))
        with _ ->
            None

    let private numberOf (value: obj) : int option =
        if isMissing value then None else unboxInteger value

    let private anchorKey (anchor: int) = "anchor:" + string anchor

    let private entrySummary (raw: obj) (key: string) : string option =
        let summary = getProp raw key

        if isMissing summary || String.IsNullOrWhiteSpace(string summary) then
            None
        else
            Some(string summary)

    let private parseRemovalEntry (raw: obj) (key: string) : (int * string) option =
        match Int32.TryParse key with
        | true, anchor when string anchor = key -> entrySummary raw key |> Option.map (fun summary -> anchor, summary)
        | _ -> None

    let private removalsOf (view: obj) : Map<int, string> =
        let raw = getProp view "thenRemove"

        if isMissing raw || not (isPlainObject raw) then
            Map.empty
        else
            objectKeys raw |> Array.choose (parseRemovalEntry raw) |> Map.ofArray

    let private eligiblePiece
        (prefixUntil: int)
        (already: Set<string>)
        (anchor: int, summary: string)
        : ReturnedPiece option =
        let key = anchorKey anchor

        if anchor < prefixUntil || Set.contains key already then
            None
        else
            Some { Key = key; Text = summary }

    /// Prose the view chooses to hand back, minus identities already returned.
    /// No view means no assistant prose. An item with no host-message id and no
    /// anchor number is not returned: there is no cursor key that is not a guess
    /// about the text.
    let select (view: obj option) (already: Set<string>) (removals: Map<int, string>) : ReturnedPiece list =
        let pieces =
            view
            |> Option.bind (fun declaration ->
                numberOf (getProp declaration "prefixUntil")
                |> Option.map (fun prefixUntil ->
                    let named =
                        if Map.isEmpty removals then
                            removalsOf declaration
                        else
                            removals

                    named |> Map.toList |> List.choose (eligiblePiece prefixUntil already)))

        pieces |> Option.defaultValue []

    let private presentValueBlocks (value: obj) =
        match LlmFacing.Data.ofJs value with
        | LlmFacing.Data.Value.Null -> []
        | LlmFacing.Data.Value.String text when String.IsNullOrWhiteSpace text -> []
        | LlmFacing.Data.Value.String text -> [ LlmFacing.Data.stringField "value" text ]
        | structured -> LlmFacing.Data.structuredValue structured

    let private valueBlocks (value: obj) =
        if isMissing value then [] else presentValueBlocks value

    let private renderHandoff (value: obj) (pieces: ReturnedPiece list) : string option =
        if List.isEmpty pieces then
            render value
        else
            let prose =
                pieces
                |> List.map (fun piece ->
                    LlmFacing.Data.tableArray
                        "returned"
                        [ LlmFacing.Data.stringMember "id" piece.Key
                          LlmFacing.Data.stringMember "text" piece.Text ])

            (LlmFacing.empty
             |> LlmFacing.withData (prose @ valueBlocks value)
             |> LlmFacing.render)
                .TrimEnd('\n')
            |> renderNonBlank

    let private proseKey (sessionId: string) (text: string) =
        "prose:"
        + sessionId
        + ":"
        + string text.Length
        + ":"
        + text.Substring(0, min 24 text.Length)

    /// The last assistant prose is handed back only when this deliver carried a
    /// view. The cursor key is the session plus the prose itself, not a guess
    /// about some other text: a later deliver that still names the same prose
    /// does not return it again.
    let private withAssistant
        (sessionId: string)
        (view: obj option)
        (pieces: ReturnedPiece list)
        (assistant: string option)
        =
        match view, assistant with
        | Some _, Some text when not (String.IsNullOrWhiteSpace text) ->
            let key = proseKey sessionId text

            if
                alreadyReturned sessionId |> Set.contains key
                || pieces |> List.exists (fun piece -> piece.Key = key)
            then
                pieces
            else
                pieces @ [ { Key = key; Text = text } ]
        | _ -> pieces

    let private handoff
        (sessionId: string)
        (cell: DeliveredCell)
        (advance: bool)
        (assistant: string option)
        : string option =
        let pieces =
            select cell.View (alreadyReturned sessionId) Map.empty
            |> fun selected -> withAssistant sessionId cell.View selected assistant

        if advance then
            markReturned sessionId (pieces |> List.map _.Key)

        renderHandoff cell.Value pieces

    let tryTakeRenderedWith (sessionId: string) (assistant: string option) : string option =
        match delivered.TryGetValue sessionId with
        | false, _ -> None
        | true, cell ->
            delivered.Remove sessionId |> ignore
            handoff sessionId cell true assistant

    let tryTakeRendered (sessionId: string) : string option = tryTakeRenderedWith sessionId None

    /// Snapshot of the handoff. Does not consume the cell and does not advance
    /// the returned cursor — showing the latest delivery is not itself a return.
    let tryPeekRendered (sessionId: string) : string option =
        match delivered.TryGetValue sessionId with
        | false, _ -> None
        | true, cell -> handoff sessionId cell false None

    let clear (sessionId: string) : unit = delivered.Remove sessionId |> ignore

    let clearReturned (sessionId: string) : unit = returned.Remove sessionId |> ignore

    let clearAll () : unit =
        delivered.Clear()
        returned.Clear()
