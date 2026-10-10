namespace Wanxiangshu.Interaction.Attention

open Wanxiangshu.Foundation.Identity

type DeferredWorkItem = { OccurrenceId: string; Text: string }

type AttentionProjectionState =
    { BySession: Map<SessionId, DeferredWorkItem list>
      ConsumedBySession: Map<SessionId, Set<string>> }

[<RequireQualifiedAccess>]
module AttentionProjection =
    val empty: AttentionProjectionState
    val pending: sessionId: SessionId -> state: AttentionProjectionState -> DeferredWorkItem list

    val tryFind:
        sessionId: SessionId -> occurrenceId: string -> state: AttentionProjectionState -> DeferredWorkItem option

    val wasConsumed: sessionId: SessionId -> occurrenceId: string -> state: AttentionProjectionState -> bool

    val record:
        sessionId: SessionId ->
        occurrenceId: string ->
        text: string ->
        state: AttentionProjectionState ->
            AttentionProjectionState

    val consume:
        sessionId: SessionId -> workIds: string list -> state: AttentionProjectionState -> AttentionProjectionState

    val closeLife: sessionId: SessionId -> state: AttentionProjectionState -> AttentionProjectionState
