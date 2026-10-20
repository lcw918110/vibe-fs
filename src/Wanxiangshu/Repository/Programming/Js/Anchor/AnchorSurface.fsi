namespace Wanxiangshu.Repository.Programming.Js.Anchor

open Wanxiangshu.Repository.Programming.Js

/// JS-native ANCHOR boundary: numbering, one-turn view declarations and the
/// selection/token report used by the transform pipeline. Numbers and
/// replacements are process-live (a restart resets them); the ledger owns no
/// durable fact.
[<RequireQualifiedAccess>]
module AnchorSurface =

    val numberMessage: sessionId: string -> messageId: string -> int
    val anchors: sessionId: string -> obj array
    /// Capture one view declaration for the next request; an invalid
    /// declaration is refused with a stable failure and changes nothing.
    val parse: declaration: obj -> Result<obj, JsFailure>
    val setView: sessionId: string -> declaration: obj -> Result<unit, JsFailure>
    val takeView: sessionId: string -> obj
    val countTokens: text: string -> int
    /// Apply the pending declaration to `[{ id, text }, ...]`; returns
    /// `{ messages, summaries, report }` without consuming the declaration twice.
    val applyView: sessionId: string -> messages: obj array -> obj
    val clearSession: sessionId: string -> unit
    val clearAll: unit -> unit
