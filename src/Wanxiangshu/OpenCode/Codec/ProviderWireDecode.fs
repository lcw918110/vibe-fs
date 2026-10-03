namespace Wanxiangshu.OpenCode

open Wanxiangshu.Interaction.Dispatch.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Host raw object → `ProviderWireProjection` (provider-projection-003).
///
/// This module owns dynamic property access; `Domain.ProviderProjection` owns the
/// types and the questions asked of them. It produces ONLY the wire projection —
/// the semantic one is reached through `toSemantic`, so no second decoding path
/// can disagree about what a message meant.
///
/// The previous version defined its own `ProviderVisibleMessage` used for both
/// byte equality and cross-session comparison, which is why the canary matcher
/// grew a separate normaliser beside it (provider-projection-003).
///
/// Wave 3 split: this module is the raw decode boundary. `ProviderWireCapture`
/// owns the capture variants that retain Host stable addresses; it depends on
/// `readField`/`firstString`/`rawArray`/`infoObject` here, which are therefore
/// public rather than private.
module ProviderWireDecode =

    let readField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private readString (value: obj) (name: string) : string option =
        let field = readField value name

        if isNull field then
            None
        else
            let text = unbox<string> field
            if String.IsNullOrWhiteSpace text then None else Some text

    let firstString (value: obj) (names: string list) =
        names |> List.tryPick (readString value)

    /// Host messages arrive either bare or wrapped as `{ info, parts }`.
    let infoObject (rawObj: obj) : obj =
        if isNull rawObj then null
        elif not (isNull rawObj?info) then rawObj?info
        else rawObj

    let rawArray (value: obj) : obj list =
        if isNull value then
            []
        else
            emitJsExpr value "Array.from($0)" |> unbox<obj array> |> Array.toList

    let private canonicalArgs (value: obj) : string =
        if isNull value then
            "{}"
        elif emitJsExpr value "typeof $0 === 'string'" then
            unbox<string> value
        else
            Wanxiangshu.Foundation.CanonicalJson.canonicalJson value

    let private firstCanonical (partObj: obj) (names: string list) =
        names
        |> List.tryPick (fun name ->
            let value = readField partObj name
            if isNull value then None else Some(canonicalArgs value))

    /// Decode one Host part.
    ///
    /// `None` for bookkeeping parts. ARCH-004 and COMPANION-012 both require that
    /// step markers, patches, files and compaction entries never enter a
    /// projection: the model never saw them, so including them would make the
    /// prefix-cache check fail on content that was never sent.
    let private decodeToolState stateObj callId name partObj =
        // Host session-shaped tool parts carry the call and its completed result
        // together. Completed/error states project as results; all other states
        // remain pending calls.
        match readString stateObj "status" with
        | Some "completed" ->
            let result =
                firstCanonical stateObj [ "output"; "result"; "content" ]
                |> Option.defaultValue "null"

            Some(WireToolResult(ToolCallId.create callId, result))
        | Some "error" ->
            let result =
                firstCanonical stateObj [ "error"; "errorText"; "output" ]
                |> Option.defaultValue "null"

            Some(WireToolResult(ToolCallId.create callId, result))
        | _ when String.IsNullOrWhiteSpace name -> None
        | _ ->
            let args =
                firstCanonical stateObj [ "input" ]
                |> Option.orElse (firstCanonical partObj [ "args"; "arguments" ])
                |> Option.defaultValue "{}"

            Some(WireToolCall(ToolCallId.create callId, name, args))

    let private decodeToolCallPart partObj =
        // REVIEW-004 needs the call id, so a tool call without one is not usable
        // evidence. A completed/error result does not need the original tool name.
        match firstString partObj [ "callID"; "callId"; "id" ] with
        | Some callId ->
            let name = firstString partObj [ "tool"; "name" ] |> Option.defaultValue ""
            decodeToolState (readField partObj "state") callId name partObj
        | None -> None

    let private decodeNonNullPart partObj =
        let kind =
            readString partObj "type"
            |> Option.defaultValue ""
            |> fun value -> value.ToLowerInvariant()

        match kind with
        | "text" -> readString partObj "text" |> Option.map WireText
        | "reasoning"
        | "thinking" -> firstString partObj [ "text"; "reasoning" ] |> Option.map WireReasoning
        | "tool-call"
        | "tool_call"
        | "tool" -> decodeToolCallPart partObj
        | "tool-result"
        | "tool_result" ->
            let state = readField partObj "state"

            let result =
                firstCanonical partObj [ "result"; "output"; "content" ]
                |> Option.orElse (firstCanonical state [ "result"; "output"; "content" ])
                |> Option.defaultValue "null"

            firstString partObj [ "callID"; "callId"; "id" ]
            |> Option.map (fun callId -> WireToolResult(ToolCallId.create callId, result))
        | kind when kind.StartsWith "tool-" ->
            let result =
                firstCanonical partObj [ "output"; "errorText"; "result"; "content" ]
                |> Option.defaultValue "null"

            firstString partObj [ "toolCallId"; "callID"; "callId"; "id" ]
            |> Option.map (fun callId -> WireToolResult(ToolCallId.create callId, result))
        | "file" ->
            firstString partObj [ "url" ]
            |> Option.map (fun url -> WireMedia(firstString partObj [ "mime"; "mediaType" ], HostDigest.sha256Hex url))
        | _ -> None

    let decodePart (partObj: obj) : WirePart option =
        if isNull partObj then None else decodeNonNullPart partObj

    let messagesFromTransformOutput (output: obj) : obj list =
        unbox<obj array> output?messages |> Array.toList

    /// The Host's own message id.
    ///
    /// Not part of either projection: an id identifies a message, it is not
    /// content the model saw. HOST-010's binding needs it, so it is read
    /// separately and stays out of both.
    let hostMessageId (rawObj: obj) : string option =
        let info = infoObject rawObj
        readString info "id" |> Option.orElse (readString rawObj "id")

    /// Raw `parts` array of a Host message, for write-back paths that must
    /// preserve non-text parts verbatim.
    let rawPartsOf (rawObj: obj) : obj list =
        rawArray (if isNull rawObj then null else rawObj?parts)

    /// The `PromptKey` a Host message carries in its metadata (PROMPT-011).
    /// Reads the field PromptMetadataCodec wrote; single-message variant of
    /// PromptIngressCodec's input/output pair reader.
    let private nonBlankMetadataText (value: obj) : string option =
        if isNull value then
            None
        else
            let text = unbox<string> value
            if String.IsNullOrWhiteSpace text then None else Some text

    let private metadataStringOfMessage (field: string) (rawObj: obj) : string option =
        let fromMetadata (source: obj) =
            if isNull source || isNull source?metadata then
                None
            else
                source?metadata?(field) |> nonBlankMetadataText

        let info = infoObject rawObj

        let fromParts () =
            rawArray (if isNull rawObj then null else rawObj?parts)
            |> List.tryPick (fun part -> if isNull part then None else fromMetadata part)

        [ fromMetadata info; fromMetadata rawObj; fromParts () ] |> List.tryPick id

    let promptKeyOfMessage (rawObj: obj) : PromptKey option =
        metadataStringOfMessage PromptMetadataCodec.PromptKeyField rawObj
        |> Option.map PromptKey.create

    /// Typed Host continuation origin carried out-of-band from provider semantic
    /// content. Consumers use this only at the Host membrane (for example to keep
    /// ProviderRetryAttempt transport rows out of durable X); it never enters the
    /// semantic/wire projection itself (COMPANION-012).
    let promptOriginOfMessage (rawObj: obj) : string option =
        metadataStringOfMessage PromptMetadataCodec.OriginField rawObj

    /// Extract the single, unambiguous session id from a transform output's
    /// `messages` array. Used by hooks that need to identify the managed session
    /// before the Host has bound the run.
    ///
    /// Returns `None` when there are zero, multiple, or malformed session ids.
    let private sessionIdOfMessage (msg: obj) : string option =
        if not (isNull msg) && not (isNull msg?info) && not (isNull msg?info?sessionID) then
            Some(unbox<string> msg?info?sessionID)
        elif not (isNull msg) && not (isNull msg?sessionID) then
            Some(unbox<string> msg?sessionID)
        elif not (isNull msg) && not (isNull msg?sessionId) then
            Some(unbox<string> msg?sessionId)
        else
            None

    let private projectionSessionIdFromMessageArray (messages: obj array) : string option =
        let sessionIds = messages |> Array.choose sessionIdOfMessage |> Array.distinct

        match sessionIds with
        | [| sessionId |] -> Some sessionId
        | _ -> None

    let projectionSessionIdFromMessages (output: obj) : string option =
        if isNull output || isNull output?messages then
            None
        else
            projectionSessionIdFromMessageArray (unbox<obj array> output?messages)
