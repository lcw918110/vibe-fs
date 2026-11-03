namespace Wanxiangshu.Persistence.EventStore

open Wanxiangshu.Foundation.Identity

type SemanticCutPlan = { ResetJson: string }

/// 每个领域规则只接收自己的 Current 和当前事件。
type IntegrationRule =
    {
        Name: string
        Initial: obj
        Accepts: EventEnvelope -> bool
        /// Semantic-failure quarantine domain. Rules with independent durable streams
        /// must not let one bad stream suppress unrelated streams in the same rule.
        FaultScope: EventEnvelope -> string
        Integrate: obj -> EventEnvelope -> Result<obj, string>
        PlanCut: obj -> EventEnvelope -> string -> bool -> Result<SemanticCutPlan, string>
        ApplyCut: obj -> string -> Result<obj, string>
    }

type PreparedIntegration =
    { DurableEvents: EventEnvelope list
      Cuts: SemanticCut list
      Commit: unit -> unit }

/// Read-only view exposed by the unique canonical Integrator.
type ICanonicalIntegrator =
    /// Boot/reload ownership: only the canonical Integrator opens local event
    /// history for the purpose of deriving Current.
    abstract ReloadLocal: commonDir: string -> Result<unit, string>
    /// Validate a live batch against Current and return the commit closure.
    /// EventStore invokes the closure only after the complete canonical lines are durable.
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
    let tryGet<'T> (key: string) (integrator: ICanonicalIntegrator) : 'T option =
        integrator.TryCurrent key |> Option.map unbox<'T>
