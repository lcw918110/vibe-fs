namespace Wanxiangshu.Persistence.Journal

open System
open Fable.Core
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.EventStore

/// JS-native owner surface for Journal.Envelope ↔ universal EventEnvelope codec.
/// No Journal DU, typed ID, or Fable collection crosses this boundary.
[<RequireQualifiedAccess>]
module JournalCodecSurface =

    let JournalEnvelopeEventType = EventStoreJournalCodec.JournalEnvelopeEventType

    let private text (value: obj) : string =
        if isNull value then "" else string value

    let private streamOfJs (value: obj) : StreamId =
        match text (value?kind) with
        | "Workspace" -> Workspace
        | "Session" -> Session(SessionId.create (text (value?id)))
        | "Child" -> Child(ChildId.create (text (value?id)))
        | "Process" -> Process(ProcessId.create (text (value?id)))
        | other -> failwith $"JournalCodecSurface: unknown stream '{other}'"

    let private roleOf (value: obj) =
        Roles.tryParseRole (text value)
        |> Option.defaultWith (fun () -> failwith $"JournalCodecSurface: unknown role '{text value}'")

    let private originOf (value: obj) =
        match text value with
        | "ResolvedAtRoot" -> PersonaOrigin.ResolvedAtRoot
        | "InheritedFromOwner" -> PersonaOrigin.InheritedFromOwner
        | other -> failwith $"JournalCodecSurface: unknown persona origin '{other}'"

    let private identityInputOfJs (value: obj) =
        let selected =
            let p = text (value?participant)

            if System.String.IsNullOrWhiteSpace p then
                text (value?selectedAgent)
            else
                p

        let roleVal =
            let r = text (value?role)

            if System.String.IsNullOrWhiteSpace r then
                text (value?canonicalRole)
            else
                r

        { SelectedAgent = selected
          Role =
            if roleVal = "bookkeeper" then
                None
            else
                Some(roleOf roleVal)
          Persona = text (value?persona)
          PersonaCatalogVersion = unbox<int> (value?personaCatalogVersion)
          Origin = originOf (value?origin) }

    let private identitySeedOfJs (value: obj) =
        let identityInput = identityInputOfJs (value?participantIdentity)

        match text (value?kind) with
        | "RootSelection" -> PromptAuthority.IdentitySeedInput.RootSelectionInput identityInput
        | "InheritedFromOwner" ->
            PromptAuthority.IdentitySeedInput.InheritedFromOwnerInput
                { OwnerSessionId = SessionId.create (text (value?ownerSession))
                  OwnerLogicalRunId = LogicalRunId.create (text (value?ownerLogicalRun))
                  OwnerAuthorityRootUserMessageId = AuthorityRootUserMessageId.create (text (value?ownerAuthorityRoot))
                  ParticipantIdentity = identityInput }
        | other -> failwith $"JournalCodecSurface: unknown identity seed '{other}'"
        |> PromptIdentitySeed.rehydrate
        |> Result.defaultWith (fun error -> failwith $"JournalCodecSurface: invalid identity seed: {error}")

    let private identityToJs evidence =
        box
            {| participant = ParticipantIdentity.selectedAgent evidence
               role = ParticipantIdentity.roleLabel evidence
               persona = ParticipantIdentity.persona evidence
               personaCatalogVersion = ParticipantIdentity.personaCatalogVersion evidence
               origin =
                match ParticipantIdentity.origin evidence with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedToJs seed =
        let participantIdentity =
            PromptAuthority.identitySeedParticipantIdentity seed |> identityToJs

        match PromptAuthority.identitySeedOwner seed with
        | None ->
            box
                {| kind = "RootSelection"
                   ownerSession = null
                   ownerLogicalRun = null
                   ownerAuthorityRoot = null
                   participantIdentity = participantIdentity |}
        | Some(ownerSession, ownerLogicalRun, ownerAuthorityRoot) ->
            box
                {| kind = "InheritedFromOwner"
                   ownerSession = SessionId.value ownerSession
                   ownerLogicalRun = LogicalRunId.value ownerLogicalRun
                   ownerAuthorityRoot = AuthorityRootUserMessageId.value ownerAuthorityRoot
                   participantIdentity = participantIdentity |}

    let private streamToJs (stream: StreamId) : obj =
        match stream with
        | Workspace -> box {| kind = "Workspace" |}
        | Session id ->
            box
                {| kind = "Session"
                   id = SessionId.value id |}
        | Child id ->
            box
                {| kind = "Child"
                   id = ChildId.value id |}
        | Process id ->
            box
                {| kind = "Process"
                   id = ProcessId.value id |}

    let private factOfJs (value: obj) : Fact =
        let family = text (value?family)
        let case = text (value?case)
        let payload = unbox<obj> (value?payload)

        match family, case with
        | "Prompt", "AuthorityRootAccepted" ->
            Fact.Agent(
                AgentFact.Prompt(
                    PromptFactCases.AuthorityRootAccepted
                        { SchemaVersion = unbox<int> (payload?SchemaVersion)
                          SessionId = SessionId.create (text (payload?SessionId))
                          LogicalRunId = LogicalRunId.create (text (payload?LogicalRunId))
                          AuthorityRootUserMessageId =
                            AuthorityRootUserMessageId.create (text (payload?AuthorityRootUserMessageId))
                          AuthorityKind = text (payload?AuthorityKind)
                          IdentitySeed = identitySeedOfJs (payload?IdentitySeed) }
                )
            )
        | familyName, caseName -> failwith $"JournalCodecSurface: unknown fact '{familyName}.{caseName}'"

    let private factToJs (fact: Fact) : obj =
        match fact with
        | Fact.Agent(AgentFact.Prompt(PromptFactCases.AuthorityRootAccepted payload)) ->
            box
                {| family = "Prompt"
                   case = "AuthorityRootAccepted"
                   payload =
                    {| SchemaVersion = payload.SchemaVersion
                       SessionId = SessionId.value payload.SessionId
                       LogicalRunId = LogicalRunId.value payload.LogicalRunId
                       AuthorityRootUserMessageId = AuthorityRootUserMessageId.value payload.AuthorityRootUserMessageId
                       AuthorityKind = payload.AuthorityKind
                       IdentitySeed = identitySeedToJs payload.IdentitySeed |} |}
        | _ ->
            box
                {| family = "Unknown"
                   case = "Unknown"
                   payload = {| |} |}

    let private envelopeOfJs (value: obj) : Envelope =
        { RuntimeId = RuntimeId.create (text (value?runtime))
          LocalSeq = LocalSeq.create (int64 (unbox<int> (value?seq)))
          ObservedAt = DateTimeOffset.Parse(text (value?observedAt))
          EventId = EventId.create (text (value?id))
          Stream = streamOfJs (value?stream)
          ProviderRun =
            if isNull (value?providerRun) then
                None
            else
                Some(ProviderRunIdentity.create (text (value?providerRun)))
          Fact = factOfJs (value?fact) }

    let private envelopeToJs (envelope: Envelope) : obj =
        box
            {| runtime = RuntimeId.value envelope.RuntimeId
               seq = LocalSeq.value envelope.LocalSeq
               observedAt = envelope.ObservedAt.ToOffset(TimeSpan.Zero).ToString("O")
               id = EventId.value envelope.EventId
               stream = streamToJs envelope.Stream
               providerRun =
                match envelope.ProviderRun with
                | None -> null
                | Some run -> box (ProviderRunIdentity.value run)
               fact = factToJs envelope.Fact
               line = Envelope.serialize envelope |}

    [<Emit("Buffer.from($0, 'base64')")>]
    let private bytesOfBase64 (value: string) : byte[] = jsNative

    let private eventOfJs (value: obj) : EventEnvelope =
        let payload =
            match value?payload with
            | null -> null
            | raw -> JS.JSON.stringify raw

        { EventId = EventId.create (text (value?eventId))
          StreamId = EventStreamId.create (text (value?streamId))
          EventType = text (value?eventType)
          Parents =
            if isNull (value?parents) then
                []
            else
                unbox<string array> value?parents |> Array.toList |> List.map EventId.create
          Payload = unbox<JsonValue> (JS.JSON.parse (if isNull payload then "null" else payload))
          PayloadRefs =
            if isNull (value?payloadRefs) then
                []
            else
                unbox<string array> value?payloadRefs
                |> Array.toList
                |> List.map PayloadRef.create
          Payloads =
            if isNull (value?payloads) then
                Map.empty
            else
                emitJsExpr (value?payloads) "Object.keys($0 || {}).map(function (key) { return [key, $0[key]]; })"
                |> unbox<(string * string) array>
                |> Array.fold
                    (fun acc (key, base64) -> Map.add (PayloadRef.create key) (bytesOfBase64 base64) acc)
                    Map.empty }
        |> EventEnvelope.normalize

    let private eventToJs (event: EventEnvelope) : obj =
        let body =
            CanonicalEventCodec.encode event
            |> (fun value -> value.TrimEnd('\n'))
            |> JS.JSON.parse

        box
            {| eventId = EventId.value event.EventId
               streamId = EventStreamId.value event.StreamId
               eventType = event.EventType
               parents = event.Parents |> List.map EventId.value |> List.toArray
               payload = body?payload
               payloadRefs = event.PayloadRefs |> List.map PayloadRef.value |> List.toArray |}

    let private envelopeForEvent (event: EventEnvelope) : obj =
        match EventStoreJournalCodec.tryDecode event with
        | Ok envelope -> envelopeToJs envelope
        | Error error -> box {| ok = false; error = error |}

    /// Encode one JS-native journal envelope into a JS-native event object.
    let encode (parents: string array) (payloadRefs: string array) (envelope: obj) : obj =
        let parentIds = parents |> Array.toList |> List.map EventId.create
        let refs = payloadRefs |> Array.toList |> List.map PayloadRef.create

        EventStoreJournalCodec.encode parentIds refs (envelopeOfJs envelope)
        |> eventToJs

    /// Decode one JS-native event object into a normalized journal envelope.
    let decode (event: obj) : obj =
        match EventStoreJournalCodec.tryDecode (eventOfJs event) with
        | Ok envelope ->
            box
                {| ok = true
                   value = envelopeToJs envelope |}
        | Error error -> box {| ok = false; error = error |}

    let encodeStreamId (stream: obj) : string =
        EventStoreJournalCodec.encodeStreamId (streamOfJs stream) |> EventStreamId.value

    let decodeStreamId (streamId: string) : obj =
        match EventStoreJournalCodec.tryDecodeStreamId (EventStreamId.create streamId) with
        | Ok stream ->
            box
                {| ok = true
                   value = streamToJs stream |}
        | Error error -> box {| ok = false; error = error |}

    let serialize (envelope: obj) : string =
        envelopeOfJs envelope |> Envelope.serialize

    let deserialize (line: string) : obj =
        match Envelope.deserialize line with
        | Ok envelope ->
            box
                {| ok = true
                   value = envelopeToJs envelope |}
        | Error error -> box {| ok = false; error = error |}

    let compareSortKey (left: obj) (right: obj) : int =
        Envelope.compareSortKey (envelopeOfJs left) (envelopeOfJs right)

    let kWayMerge (streams: obj array) : obj array =
        streams
        |> Array.toList
        |> List.collect (fun stream -> unbox<obj array> stream |> Array.toList |> List.map envelopeOfJs)
        |> List.sortWith Envelope.compareSortKey
        |> List.map envelopeToJs
        |> List.toArray

    let decodeEventEnvelope (event: obj) : obj = envelopeForEvent (eventOfJs event)
