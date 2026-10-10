namespace Wanxiangshu.Mission.Planning

open Thoth.Json
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

module PlanEventCodec =
    val encodePayload: event: PlanEvent -> JsonValue
    val decodePayload: eventType: string -> payload: JsonValue -> Result<PlanEvent, string>
    val toEnvelope: workId: PlanWorkId -> event: PlanEvent -> parents: EventId list -> EventEnvelope
    val tryDecodeEnvelope: envelope: EventEnvelope -> Result<PlanEvent, string>
    val encodeCanonicalJson: envelope: EventEnvelope -> string
