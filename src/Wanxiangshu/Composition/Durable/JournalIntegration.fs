namespace Wanxiangshu.Composition.Durable

open Fable.Core
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

/// Registered Journal oracle: one EventEnvelope in, one ProjectionSet out.
[<RequireQualifiedAccess>]
module JournalIntegration =
    /// The writer (process RuntimeId) that committed a Journal envelope, read
    /// straight off the canonical payload without a second full decode.
    [<Emit("(($0 != null && Array.isArray($0.RuntimeId) && typeof $0.RuntimeId[1] === 'string') ? $0.RuntimeId[1] : null)")>]
    let private payloadWriter (payload: obj) : string = jsNative

    /// Fault isolation is one writer's chain inside one Journal stream.
    ///
    /// A writer commits each fact against its own Current; a fact another
    /// process committed concurrently into the same session stream is not a
    /// causal predecessor of it. When the k-way merge interleaves two writers
    /// and a fact of writer Y is rejected (e.g. a second process admitting a
    /// conflicting HumanRoot into a session already owned by writer X), only
    /// Y's later facts in that stream may be quarantined. Scoping the fault to
    /// the whole stream would retroactively erase X's committed facts (its
    /// HandleLinked / work admissions) from every later reload, and the
    /// running owner then refuses its own child completion as "never
    /// canonically admitted" (join: TerminalMaterializationFailed).
    let private faultScope (envelope: EventEnvelope) : string =
        let stream = EventStreamId.value envelope.StreamId

        match payloadWriter (box envelope.Payload) with
        | null -> stream
        | writer -> stream + "@" + writer

    let rule: IntegrationRule =
        { Name = "Journal"
          Initial = box Fold.empty
          Accepts = fun envelope -> envelope.EventType = EventStoreJournalCodec.JournalEnvelopeEventType
          // A Journal EventStreamId is the durable isolation boundary. A semantic
          // rejection in one session/child/process stream must not black out other
          // Journal streams while waiting for that stream's cut-tail; within a
          // stream the boundary is the committing writer (see `faultScope`).
          FaultScope = faultScope
          Integrate =
            fun current envelope ->
                match EventStoreJournalCodec.tryDecode envelope with
                | Error error when FactCodec.isIgnoredLegacyDecodeError error -> Ok current
                | Error error -> Error error
                | Ok journalEnvelope ->
                    Fold.foldEnvelope (unbox<ProjectionSet> current) journalEnvelope
                    |> Result.map box
                    |> Result.mapError (fun rejection ->
                        sprintf "Journal fact '%s' rejected: %s" rejection.Fact rejection.Reason)
          PlanCut = fun _ _ _ _ -> Ok { ResetJson = "{}" }
          ApplyCut = fun current _ -> Ok current }
