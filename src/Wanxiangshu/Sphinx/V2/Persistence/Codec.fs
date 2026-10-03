namespace Wanxiangshu.Sphinx.V2.Persistence

open System
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx.V2.Core

/// The v2 batch ↔ canonical envelope codec.
///
/// WHAT[sphinx-v2-019]: one inquiry transition becomes one canonical EventEnvelope.
/// That is a deliberate choice made because the snapshot proved the store is used but
/// did not prove `Append [e1;e2;e3]` is business-atomic. Putting the whole transition in
/// a single envelope makes the unit of durability and the unit of semantics the same
/// thing, so a partial transition can never become the accepted current.
module Codec =

    /// The registered canonical event type. Derived from the Core vocabulary so the
    /// reducer, the codec and the shared whitelist cannot drift apart.
    let transitionEventType = SphinxV2EventTypes.strictTransition
    let legacyTransitionEventType = SphinxV2EventTypes.transition

    type CodecError = { Code: string; Message: string }

    let private error code message : Result<'value, CodecError> =
        Error { Code = code; Message = message }

    let private inquiryStream (inquiryId: InquiryId) : EventStreamId =
        EventStreamId.create ("sphinx-v2/" + InquiryId.value inquiryId)

    /// The event body tag the Integrator routes on; the payload carries the bytes the
    /// reducer rebuilds the body from.
    let bodyTag (body: InquiryEventBody) : string =
        match body with
        | InquiryEventBody.InquiryCreated _ -> "InquiryCreated"
        | InquiryEventBody.GoalAmended _ -> "GoalAmended"
        | InquiryEventBody.SnapshotRegistered _ -> "SnapshotRegistered"
        | InquiryEventBody.DecisionScopeOpened _ -> "DecisionScopeOpened"
        | InquiryEventBody.RoundOpened _ -> "RoundOpened"
        | InquiryEventBody.WorkPlanned _ -> "WorkPlanned"
        | InquiryEventBody.RoundClosed _ -> "RoundClosed"
        | InquiryEventBody.BudgetReserved _ -> "BudgetReserved"
        | InquiryEventBody.UsageSettled _ -> "UsageSettled"
        | InquiryEventBody.ReservationReleased _ -> "ReservationReleased"
        | InquiryEventBody.UsageOverrunRecorded _ -> "UsageOverrunRecorded"
        | InquiryEventBody.DispatchRequested _ -> "DispatchRequested"
        | InquiryEventBody.DispatchReceiptRecorded _ -> "DispatchReceiptRecorded"
        | InquiryEventBody.WorkAttemptTransitioned _ -> "WorkAttemptTransitioned"
        | InquiryEventBody.HostTerminalRecorded _ -> "HostTerminalRecorded"
        | InquiryEventBody.ResultAccepted _ -> "ResultAccepted"
        | InquiryEventBody.InterpretationPending _ -> "InterpretationPending"
        | InquiryEventBody.InterpretationApplied _ -> "InterpretationApplied"
        | InquiryEventBody.InterpretationFailed _ -> "InterpretationFailed"
        | InquiryEventBody.GraphPatched _ -> "GraphPatched"
        | InquiryEventBody.CertificateSlotsPatched _ -> "CertificateSlotsPatched"
        | InquiryEventBody.CertificateInvalidated _ -> "CertificateInvalidated"
        | InquiryEventBody.DecisionRecorded _ -> "DecisionRecorded"
        | InquiryEventBody.AnswerPrepared _ -> "AnswerPrepared"
        | InquiryEventBody.AnswerCommitted _ -> "AnswerCommitted"
        | InquiryEventBody.CancelRequested _ -> "CancelRequested"
        | InquiryEventBody.InquiryCancelled _ -> "InquiryCancelled"
        | InquiryEventBody.InquirySuspended _ -> "InquirySuspended"
        | InquiryEventBody.InquiryFailed _ -> "InquiryFailed"
        | InquiryEventBody.InquiryStatusChanged _ -> "InquiryStatusChanged"

    let private batchDecoder: Decoder<TransitionBatch> =
        BodyDto.exact
            [ "schemaVersion"
              "inquiry"
              "previousRevision"
              "previousHead"
              "revision"
              "commandId"
              "commandFingerprint"
              "postStateFingerprint"
              "events" ]
            (Decode.object (fun get ->
                { SchemaVersion =
                    get.Required.Field
                        "schemaVersion"
                        (Decode.string
                         |> Decode.andThen (fun value ->
                             if value = "2" then
                                 Decode.succeed value
                             else
                                 Decode.fail "unsupported Sphinx API version"))
                  InquiryId =
                    get.Required.Field
                        "inquiry"
                        (BodyDto.nonBlank
                         |> Decode.andThen (fun value ->
                             match InquiryId.tryCreate value with
                             | Ok id -> Decode.succeed id
                             | Error reason -> Decode.fail reason))
                  PreviousRevision = get.Required.Field "previousRevision" BodyDto.revision
                  PreviousHead =
                    get.Required.Field
                        "previousHead"
                        (Decode.option (
                            BodyDto.nonBlank
                            |> Decode.andThen (fun value ->
                                match Wanxiangshu.Sphinx.V2.Core.EventId.tryCreate value with
                                | Ok id -> Decode.succeed id
                                | Error reason -> Decode.fail reason)
                        ))
                  Revision = get.Required.Field "revision" BodyDto.revision
                  CommandId = get.Required.Field "commandId" BodyDto.nonBlank
                  CommandFingerprint = get.Required.Field "commandFingerprint" BodyDto.hash
                  PostStateFingerprint = get.Required.Field "postStateFingerprint" (Decode.option BodyDto.hash)
                  Events =
                    get.Required.Field
                        "events"
                        (Decode.list BodyDto.decoder
                         |> Decode.andThen (fun values ->
                             if List.isEmpty values then
                                 Decode.fail "empty transition batch"
                             else
                                 Decode.succeed values)) }))

    /// This is the only raw-to-domain ingress. Unknown fields/tags and malformed
    /// values fail as a whole; a caller cannot recover a prefix of the bodies.
    let decodeInput (raw: obj) : Result<TransitionBatch, CodecError> =
        Decode.fromValue "sphinx-transition@2" batchDecoder (unbox<JsonValue> raw)
        |> Result.mapError (fun reason ->
            { Code = "INVALID_TRANSITION_DTO"
              Message = reason })

    let decodeBody (raw: obj) : Result<InquiryEventBody, CodecError> =
        Decode.fromValue "sphinx-body@2" BodyDto.decoder (unbox<JsonValue> raw)
        |> Result.mapError (fun reason ->
            { Code = "INVALID_BODY_DTO"
              Message = reason })

    /// One native representation is used in input, durable payload and output.
    let toWire (batch: TransitionBatch) : obj =
        createObj
            [ "schemaVersion" ==> batch.SchemaVersion
              "inquiry" ==> InquiryId.value batch.InquiryId
              "previousRevision" ==> string (Revision.value batch.PreviousRevision)
              "previousHead"
              ==> (batch.PreviousHead
                   |> Option.map (Wanxiangshu.Sphinx.V2.Core.EventId.value >> box)
                   |> Option.defaultValue null)
              "revision" ==> string (Revision.value batch.Revision)
              "commandId" ==> batch.CommandId
              "commandFingerprint" ==> batch.CommandFingerprint
              "postStateFingerprint"
              ==> (batch.PostStateFingerprint |> Option.map box |> Option.defaultValue null)
              "events" ==> (batch.Events |> List.map Representation.body |> List.toArray) ]

    /// The complete post-state contains this identity. Deriving the identity from
    /// post-state would therefore be circular; only immutable command identity is used.
    let eventIdentity (digest: string -> string) (batch: TransitionBatch) : Identity.EventId =
        let derivation =
            createObj
                [ "eventType" ==> transitionEventType
                  "inquiry" ==> InquiryId.value batch.InquiryId
                  "revision" ==> string (Revision.value batch.Revision)
                  "commandId" ==> batch.CommandId
                  "commandFingerprint" ==> batch.CommandFingerprint ]

        Identity.EventId.create ("ev" + digest (CanonicalJson.canonicalJson derivation))

    let encode
        (digest: string -> string)
        (batch: TransitionBatch)
        (previousHead: Identity.EventId option)
        : Result<EventEnvelope, CodecError> =
        let parents =
            batch.PreviousHead
            |> Option.map (Wanxiangshu.Sphinx.V2.Core.EventId.value >> Identity.EventId.create)
            |> Option.toList

        if batch.SchemaVersion <> "2" then
            error "UNSUPPORTED_TRANSITION_VERSION" "new writes require strict @2"
        elif (previousHead |> Option.toList) <> parents then
            error "PARENT_MISMATCH" "envelope and batch must name the same parent"
        elif batch.PostStateFingerprint.IsNone then
            error
                "MISSING_POST_STATE_FINGERPRINT"
                "a durable @2 transition must be sealed against its complete post-state"
        else
            Ok
                { EventId = eventIdentity digest batch
                  StreamId = inquiryStream batch.InquiryId
                  EventType = transitionEventType
                  Parents = parents
                  Payload = toWire batch |> unbox<JsonValue>
                  // ArtifactRef is not PayloadRef. No external blob DTO is defined here.
                  PayloadRefs = [] }

    let private validateDecodedBatch
        (digest: string -> string)
        (envelope: EventEnvelope)
        (batch: TransitionBatch)
        : Result<TransitionBatch, CodecError> =
        let parents =
            batch.PreviousHead
            |> Option.map (Wanxiangshu.Sphinx.V2.Core.EventId.value >> Identity.EventId.create)
            |> Option.toList

        if envelope.StreamId <> inquiryStream batch.InquiryId then
            error "STREAM_MISMATCH" "envelope stream does not match its inquiry"
        elif EventParents.canonicalize envelope.Parents <> parents then
            error "PARENT_MISMATCH" "inner and outer parent edges disagree"
        elif envelope.EventId <> eventIdentity digest batch then
            error "EVENT_ID_MISMATCH" "envelope identity does not match its immutable command identity"
        elif batch.PostStateFingerprint.IsNone then
            error "MISSING_POST_STATE_FINGERPRINT" "a durable @2 transition is not sealed"
        else
            Ok batch

    let decode (digest: string -> string) (envelope: EventEnvelope) : Result<TransitionBatch, CodecError> =
        if envelope.EventType = legacyTransitionEventType then
            error
                "LEGACY_TRANSITION_UNSUPPORTED"
                "@1 bytes are frozen and cannot be proved to reconstruct a complete inquiry; explicit offline recovery is required"
        elif envelope.EventType <> transitionEventType then
            error "UNSUPPORTED_TRANSITION_TYPE" "expected sphinx/v2-transition@2"
        elif not (List.isEmpty envelope.PayloadRefs) then
            error
                "UNSUPPORTED_PAYLOAD_DEPENDENCY"
                "@2 has inline canonical payloads only; no artifact string is interpreted as an external blob hash"
        else
            decodeInput (box envelope.Payload)
            |> Result.bind (validateDecodedBatch digest envelope)

    /// Pure preparation, not publication. The same Core operation later validates the
    /// sealed bytes under the shared engine. No Current is changed before append.
    let seal
        (digest: string -> string)
        (prior: InquiryState option)
        (batch: TransitionBatch)
        : Result<EventEnvelope, CoreError> =
        let eventId =
            eventIdentity digest batch
            |> Identity.EventId.value
            |> Wanxiangshu.Sphinx.V2.Core.EventId.create

        Reducer.applyTransition digest eventId prior batch
        |> Result.bind (fun next ->
            let sealedBatch =
                { batch with
                    PostStateFingerprint = Some(Representation.fingerprint digest next) }

            let parent =
                batch.PreviousHead
                |> Option.map (Wanxiangshu.Sphinx.V2.Core.EventId.value >> Identity.EventId.create)

            encode digest sealedBatch parent
            |> Result.mapError (fun fault ->
                { Code = fault.Code
                  Message = fault.Message }
                : CoreError))
