namespace Wanxiangshu.Repository.Programming.Js.Anchor

open Wanxiangshu.Repository.Programming.Js
open Fable.Core
open Fable.Core.JsInterop

/// JS-native ANCHOR boundary for semantic tests and the transform pipeline.
[<RequireQualifiedAccess>]
module AnchorSurface =

    [<Import("encode", "gpt-tokenizer/encoding/o200k_base")>]
    let private encodeTokens (text: string) : obj = jsNative

    [<Emit("$0.length")>]
    let private tokenCount (tokens: obj) : int = jsNative

    [<Emit("$0 === undefined || $0 === null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isString (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isNumber (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'object'")>]
    let private isObject (value: obj) : bool = jsNative

    [<Emit("$0[$1]")>]
    let private getProperty (value: obj) (name: string) : obj = jsNative

    [<Emit("$0[$1]")>]
    let private getIndex (value: obj) (index: int) : obj = jsNative

    [<Emit("$0.length")>]
    let private length (value: obj) : int = jsNative

    [<Emit("Object.keys($0)")>]
    let private keys (value: obj) : string[] = jsNative

    let countTokens (text: string) : int = tokenCount (encodeTokens text)

    let private invalid detail =
        Error(JsFailure.ViewDeclarationInvalid detail)

    let private anchorOfKey (key: string) : int option =
        match System.Int32.TryParse key with
        | true, anchor when string anchor = key -> Some anchor
        | _ -> None

    /// `thenRemove` is an anchor→summary map: the summary is what stands in for
    /// that anchor from here on, so a missing or blank summary is a declaration
    /// that would silently drop knowledge.
    let private replacePair (source: obj) (key: string) : Result<int * string, JsFailure> =
        match anchorOfKey key with
        | None -> invalid (sprintf "thenRemove keys must be anchor numbers; \"%s\" is not one" key)
        | Some anchor ->
            let summary = getProperty source key

            if
                not (isString summary)
                || System.String.IsNullOrWhiteSpace(unbox<string> summary)
            then
                invalid (
                    sprintf
                        "thenRemove[%s] must be a non-blank summary string (the summary replaces that anchor's segments, not deletes them)"
                        key
                )
            else
                Ok(anchor, string summary)

    /// Parse one `view` argument against the declaration contract:
    /// `prefixUntil` is a required anchor number (never "all"), and `thenRemove`
    /// is an optional anchor→summary map naming what to replace for good.
    /// Anything may be replaced, including anchors below `prefixUntil`; the
    /// prefix is a price boundary, not a protection fence.
    let private declarationOf (value: obj) : Result<AnchorViewDeclaration, JsFailure> =
        let rawPrefixUntil = getProperty value "prefixUntil"
        let rawThenRemove = getProperty value "thenRemove"

        let prefixUntil =
            if isNumber rawPrefixUntil then
                Ok(int (unbox<float> rawPrefixUntil))
            else
                invalid
                    "prefixUntil must be an anchor number; pass one above every anchor you have seen to keep everything"

        let thenRemove =
            if isNullish rawThenRemove then
                Ok []
            elif isArray rawThenRemove || not (isObject rawThenRemove) then
                invalid
                    "thenRemove must be an object mapping anchor numbers to summaries, e.g. { \"3\": \"what segment 3 held\" }"
            else
                keys rawThenRemove
                |> Array.toList
                |> List.map (replacePair rawThenRemove)
                |> List.fold
                    (fun accumulated pair ->
                        match accumulated, pair with
                        | Ok pairs, Ok entry -> Ok(pairs @ [ entry ])
                        | Error failure, _ -> Error failure
                        | _, Error failure -> Error failure)
                    (Ok [])
                |> Result.map (List.sortBy fst)

        match prefixUntil, thenRemove with
        | Ok prefixUntil, Ok thenRemove ->
            Ok
                { PrefixUntil = prefixUntil
                  ThenRemove = thenRemove }
        | Error failure, _
        | _, Error failure -> Error failure

    let private declarationValue (declaration: AnchorViewDeclaration) : obj =
        createObj
            [ "prefixUntil" ==> declaration.PrefixUntil
              "thenRemove"
              ==> (declaration.ThenRemove
                   |> List.map (fun (anchor, summary) -> string anchor, box summary)
                   |> List.toArray
                   |> createObj) ]

    let numberMessage (sessionId: string) (messageId: string) : int =
        AnchorLedger.numberMessage sessionId messageId

    let anchors (sessionId: string) : obj array =
        AnchorLedger.anchors sessionId
        |> List.map (fun (anchor, messageId) ->
            box
                {| anchor = anchor
                   messageId = messageId |})
        |> Array.ofList

    /// Capture one declaration for the next request. The declaration itself is
    /// one-turn; the replacements it names stand for their anchors from now on.
    /// Validate one deliver/view declaration without capturing it for the next
    /// request. The deliver view chooses what the child hands back; it is not
    /// the next-request ANCHOR declaration.
    let parse (declaration: obj) : Result<obj, JsFailure> =
        if isNullish declaration then
            invalid "view is missing"
        else
            match declarationOf declaration with
            | Error failure -> Error failure
            | Ok _ -> Ok declaration

    let setView (sessionId: string) (declaration: obj) : Result<unit, JsFailure> =
        if isNullish declaration then
            Ok()
        else
            match declarationOf declaration with
            | Error failure -> Error failure
            | Ok view ->
                AnchorLedger.replaceAnchors sessionId view.ThenRemove
                AnchorLedger.setView sessionId view
                Ok()

    let takeView (sessionId: string) : obj =
        match AnchorLedger.takeView sessionId with
        | Some declaration -> declarationValue declaration
        | None -> null

    let applyView (sessionId: string) (messages: obj array) : obj =
        let declaration =
            AnchorLedger.takeView sessionId
            |> Option.defaultValue { PrefixUntil = 0; ThenRemove = [] }

        let replaced = AnchorLedger.replacements sessionId

        let inputs =
            if isNullish (box messages) then
                []
            else
                messages
                |> Array.toList
                |> List.map (fun raw ->
                    { AnchorView.AnchorMessage.Id =
                        getProperty raw "id" |> fun id -> if isString id then Some(string id) else None
                      AnchorView.AnchorMessage.Text =
                        getProperty raw "text" |> fun text -> if isString text then string text else ""
                      AnchorView.AnchorMessage.Raw = raw })

        let entries, report =
            AnchorView.apply (AnchorLedger.numberMessage sessionId) countTokens replaced declaration inputs

        createObj
            [ "entries"
              ==> (entries
                   |> List.map (function
                       | AnchorView.Kept message -> createObj [ "kind" ==> "kept"; "message" ==> message.Raw ]
                       | AnchorView.Summarized(message, summary) ->
                           createObj [ "kind" ==> "summarized"; "message" ==> message.Raw; "summary" ==> summary ])
                   |> List.toArray
                   |> box)
              "report"
              ==> createObj
                      [ "total" ==> report.Total
                        "used" ==> report.Used
                        "cached" ==> report.Cached
                        "kept" ==> report.Kept
                        "summarized" ==> report.Summarized
                        "folded" ==> report.Folded
                        "replaced" ==> (report.Replaced |> List.toArray |> box) ] ]

    let clearSession (sessionId: string) : unit = AnchorLedger.clearSession sessionId

    let clearAll () : unit = AnchorLedger.clearAll ()
