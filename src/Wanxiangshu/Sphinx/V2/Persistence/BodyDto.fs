namespace Wanxiangshu.Sphinx.V2.Persistence

open System
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Foundation
open Wanxiangshu.Sphinx.V2.Core

/// @2 has one closed DTO per body. Decode the native ingress once, through the
/// existing validating constructors; no runtime reflection or empty-string fallback.
module internal BodyDto =
    let private validated validate decoder =
        Decode.andThen
            (fun value ->
                match validate value with
                | Ok valid -> Decode.succeed valid
                | Error reason -> Decode.fail reason)
            decoder

    let private decodeExactFields (fields: string list) (decoder: Decoder<'value>) (raw: obj) =
        let keys: string array = emitJsExpr raw "Object.keys($0)"

        if Set.ofArray keys <> Set.ofList fields then
            Decode.fail "DTO fields are missing or unknown"
        else
            decoder

    let exact (fields: string list) (decoder: Decoder<'value>) : Decoder<'value> =
        Decode.andThen
            (fun raw ->
                let plain: bool =
                    emitJsExpr
                        raw
                        "($0 !== null && typeof $0 === 'object' && !Array.isArray($0) && (Object.getPrototypeOf($0) === Object.prototype || Object.getPrototypeOf($0) === null))"

                if not plain then
                    Decode.fail "expected a plain DTO object"
                else
                    decodeExactFields fields decoder raw)
            Decode.value

    let nonBlank =
        Decode.string
        |> validated (fun text ->
            if String.IsNullOrWhiteSpace text then
                Error "expected a non-blank string"
            else
                Ok text)

    let hash =
        nonBlank
        |> validated (fun text ->
            if
                text.Length = 64
                && (text |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            then
                Ok text
            else
                Error "expected a lowercase SHA-256 digest")

    let private decimal =
        Decode.string
        |> validated (fun text ->
            match Int64.TryParse text with
            | true, number when number >= 0L && string number = text -> Ok number
            | _ -> Error "expected an exact nonnegative int64 decimal string")

    let revision = decimal |> validated Revision.tryCreate

    let private attempt =
        Decode.float
        |> validated (fun number ->
            if
                Double.IsNaN number
                || Double.IsInfinity number
                || number < 1.0
                || number > 9007199254740991.0
                || Math.Floor number <> number
            then
                Error "expected a positive safe-integer attempt"
            else
                Attempt.tryCreate (int64 number))

    let private amount =
        Decode.float
        |> validated (fun number ->
            if Double.IsNaN number || Double.IsInfinity number || number < 0.0 then
                Error "expected a finite nonnegative resource amount"
            else
                Ok number)

    let private id parser = nonBlank |> validated parser
    let private inquiryId = id InquiryId.tryCreate
    let private workId = id WorkId.tryCreate
    let private nodeId = id NodeId.tryCreate
    let private edgeId = id EdgeId.tryCreate
    let private roundId = id RoundId.tryCreate
    let private observationId = id ObservationId.tryCreate
    let private fence = id Fence.tryCreate
    let private optional decoder = Decode.option decoder

    let private unique values =
        if List.length values = Set.count (Set.ofList values) then
            Ok values
        else
            Error "duplicate set or map entry"

    let private set decoder =
        Decode.list decoder
        |> validated (fun values -> unique values |> Result.map Set.ofList)

    let private resources =
        exact
            [ "key"; "value" ]
            (Decode.object (fun get -> get.Required.Field "key" nonBlank, get.Required.Field "value" amount))
        |> Decode.list
        |> validated (fun pairs -> unique (List.map fst pairs) |> Result.map (fun _ -> Map.ofList pairs))

    let private canonical =
        Decode.string
        |> validated (fun text ->
            match Decode.fromString Decode.value text with
            | Ok value when CanonicalJson.canonicalJson value = text -> Ok text
            | Ok _ -> Error "payload text is not canonical JSON"
            | Error reason -> Error reason)

    let private schema: Decoder<SchemaRef> =
        exact
            [ "id"; "hash" ]
            (Decode.object (fun get ->
                { Id = get.Required.Field "id" nonBlank
                  Hash = get.Required.Field "hash" hash }))
        |> validated (fun value ->
            Envelope.tryCreate value.Id value.Hash
            |> Result.mapError (fun fault -> fault.Message))

    let private envelope: Decoder<JsonEnvelope> =
        exact
            [ "schema"; "canonicalPayload" ]
            (Decode.object (fun get ->
                { Schema = get.Required.Field "schema" schema
                  CanonicalPayload = get.Required.Field "canonicalPayload" canonical }))
        |> validated (fun value ->
            JsonEnvelope.tryOfCanonical value.Schema value.CanonicalPayload
            |> Result.mapError (fun fault -> fault.Message))

    let private amendment: Decoder<GoalAmendment> =
        exact
            [ "authorizedBy"; "revision"; "addedConstraints"; "replacedText" ]
            (Decode.object (fun get ->
                { AuthorizedBy = get.Required.Field "authorizedBy" nonBlank
                  Revision = get.Required.Field "revision" revision
                  AddedConstraints = get.Required.Field "addedConstraints" (Decode.list Decode.string)
                  ReplacedText = get.Required.Field "replacedText" (optional Decode.string) }))

    let private goal: Decoder<GoalSpec> =
        exact
            [ "goalId"
              "revision"
              "originalText"
              "constraints"
              "materialRefs"
              "authorizationRef"
              "createdBy"
              "amendments" ]
            (Decode.object (fun get ->
                { GoalId = get.Required.Field "goalId" (id GoalId.tryCreate)
                  Revision = get.Required.Field "revision" revision
                  OriginalText = get.Required.Field "originalText" Decode.string
                  Constraints = get.Required.Field "constraints" (Decode.list Decode.string)
                  MaterialRefs = get.Required.Field "materialRefs" (Decode.list (id ArtifactRef.tryCreate))
                  AuthorizationRef = get.Required.Field "authorizationRef" nonBlank
                  CreatedBy = get.Required.Field "createdBy" nonBlank
                  Amendments = get.Required.Field "amendments" (Decode.list amendment) }))
        |> validated (fun value -> Goal.tryCreate value |> Result.mapError (fun fault -> fault.Message))

    let private resourceKind: Decoder<ResourceKind> =
        Decode.field "case" Decode.string
        |> Decode.andThen (function
            | "Consumed" ->
                exact [ "case"; "unitName" ] (Decode.field "unitName" nonBlank |> Decode.map ResourceKind.Consumed)
            | "Capacity" ->
                exact [ "case"; "slotName" ] (Decode.field "slotName" nonBlank |> Decode.map ResourceKind.Capacity)
            | _ -> Decode.fail "unknown resource kind")

    let private resourceSpec: Decoder<ResourceSpec> =
        exact
            [ "name"; "kind"; "authorizedLimit" ]
            (Decode.object (fun get ->
                { Name = get.Required.Field "name" nonBlank
                  Kind = get.Required.Field "kind" resourceKind
                  AuthorizedLimit = get.Required.Field "authorizedLimit" amount }))

    let private resourceSpecs =
        Decode.list resourceSpec
        |> validated (fun values ->
            Budget.validateSpecs values
            |> Result.map (fun () -> values)
            |> Result.mapError (fun fault -> fault.Message))

    let private workSpec: Decoder<WorkSpec> =
        exact
            [ "id"
              "attempt"
              "fence"
              "roundId"
              "planId"
              "producer"
              "capability"
              "input"
              "outputSchema"
              "dependencies"
              "conflictKeys"
              "physicalRef"
              "reserved" ]
            (Decode.object (fun get ->
                { Id = get.Required.Field "id" workId
                  Attempt = get.Required.Field "attempt" attempt
                  Fence = get.Required.Field "fence" fence
                  RoundId = get.Required.Field "roundId" (optional roundId)
                  PlanId = get.Required.Field "planId" (id PlanId.tryCreate)
                  Producer = get.Required.Field "producer" nonBlank
                  Capability = get.Required.Field "capability" nonBlank
                  Input = get.Required.Field "input" (optional envelope)
                  OutputSchema = get.Required.Field "outputSchema" (optional schema)
                  Dependencies = get.Required.Field "dependencies" (set workId)
                  ConflictKeys = get.Required.Field "conflictKeys" (set nonBlank)
                  PhysicalRef = get.Required.Field "physicalRef" (optional nonBlank)
                  Reserved = get.Required.Field "reserved" resources }))
        |> validated (fun value ->
            Work.validateSpec value
            |> Result.map (fun () -> value)
            |> Result.mapError (fun fault -> fault.Message))

    let private workState: Decoder<WorkState> =
        Decode.field "case" Decode.string
        |> Decode.andThen (function
            | "Planned" -> exact [ "case" ] (Decode.succeed WorkState.Planned)
            | "Ready" -> exact [ "case" ] (Decode.succeed WorkState.Ready)
            | "Leased" -> exact [ "case"; "fence" ] (Decode.field "fence" fence |> Decode.map WorkState.Leased)
            | "Running" ->
                exact
                    [ "case"; "fence"; "physicalRef" ]
                    (Decode.object (fun get ->
                        WorkState.Running(get.Required.Field "fence" fence, get.Required.Field "physicalRef" nonBlank)))
            | "InputRequired" ->
                exact [ "case"; "fence" ] (Decode.field "fence" fence |> Decode.map WorkState.InputRequired)
            | "Succeeded" ->
                exact [ "case"; "attempt" ] (Decode.field "attempt" attempt |> Decode.map WorkState.Succeeded)
            | "Failed" -> exact [ "case"; "attempt" ] (Decode.field "attempt" attempt |> Decode.map WorkState.Failed)
            | "CancelRequested" ->
                exact [ "case"; "attempt" ] (Decode.field "attempt" attempt |> Decode.map WorkState.CancelRequested)
            | "Cancelled" ->
                exact [ "case"; "attempt" ] (Decode.field "attempt" attempt |> Decode.map WorkState.Cancelled)
            | "Superseded" ->
                exact [ "case"; "successor" ] (Decode.field "successor" workId |> Decode.map WorkState.Superseded)
            | _ -> Decode.fail "unknown work state")

    let private workStateName =
        Decode.string
        |> validated (fun value ->
            if
                List.contains
                    value
                    [ "Planned"
                      "Ready"
                      "Leased"
                      "Running"
                      "InputRequired"
                      "Succeeded"
                      "Failed"
                      "CancelRequested"
                      "Cancelled"
                      "Superseded" ]
            then
                Ok value
            else
                Error "unknown fromState")

    let private reservation: Decoder<Reservation> =
        exact
            [ "workId"; "attempt"; "resources"; "moneyMinor" ]
            (Decode.object (fun get ->
                { WorkId = get.Required.Field "workId" workId
                  Attempt = get.Required.Field "attempt" attempt
                  Resources = get.Required.Field "resources" resources
                  MoneyMinor = get.Required.Field "moneyMinor" (optional decimal) }))

    let private usage: Decoder<SettledUsage> =
        exact
            [ "workId"; "attempt"; "resources"; "moneyMinor"; "usageUnresolved"; "overrun" ]
            (Decode.object (fun get ->
                { WorkId = get.Required.Field "workId" workId
                  Attempt = get.Required.Field "attempt" attempt
                  Resources = get.Required.Field "resources" resources
                  MoneyMinor = get.Required.Field "moneyMinor" (optional decimal)
                  UsageUnresolved = get.Required.Field "usageUnresolved" Decode.bool
                  Overrun = get.Required.Field "overrun" Decode.bool }))

    let private role: Decoder<GraphRole> =
        Decode.string
        |> Decode.andThen (function
            | "epistemic" -> Decode.succeed GraphRole.Epistemic
            | "work-dependency" -> Decode.succeed GraphRole.WorkDependency
            | "plan-tree" -> Decode.succeed GraphRole.PlanTree
            | "refiner-state" -> Decode.succeed GraphRole.RefinerState
            | _ -> Decode.fail "unknown graph role")

    let private node: Decoder<GraphNode> =
        exact
            [ "id"; "role"; "kind"; "payload"; "revision"; "contentHash" ]
            (Decode.object (fun get ->
                { Id = get.Required.Field "id" nodeId
                  Role = get.Required.Field "role" role
                  Kind = get.Required.Field "kind" nonBlank
                  Payload = get.Required.Field "payload" envelope
                  Revision = get.Required.Field "revision" revision
                  ContentHash = get.Required.Field "contentHash" hash }))

    let private edge: Decoder<HyperEdge> =
        exact
            [ "id"; "tails"; "heads"; "relation"; "payload"; "revision" ]
            (Decode.object (fun get ->
                { Id = get.Required.Field "id" edgeId
                  Tails = get.Required.Field "tails" (set nodeId)
                  Heads = get.Required.Field "heads" (set nodeId)
                  Relation = get.Required.Field "relation" nonBlank
                  Payload = get.Required.Field "payload" (optional envelope)
                  Revision = get.Required.Field "revision" revision }))

    let private graphPatch: Decoder<GraphPatch> =
        exact
            [ "upsertNodes"; "removeNodes"; "upsertEdges"; "removeEdges" ]
            (Decode.object (fun get ->
                { UpsertNodes = get.Required.Field "upsertNodes" (Decode.list node)
                  RemoveNodes = get.Required.Field "removeNodes" (Decode.list nodeId)
                  UpsertEdges = get.Required.Field "upsertEdges" (Decode.list edge)
                  RemoveEdges = get.Required.Field "removeEdges" (Decode.list edgeId) }))

    let private guarantee: Decoder<CertificateGuarantee> =
        Decode.field "case" Decode.string
        |> Decode.andThen (function
            | "EmpiricalSummary" ->
                exact
                    [ "case"; "assumptions" ]
                    (Decode.field "assumptions" (Decode.list Decode.string)
                     |> Decode.map CertificateGuarantee.EmpiricalSummary)
            | "OrdinalObservation" ->
                exact
                    [ "case"; "protocolRef" ]
                    (Decode.field "protocolRef" nonBlank
                     |> Decode.map CertificateGuarantee.OrdinalObservation)
            | "ModelEstimate" ->
                exact
                    [ "case"; "modelRef"; "approximation" ]
                    (Decode.object (fun get ->
                        CertificateGuarantee.ModelEstimate(
                            get.Required.Field "modelRef" nonBlank,
                            get.Required.Field "approximation" nonBlank
                        )))
            | "PosteriorCredible" ->
                exact
                    [ "case"; "modelRef"; "mass"; "approximation" ]
                    (Decode.object (fun get ->
                        CertificateGuarantee.PosteriorCredible(
                            get.Required.Field "modelRef" nonBlank,
                            get.Required.Field "mass" amount,
                            get.Required.Field "approximation" nonBlank
                        )))
            | "FrequentistCoverage" ->
                exact
                    [ "case"; "coverageRef"; "delta"; "scope" ]
                    (Decode.object (fun get ->
                        CertificateGuarantee.FrequentistCoverage(
                            get.Required.Field "coverageRef" nonBlank,
                            get.Required.Field "delta" amount,
                            get.Required.Field "scope" nonBlank
                        )))
            | "DeterministicBound" ->
                exact
                    [ "case"; "theoremRef"; "assumptions" ]
                    (Decode.object (fun get ->
                        CertificateGuarantee.DeterministicBound(
                            get.Required.Field "theoremRef" nonBlank,
                            get.Required.Field "assumptions" (Decode.list Decode.string)
                        )))
            | "ExactWithinModel" ->
                exact
                    [ "case"; "modelRef"; "numericError" ]
                    (Decode.object (fun get ->
                        CertificateGuarantee.ExactWithinModel(
                            get.Required.Field "modelRef" nonBlank,
                            get.Required.Field "numericError" nonBlank
                        )))
            | "ResidualOnly" ->
                exact
                    [ "case"; "reason" ]
                    (Decode.field "reason" nonBlank |> Decode.map CertificateGuarantee.ResidualOnly)
            | _ -> Decode.fail "unknown certificate guarantee")
        |> validated (fun value ->
            Certificate.validateGuarantee value
            |> Result.map (fun () -> value)
            |> Result.mapError (fun fault -> fault.Message))

    let private certificateStatus: Decoder<CertificateStatus> =
        Decode.field "case" Decode.string
        |> Decode.andThen (function
            | "Current" -> exact [ "case" ] (Decode.succeed CertificateStatus.Current)
            | "Stale" ->
                exact [ "case"; "reason" ] (Decode.field "reason" nonBlank |> Decode.map CertificateStatus.Stale)
            | "Invalidated" ->
                exact
                    [ "case"; "sourceRevision" ]
                    (Decode.field "sourceRevision" revision
                     |> Decode.map CertificateStatus.Invalidated)
            | "Conflicted" -> exact [ "case" ] (Decode.succeed CertificateStatus.Conflicted)
            | _ -> Decode.fail "unknown certificate status")

    let private slot: Decoder<CertificateSlot> =
        exact
            [ "slot"
              "producer"
              "schema"
              "canonicalPayload"
              "revision"
              "guarantee"
              "status" ]
            (Decode.object (fun get ->
                { Slot = get.Required.Field "slot" nonBlank
                  Producer = get.Required.Field "producer" nonBlank
                  Schema = get.Required.Field "schema" schema
                  CanonicalPayload = get.Required.Field "canonicalPayload" canonical
                  Revision = get.Required.Field "revision" revision
                  Guarantee = get.Required.Field "guarantee" guarantee
                  Status = get.Required.Field "status" certificateStatus }))
        |> validated (fun value ->
            Certificate.validateSlot value
            |> Result.map (fun () -> value)
            |> Result.mapError (fun fault -> fault.Message))

    let private patch: Decoder<CertificateSlotPatch> =
        exact
            [ "certificateId"
              "targetRef"
              "valueSpaceId"
              "scopeId"
              "semanticsModelRef"
              "slot"
              "expectedSlotRevision" ]
            (Decode.object (fun get ->
                { CertificateId = get.Required.Field "certificateId" (id CertificateId.tryCreate)
                  TargetRef = get.Required.Field "targetRef" nonBlank
                  ValueSpaceId = get.Required.Field "valueSpaceId" nonBlank
                  ScopeId = get.Required.Field "scopeId" nonBlank
                  SemanticsModelRef = get.Required.Field "semanticsModelRef" nonBlank
                  Slot = get.Required.Field "slot" slot
                  ExpectedSlotRevision = get.Required.Field "expectedSlotRevision" revision }))

    let private reason = exact [ "reason" ] (Decode.field "reason" Decode.string)

    let private inquiryStatus =
        Decode.string
        |> validated (fun value ->
            if
                List.contains value [ "active"; "input-required"; "suspended"; "cancelling"; "cancelled"; "failed" ]
            then
                Ok value
            else
                Error "unknown inquiry status")

    let private payloadDecoder tag : Decoder<InquiryEventBody> =
        match tag with
        | "InquiryCreated" ->
            exact
                [ "goal"; "resourceSpecs"; "profileRef"; "configHash"; "renderReserve" ]
                (Decode.object (fun get ->
                    InquiryEventBody.InquiryCreated
                        { Goal = get.Required.Field "goal" goal
                          ResourceSpecs = get.Required.Field "resourceSpecs" resourceSpecs
                          ProfileRef = get.Required.Field "profileRef" nonBlank
                          ConfigHash = get.Required.Field "configHash" hash
                          RenderReserve = get.Required.Field "renderReserve" resources }))
        | "GoalAmended" -> goal |> Decode.map InquiryEventBody.GoalAmended
        | "SnapshotRegistered" -> envelope |> Decode.map InquiryEventBody.SnapshotRegistered
        | "DecisionScopeOpened" -> envelope |> Decode.map InquiryEventBody.DecisionScopeOpened
        | "RoundOpened" ->
            exact
                [ "roundId"; "scopeId"; "expectedWork" ]
                (Decode.object (fun get ->
                    InquiryEventBody.RoundOpened
                        { RoundId = get.Required.Field "roundId" roundId
                          ScopeId = get.Required.Field "scopeId" nonBlank
                          ExpectedWork = get.Required.Field "expectedWork" (Decode.list workId |> validated unique) }))
        | "WorkPlanned" ->
            exact
                [ "work" ]
                (Decode.field "work" (Decode.list workSpec)
                 |> Decode.map InquiryEventBody.WorkPlanned)
        | "RoundClosed" ->
            exact
                [ "roundId"; "outcome" ]
                (Decode.object (fun get ->
                    InquiryEventBody.RoundClosed(
                        get.Required.Field "roundId" roundId,
                        get.Required.Field "outcome" Decode.string
                    )))
        | "BudgetReserved" ->
            exact
                [ "reservation"; "renderReserve" ]
                (Decode.object (fun get ->
                    InquiryEventBody.BudgetReserved
                        { Reservation = get.Required.Field "reservation" reservation
                          RenderReserve = get.Required.Field "renderReserve" resources }))
        | "UsageSettled" ->
            exact
                [ "usage" ]
                (Decode.object (fun get -> InquiryEventBody.UsageSettled { Usage = get.Required.Field "usage" usage }))
        | "ReservationReleased" ->
            exact
                [ "workId"; "attempt" ]
                (Decode.object (fun get ->
                    InquiryEventBody.ReservationReleased(
                        get.Required.Field "workId" workId,
                        get.Required.Field "attempt" attempt
                    )))
        | "UsageOverrunRecorded" ->
            exact
                [ "usage" ]
                (Decode.object (fun get ->
                    InquiryEventBody.UsageOverrunRecorded { Usage = get.Required.Field "usage" usage }))
        | "DispatchRequested" ->
            exact
                [ "work"; "dispatchIntentId"; "publicEnvelope"; "privateTicket" ]
                (Decode.object (fun get ->
                    InquiryEventBody.DispatchRequested
                        { Work = get.Required.Field "work" workSpec
                          DispatchIntentId = get.Required.Field "dispatchIntentId" nonBlank
                          PublicEnvelope = get.Required.Field "publicEnvelope" envelope
                          PrivateTicket = get.Required.Field "privateTicket" envelope }))
        | "DispatchReceiptRecorded" ->
            exact
                [ "workId"; "attempt"; "fence"; "dispatchIntentId"; "physicalRef"; "receipt" ]
                (Decode.object (fun get ->
                    InquiryEventBody.DispatchReceiptRecorded
                        { WorkId = get.Required.Field "workId" workId
                          Attempt = get.Required.Field "attempt" attempt
                          Fence = get.Required.Field "fence" fence
                          DispatchIntentId = get.Required.Field "dispatchIntentId" nonBlank
                          PhysicalRef = get.Required.Field "physicalRef" nonBlank
                          Receipt = get.Required.Field "receipt" envelope }))
        | "WorkAttemptTransitioned" ->
            exact
                [ "workId"; "attempt"; "fence"; "fromState"; "nextState"; "physicalRef" ]
                (Decode.object (fun get ->
                    InquiryEventBody.WorkAttemptTransitioned
                        { WorkId = get.Required.Field "workId" workId
                          Attempt = get.Required.Field "attempt" attempt
                          Fence = get.Required.Field "fence" fence
                          FromState = get.Required.Field "fromState" workStateName
                          NextState = get.Required.Field "nextState" workState
                          PhysicalRef = get.Required.Field "physicalRef" (optional nonBlank) }))
        | "HostTerminalRecorded" ->
            exact
                [ "workId"; "attempt"; "fence"; "terminal"; "receipt" ]
                (Decode.object (fun get ->
                    InquiryEventBody.HostTerminalRecorded
                        { WorkId = get.Required.Field "workId" workId
                          Attempt = get.Required.Field "attempt" attempt
                          Fence = get.Required.Field "fence" fence
                          Terminal = get.Required.Field "terminal" nonBlank
                          Receipt = get.Required.Field "receipt" envelope }))
        | "ResultAccepted" ->
            exact
                [ "workId"
                  "attempt"
                  "fence"
                  "observationId"
                  "canonicalResult"
                  "resultSchema"
                  "clusterId" ]
                (Decode.object (fun get ->
                    InquiryEventBody.ResultAccepted
                        { WorkId = get.Required.Field "workId" workId
                          Attempt = get.Required.Field "attempt" attempt
                          Fence = get.Required.Field "fence" fence
                          ObservationId = get.Required.Field "observationId" observationId
                          CanonicalResult = get.Required.Field "canonicalResult" canonical
                          ResultSchema = get.Required.Field "resultSchema" schema
                          ClusterId = get.Required.Field "clusterId" nonBlank }))
        | "InterpretationPending" ->
            exact
                [ "observationId"; "workId"; "attempt" ]
                (Decode.object (fun get ->
                    InquiryEventBody.InterpretationPending
                        { ObservationId = get.Required.Field "observationId" observationId
                          WorkId = get.Required.Field "workId" workId
                          Attempt = get.Required.Field "attempt" attempt }))
        | "InterpretationApplied" ->
            exact
                [ "observationId"; "interpretationId"; "pluginRef"; "delta" ]
                (Decode.object (fun get ->
                    InquiryEventBody.InterpretationApplied
                        { ObservationId = get.Required.Field "observationId" observationId
                          InterpretationId = get.Required.Field "interpretationId" nonBlank
                          PluginRef = get.Required.Field "pluginRef" nonBlank
                          Delta = get.Required.Field "delta" envelope }))
        | "InterpretationFailed" ->
            exact
                [ "observationId"; "interpretationId"; "pluginRef"; "reason" ]
                (Decode.object (fun get ->
                    InquiryEventBody.InterpretationFailed
                        { ObservationId = get.Required.Field "observationId" observationId
                          InterpretationId = get.Required.Field "interpretationId" nonBlank
                          PluginRef = get.Required.Field "pluginRef" nonBlank
                          Reason = get.Required.Field "reason" Decode.string }))
        | "GraphPatched" ->
            exact
                [ "patch"; "pluginRef" ]
                (Decode.object (fun get ->
                    InquiryEventBody.GraphPatched
                        { Patch = get.Required.Field "patch" graphPatch
                          PluginRef = get.Required.Field "pluginRef" nonBlank }))
        | "CertificateSlotsPatched" ->
            exact
                [ "patches" ]
                (Decode.object (fun get ->
                    InquiryEventBody.CertificateSlotsPatched
                        { Patches = get.Required.Field "patches" (Decode.list patch) }))
        | "CertificateInvalidated" ->
            exact
                [ "invalidation"; "reason" ]
                (Decode.object (fun get ->
                    InquiryEventBody.CertificateInvalidated
                        { Invalidation = get.Required.Field "invalidation" envelope
                          Reason = get.Required.Field "reason" Decode.string }))
        | "DecisionRecorded" ->
            exact
                [ "decision" ]
                (Decode.object (fun get ->
                    InquiryEventBody.DecisionRecorded { Decision = get.Required.Field "decision" envelope }))
        | "AnswerPrepared" ->
            exact
                [ "renderWorkId"; "draftRef" ]
                (Decode.object (fun get ->
                    InquiryEventBody.AnswerPrepared
                        { RenderWorkId = get.Required.Field "renderWorkId" workId
                          DraftRef = get.Required.Field "draftRef" nonBlank }))
        | "AnswerCommitted" ->
            exact
                [ "renderWorkId"; "answerRef"; "stopReason" ]
                (Decode.object (fun get ->
                    InquiryEventBody.AnswerCommitted
                        { RenderWorkId = get.Required.Field "renderWorkId" workId
                          AnswerRef = get.Required.Field "answerRef" nonBlank
                          StopReason = get.Required.Field "stopReason" nonBlank }))
        | "CancelRequested" -> reason |> Decode.map InquiryEventBody.CancelRequested
        | "InquiryCancelled" -> reason |> Decode.map InquiryEventBody.InquiryCancelled
        | "InquirySuspended" -> reason |> Decode.map InquiryEventBody.InquirySuspended
        | "InquiryFailed" -> reason |> Decode.map InquiryEventBody.InquiryFailed
        | "InquiryStatusChanged" ->
            exact
                [ "status"; "reason" ]
                (Decode.object (fun get ->
                    InquiryEventBody.InquiryStatusChanged
                        { Status = get.Required.Field "status" inquiryStatus
                          Reason = get.Required.Field "reason" Decode.string }))
        | _ -> Decode.fail (sprintf "unknown @2 body tag: %s" tag)

    let decoder: Decoder<InquiryEventBody> =
        exact
            [ "case"; "payload" ]
            (Decode.field "case" Decode.string
             |> Decode.andThen (fun tag -> Decode.field "payload" (payloadDecoder tag)))
