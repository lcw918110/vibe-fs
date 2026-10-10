namespace Wanxiangshu.OpenCode.Host

open Wanxiangshu.Foundation.Identity

/// The exact identity of one tool call, used to bind and release one
/// object-local protocol argument stash. Kept at namespace level so the
/// manager review contract and the read-only delegation contract share the
/// same vocabulary without redefining it.
type ProtocolArgumentCall =
    { SessionId: SessionId
      ToolCallId: ToolCallId
      Tool: string }

[<RequireQualifiedAccess>]
type HiddenProtocolArguments =
    | NotHidden
    | SameCall
    | DifferentCallOrChangedArguments

/// The object-local stash shared by the two protocol argument families.
/// It is deliberately distinct from ProtocolArgumentVault: the vault keeps
/// wire argument evidence keyed by (sessionId, callId) for the provider-facing
/// transform, while this stash hides one arguments object's protocol fields
/// under a private Symbol and restores the same object in the matching after
/// callback. No decorator container, dynamic registry or central runtime.
module ProtocolArgumentStash =

    /// One hidden protocol field: the JavaScript property name and the key
    /// its original property descriptor is saved under.
    type StashField = { Name: string; SavedKey: string }

    /// One static stash definition: the private Symbol singleton, the hidden
    /// field table, the field set whose reappearance marks a changed call,
    /// the loud failure messages, and whether a failed field deletion
    /// compensates by deleting the Symbol record. The two protocol families
    /// keep their current, different compensation behavior through this
    /// explicit option instead of silent unification.
    type StashSpec =
        { Symbol: obj
          Fields: StashField list
          ReappearanceFields: string list
          HoldMessage: string
          RestoreOrderMessage: string
          RestoreFrozenMessage: string
          FieldDeleteFailure: string -> string
          RestoreDeleteFailure: string -> string
          SymbolDeleteFailure: string
          CompensateDeleteFailure: bool }

    /// Saves the hidden fields' property descriptors (or undefined) and the
    /// current key order under the private Symbol, then deletes the fields.
    /// Idempotent when a record is already present. Fails atomically if the
    /// object is not extensible or a field cannot be held.
    val hide: spec: StashSpec -> args: obj -> unit

    /// Binds a call owner only after hiding completes; a missing identity or
    /// an unfinished hide never becomes a same-call repeat witness.
    val hideForCall: spec: StashSpec -> owner: ProtocolArgumentCall option -> args: obj -> unit

    /// Classifies parameter cleanup ownership only; it grants no tool permission.
    val classifyHiddenArguments:
        spec: StashSpec -> owner: ProtocolArgumentCall option -> args: obj -> HiddenProtocolArguments

    /// Restores the hidden fields from the private Symbol on the args object.
    /// Idempotent (no-op when no record is present). Throws TypeError if the
    /// object is frozen/non-extensible.
    val restore: spec: StashSpec -> args: obj -> unit

    /// Normal after callbacks release only their own stash; an unidentified
    /// callback may restore an unidentified stash, never an identified one.
    val restoreForCall: spec: StashSpec -> owner: ProtocolArgumentCall option -> args: obj -> unit
