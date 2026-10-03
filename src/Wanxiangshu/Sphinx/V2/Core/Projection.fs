namespace Wanxiangshu.Sphinx.V2.Core

open System

open Wanxiangshu.Foundation

module private PatchStatus =

    let name (status: CertificateStatus) : string =
        match status with
        | CertificateStatus.Current -> "current"
        | CertificateStatus.Stale _ -> "stale"
        | CertificateStatus.Invalidated _ -> "invalidated"
        | CertificateStatus.Conflicted -> "conflicted"

module private GuaranteeView =

    let name (guarantee: CertificateGuarantee) : string =
        match guarantee with
        | CertificateGuarantee.EmpiricalSummary _ -> "empirical-summary"
        | CertificateGuarantee.OrdinalObservation _ -> "ordinal-observation"
        | CertificateGuarantee.ModelEstimate _ -> "model-estimate"
        | CertificateGuarantee.PosteriorCredible _ -> "posterior-credible"
        | CertificateGuarantee.FrequentistCoverage _ -> "frequentist-coverage"
        | CertificateGuarantee.DeterministicBound _ -> "deterministic-bound"
        | CertificateGuarantee.ExactWithinModel _ -> "exact-within-model"
        | CertificateGuarantee.ResidualOnly _ -> "residual-only"

/// The semantic view is a set of plain records, not a tangle of nested anonymous
/// records. Named fields make the projection reviewable: a reader can see exactly
/// which facts are in the semantic hash and which are not.
type GoalProjection =
    { GoalId: string
      Revision: int64
      OriginalText: string
      Constraints: string list
      MaterialRefs: string list
      Authorization: string
      Amendments: AmendmentProjection list }

and AmendmentProjection =
    { AuthorizedBy: string
      Revision: int64
      AddedConstraints: string list
      ReplacedText: string option }

type NodeProjection =
    { Id: string
      Role: string
      Kind: string
      SchemaId: string
      SchemaHash: string
      Revision: int64
      ContentHash: string
      Payload: string }

type EdgeProjection =
    { Id: string
      Tails: string list
      Heads: string list
      Relation: string
      Revision: int64
      Payload: string option
      PayloadSchemaId: string option }

type SlotProjection =
    { Slot: string
      Producer: string
      SchemaId: string
      SchemaHash: string
      Revision: int64
      Status: string
      Guarantee: string
      Payload: string
      ExpectedBase: int64 }

type CertificateProjection =
    { Key: string
      Slots: SlotProjection list }

type WorkProjection =
    { Id: string
      Round: string option
      Plan: string
      Producer: string
      Capability: string
      Attempt: int64
      State: string
      Dependencies: string list
      ConflictKeys: string list
      Input: string option }

type ObservationProjection =
    { Key: string
      Work: string
      Attempt: int64
      Observation: string
      Cluster: string
      SchemaId: string
      SchemaHash: string
      Result: string }

type InterpretationProjection =
    { Key: string
      Status: string
      InterpretationId: string option
      Plugin: string option }

type RoundProjection =
    { Round: string
      Scope: string
      Expected: string list
      Received: string list
      Terminal: string list
      Closed: bool
      Outcome: string option }

type OverrunProjection =
    { Work: string
      Attempt: int64
      Resources: (string * float) list }

type BudgetProjection =
    { SettledUsage: (string * float) list
      SettledMoneyMinor: int64
      Overruns: OverrunProjection list
      Reservations: (string * (string * float) list) list }

type AnswerProjection =
    { RenderWork: string
      AnswerRef: string
      StopReason: string }

/// The semantic projection itself.
type SemanticProjection =
    { ApiVersion: string
      Goal: GoalProjection
      Graph: NodeProjection list
      Edges: EdgeProjection list
      Certificates: CertificateProjection list
      Work: WorkProjection list
      Observations: ObservationProjection list
      Interpretations: InterpretationProjection list
      Rounds: RoundProjection list
      Decisions: (string * string) list
      Budget: BudgetProjection
      Answer: AnswerProjection option }

/// Three different hashes with three different meanings.
///
/// WHAT[sphinx-v2-020]: `traceHash` covers the accepted canonical envelopes including
/// their order; `stateHash` covers the whole materialized state including physical
/// bindings; `semanticHash` covers only semantic facts and deliberately excludes
/// session ids, transport cursors and wall-clock timestamps. Mixing them up is how a
/// projection ends up claiming Host-independence it does not have.
///
/// The old surface hashed an event array's length, which made two different
/// investigations with the same event count compare equal. Here every hash is computed
/// over a canonical projection of the projected content, never over a count.
module Projection =

    let canonical (value: obj) : string = CanonicalJson.canonicalJson value

    let private digest (value: obj) : string =
        canonical value |> Wanxiangshu.Host.HostDigest.sha256Hex

    let private sortedPairs (map: Map<string, float>) = map |> Map.toList |> List.sortBy fst

    let private goalProjection (goal: GoalSpec) : GoalProjection =
        { GoalId = GoalId.value goal.GoalId
          Revision = Revision.value goal.Revision
          OriginalText = goal.OriginalText
          Constraints = goal.Constraints
          MaterialRefs = goal.MaterialRefs |> List.map ArtifactRef.value
          Authorization = goal.AuthorizationRef
          Amendments =
            goal.Amendments
            |> List.map (fun amendment ->
                { AuthorizedBy = amendment.AuthorizedBy
                  Revision = Revision.value amendment.Revision
                  AddedConstraints = amendment.AddedConstraints
                  ReplacedText = amendment.ReplacedText }) }

    let private nodeProjection (node: GraphNode) : NodeProjection =
        { Id = NodeId.value node.Id
          Role = GraphRole.name node.Role
          Kind = node.Kind
          SchemaId = node.Payload.Schema.Id
          SchemaHash = node.Payload.Schema.Hash
          Revision = Revision.value node.Revision
          ContentHash = node.ContentHash
          Payload = node.Payload.CanonicalPayload }

    let private edgeProjection (edge: HyperEdge) : EdgeProjection =
        { Id = EdgeId.value edge.Id
          Tails = edge.Tails |> Set.toList |> List.map NodeId.value |> List.sort
          Heads = edge.Heads |> Set.toList |> List.map NodeId.value |> List.sort
          Relation = edge.Relation
          Revision = Revision.value edge.Revision
          Payload = edge.Payload |> Option.map (fun envelope -> envelope.CanonicalPayload)
          PayloadSchemaId = edge.Payload |> Option.map (fun envelope -> envelope.Schema.Id) }

    let private slotProjection (patch: CertificateSlotPatch) : SlotProjection =
        { Slot = patch.Slot.Slot
          Producer = patch.Slot.Producer
          SchemaId = patch.Slot.Schema.Id
          SchemaHash = patch.Slot.Schema.Hash
          Revision = Revision.value patch.Slot.Revision
          Status = PatchStatus.name patch.Slot.Status
          Guarantee = GuaranteeView.name patch.Slot.Guarantee
          Payload = patch.Slot.CanonicalPayload
          ExpectedBase = Revision.value patch.ExpectedSlotRevision }

    let private workProjection (item: WorkItem) : WorkProjection =
        { Id = WorkId.value item.Spec.Id
          Round = item.Spec.RoundId |> Option.map RoundId.value
          Plan = PlanId.value item.Spec.PlanId
          Producer = item.Spec.Producer
          Capability = item.Spec.Capability
          Attempt = Attempt.value item.Spec.Attempt
          State = Work.stateName item.State
          Dependencies = item.Spec.Dependencies |> Set.toList |> List.map WorkId.value |> List.sort
          ConflictKeys = item.Spec.ConflictKeys |> Set.toList |> List.sort
          Input = item.Spec.Input |> Option.map (fun envelope -> envelope.CanonicalPayload) }

    let private observationProjection (key: string) (body: ResultAcceptedBody) : ObservationProjection =
        { Key = key
          Work = WorkId.value body.WorkId
          Attempt = Attempt.value body.Attempt
          Observation = ObservationId.value body.ObservationId
          Cluster = body.ClusterId
          SchemaId = body.ResultSchema.Id
          SchemaHash = body.ResultSchema.Hash
          Result = body.CanonicalResult }

    let private roundProjection (record: RoundRecord) : RoundProjection =
        { Round = RoundId.value record.RoundId
          Scope = record.ScopeId
          Expected = record.ExpectedWork |> Set.toList |> List.map WorkId.value |> List.sort
          Received = record.ReceivedWork |> Set.toList |> List.map WorkId.value |> List.sort
          Terminal = record.TerminalWork |> Set.toList |> List.map WorkId.value |> List.sort
          Closed = record.Closed
          Outcome = record.Outcome }

    let private budgetProjection (state: InquiryState) : BudgetProjection =
        { SettledUsage = sortedPairs state.SettledUsage
          SettledMoneyMinor = state.SettledMoneyMinor
          Overruns =
            state.Overruns
            |> List.map (fun fact ->
                { Work = WorkId.value fact.WorkId
                  Attempt = Attempt.value fact.Attempt
                  Resources = sortedPairs fact.Resources })
          Reservations =
            state.Reservations
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (key, pair) -> key, sortedPairs (snd pair)) }

    /// Semantic projection: inquiry facts only. Canonical JSON erases map iteration
    /// order, so two hosts that recorded the same facts in a different order still hash
    /// identically (C-13), while a changed value or a changed goal revision does move
    /// the hash (C-10, C-12).
    let semanticProjection (state: InquiryState) : SemanticProjection =
        { ApiVersion = state.ApiVersion
          Goal = goalProjection state.Goal
          Graph =
            state.Graph
            |> Map.toList
            |> List.sortBy (fun (nodeId, _) -> NodeId.value nodeId)
            |> List.map (snd >> nodeProjection)
          Edges =
            state.Edges
            |> Map.toList
            |> List.sortBy (fun (edgeId, _) -> EdgeId.value edgeId)
            |> List.map (snd >> edgeProjection)
          Certificates =
            state.Certificates
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (key, patches) ->
                { Key = key
                  Slots = patches |> List.map slotProjection |> List.sortBy (fun slot -> slot.Slot) })
          Work =
            state.Work
            |> Map.toList
            |> List.sortBy (fun (workId, _) -> WorkId.value workId)
            |> List.map (snd >> workProjection)
          Observations =
            state.Observations
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (key, body) -> observationProjection key body)
          Interpretations =
            state.Interpretations
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (key, record) ->
                { Key = key
                  Status = record.Status
                  InterpretationId = record.InterpretationId
                  Plugin = record.PluginRef })
          Rounds =
            state.Rounds
            |> Map.toList
            |> List.sortBy fst
            |> List.map (snd >> roundProjection)
          Decisions = state.Decisions |> Map.toList |> List.sortBy fst
          Budget = budgetProjection state
          Answer =
            state.Answer
            |> Option.map (fun answer ->
                { RenderWork = WorkId.value answer.RenderWorkId
                  AnswerRef = answer.AnswerRef
                  StopReason = answer.StopReason }) }

    let semanticHash (state: InquiryState) : string = state |> semanticProjection |> digest

    /// Covers every materialized field, including the physical bindings recovery needs.
    /// Two hosts that dispatched through different sessions differ here, which is why
    /// this hash is never offered as Host-independent.
    let stateHash (state: InquiryState) : string = state |> Representation.state |> digest

    /// Covers the accepted canonical envelopes in their exact order. Two hosts that ran
    /// different physical retries legitimately differ here; that is the point.
    let traceHash (envelopes: string list) : string =
        envelopes |> List.toArray |> box |> digest
