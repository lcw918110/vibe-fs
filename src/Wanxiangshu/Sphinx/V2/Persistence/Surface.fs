namespace Wanxiangshu.Sphinx.V2.Persistence

open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Sphinx.V2.Core
open Wanxiangshu.Sphinx.V2.Runtime

module Surface =
    let private failure code message =
        box
            {| ok = false
               error = box {| code = code; message = message |} |}

    let private success value = box {| ok = true; value = value |}

    let private currentError fault =
        match fault with
        | CurrentError.SemanticRejected reason -> failure "SemanticCut" reason
        | CurrentError.DomainConflict(DomainConflict.ConcurrentHeads(stream, heads)) ->
            box
                {| ok = false
                   error =
                    box
                        {| code = "DomainConflict"
                           stream = EventStreamId.value stream
                           heads = heads |> List.map Identity.EventId.value |> List.toArray |} |}

    let private current (handle: EventStoreHandle) inquiryId =
        handle.Store.TryCurrent Integrator.currentKey
        |> Option.map (fun value -> Integrator.tryState value inquiryId)
        |> Option.defaultValue (Ok None)

    let private inquiry raw =
        Decode.fromValue "inquiryId" BodyDto.nonBlank (unbox<JsonValue> raw)
        |> Result.bind InquiryId.tryCreate

    let private eventView (value: EventEnvelope) =
        box
            {| id = Identity.EventId.value value.EventId
               stream = EventStreamId.value value.StreamId
               ``type`` = value.EventType
               parents = value.Parents |> List.map Identity.EventId.value |> List.toArray
               payload = box value.Payload
               payloadRefs = value.PayloadRefs |> List.map PayloadRef.value |> List.toArray |}

    let canonicalizeBody (raw: obj) =
        match Codec.decodeBody raw with
        | Ok body -> success (Representation.body body)
        | Error fault -> failure fault.Code fault.Message

    let canonicalizeTransition (raw: obj) =
        match Codec.decodeInput raw with
        | Ok batch -> success (Codec.toWire batch)
        | Error fault -> failure fault.Code fault.Message

    let prepareTransition (handle: EventStoreHandle, digest: string -> string, raw: obj) =
        let prepared =
            Codec.decodeInput raw
            |> Result.mapError (fun fault -> fault.Message)
            |> Result.bind (fun batch ->
                let published =
                    handle.Store.TryCurrent Integrator.currentKey |> Option.defaultValue null

                Integrator.parentState published batch.InquiryId batch.PreviousHead
                |> Result.bind (fun prior ->
                    Codec.seal digest prior batch |> Result.mapError (fun fault -> fault.Message)))

        match prepared with
        | Ok envelope -> success (eventView envelope)
        | Error reason -> failure "TRANSITION_REJECTED" reason

    let canonicalCurrent (handle: EventStoreHandle, digest: string -> string, inquiryId: string) =
        match inquiry (box inquiryId) with
        | Error reason -> failure "INVALID_INQUIRY_ID" reason
        | Ok inquiryId ->
            match current handle inquiryId with
            | Error fault -> currentError fault
            | Ok None -> failure "UNKNOWN_INQUIRY" "inquiry has no accepted durable creation"
            | Ok(Some state) ->
                box
                    {| ok = true
                       value = Representation.state state
                       stateHash = Representation.fingerprint digest state |}

    let admitCancel (handle: EventStoreHandle, raw: obj) =
        let decoder =
            BodyDto.exact
                [ "inquiry"; "commandId"; "commandFingerprint"; "reason" ]
                (Decode.object (fun get ->
                    get.Required.Field "inquiry" BodyDto.nonBlank,
                    get.Required.Field "commandId" BodyDto.nonBlank,
                    get.Required.Field "commandFingerprint" BodyDto.hash,
                    get.Required.Field "reason" Decode.string))

        let parsed =
            Decode.fromValue "cancel-admission" decoder (unbox<JsonValue> raw)
            |> Result.bind (fun (id, command, fingerprint, reason) ->
                InquiryId.tryCreate id
                |> Result.map (fun id -> id, command, fingerprint, reason))

        match parsed with
        | Error reason -> failure "INVALID_COMMAND_DTO" reason
        | Ok(inquiryId, commandId, fingerprint, reason) ->
            match current handle inquiryId with
            | Error fault -> currentError fault
            | Ok None -> failure "UNKNOWN_INQUIRY" "inquiry has no accepted durable creation"
            | Ok(Some state) ->
                match Admission.admitCommand state commandId fingerprint (InquiryCommand.CancelCommand reason) with
                | Error fault -> failure fault.Code fault.Message
                | Ok(IdempotencyOutcome.Conflict message) -> failure "COMMAND_CONFLICT" message
                | Ok(IdempotencyOutcome.Fresh _) -> success (box {| outcome = "fresh" |})
                | Ok(IdempotencyOutcome.Replay revision) ->
                    let receipt = InquiryState.commandReceipt state commandId |> Option.get

                    success (
                        box
                            {| outcome = "replayed"
                               revision = string (Revision.value revision)
                               eventId = Wanxiangshu.Sphinx.V2.Core.EventId.value receipt.EventId |}
                    )
