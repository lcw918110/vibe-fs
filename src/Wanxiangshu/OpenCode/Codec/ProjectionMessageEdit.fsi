namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection

module ProjectionMessageEdit =
    val suppressHostMessagesByIds: rawMessages: obj list -> messageIds: Set<string> -> obj list

    val tryApplyRenderedMessages:
        sessionId: string -> sha256: (string -> string) -> rendered: RenderedMessages -> Result<obj list, string>

    module HostWireEncoding =
        val tryEncodeNonToolParts: parts: WirePart list -> Result<obj list, string>

        val completedToolPart:
            callId: ToolCallId -> name: string -> argsCanonical: string -> resultCanonical: string -> obj

        val rawMessage:
            sessionId: string ->
            sha256: (string -> string) ->
            index: int ->
            message: WireMessage ->
            hostId: string option ->
            role: string ->
            parts: obj list ->
                obj

    val tryApplyRenderedInsertionsPreservingBase:
        sessionId: string ->
        sha256: (string -> string) ->
        rawMessages: obj list ->
        rendered: RenderedMessages ->
            Result<obj list, string>
