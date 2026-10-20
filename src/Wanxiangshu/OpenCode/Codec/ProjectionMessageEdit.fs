namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Rendered projection → Host raw write-back adapters (Wave 3 split of the old
/// `Projection` module). This module turns rendered messages and stable Host-id
/// edits into Host object lists; decoding back the other way lives in
/// `ProviderWireDecode` / `ProviderWireCapture`.
module ProjectionMessageEdit =

    /// Host transport membrane: remove exactly the addressed physical rows.
    /// No role/count heuristic is permitted here — transport metadata identifies
    /// Host messages, so write-back must consume the same stable identity universe.
    let suppressHostMessagesByIds (rawMessages: obj list) (messageIds: Set<string>) : obj list =
        if Set.isEmpty messageIds then
            rawMessages
        else
            rawMessages
            |> List.filter (fun message ->
                ProviderWireDecode.hostMessageId message
                |> Option.forall (fun messageId -> not (Set.contains messageId messageIds)))

    let private canonicalValue (canonical: string) : obj =
        try
            emitJsExpr canonical "JSON.parse($0)"
        with _ ->
            box canonical

    let private encodeWirePart (part: WirePart) : Result<obj, string> =
        match part with
        | WireText text -> Ok(createObj [ "type", box "text"; "text", box text ])
        | WireReasoning text -> Ok(createObj [ "type", box "reasoning"; "text", box text ])
        | WireToolCall(callId, name, argsCanonical) ->
            Ok(
                createObj
                    [ "type", box "tool-call"
                      "callID", box (ToolCallId.value callId)
                      "tool", box name
                      "args", canonicalValue argsCanonical ]
            )
        | WireToolResult(callId, resultCanonical) ->
            Ok(
                createObj
                    [ "type", box "tool-result"
                      "callID", box (ToolCallId.value callId)
                      "result", canonicalValue resultCanonical ]
            )
        | WireMedia _ ->
            // provider-projection-010: semantic/media digests are one-way. Reconstructing media
            // bytes from a digest would invent provider-visible content.
            Error "wire media cannot be reconstructed from semantic digest"

    let private encodeParts (parts: WirePart list) : Result<obj list, string> =
        parts |> List.traverseResultM encodeWirePart

    let private derivedHostMessageId (sha256: string -> string) (index: int) (message: WireMessage) =
        let single: ProviderWireProjection =
            { ProviderId = None
              ModelId = None
              Variant = None
              Tools = []
              System = []
              Messages = [ message ] }

        sha256 (sprintf "%d\u001f%s" index (renderWire single))

    let private encodeRenderedMessage
        (sessionId: string)
        (sha256: string -> string)
        (index: int)
        (message: WireMessage)
        (hostId: string option)
        : Result<obj, string> =
        result {
            let! parts = encodeParts message.Parts

            let id =
                hostId
                |> Option.defaultWith (fun () -> derivedHostMessageId sha256 index message)

            return
                createObj
                    [ "info", box (createObj [ "id", box id; "sessionID", box sessionId; "role", box message.Role ])
                      "parts", box (List.toArray parts) ]
        }

    /// Write a fully rendered DSL message view back to
    /// Host objects. This is intentionally an adapter, not business assembly.
    /// Missing Host ids are derived from wire bytes + ordinal and are Host-only;
    /// the generated identity never enters ProviderSemanticProjection.
    let tryApplyRenderedMessages
        (sessionId: string)
        (sha256: string -> string)
        (rendered: Wanxiangshu.Participant.Provider.Projection.RenderedMessages)
        : Result<obj list, string> =
        List.zip3 rendered.Messages rendered.HostMessageIds rendered.HostIsPhysical
        |> List.mapi (fun index (message, hostId, _) -> index, message, hostId)
        |> List.traverseResultM (fun (index, message, hostId) ->
            encodeRenderedMessage sessionId sha256 index message hostId)

    /// Small physical Host encoding port for owner modules that need a native
    /// representation different from the generic one-row-per-wire-message adapter.
    module HostWireEncoding =

        let tryEncodeNonToolParts (parts: WirePart list) : Result<obj list, string> =
            parts
            |> List.traverseResultM (fun part ->
                match part with
                | WireToolCall _
                | WireToolResult _ -> Error "Host wire encoding received a tool part"
                | _ -> encodeWirePart part)

        let completedToolPart
            (callId: ToolCallId)
            (name: string)
            (argsCanonical: string)
            (resultCanonical: string)
            : obj =
            createObj
                [ "type", box "tool"
                  "tool", box name
                  "callID", box (ToolCallId.value callId)
                  "state",
                  box (
                      createObj
                          [ "status", box "completed"
                            "input", canonicalValue argsCanonical
                            "output", box resultCanonical
                            "time", box (createObj [ "start", box 0; "end", box 0 ]) ]
                  ) ]

        let rawMessage
            (sessionId: string)
            (sha256: string -> string)
            (index: int)
            (message: WireMessage)
            (hostId: string option)
            (role: string)
            (parts: obj list)
            : obj =
            let id =
                hostId
                |> Option.defaultWith (fun () -> derivedHostMessageId sha256 index message)

            createObj
                [ "info", box (createObj [ "id", box id; "sessionID", box sessionId; "role", box role ])
                  "parts", box (List.toArray parts) ]

    let private decodeSingle raw =
        match (ProviderWireCapture.decodeMessageView [ raw ]).Messages with
        | [ message ] -> Ok message
        | _ -> Error "raw Host message does not decode to exactly one wire message"

    let private decodeRaw (rawMessages: obj list) =
        rawMessages
        |> List.traverseResultM (fun raw -> decodeSingle raw |> Result.map (fun wire -> raw, wire))

    let private requireSingletonEncoded (encoded: obj list) : Result<obj, string> =
        match encoded with
        | [ raw ] -> Ok raw
        | _ -> Error "single synthetic message encoded to an unexpected cardinality"

    let private encodeInserted
        (sessionId: string)
        (sha256: string -> string)
        (message: WireMessage)
        (hostId: string option)
        : Result<obj, string> =
        match hostId with
        | None -> Error "insertion-only write-back requires an explicit synthetic Host message id"
        | Some _ ->
            let singleton: Wanxiangshu.Participant.Provider.Projection.RenderedMessages =
                { Messages = [ message ]
                  HostMessageIds = [ hostId ]
                  HostIsPhysical = [ false ] }

            tryApplyRenderedMessages sessionId sha256 singleton
            |> Result.bind requireSingletonEncoded

    let private insertOrReject
        (sessionId: string)
        (sha256: string -> string)
        (isPhysical: bool)
        (message: WireMessage)
        (hostId: string option)
        : Result<obj, string> =
        if isPhysical then
            Error "projection insertion invented a physical Host message"
        else
            encodeInserted sessionId sha256 message hostId

    let private mergeInsertions
        (sessionId: string)
        (sha256: string -> string)
        (renderedRows: (WireMessage * string option * bool) list)
        (decodedRaw: (obj * WireMessage) list)
        : Result<obj list, string> =
        let rec merge rows raw acc =
            match rows, raw with
            | [], [] -> Ok(List.rev acc)
            | [], _ :: _ -> Error "projection insertion dropped raw Host messages"
            | (message, _, _) :: tail, (rawObj, rawWire) :: rawTail when message = rawWire ->
                merge tail rawTail (rawObj :: acc)
            | (message, hostId, isPhysical) :: tail, rawRemaining ->
                insertOrReject sessionId sha256 isPhysical message hostId
                |> Result.bind (fun inserted -> merge tail rawRemaining (inserted :: acc))

        merge renderedRows decodedRaw []

    /// Insertion-only write-back for owner Work history.
    /// Existing Host objects are reused byte/object-for-object; only renderer rows
    /// that do not match the next raw semantic message may be synthesized, and
    /// such rows must carry an explicit algebra-owned Host id. This prevents an
    /// insertion from silently re-identifying physical owner history.
    let tryApplyRenderedInsertionsPreservingBase
        (sessionId: string)
        (sha256: string -> string)
        (rawMessages: obj list)
        (rendered: Wanxiangshu.Participant.Provider.Projection.RenderedMessages)
        : Result<obj list, string> =
        result {
            let! decodedRaw = decodeRaw rawMessages

            let renderedRows =
                List.zip3 rendered.Messages rendered.HostMessageIds rendered.HostIsPhysical

            return! mergeInsertions sessionId sha256 renderedRows decodedRaw
        }
