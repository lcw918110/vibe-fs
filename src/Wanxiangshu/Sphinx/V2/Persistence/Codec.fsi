namespace Wanxiangshu.Sphinx.V2.Persistence

open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx.V2.Core

/// The v2 batch ↔ canonical envelope codec.
///
/// WHAT[sphinx-v2-019]: one inquiry transition becomes one canonical EventEnvelope.
/// That is a deliberate choice made because the snapshot proved the store is used but
/// did not prove `Append [e1; e2; e3]` is business-atomic. Putting the whole transition
/// in a single envelope makes the unit of durability and the unit of semantics the same
/// thing, so a partial transition can never become the accepted current.
module Codec =

    /// New writes use @2. @1 keeps its identity and is explicitly rejected/cut, not reinterpreted.
    val transitionEventType: string
    val legacyTransitionEventType: string

    type CodecError = { Code: string; Message: string }

    val decodeBody: obj -> Result<InquiryEventBody, CodecError>
    val decodeInput: obj -> Result<TransitionBatch, CodecError>
    val decode: (string -> string) -> EventEnvelope -> Result<TransitionBatch, CodecError>

    /// The event body tag the Integrator routes on.
    val bodyTag: InquiryEventBody -> string

    /// Native @2 payload: schemaVersion/inquiry/previousRevision/previousHead/revision,
    /// commandId/commandFingerprint/postStateFingerprint/events. Revisions are exact
    /// int64 decimal strings; bodies are { case: string, payload: native DTO }; all
    /// collections are arrays, and optional fields are explicit null. Persisted @2
    /// requires a complete sealed post-state fingerprint; preparation may carry null.
    val toWire: TransitionBatch -> obj
    val eventIdentity: (string -> string) -> TransitionBatch -> Wanxiangshu.Foundation.Identity.EventId
    val seal: (string -> string) -> InquiryState option -> TransitionBatch -> Result<EventEnvelope, CoreError>

    /// Encodes one sealed transition. Id derives from immutable command identity,
    /// not post-state, so altered bytes at the same identity remain a shared-engine
    /// collision. Parents must agree; only inline payloads are supported. ArtifactRef
    /// never becomes PayloadRef by spelling or hash guessing.
    val encode:
        (string -> string) ->
        TransitionBatch ->
        Wanxiangshu.Foundation.Identity.EventId option ->
            Result<EventEnvelope, CodecError>
