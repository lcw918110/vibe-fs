namespace Wanxiangshu.Mission.Planning

open System
open Thoth.Json
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

module PlanEventCodec =

    // --- Encoders (keys strictly ascending by Unicode codepoint) ---

    let private encodeWorkOpened (workId: PlanWorkId) (root: string) : JsonValue =
        Encode.object
            [ "root", Encode.string root
              "workId", Encode.string (PlanWorkId.value workId) ]

    let private encodeDevOpsBound (workId: PlanWorkId) (devopsId: string) (target: string option) : JsonValue =
        Encode.object
            [ "devopsId", Encode.string devopsId
              "target",
              match target with
              | Some t -> Encode.string t
              | None -> Encode.nil
              "workId", Encode.string (PlanWorkId.value workId) ]

    let private encodeIncumbencyOpened
        (workId: PlanWorkId)
        (incumbencyId: PlanIncumbencyId)
        (stage: PlanStage)
        (openingCursor: XTraceCursor)
        : JsonValue =
        Encode.object
            [ "incumbencyId", Encode.string (PlanIncumbencyId.value incumbencyId)
              "openingCursor", Encode.int64 (XTraceCursor.sequence openingCursor)
              "stage", Encode.string (PlanStage.render stage)
              "workId", Encode.string (PlanWorkId.value workId) ]

    let private encodeIncumbencyRetired
        (incumbencyId: PlanIncumbencyId)
        (outcome: PlanRetirementOutcome)
        (retirementCursor: XTraceCursor)
        : JsonValue =
        Encode.object
            [ "incumbencyId", Encode.string (PlanIncumbencyId.value incumbencyId)
              "outcome", Encode.string (PlanRetirementOutcome.render outcome)
              "retirementCursor", Encode.int64 (XTraceCursor.sequence retirementCursor) ]

    let private encodeDelivered
        (incumbencyId: PlanIncumbencyId)
        (workId: PlanWorkId)
        (digest: string)
        (path: string)
        : JsonValue =
        Encode.object
            [ "digest", Encode.string digest
              "incumbencyId", Encode.string (PlanIncumbencyId.value incumbencyId)
              "path", Encode.string path
              "workId", Encode.string (PlanWorkId.value workId) ]

    let private encodeAskPending
        (workId: PlanWorkId)
        (incumbencyId: PlanIncumbencyId)
        (question: string)
        (cursor: XTraceCursor)
        : JsonValue =
        Encode.object
            [ "cursor", Encode.int64 (XTraceCursor.sequence cursor)
              "incumbencyId", Encode.string (PlanIncumbencyId.value incumbencyId)
              "question", Encode.string question
              "workId", Encode.string (PlanWorkId.value workId) ]

    let private encodeAskResolved (incumbencyId: PlanIncumbencyId) (cursor: XTraceCursor) : JsonValue =
        Encode.object
            [ "cursor", Encode.int64 (XTraceCursor.sequence cursor)
              "incumbencyId", Encode.string (PlanIncumbencyId.value incumbencyId) ]

    let encodePayload (event: PlanEvent) : JsonValue =
        match event with
        | PlanEvent.PlanWorkOpened(workId, root) -> encodeWorkOpened workId root
        | PlanEvent.PlanDevOpsBound(workId, devopsId, target) -> encodeDevOpsBound workId devopsId target
        | PlanEvent.PlanIncumbencyOpened(workId, incumbencyId, stage, openingCursor) ->
            encodeIncumbencyOpened workId incumbencyId stage openingCursor
        | PlanEvent.PlanIncumbencyRetired(incumbencyId, outcome, retirementCursor) ->
            encodeIncumbencyRetired incumbencyId outcome retirementCursor
        | PlanEvent.PlanDelivered(incumbencyId, workId, digest, path) -> encodeDelivered incumbencyId workId digest path
        | PlanEvent.PlanAskPending(workId, incumbencyId, question, cursor) ->
            encodeAskPending workId incumbencyId question cursor
        | PlanEvent.PlanAskResolved(incumbencyId, cursor) -> encodeAskResolved incumbencyId cursor

    // --- Decoders ---

    let private decodeWorkOpened: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let root = get.Required.Field "root" Decode.string
            let workId = get.Required.Field "workId" Decode.string |> PlanWorkId.create
            PlanEvent.PlanWorkOpened(workId, root))

    let private decodeDevOpsBound: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let devopsId = get.Required.Field "devopsId" Decode.string
            let target = get.Optional.Field "target" Decode.string
            let workId = get.Required.Field "workId" Decode.string |> PlanWorkId.create
            PlanEvent.PlanDevOpsBound(workId, devopsId, target))

    let private decodeIncumbencyOpened: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let incumbencyId =
                get.Required.Field "incumbencyId" Decode.string |> PlanIncumbencyId.create

            let cursorSeq = get.Required.Field "openingCursor" Decode.int64
            let stageStr = get.Required.Field "stage" Decode.string
            let workId = get.Required.Field "workId" Decode.string |> PlanWorkId.create

            match PlanStage.ofString stageStr with
            | Some st -> PlanEvent.PlanIncumbencyOpened(workId, incumbencyId, st, XTraceCursor.create cursorSeq)
            | None -> failwith (sprintf "Unrecognized plan stage: %s" stageStr))

    let private decodeIncumbencyRetired: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let incumbencyId =
                get.Required.Field "incumbencyId" Decode.string |> PlanIncumbencyId.create

            let outcomeStr = get.Required.Field "outcome" Decode.string
            let cursorSeq = get.Required.Field "retirementCursor" Decode.int64

            match PlanRetirementOutcome.ofString outcomeStr with
            | Some outc -> PlanEvent.PlanIncumbencyRetired(incumbencyId, outc, XTraceCursor.create cursorSeq)
            | None -> failwith (sprintf "Unrecognized plan retirement outcome: %s" outcomeStr))

    let private decodeDelivered: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let digest = get.Required.Field "digest" Decode.string

            let incumbencyId =
                get.Required.Field "incumbencyId" Decode.string |> PlanIncumbencyId.create

            let path = get.Required.Field "path" Decode.string
            let workId = get.Required.Field "workId" Decode.string |> PlanWorkId.create
            PlanEvent.PlanDelivered(incumbencyId, workId, digest, path))

    let private decodeAskPending: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let cursorSeq = get.Required.Field "cursor" Decode.int64

            let incumbencyId =
                get.Required.Field "incumbencyId" Decode.string |> PlanIncumbencyId.create

            let question = get.Required.Field "question" Decode.string
            let workId = get.Required.Field "workId" Decode.string |> PlanWorkId.create
            PlanEvent.PlanAskPending(workId, incumbencyId, question, XTraceCursor.create cursorSeq))

    let private decodeAskResolved: Decoder<PlanEvent> =
        Decode.object (fun get ->
            let cursorSeq = get.Required.Field "cursor" Decode.int64

            let incumbencyId =
                get.Required.Field "incumbencyId" Decode.string |> PlanIncumbencyId.create

            PlanEvent.PlanAskResolved(incumbencyId, XTraceCursor.create cursorSeq))

    let decodePayload (eventType: string) (payload: JsonValue) : Result<PlanEvent, string> =
        if eventType = PlanEventTypes.WorkOpened then
            Decode.fromValue "$" decodeWorkOpened payload
        elif eventType = PlanEventTypes.DevOpsBound then
            Decode.fromValue "$" decodeDevOpsBound payload
        elif eventType = PlanEventTypes.IncumbencyOpened then
            Decode.fromValue "$" decodeIncumbencyOpened payload
        elif eventType = PlanEventTypes.IncumbencyRetired then
            Decode.fromValue "$" decodeIncumbencyRetired payload
        elif eventType = PlanEventTypes.Delivered then
            Decode.fromValue "$" decodeDelivered payload
        elif eventType = PlanEventTypes.AskPending then
            Decode.fromValue "$" decodeAskPending payload
        elif eventType = PlanEventTypes.AskResolved then
            Decode.fromValue "$" decodeAskResolved payload
        else
            Error(sprintf "Unknown plan event type: %s" eventType)

    let private eventTypeOf (event: PlanEvent) : string =
        match event with
        | PlanEvent.PlanWorkOpened _ -> PlanEventTypes.WorkOpened
        | PlanEvent.PlanDevOpsBound _ -> PlanEventTypes.DevOpsBound
        | PlanEvent.PlanIncumbencyOpened _ -> PlanEventTypes.IncumbencyOpened
        | PlanEvent.PlanIncumbencyRetired _ -> PlanEventTypes.IncumbencyRetired
        | PlanEvent.PlanDelivered _ -> PlanEventTypes.Delivered
        | PlanEvent.PlanAskPending _ -> PlanEventTypes.AskPending
        | PlanEvent.PlanAskResolved _ -> PlanEventTypes.AskResolved

    let toEnvelope (workId: PlanWorkId) (event: PlanEvent) (parents: EventId list) : EventEnvelope =
        let eventId = EventId.create (Guid.NewGuid().ToString("N"))
        let streamId = EventStreamId.create ("plan/" + PlanWorkId.value workId)
        let eventType = eventTypeOf event
        let payload = encodePayload event

        EventEnvelope.normalize
            { EventId = eventId
              StreamId = streamId
              EventType = eventType
              Parents = parents
              Payload = payload
              PayloadRefs = []
              Payloads = Map.empty }

    let tryDecodeEnvelope (envelope: EventEnvelope) : Result<PlanEvent, string> =
        decodePayload envelope.EventType envelope.Payload

    let encodeCanonicalJson (envelope: EventEnvelope) : string = CanonicalEventCodec.encode envelope
