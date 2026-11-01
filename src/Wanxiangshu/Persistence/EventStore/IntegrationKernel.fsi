namespace Wanxiangshu.Persistence.EventStore

open Wanxiangshu.Foundation.Identity

type SemanticCutPlan = { ResetJson: string }

type IntegrationRule =
    { Name: string
      Initial: obj
      Accepts: EventEnvelope -> bool
      FaultScope: EventEnvelope -> string
      Integrate: obj -> EventEnvelope -> Result<obj, string>
      PlanCut: obj -> EventEnvelope -> string -> bool -> Result<SemanticCutPlan, string>
      ApplyCut: obj -> string -> Result<obj, string> }

type PreparedIntegration =
    { DurableEvents: EventEnvelope list
      Cuts: SemanticCut list
      Commit: unit -> unit }

type ICanonicalIntegrator =
    abstract ReloadLocal: commonDir: string -> Result<unit, string>
    abstract PrepareLive: events: EventEnvelope list -> Result<PreparedIntegration, string>
    abstract IsEventTypeKnown: eventType: string -> bool
    abstract TryCurrent: key: string -> obj option
    abstract TryEvent: eventId: EventId -> EventEnvelope option
    /// durable-events-012: committed inline payload content by content address.
    /// The Integrator owns every accepted envelope, so it owns the payload index
    /// too; no second reader over durable history may exist.
    abstract TryPayload: payloadRef: PayloadRef -> byte[] option
    abstract TryHeads: streamId: EventStreamId -> EventId list
    abstract TryHead: streamId: EventStreamId -> EventId option
    abstract AllHeads: unit -> EventId list

[<RequireQualifiedAccess>]
module IntegrationCurrent =
    val tryGet<'T> : key: string -> integrator: ICanonicalIntegrator -> 'T option
