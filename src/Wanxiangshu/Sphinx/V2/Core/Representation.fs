namespace Wanxiangshu.Sphinx.V2.Core

open Fable.Core.JsInterop
open Wanxiangshu.Foundation

module Representation =
    let private record fields = createObj fields

    let private optional view value =
        value |> Option.map view |> Option.defaultValue null

    let private items view values =
        values |> List.map view |> List.toArray |> box

    let private strings values = values |> List.toArray |> box
    let private revision value = box (string (Revision.value value))
    let private attempt value = box (float (Attempt.value value))

    let private pairs key view values =
        values
        |> Map.toList
        |> items (fun (name, value) -> record [ "key", box (key name); "value", view value ])

    let private resources values = pairs id box values

    let private workIds values =
        values |> Set.toList |> List.map WorkId.value |> strings

    let private nodeIds values =
        values |> Set.toList |> List.map NodeId.value |> strings

    let private schema (value: SchemaRef) =
        record [ "id", box value.Id; "hash", box value.Hash ]

    let private envelope (value: JsonEnvelope) =
        record
            [ "schema", schema value.Schema
              "canonicalPayload", box value.CanonicalPayload ]

    let private goal (value: GoalSpec) =
        record
            [ "goalId", box (GoalId.value value.GoalId)
              "revision", revision value.Revision
              "originalText", box value.OriginalText
              "constraints", strings value.Constraints
              "materialRefs", value.MaterialRefs |> List.map ArtifactRef.value |> strings
              "authorizationRef", box value.AuthorizationRef
              "createdBy", box value.CreatedBy
              "amendments",
              value.Amendments
              |> items (fun (amendment: GoalAmendment) ->
                  record
                      [ "authorizedBy", box amendment.AuthorizedBy
                        "revision", revision amendment.Revision
                        "addedConstraints", strings amendment.AddedConstraints
                        "replacedText", optional box amendment.ReplacedText ]) ]

    let private resourceSpec (value: ResourceSpec) =
        let kind =
            match value.Kind with
            | ResourceKind.Consumed name -> record [ "case", box "Consumed"; "unitName", box name ]
            | ResourceKind.Capacity name -> record [ "case", box "Capacity"; "slotName", box name ]

        record
            [ "name", box value.Name
              "kind", kind
              "authorizedLimit", box value.AuthorizedLimit ]

    let private workSpec (value: WorkSpec) =
        record
            [ "id", box (WorkId.value value.Id)
              "attempt", attempt value.Attempt
              "fence", box (Fence.value value.Fence)
              "roundId", optional (RoundId.value >> box) value.RoundId
              "planId", box (PlanId.value value.PlanId)
              "producer", box value.Producer
              "capability", box value.Capability
              "input", optional envelope value.Input
              "outputSchema", optional schema value.OutputSchema
              "dependencies", workIds value.Dependencies
              "conflictKeys", value.ConflictKeys |> Set.toList |> strings
              "physicalRef", optional box value.PhysicalRef
              "reserved", resources value.Reserved ]

    let private workState value =
        match value with
        | WorkState.Planned -> record [ "case", box "Planned" ]
        | WorkState.Ready -> record [ "case", box "Ready" ]
        | WorkState.Leased fence -> record [ "case", box "Leased"; "fence", box (Fence.value fence) ]
        | WorkState.Running(fence, physical) ->
            record
                [ "case", box "Running"
                  "fence", box (Fence.value fence)
                  "physicalRef", box physical ]
        | WorkState.InputRequired fence -> record [ "case", box "InputRequired"; "fence", box (Fence.value fence) ]
        | WorkState.Succeeded value -> record [ "case", box "Succeeded"; "attempt", attempt value ]
        | WorkState.Failed value -> record [ "case", box "Failed"; "attempt", attempt value ]
        | WorkState.CancelRequested value -> record [ "case", box "CancelRequested"; "attempt", attempt value ]
        | WorkState.Cancelled value -> record [ "case", box "Cancelled"; "attempt", attempt value ]
        | WorkState.Superseded value -> record [ "case", box "Superseded"; "successor", box (WorkId.value value) ]

    let private reservation (value: Reservation) =
        record
            [ "workId", box (WorkId.value value.WorkId)
              "attempt", attempt value.Attempt
              "resources", resources value.Resources
              "moneyMinor", optional (string >> box) value.MoneyMinor ]

    let private usage (value: SettledUsage) =
        record
            [ "workId", box (WorkId.value value.WorkId)
              "attempt", attempt value.Attempt
              "resources", resources value.Resources
              "moneyMinor", optional (string >> box) value.MoneyMinor
              "usageUnresolved", box value.UsageUnresolved
              "overrun", box value.Overrun ]

    let private node (value: GraphNode) =
        record
            [ "id", box (NodeId.value value.Id)
              "role", box (GraphRole.name value.Role)
              "kind", box value.Kind
              "payload", envelope value.Payload
              "revision", revision value.Revision
              "contentHash", box value.ContentHash ]

    let private edge (value: HyperEdge) =
        record
            [ "id", box (EdgeId.value value.Id)
              "tails", nodeIds value.Tails
              "heads", nodeIds value.Heads
              "relation", box value.Relation
              "payload", optional envelope value.Payload
              "revision", revision value.Revision ]

    let private graphPatch (value: GraphPatch) =
        record
            [ "upsertNodes", items node value.UpsertNodes
              "removeNodes", value.RemoveNodes |> List.map NodeId.value |> strings
              "upsertEdges", items edge value.UpsertEdges
              "removeEdges", value.RemoveEdges |> List.map EdgeId.value |> strings ]

    let private guarantee value =
        match value with
        | CertificateGuarantee.EmpiricalSummary assumptions ->
            record [ "case", box "EmpiricalSummary"; "assumptions", strings assumptions ]
        | CertificateGuarantee.OrdinalObservation protocol ->
            record [ "case", box "OrdinalObservation"; "protocolRef", box protocol ]
        | CertificateGuarantee.ModelEstimate(model, approximation) ->
            record
                [ "case", box "ModelEstimate"
                  "modelRef", box model
                  "approximation", box approximation ]
        | CertificateGuarantee.PosteriorCredible(model, mass, approximation) ->
            record
                [ "case", box "PosteriorCredible"
                  "modelRef", box model
                  "mass", box mass
                  "approximation", box approximation ]
        | CertificateGuarantee.FrequentistCoverage(coverage, delta, scope) ->
            record
                [ "case", box "FrequentistCoverage"
                  "coverageRef", box coverage
                  "delta", box delta
                  "scope", box scope ]
        | CertificateGuarantee.DeterministicBound(theorem, assumptions) ->
            record
                [ "case", box "DeterministicBound"
                  "theoremRef", box theorem
                  "assumptions", strings assumptions ]
        | CertificateGuarantee.ExactWithinModel(model, numericError) ->
            record
                [ "case", box "ExactWithinModel"
                  "modelRef", box model
                  "numericError", box numericError ]
        | CertificateGuarantee.ResidualOnly reason -> record [ "case", box "ResidualOnly"; "reason", box reason ]

    let private certificateStatus value =
        match value with
        | CertificateStatus.Current -> record [ "case", box "Current" ]
        | CertificateStatus.Stale reason -> record [ "case", box "Stale"; "reason", box reason ]
        | CertificateStatus.Invalidated source ->
            record [ "case", box "Invalidated"; "sourceRevision", revision source ]
        | CertificateStatus.Conflicted -> record [ "case", box "Conflicted" ]

    let private patch (value: CertificateSlotPatch) =
        let slot = value.Slot

        record
            [ "certificateId", box (CertificateId.value value.CertificateId)
              "targetRef", box value.TargetRef
              "valueSpaceId", box value.ValueSpaceId
              "scopeId", box value.ScopeId
              "semanticsModelRef", box value.SemanticsModelRef
              "expectedSlotRevision", revision value.ExpectedSlotRevision
              "slot",
              record
                  [ "slot", box slot.Slot
                    "producer", box slot.Producer
                    "schema", schema slot.Schema
                    "canonicalPayload", box slot.CanonicalPayload
                    "revision", revision slot.Revision
                    "guarantee", guarantee slot.Guarantee
                    "status", certificateStatus slot.Status ] ]

    let private accepted (value: ResultAcceptedBody) =
        record
            [ "workId", box (WorkId.value value.WorkId)
              "attempt", attempt value.Attempt
              "fence", box (Fence.value value.Fence)
              "observationId", box (ObservationId.value value.ObservationId)
              "canonicalResult", box value.CanonicalResult
              "resultSchema", schema value.ResultSchema
              "clusterId", box value.ClusterId ]

    let private answer (value: AnswerCommittedBody) =
        record
            [ "renderWorkId", box (WorkId.value value.RenderWorkId)
              "answerRef", box value.AnswerRef
              "stopReason", box value.StopReason ]

    let private tagged tag payload =
        record [ "case", box tag; "payload", payload ]

    let private reasonPayload reason = record [ "reason", box reason ]

    /// Every body has a named DTO. No DU, Option, F# list or map crosses canonical JSON.
    let body value =
        match value with
        | InquiryEventBody.InquiryCreated value ->
            tagged
                "InquiryCreated"
                (record
                    [ "goal", goal value.Goal
                      "resourceSpecs", items resourceSpec value.ResourceSpecs
                      "profileRef", box value.ProfileRef
                      "configHash", box value.ConfigHash
                      "renderReserve", resources value.RenderReserve ])
        | InquiryEventBody.GoalAmended value -> tagged "GoalAmended" (goal value)
        | InquiryEventBody.SnapshotRegistered value -> tagged "SnapshotRegistered" (envelope value)
        | InquiryEventBody.DecisionScopeOpened value -> tagged "DecisionScopeOpened" (envelope value)
        | InquiryEventBody.RoundOpened value ->
            tagged
                "RoundOpened"
                (record
                    [ "roundId", box (RoundId.value value.RoundId)
                      "scopeId", box value.ScopeId
                      "expectedWork", value.ExpectedWork |> List.map WorkId.value |> strings ])
        | InquiryEventBody.WorkPlanned values -> tagged "WorkPlanned" (record [ "work", items workSpec values ])
        | InquiryEventBody.RoundClosed(round, outcome) ->
            tagged "RoundClosed" (record [ "roundId", box (RoundId.value round); "outcome", box outcome ])
        | InquiryEventBody.BudgetReserved value ->
            tagged
                "BudgetReserved"
                (record
                    [ "reservation", reservation value.Reservation
                      "renderReserve", resources value.RenderReserve ])
        | InquiryEventBody.UsageSettled value -> tagged "UsageSettled" (record [ "usage", usage value.Usage ])
        | InquiryEventBody.ReservationReleased(work, value) ->
            tagged "ReservationReleased" (record [ "workId", box (WorkId.value work); "attempt", attempt value ])
        | InquiryEventBody.UsageOverrunRecorded value ->
            tagged "UsageOverrunRecorded" (record [ "usage", usage value.Usage ])
        | InquiryEventBody.DispatchRequested value ->
            tagged
                "DispatchRequested"
                (record
                    [ "work", workSpec value.Work
                      "dispatchIntentId", box value.DispatchIntentId
                      "publicEnvelope", envelope value.PublicEnvelope
                      "privateTicket", envelope value.PrivateTicket ])
        | InquiryEventBody.DispatchReceiptRecorded value ->
            tagged
                "DispatchReceiptRecorded"
                (record
                    [ "workId", box (WorkId.value value.WorkId)
                      "attempt", attempt value.Attempt
                      "fence", box (Fence.value value.Fence)
                      "dispatchIntentId", box value.DispatchIntentId
                      "physicalRef", box value.PhysicalRef
                      "receipt", envelope value.Receipt ])
        | InquiryEventBody.WorkAttemptTransitioned value ->
            tagged
                "WorkAttemptTransitioned"
                (record
                    [ "workId", box (WorkId.value value.WorkId)
                      "attempt", attempt value.Attempt
                      "fence", box (Fence.value value.Fence)
                      "fromState", box value.FromState
                      "nextState", workState value.NextState
                      "physicalRef", optional box value.PhysicalRef ])
        | InquiryEventBody.HostTerminalRecorded value ->
            tagged
                "HostTerminalRecorded"
                (record
                    [ "workId", box (WorkId.value value.WorkId)
                      "attempt", attempt value.Attempt
                      "fence", box (Fence.value value.Fence)
                      "terminal", box value.Terminal
                      "receipt", envelope value.Receipt ])
        | InquiryEventBody.ResultAccepted value -> tagged "ResultAccepted" (accepted value)
        | InquiryEventBody.InterpretationPending value ->
            tagged
                "InterpretationPending"
                (record
                    [ "observationId", box (ObservationId.value value.ObservationId)
                      "workId", box (WorkId.value value.WorkId)
                      "attempt", attempt value.Attempt ])
        | InquiryEventBody.InterpretationApplied value ->
            tagged
                "InterpretationApplied"
                (record
                    [ "observationId", box (ObservationId.value value.ObservationId)
                      "interpretationId", box value.InterpretationId
                      "pluginRef", box value.PluginRef
                      "delta", envelope value.Delta ])
        | InquiryEventBody.InterpretationFailed value ->
            tagged
                "InterpretationFailed"
                (record
                    [ "observationId", box (ObservationId.value value.ObservationId)
                      "interpretationId", box value.InterpretationId
                      "pluginRef", box value.PluginRef
                      "reason", box value.Reason ])
        | InquiryEventBody.GraphPatched value ->
            tagged "GraphPatched" (record [ "patch", graphPatch value.Patch; "pluginRef", box value.PluginRef ])
        | InquiryEventBody.CertificateSlotsPatched value ->
            tagged "CertificateSlotsPatched" (record [ "patches", items patch value.Patches ])
        | InquiryEventBody.CertificateInvalidated value ->
            tagged
                "CertificateInvalidated"
                (record [ "invalidation", envelope value.Invalidation; "reason", box value.Reason ])
        | InquiryEventBody.DecisionRecorded value ->
            tagged "DecisionRecorded" (record [ "decision", envelope value.Decision ])
        | InquiryEventBody.AnswerPrepared value ->
            tagged
                "AnswerPrepared"
                (record
                    [ "renderWorkId", box (WorkId.value value.RenderWorkId)
                      "draftRef", box value.DraftRef ])
        | InquiryEventBody.AnswerCommitted value -> tagged "AnswerCommitted" (answer value)
        | InquiryEventBody.CancelRequested reason -> tagged "CancelRequested" (reasonPayload reason)
        | InquiryEventBody.InquiryCancelled reason -> tagged "InquiryCancelled" (reasonPayload reason)
        | InquiryEventBody.InquirySuspended reason -> tagged "InquirySuspended" (reasonPayload reason)
        | InquiryEventBody.InquiryFailed reason -> tagged "InquiryFailed" (reasonPayload reason)
        | InquiryEventBody.InquiryStatusChanged value ->
            tagged "InquiryStatusChanged" (record [ "status", box value.Status; "reason", box value.Reason ])

    let private status value =
        match value with
        | InquiryStatus.Active -> record [ "case", box "Active" ]
        | InquiryStatus.InputRequired authorization ->
            record [ "case", box "InputRequired"; "authorization", box authorization ]
        | InquiryStatus.Suspended reason -> record [ "case", box "Suspended"; "reason", box reason ]
        | InquiryStatus.Cancelling -> record [ "case", box "Cancelling" ]
        | InquiryStatus.StopReached reason -> record [ "case", box "StopReached"; "stopReason", box reason ]
        | InquiryStatus.Failed reason -> record [ "case", box "Failed"; "reason", box reason ]
        | InquiryStatus.Cancelled reason -> record [ "case", box "Cancelled"; "reason", box reason ]

    /// All InquiryState fields, including receipt contents and physical bindings. Map/set
    /// order is canonical; list order is preserved. Event identity excludes this view.
    let state (value: InquiryState) =
        record
            [ "id", box (InquiryId.value value.Id)
              "apiVersion", box value.ApiVersion
              "revision", revision value.Revision
              "eventHead", optional (EventId.value >> box) value.EventHead
              "goal", goal value.Goal
              "resourceSpecs", items resourceSpec value.ResourceSpecs
              "renderReserve", resources value.RenderReserve
              "configHash", box value.ConfigHash
              "profileRef", box value.ProfileRef
              "graph", pairs NodeId.value node value.Graph
              "edges", pairs EdgeId.value edge value.Edges
              "certificates", pairs id (items patch) value.Certificates
              "work",
              pairs
                  WorkId.value
                  (fun (item: WorkItem) -> record [ "spec", workSpec item.Spec; "state", workState item.State ])
                  value.Work
              "reservations",
              pairs
                  id
                  (fun ((key: ReservationKey), amounts) ->
                      record
                          [ "workId", box (WorkId.value key.WorkId)
                            "attempt", attempt key.Attempt
                            "resources", resources amounts ])
                  value.Reservations
              "settledUsage", resources value.SettledUsage
              "settledMoneyMinor", box (string value.SettledMoneyMinor)
              "overruns",
              value.Overruns
              |> items (fun (overrun: OverrunFact) ->
                  record
                      [ "workId", box (WorkId.value overrun.WorkId)
                        "attempt", attempt overrun.Attempt
                        "resources", resources overrun.Resources ])
              "observations", pairs id accepted value.Observations
              "interpretations",
              pairs
                  id
                  (fun (interpretation: InterpretationRecord) ->
                      record
                          [ "observationId", box (ObservationId.value interpretation.ObservationId)
                            "workId", box (WorkId.value interpretation.WorkId)
                            "attempt", attempt interpretation.Attempt
                            "interpretationId", optional box interpretation.InterpretationId
                            "pluginRef", optional box interpretation.PluginRef
                            "status", box interpretation.Status
                            "reason", optional box interpretation.Reason ])
                  value.Interpretations
              "rounds",
              pairs
                  RoundId.value
                  (fun (round: RoundRecord) ->
                      record
                          [ "roundId", box (RoundId.value round.RoundId)
                            "scopeId", box round.ScopeId
                            "expectedWork", workIds round.ExpectedWork
                            "receivedWork", workIds round.ReceivedWork
                            "terminalWork", workIds round.TerminalWork
                            "closed", box round.Closed
                            "outcome", optional box round.Outcome ])
                  value.Rounds
              "decisions", pairs id box value.Decisions
              "answer", optional answer value.Answer
              "commandReceipts",
              pairs
                  id
                  (fun (receipt: CommandReceipt) ->
                      record
                          [ "fingerprint", box receipt.Fingerprint
                            "revision", revision receipt.Revision
                            "eventId", box (EventId.value receipt.EventId) ])
                  value.CommandReceipts
              "physicalBindings",
              pairs
                  id
                  (fun (binding: PhysicalBinding) ->
                      record
                          [ "workId", box (WorkId.value binding.WorkId)
                            "attempt", attempt binding.Attempt
                            "dispatchIntentId", box binding.DispatchIntentId
                            "physicalRef", optional box binding.PhysicalRef
                            "receipt", optional box binding.Receipt ])
                  value.PhysicalBindings
              "status", status value.Status ]

    let fingerprint (digest: string -> string) value =
        state value |> CanonicalJson.canonicalJson |> digest
