namespace Wanxiangshu.Strength

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Resources
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Strength.OpenCode
open Wanxiangshu.Strength.Migration
open Wanxiangshu.Strength.Persistence
open Wanxiangshu.Strength.Projection
open Wanxiangshu.Strength.Replica

/// JS-native owner surface for Strength semantics.
///
/// Strength records, unions, identities, collections and live registries remain
/// private to their owners. Tests cross this module with JSON-shaped values and
/// opaque handles only; Fable representation is never a contract.
module StrengthSurface =

    type private EventHandle(event: StrengthEvent) =
        member _.Value = event

    type private ProjectionHandle(projection: StrengthProjection) =
        member _.Value = projection

    type private EnvelopeHandle(envelope: EventEnvelope) =
        member _.Value = envelope

    type private DurabilityHandle(port: StrengthDurabilityPort) =
        member _.Value = port

    type private RuntimeHandle(runtime: StrengthRuntime) =
        member _.Value = runtime

    let private isUndefined (value: obj) : bool = emitJsExpr value "$0 === undefined"

    let private isNullish (value: obj) = isNull value || isUndefined value

    let private arrayOf (value: obj) : obj array =
        if isNullish value then [||] else unbox<obj array> value

    let private textOf (value: obj) =
        if isNullish value then "" else string value

    let private optionalText (value: obj) =
        if isNullish value then None else Some(string value)

    let private isJsString (value: obj) : bool =
        emitJsExpr value "typeof $0 === 'string'"

    let private isJsArray (value: obj) : bool = emitJsExpr value "Array.isArray($0)"

    let private roleResult (value: obj) : Result<Role, string> =
        match Roles.tryParseRole (textOf value) with
        | Some role -> Ok role
        | None -> Error(sprintf "unknown role: %s" (textOf value))

    /// host-boundary-030: only an exact JavaScript integer inside the declared
    /// range is a rounds value. A string, a boolean, a fraction, NaN and
    /// infinity are refused here instead of being coerced by a blind unbox.
    /// The upper bound is the `estimated_readonly_rounds` schema maximum.
    let private isDeclaredRounds (value: obj) : bool =
        emitJsExpr value "typeof $0 === 'number' && Number.isInteger($0) && $0 >= 0 && $0 <= 2147483647"

    /// WHAT[002]: an illegal declared budget is a new-call parameter error, not
    /// a silent normalization. Admission states it as a visible reason.
    let private requestedRoundsOutOfRange = "requested-rounds-out-of-range"

    /// A binding boundary has no channel to argue a parameter with its caller:
    /// a missing, non-positive or otherwise illegal declared value is simply an
    /// error there, so nothing may be registered on it.
    let private roundsResult (value: obj) : Result<ReadonlyRoundBudget, string> =
        if isDeclaredRounds value then
            ReadonlyRoundBudget.tryCreate (unbox<int> value)
        else
            Error requestedRoundsOutOfRange

    let private requestKindResult (value: obj) : Result<ProviderRequestKind, string> =
        match textOf value with
        | "work-main" -> Ok ProviderRequestKind.WorkMain
        | "blogger-main" -> Ok ProviderRequestKind.BloggerMain
        | "blogger-squash" -> Ok ProviderRequestKind.BloggerSquash
        | "interaction-repair" -> Ok ProviderRequestKind.InteractionRepair
        | "strength-replica" -> Ok ProviderRequestKind.StrengthReplica
        | unknown -> Error(sprintf "unknown request kind: %s" unknown)

    let private roleLabel role = Roles.roleLabel role

    let private permissionLabel permission =
        match permission with
        | ToolPermission.Fork -> "Fork"
        | ToolPermission.Resume -> "Resume"
        | ToolPermission.Join -> "Join"
        | ToolPermission.Horizon -> "Horizon"
        | ToolPermission.Fission -> "Fission"
        | ToolPermission.Read -> "Read"
        | ToolPermission.Write -> "Write"
        | ToolPermission.Edit -> "Edit"
        | ToolPermission.Glob -> "Glob"
        | ToolPermission.Grep -> "Grep"
        | ToolPermission.Move -> "Move"
        | ToolPermission.Remove -> "Remove"
        | ToolPermission.Exec -> "Exec"
        | ToolPermission.Pty -> "Pty"
        | ToolPermission.ReviewAssessment -> "ReviewAssessment"
        | ToolPermission.Chronicle -> "Chronicle"
        | ToolPermission.Fetch -> "Fetch"
        | ToolPermission.Finality -> "Finality"
        | ToolPermission.BashHoneypot -> "BashHoneypot"
        | ToolPermission.JsPlan -> "JsPlan"
        | ToolPermission.Ask -> "Ask"
        | ToolPermission.Handoff -> "Handoff"
        | ToolPermission.Deliver -> "Deliver"

    let private permissionsToJs permissions =
        permissions
        |> Set.toList
        |> List.map permissionLabel
        |> List.sort
        |> List.toArray

    let private partResult (value: obj) : Result<MessagePart, string> =
        match textOf value?kind with
        | "text" -> Ok(MessagePart.Text(textOf value?text))
        | "reasoning" -> Ok(MessagePart.Reasoning(textOf value?text))
        | "tool-call" -> Ok(MessagePart.ToolCall(textOf value?callId, textOf value?name, textOf value?args))
        | "tool-result" -> Ok(MessagePart.ToolResult(textOf value?callId, textOf value?result))
        | "patch"
        | "step-start"
        | "step-finish" -> Ok(MessagePart.Activity(textOf value?kind))
        | unknown -> Error(sprintf "unknown message part kind: %s" unknown)

    let private wirePartOf (value: obj) : ProviderProjection.WirePart =
        match textOf value?kind with
        | "text" -> ProviderProjection.WireText(textOf value?text)
        | "reasoning" -> ProviderProjection.WireReasoning(textOf value?text)
        | "tool-call" ->
            ProviderProjection.WireToolCall(
                ToolCallId.create (textOf value?callId),
                textOf value?name,
                textOf value?args
            )
        | "tool-result" ->
            ProviderProjection.WireToolResult(ToolCallId.create (textOf value?callId), textOf value?result)
        | "media" -> ProviderProjection.WireMedia(optionalText value?mediaType, textOf value?contentDigest)
        | other -> failwithf "StrengthSurface: unknown wire part kind %s" other

    let private wireMessageOf (value: obj) : ProviderProjection.WireMessage =
        { Role = textOf value?role
          Parts = arrayOf value?parts |> Array.toList |> List.map wirePartOf }

    let private wirePartToJs (part: ProviderProjection.WirePart) : obj =
        match part with
        | ProviderProjection.WireText value -> box {| kind = "text"; text = value |}
        | ProviderProjection.WireReasoning value -> box {| kind = "reasoning"; text = value |}
        | ProviderProjection.WireToolCall(id, name, args) ->
            box
                {| kind = "tool-call"
                   callId = ToolCallId.value id
                   name = name
                   args = args |}
        | ProviderProjection.WireToolResult(id, result) ->
            box
                {| kind = "tool-result"
                   callId = ToolCallId.value id
                   result = result |}
        | ProviderProjection.WireMedia(mediaType, digest) ->
            box
                {| kind = "media"
                   mediaType = Option.toObj mediaType
                   contentDigest = digest |}

    let private wireMessageToJs (message: ProviderProjection.WireMessage) : obj =
        box
            {| role = message.Role
               parts = message.Parts |> List.map wirePartToJs |> List.toArray |}

    let private messagesOf (value: obj) =
        arrayOf value |> Array.toList |> List.map wireMessageOf

    let private projectionMessageRowOf (value: obj) : ProjectionMessageRow =
        { Message = wireMessageOf value?message
          HostMessageId = optionalText value?hostMessageId
          HostIsPhysical = unbox<bool> value?hostIsPhysical }

    let private projectionMessageRowsOf (value: obj) =
        arrayOf value |> Array.toList |> List.map projectionMessageRowOf

    let private renderedMessagesOf (value: obj) : RenderedMessages =
        { Messages = messagesOf value?messages
          HostMessageIds =
            arrayOf value?hostMessageIds
            |> Array.toList
            |> List.map (fun id -> if isNullish id then None else Some(textOf id))
          HostIsPhysical = arrayOf value?hostIsPhysical |> Array.toList |> List.map unbox<bool> }

    /// Apply Strength's native completed-tool Host adaptation to rendered rows.
    let tryApplyRenderedMessages (sessionId: string) (sha256: string -> string) (rendered: obj) : obj =
        match StrengthReplicaTransform.tryApplyRenderedMessages sessionId sha256 (renderedMessagesOf rendered) with
        | Ok values ->
            box
                {| ok = true
                   value = values |> List.toArray |}
        | Error error -> box {| ok = false; error = error |}

    let tryEncodeOwnerMessages (sha256: string -> string) (messages: obj array) : obj =
        match StrengthReplicaTransform.tryEncodeOwnerMessages sha256 (Array.toList messages) with
        | Ok values ->
            box
                {| ok = true
                   value = values |> List.toArray |}
        | Error error -> box {| ok = false; error = error |}

    let private exchangesOf (value: obj) : StrengthToolExchange list =
        arrayOf value
        |> Array.toList
        |> List.map (fun exchange ->
            { ToolName = textOf exchange?toolName
              CanonicalArguments = textOf exchange?canonicalArguments
              CanonicalResult = textOf exchange?canonicalResult })

    let private batchesOf (value: obj) : StrengthRequestBatch list =
        arrayOf value
        |> Array.toList
        |> List.map (fun batch ->
            { RequestOrdinal = int (textOf batch?requestOrdinal)
              AssistantText = arrayOf batch?assistantText |> Array.toList |> List.map textOf
              Exchanges = exchangesOf batch?exchanges })

    let private bundleOf (value: obj) : StrengthFrameBundle =
        { Batches = batchesOf value?batches
          Digest = textOf value?digest
          ByteLength = int (textOf value?byteLength) }

    let private exchangeToJs (exchange: StrengthToolExchange) : obj =
        box
            {| toolName = exchange.ToolName
               canonicalArguments = exchange.CanonicalArguments
               canonicalResult = exchange.CanonicalResult |}

    let private batchToJs (batch: StrengthRequestBatch) : obj =
        createObj
            [ "requestOrdinal", box batch.RequestOrdinal
              "exchanges", box (batch.Exchanges |> List.map exchangeToJs |> List.toArray)
              if not (List.isEmpty batch.AssistantText) then
                  "assistantText", box (List.toArray batch.AssistantText) ]

    let private bundleToJs (bundle: StrengthFrameBundle) : obj =
        box
            {| batches = bundle.Batches |> List.map batchToJs |> List.toArray
               digest = bundle.Digest
               byteLength = bundle.ByteLength |}

    let private errorName error =
        match error with
        | StrengthFrameError.EmptyBundle -> "EmptyBundle"
        | StrengthFrameError.EmptyBatch _ -> "EmptyBatch"
        | StrengthFrameError.InvalidRequestOrdinal _ -> "InvalidRequestOrdinal"
        | StrengthFrameError.UnsupportedTool _ -> "UnsupportedTool"

    let private resultToJs valueOf errorOf result =
        match result with
        | Ok value -> box {| ok = true; value = valueOf value |}
        | Error error -> box {| ok = false; error = errorOf error |}

    let private projectionIntentErrorName error =
        match error with
        | StrengthProjectionIntentError.CandidateWrongTarget _ -> "StrengthCandidateWrongTarget"
        | StrengthProjectionIntentError.PromotedReplicaReflection _ -> "StrengthPromotedReplicaReflection"
        | StrengthProjectionIntentError.FrameDigestMismatch _ -> "StrengthFrameDigestMismatch"
        | StrengthProjectionIntentError.InvalidAnchor _ -> "InvalidStrengthAnchor"

    let private projectionIntentResultToJs result =
        match result with
        | Ok intent ->
            box
                {| ok = true
                   value = ProjectionSurface.intentToSurfaceValue intent
                   error = null |}
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = projectionIntentErrorName error |}

    let projectionMirror (value: obj) : obj =
        StrengthProjectionIntent.projectionMirror (projectionMessageRowsOf value?rows)
        |> projectionIntentResultToJs

    /// Owner-facing display name for one projected exchange: the replica calls
    /// `js-predictor`, but the owner must see its own `js-<role>` surface.
    let private ownerDisplayNameOf (value: obj) (toolName: string) : string =
        if isNull value || isNull value?ownerRole then
            toolName
        else
            match Roles.tryParseRole (textOf value?ownerRole) with
            | Some role when toolName = "js-predictor" -> "js-" + Roles.roleLabel role
            | _ -> toolName

    let candidate (sha256: string -> string) (value: obj) : obj =
        StrengthProjectionIntent.candidate
            sha256
            (SessionId.create (textOf value?ownerSessionId))
            (StrengthDecisionId.create (textOf value?decisionId))
            (ProviderRunIdentity.create (textOf value?targetProviderRun))
            (ProviderRunIdentity.create (textOf value?currentProviderRun))
            (ownerDisplayNameOf value)
            (bundleOf value?bundle)
        |> projectionIntentResultToJs

    let promoted (sha256: string -> string) (value: obj) : obj =
        StrengthProjectionIntent.promoted
            sha256
            (SessionId.create (textOf value?ownerSessionId))
            (StrengthDecisionId.create (textOf value?decisionId))
            (int (textOf value?beforeIndex))
            (unbox<bool> value?isReplicaRequest)
            (ownerDisplayNameOf value)
            (bundleOf value?bundle)
        |> projectionIntentResultToJs

    let replicaLocal (sha256: string -> string) (value: obj) : obj =
        StrengthProjectionIntent.replicaLocal
            sha256
            (SessionId.create (textOf value?ownerSessionId))
            (StrengthDecisionId.create (textOf value?decisionId))
            (bundleOf value?bundle)
        |> projectionIntentResultToJs

    /// Build one deterministic frame bundle from plain request batches.
    /// DELEGATE-10: no Delegate-specific byte ceiling; integrity (digest,
    /// real byte length) is still enforced inside tryBuild.
    let frameTryBuild (sha256: string -> string) (batches: obj array) : obj =
        StrengthFrame.tryBuild sha256 (batchesOf batches)
        |> resultToJs bundleToJs errorName

    /// Localize owner wire ids into decision-local ids without changing semantics.
    let frameTryLocalizeMirror
        (sha256: string -> string)
        (decisionId: string)
        (semanticDigest: string)
        (messages: obj array)
        : obj =
        let errorName =
            function
            | StrengthMirrorError.DuplicateToolCallId _ -> "DuplicateToolCallId"
            | StrengthMirrorError.OrphanToolResultId _ -> "OrphanToolResultId"
            | StrengthMirrorError.MediaCannotCrossSession -> "MediaCannotCrossSession"

        StrengthFrame.tryLocalizeMirror
            sha256
            (StrengthDecisionId.create decisionId)
            semanticDigest
            (messagesOf messages)
        |> resultToJs (List.map wireMessageToJs >> List.toArray) errorName

    let frameWireToolCallId
        (sha256: string -> string)
        (ownerSessionId: string)
        (decisionId: string)
        (requestOrdinal: int)
        (exchangeOrdinal: int)
        (semanticDigest: string)
        : string =
        StrengthFrame.wireToolCallId
            sha256
            (SessionId.create ownerSessionId)
            (StrengthDecisionId.create decisionId)
            requestOrdinal
            exchangeOrdinal
            semanticDigest

    let collectCompleteBatches (messages: obj array) : obj array =
        StrengthBatchCollector.collectCompleteBatches (messagesOf messages)
        |> List.map batchToJs
        |> List.toArray

    let renderWire (messages: obj array) : string =
        let wire: ProviderProjection.ProviderWireProjection =
            { ProviderId = None
              ModelId = None
              Variant = None
              Tools = []
              System = []
              Messages = messagesOf messages }

        wire |> ProviderProjection.renderWire

    let renderSemantic (messages: obj array) : string =
        let wire: ProviderProjection.ProviderWireProjection =
            { ProviderId = None
              ModelId = None
              Variant = None
              Tools = []
              System = []
              Messages = messagesOf messages }

        wire |> ProviderProjection.toSemantic |> ProviderProjection.renderSemantic

    /// DELEGATE-7.2: everything admission needs is supplied as evidence. No
    /// predictor sample, cost estimate, evidence count, holdout bucket or
    /// margin participates any more.
    let private requestedRoundsOptionResult (value: obj) : Result<ReadonlyRoundBudget option, string> =
        if isNullish value then
            Ok None
        elif isDeclaredRounds value then
            ReadonlyRoundBudget.tryCreate (unbox<int> value)
            |> Result.mapError (fun _ -> requestedRoundsOutOfRange)
            |> Result.map Some
        else
            Error requestedRoundsOutOfRange

    /// WHAT[002]: the owner logical run names the authority an authorization
    /// would consume. A frozen request carries it as the two named fields; an
    /// admission opportunity carries the [logicalRunId; authorityRootUserMessageId]
    /// pair. Both describe the same identity, so both decode here. Policy never
    /// inspects the identity's content, so a shape that neither decodes must be
    /// refused here: an empty identity would otherwise mint a real DecisionId and
    /// a durable DelegationRequested with no authority behind it.
    let private malformedOwnerLogicalRun = "malformed-owner-logical-run"

    let private ownerLogicalRunIdentity (logicalRunId: obj) (authorityRootUserMessageId: obj) =
        if
            isJsString logicalRunId
            && isJsString authorityRootUserMessageId
            && not (String.IsNullOrWhiteSpace(string logicalRunId))
            && not (String.IsNullOrWhiteSpace(string authorityRootUserMessageId))
        then
            Ok
                { LogicalRunId = LogicalRunId.create (string logicalRunId)
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (string authorityRootUserMessageId) }
        else
            Error malformedOwnerLogicalRun

    let private ownerLogicalRunOf (value: obj) : Result<OwnerLogicalRunIdentity, string> =
        if isNullish value then
            Error malformedOwnerLogicalRun
        elif isJsArray value then
            let entries = unbox<obj array> value

            if entries.Length = 2 then
                ownerLogicalRunIdentity entries.[0] entries.[1]
            else
                Error malformedOwnerLogicalRun
        else
            ownerLogicalRunIdentity value?logicalRunId value?authorityRootUserMessageId

    let private opportunityOf (value: obj) : Result<StrengthOpportunity, string> =
        match roleResult value?canonicalRole, requestedRoundsOptionResult value?requestedRounds with
        | Error error, _
        | _, Error error -> Error error
        | Ok canonicalRole, Ok requestedRounds ->
            match requestKindResult (box (textOf value?requestKind)) with
            | Error error -> Error error
            | Ok requestKind ->
                match ownerLogicalRunOf value?ownerLogicalRun with
                | Error error -> Error error
                | Ok ownerLogicalRun ->
                    Ok
                        { OwnerSessionId = SessionId.create (textOf value?ownerSessionId)
                          OwnerLogicalRun = ownerLogicalRun
                          SourcePhysicalUserMessageId =
                            PhysicalUserMessageId.create (textOf value?sourcePhysicalUserMessageId)
                          SourceProviderRun = ProviderRunIdentity.create (textOf value?sourceProviderRun)
                          SourceToolCallIds =
                            arrayOf value?sourceToolCallIds
                            |> Array.toList
                            |> List.map (ToolCallId.create << textOf)
                          RequestedRounds = requestedRounds
                          ContractRevision = DelegationContractRevisions.create (int value?contractRevision)
                          IsRootWork = unbox<bool> value?isRootWork
                          RequestKind = requestKind
                          CanonicalRole = canonicalRole
                          HasPrefixProbe = unbox<bool> value?hasPrefixProbe
                          IsReplicaOrInternalLeaf = unbox<bool> value?isReplicaOrInternalLeaf
                          IsInteractionRepair = unbox<bool> value?isInteractionRepair
                          IsExplicitRecoveryBranch = unbox<bool> value?isExplicitRecoveryBranch
                          OwnerCancelled = unbox<bool> value?ownerCancelled
                          TargetProviderRunBound = unbox<bool> value?targetProviderRunBound
                          EventStoreHealthy = unbox<bool> value?eventStoreHealthy
                          HostBoundaryHealthy = unbox<bool> value?hostBoundaryHealthy
                          ProcessFuseHealthy = unbox<bool> value?processFuseHealthy
                          OwnerLogicalRunSuperseded = unbox<bool> value?ownerLogicalRunSuperseded
                          PendingRequested = unbox<bool> value?pendingRequested
                          PredictorConfigured = unbox<bool> value?predictorConfigured }

    /// Pure eligibility read: the exact reason admission refuses.
    let policyEligibility (opportunity: obj) : obj =
        match opportunityOf opportunity with
        | Error error -> box {| ok = false; error = error |}
        | Ok opportunity ->
            match StrengthPolicy.eligibility opportunity with
            | StrengthEligibility.Eligible -> box {| kind = "Eligible" |}
            | StrengthEligibility.Ineligible reason ->
                box
                    {| kind = "Ineligible"
                       reason = reason |}

    /// Evidence in, admission decision out. The only "economic" input is the
    /// owner's own integer, already collapsed to the batch maximum upstream.
    let private delegationRequestToJs (request: DelegationRequest) : obj =
        box
            {| decisionId = StrengthDecisionId.value request.DecisionId
               ownerSessionId = SessionId.value request.OwnerSessionId
               ownerLogicalRun =
                box
                    {| logicalRunId = LogicalRunId.value request.OwnerLogicalRun.LogicalRunId
                       authorityRootUserMessageId =
                        AuthorityRootUserMessageId.value request.OwnerLogicalRun.AuthorityRootUserMessageId |}
               sourcePhysicalUserMessageId = PhysicalUserMessageId.value request.SourcePhysicalUserMessageId
               sourceProviderRun = ProviderRunIdentity.value request.SourceProviderRun
               sourceToolCallIds = request.SourceToolCallIds |> List.map ToolCallId.value |> List.toArray
               requestedRounds = ReadonlyRoundBudget.value request.RequestedRounds
               contractRevision = DelegationContractRevisions.value request.ContractRevision |}

    let private optionToObj =
        function
        | Some value -> box value
        | None -> null

    let policyDecide (sha256: string -> string) (opportunity: obj) : obj =
        match opportunityOf opportunity with
        | Error error when error = requestedRoundsOutOfRange ->
            // A budget the owner declared illegally refuses this admission.
            // A value that is not even an integer is a parameter error, not a
            // decode failure, and must never reach the domain as a budget.
            box {| kind = "Skip"; reason = error |}
        | Error error -> box {| ok = false; error = error |}
        | Ok opportunity ->
            match StrengthPolicy.decide sha256 opportunity with
            | StrengthAdmission.Admit request ->
                box
                    {| kind = "Admit"
                       request = delegationRequestToJs request |}
            | StrengthAdmission.Skip reason -> box {| kind = "Skip"; reason = reason |}


    let readonlyCapabilities (role: string) (requestKind: string) : string array =
        match roleResult (box role), requestKindResult (box requestKind) with
        | Ok role, Ok requestKind -> PromptAuthority.toolCapabilitiesFor role requestKind |> permissionsToJs
        | _ -> [||]

    /// StrengthReplica readonly capability labels for a canonical role.
    /// Kept as the short owner name consumed by policy and authority laws.
    let capabilities (role: string) : string array =
        readonlyCapabilities role "strength-replica"

    let readonlyCapabilitiesResult (role: string) (requestKind: string) : obj =
        match roleResult (box role), requestKindResult (box requestKind) with
        | Ok role, Ok requestKind ->
            box
                {| ok = true
                   value = PromptAuthority.toolCapabilitiesFor role requestKind |> permissionsToJs |}
        | Error error, _
        | _, Error error -> box {| ok = false; error = error |}

    let exactReadonlyHostToolMap: obj array =
        StrengthReplicaTools.exactReadonlyHostToolMap
        |> Map.toList
        |> List.map (fun (tool, allowed) -> box {| tool = tool; allowed = allowed |})
        |> List.toArray

    /// WHAT[004]: the readonly delegation tool gate. This is a projection of the
    /// one execution-side predicate in StrengthFrame, which already refuses a
    /// blank name and matches case-insensitively, so the capability labels
    /// ("Read"/"Glob"/"Grep") and the host tool ids ("read"/"glob"/"grep")
    /// answer the same question without a second hand-written name table.
    let isAllowedTool (tool: string) : bool = StrengthFrame.isAllowedTool tool

    let isProjectionTool (tool: string) : bool = StrengthFrame.isProjectionTool tool

    /// Prompt identity remains role-owned and cannot inherit Strength metadata.
    let systemPromptIdForRole (role: string) : string =
        match roleResult (box role) with
        | Ok role -> PromptAuthority.systemPromptIdFor role |> SystemPromptId.value
        | Error _ -> ""

    let systemPromptForRole (role: string) : string =
        match roleResult (box role) with
        | Error _ -> ""
        | Ok role ->
            let prompts = RuntimeResources.current().Prompts

            match role with
            | Role.Manager -> prompts.ManagerSystemPrompt
            | Role.Engineer -> prompts.EngineerSystemPrompt
            | Role.DevOps -> prompts.DevopsSystemPrompt
            | Role.Orchestrator -> prompts.OrchestratorSystemPrompt
            | Role.Blogger -> prompts.BloggerSystemPrompt
            | Role.Coder
            | Role.Inspector
            | Role.Browser
            | Role.Inquiry
            | Role.Distiller
            | Role.Plan -> ""

    let clearsFailureCountOnSuccess (requestKind: string) =
        match requestKindResult (box requestKind) with
        | Ok requestKind -> ProviderRequestKind.clearsFailureCountOnSuccess requestKind
        | Error _ -> false

    let mayCarryProbe (requestKind: string) =
        match requestKindResult (box requestKind) with
        | Ok requestKind -> ProviderRequestKind.mayCarryProbe requestKind
        | Error _ -> false

    let associationFacts (ownerSessionId: string) : obj =
        let ownership =
            StrengthReplicaAssociationHints.ownership (SessionId.create ownerSessionId)

        let owner, attachment =
            match ownership with
            | SessionOwnership.Attached(owner, AttachmentKind.StrengthReplica) ->
                SessionId.value owner, "StrengthReplica"
            | _ -> "", ""

        box
            {| satelliteCases = [| "Companion" |]
               hasReplicaSatellite = false
               attachmentCases =
                [| "Companion"
                   "SyncInspector"
                   "SyncCoder"
                   "SyncEngineer"
                   "Bookkeeper"
                   "StrengthReplica" |]
               executionClass =
                match StrengthReplicaAssociationHints.executionClass with
                | SessionExecutionClass.InternalLeaf -> "InternalLeaf"
                | SessionExecutionClass.Work -> "Work"
               ownerSessionId = owner
               attachment = attachment
               strengthReplicaAttachment =
                StrengthReplicaAssociationHints.isStrengthReplicaAttachment AttachmentKind.StrengthReplica
               companionAttachment =
                StrengthReplicaAssociationHints.isStrengthReplicaAttachment AttachmentKind.Companion |}

    let private decisionOfResult (value: obj) =
        match textOf value?kind with
        | "Committed" -> StrengthAppendOutcome.Committed
        | "Rejected" -> StrengthAppendOutcome.Rejected
        | _ -> StrengthAppendOutcome.CommitUnknown

    let private durableEvidenceOf (value: obj) =
        match textOf value with
        | "Matches" -> StrengthDurableEvidence.Matches
        | "Absent" -> StrengthDurableEvidence.Absent
        | "Conflicts" -> StrengthDurableEvidence.Conflicts
        | _ -> StrengthDurableEvidence.Unknown

    let private commitDecisionName decision =
        match decision with
        | StrengthCommitDecision.Proceed -> "Proceed"
        | StrengthCommitDecision.FallBackNoDelegation -> "FallBackNoDelegation"
        | StrengthCommitDecision.RetryAppend -> "RetryAppend"
        | StrengthCommitDecision.FailClosed -> "FailClosed"

    let commitResolvePrepared (appendOutcome: string) (evidence: string) =
        StrengthCommit.resolvePrepared
            (decisionOfResult (box {| kind = appendOutcome |}))
            (durableEvidenceOf (box evidence))
        |> commitDecisionName

    let commitResolvePromotion (appendOutcome: string) (evidence: string) =
        StrengthCommit.resolvePromotion
            (decisionOfResult (box {| kind = appendOutcome |}))
            (durableEvidenceOf (box evidence))
        |> commitDecisionName

    let promotionDecide (targetRun: string) (observedRun: string) (evidence: string) =
        let output =
            match evidence with
            | "RealOutput" -> StrengthProviderOutputEvidence.RealOutput
            | "TransportOnly" -> StrengthProviderOutputEvidence.TransportOnly
            | _ -> StrengthProviderOutputEvidence.NoOutput

        match
            StrengthPromotion.decide
                (ProviderRunIdentity.create targetRun)
                (ProviderRunIdentity.create observedRun)
                output
        with
        | StrengthPromotionDecision.Promote -> "Promote"
        | StrengthPromotionDecision.IgnoreWrongRun -> "IgnoreWrongRun"
        | StrengthPromotionDecision.AwaitOrAbandon -> "AwaitOrAbandon"

    /// DELEGATE-6.1: Prepared no longer carries any budget. RequestedRounds
    /// belongs to the DelegationRequested fact alone.
    let eventPrepared
        (owner: string)
        (decision: string)
        (target: string)
        (replica: string)
        (anchor: string)
        (digest: string)
        (byteLength: int)
        (refs: string array)
        : obj =
        EventHandle(
            StrengthEvents.prepared
                (SessionId.create owner)
                (StrengthDecisionId.create decision)
                (ProviderRunIdentity.create target)
                (SessionId.create replica)
                anchor
                digest
                byteLength
                (refs |> Array.toList |> List.map PayloadRef.create)
        )
        :> obj

    let eventPromoted (owner: string) (decision: string) (target: string) (digest: string) (refs: string array) : obj =
        EventHandle(
            StrengthEvents.promoted
                (SessionId.create owner)
                (StrengthDecisionId.create decision)
                (ProviderRunIdentity.create target)
                digest
                (refs |> Array.toList |> List.map PayloadRef.create)
        )
        :> obj

    let eventTraced (decision: string) (startInclusive: int64) (endExclusive: int64) : obj =
        EventHandle(StrengthEvents.traced (StrengthDecisionId.create decision) startInclusive endExclusive) :> obj

    let eventAbandoned (decision: string) (target: string) : obj =
        EventHandle(StrengthEvents.abandoned (StrengthDecisionId.create decision) (ProviderRunIdentity.create target))
        :> obj

    /// Fail closed at the JS/F# DU boundary: only a real EventHandle carries
    /// a StrengthEvent. Any other value is refused here, so no fold ever
    /// receives an undefined union value.
    let private eventOf (value: obj) : Result<StrengthEvent, string> =
        match value with
        | :? EventHandle as handle -> Ok handle.Value
        | _ -> Error "expected a Strength event handle"

    let private closedFromName =
        function
        | DelegationClosedFrom.Requested -> "Requested"
        | DelegationClosedFrom.Bound -> "Bound"

    let private closedReasonName =
        function
        | DelegationClosedReason.NoMaterial -> "NoMaterial"
        | DelegationClosedReason.CannotContinue -> "CannotContinue"
        | DelegationClosedReason.Cancelled -> "Cancelled"
        | DelegationClosedReason.Superseded -> "Superseded"
        | DelegationClosedReason.RecoveryAbandoned -> "RecoveryAbandoned"

    /// DELEGATE-015: imported history material is evidence, never an admission
    /// (WHY [015]); the adopted/relinquished split stays a closed union.
    let private importOutcomeToJs (outcome: DelegationImportOutcome) : obj =
        match outcome with
        | DelegationImportOutcome.Adopted material ->
            box
                {| kind = "adopted"
                   targetProviderRun = ProviderRunIdentity.value material.TargetProviderRun
                   frameDigest = material.FrameDigest
                   byteLength = material.ByteLength
                   materialPayloads = material.MaterialPayloads |> List.map PayloadRef.value |> List.toArray
                   tracedStartInclusive = material.TracedStartInclusive |> optionToObj
                   tracedEndExclusive = material.TracedEndExclusive |> optionToObj |}
        | DelegationImportOutcome.Relinquished material ->
            box
                {| kind = "relinquished"
                   targetProviderRun =
                    material.TargetProviderRun
                    |> Option.map ProviderRunIdentity.value
                    |> optionToObj
                   reason = material.Reason |}

    let private eventTypeOf (event: StrengthEvent) =
        match event with
        | StrengthEvent.DelegationRequested _ -> "DelegationRequested"
        | StrengthEvent.DelegationBound _ -> "DelegationBound"
        | StrengthEvent.DelegationClosed _ -> "DelegationClosed"
        | StrengthEvent.Prepared _ -> "StrengthCandidatePrepared"
        | StrengthEvent.Promoted _ -> "StrengthCandidatePromoted"
        | StrengthEvent.Traced _ -> "StrengthFramesTraced"
        | StrengthEvent.DelegationHistoryImported _ -> "DelegationHistoryImported"
        | StrengthEvent.Abandoned _ -> "StrengthCandidateAbandoned"

    let eventType (value: obj) =
        match eventOf value with
        | Error _ -> "unknown"
        | Ok event -> eventTypeOf event

    /// DELEGATE-6.2: the durable Prepared write set as the folded projection
    /// shows it — the fact fields only. The discriminator belongs to the event
    /// view; a folded view must not grow a key the durable event never had.
    let private preparedFactToJs (event: StrengthCandidatePrepared) : obj =
        box
            {| ownerSessionId = SessionId.value event.OwnerSessionId
               decisionId = StrengthDecisionId.value event.DecisionId
               targetProviderRun = ProviderRunIdentity.value event.TargetProviderRun
               replicaSessionId = SessionId.value event.ReplicaSessionId
               anchorDigest = event.AnchorDigest
               frameDigest = event.FrameDigest
               byteLength = event.ByteLength
               materialPayloads = event.MaterialPayloads |> List.map PayloadRef.value |> List.toArray |}

    let private preparedToJs (event: StrengthCandidatePrepared) : obj =
        box
            {| kind = "Prepared"
               ownerSessionId = SessionId.value event.OwnerSessionId
               decisionId = StrengthDecisionId.value event.DecisionId
               targetProviderRun = ProviderRunIdentity.value event.TargetProviderRun
               replicaSessionId = SessionId.value event.ReplicaSessionId
               anchorDigest = event.AnchorDigest
               frameDigest = event.FrameDigest
               byteLength = event.ByteLength
               materialPayloads = event.MaterialPayloads |> List.map PayloadRef.value |> List.toArray |}

    let private eventViewOf (event: StrengthEvent) : obj =
        match event with
        | StrengthEvent.DelegationRequested event ->
            // The decoded envelope value is the fact itself: the discriminator
            // and the request fields on one object, in the same field set as
            // delegationRequestToJs (the nested form stays for policy and
            // folded views, which read the request as a whole).
            box
                {| kind = "DelegationRequested"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   ownerSessionId = SessionId.value event.OwnerSessionId
                   ownerLogicalRun =
                    box
                        {| logicalRunId = LogicalRunId.value event.OwnerLogicalRun.LogicalRunId
                           authorityRootUserMessageId =
                            AuthorityRootUserMessageId.value event.OwnerLogicalRun.AuthorityRootUserMessageId |}
                   sourcePhysicalUserMessageId = PhysicalUserMessageId.value event.SourcePhysicalUserMessageId
                   sourceProviderRun = ProviderRunIdentity.value event.SourceProviderRun
                   sourceToolCallIds = event.SourceToolCallIds |> List.map ToolCallId.value |> List.toArray
                   requestedRounds = ReadonlyRoundBudget.value event.RequestedRounds
                   contractRevision = DelegationContractRevisions.value event.ContractRevision |}
        | StrengthEvent.DelegationBound event ->
            box
                {| kind = "DelegationBound"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   targetProviderRun = ProviderRunIdentity.value event.TargetProviderRun
                   replicaSessionId = SessionId.value event.ReplicaSessionId
                   anchorDigest = event.AnchorDigest |}
        | StrengthEvent.DelegationClosed event ->
            box
                {| kind = "DelegationClosed"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   from = closedFromName event.From
                   reason = closedReasonName event.Reason |}
        | StrengthEvent.Prepared event -> box (preparedToJs event)
        | StrengthEvent.Promoted event ->
            box
                {| kind = "Promoted"
                   ownerSessionId = SessionId.value event.OwnerSessionId
                   decisionId = StrengthDecisionId.value event.DecisionId
                   targetProviderRun = ProviderRunIdentity.value event.TargetProviderRun
                   frameDigest = event.FrameDigest
                   materialPayloads = event.MaterialPayloads |> List.map PayloadRef.value |> List.toArray |}
        | StrengthEvent.Traced event ->
            box
                {| kind = "Traced"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   startInclusive = event.StartInclusive
                   endExclusive = event.EndExclusive |}
        | StrengthEvent.DelegationHistoryImported event ->
            box
                {| kind = "DelegationHistoryImported"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   sourceStreamId = event.SourceStreamId
                   sourceEventId = event.SourceEventId
                   importId = event.ImportId
                   oldBudgetEvidence = event.OldBudgetEvidence |> optionToObj
                   outcome = importOutcomeToJs event.Outcome |}
        | StrengthEvent.Abandoned event ->
            box
                {| kind = "Abandoned"
                   decisionId = StrengthDecisionId.value event.DecisionId
                   targetProviderRun = ProviderRunIdentity.value event.TargetProviderRun |}

    let eventView (value: obj) : obj =
        match eventOf value with
        | Error error -> box {| ok = false; error = error |}
        | Ok event -> eventViewOf event

    let private projectionOf value =
        unbox<ProjectionHandle> value |> fun handle -> handle.Value

    let projectionEmpty () : obj =
        ProjectionHandle StrengthProjection.empty :> obj

    let private projectionErrorName error =
        match error with
        | StrengthProjectionError.RequestedConflict _ -> "RequestedConflict"
        | StrengthProjectionError.BoundWithoutRequested _ -> "BoundWithoutRequested"
        | StrengthProjectionError.BoundConflict _ -> "BoundConflict"
        | StrengthProjectionError.TargetAlreadyBound _ -> "TargetAlreadyBound"
        | StrengthProjectionError.ClosedWithoutRequested _ -> "ClosedWithoutRequested"
        | StrengthProjectionError.ClosedConflict _ -> "ClosedConflict"
        | StrengthProjectionError.PreparedWithoutBound _ -> "PreparedWithoutBound"
        | StrengthProjectionError.PreparedConflict _ -> "PreparedConflict"
        | StrengthProjectionError.PreparedBindingMismatch _ -> "PreparedBindingMismatch"
        | StrengthProjectionError.PromotionWithoutPrepared _ -> "PromotionWithoutPrepared"
        | StrengthProjectionError.PromotionMismatch _ -> "PromotionMismatch"
        | StrengthProjectionError.PromotionAfterAbandon _ -> "PromotionAfterAbandon"
        | StrengthProjectionError.TraceWithoutPrepared _ -> "TraceWithoutPrepared"
        | StrengthProjectionError.TraceWithoutPromotion _ -> "TraceWithoutPromotion"
        | StrengthProjectionError.InvalidTraceRange _ -> "InvalidTraceRange"
        | StrengthProjectionError.TraceConflict _ -> "TraceConflict"
        | StrengthProjectionError.AbandonWithoutPrepared _ -> "AbandonWithoutPrepared"
        | StrengthProjectionError.AbandonMismatch _ -> "AbandonMismatch"
        | StrengthProjectionError.AbandonAfterPromotion _ -> "AbandonAfterPromotion"
        | StrengthProjectionError.ImportConflict _ -> "ImportConflict"


    let private candidateStateName state =
        match state with
        | StrengthCandidateState.Requested -> "Requested"
        | StrengthCandidateState.Bound -> "Bound"
        | StrengthCandidateState.Prepared -> "Prepared"
        | StrengthCandidateState.Promoted -> "Promoted"
        | StrengthCandidateState.Traced -> "Traced"
        | StrengthCandidateState.Closed _ -> "Closed"
        | StrengthCandidateState.Abandoned -> "Abandoned"

    /// Folded view of one decision: the immutable request plus everything
    /// legally attached so far, expressed as a closed union — never boolean
    /// combinations (DELEGATE-6.2).
    let private delegationViewToJs (view: StrengthDelegationView) : obj =
        box
            {| request = delegationRequestToJs view.Request
               binding =
                view.Binding
                |> Option.map (fun binding ->
                    box
                        {| targetProviderRun = ProviderRunIdentity.value binding.TargetProviderRun
                           replicaSessionId = SessionId.value binding.ReplicaSessionId
                           anchorDigest = binding.AnchorDigest |})
                |> optionToObj
               prepared = view.Prepared |> Option.map preparedFactToJs |> optionToObj
               state = candidateStateName view.State
               traceRange =
                view.TraceRange
                |> Option.map (fun range ->
                    box
                        {| startInclusive = range.StartInclusive
                           endExclusive = range.EndExclusive |})
                |> optionToObj |}

    let projectionApply (projection: obj) (event: obj) : obj =
        match eventOf event with
        | Error error -> box {| ok = false; error = error |}
        | Ok event ->
            match StrengthProjection.apply (projectionOf projection) event with
            | Ok next ->
                box
                    {| ok = true
                       value = (ProjectionHandle next :> obj) |}
            | Error error ->
                box
                    {| ok = false
                       error = projectionErrorName error |}

    let projectionHasPrepared (decision: string) (projection: obj) =
        StrengthProjection.hasPrepared (StrengthDecisionId.create decision) (projectionOf projection)

    let projectionIsPromoted (decision: string) (projection: obj) =
        StrengthProjection.isPromoted (StrengthDecisionId.create decision) (projectionOf projection)

    let projectionDecisionForTarget (target: string) (projection: obj) =
        match StrengthProjection.tryDecisionForTarget (ProviderRunIdentity.create target) (projectionOf projection) with
        | Some value -> StrengthDecisionId.value value
        | None -> null

    let projectionCandidate (decision: string) (projection: obj) =
        match StrengthProjection.tryCandidate (StrengthDecisionId.create decision) (projectionOf projection) with
        | Some view -> delegationViewToJs view
        | None -> null

    let projectionCandidateBySource
        (ownerSessionId: string)
        (logicalRunId: string)
        (authorityRootUserMessageId: string)
        (sourcePhysicalUserMessageId: string)
        (sourceProviderRun: string)
        (projection: obj)
        : obj =
        let owner = SessionId.create ownerSessionId

        let logicalRun =
            { LogicalRunId = LogicalRunId.create logicalRunId
              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRootUserMessageId }

        let physicalUserMsg = PhysicalUserMessageId.create sourcePhysicalUserMessageId
        let providerRun = ProviderRunIdentity.create sourceProviderRun

        match
            StrengthProjection.tryCandidateBySource
                owner
                logicalRun
                physicalUserMsg
                providerRun
                (projectionOf projection)
        with
        | Some view -> delegationViewToJs view
        | None -> null

    let projectionRequestedRoundsBySource
        (ownerSessionId: string)
        (logicalRunId: string)
        (authorityRootUserMessageId: string)
        (sourcePhysicalUserMessageId: string)
        (sourceProviderRun: string)
        (projection: obj)
        : obj =
        let owner = SessionId.create ownerSessionId

        let logicalRun =
            { LogicalRunId = LogicalRunId.create logicalRunId
              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRootUserMessageId }

        let physicalUserMsg = PhysicalUserMessageId.create sourcePhysicalUserMessageId
        let providerRun = ProviderRunIdentity.create sourceProviderRun

        match
            StrengthProjection.tryCandidateBySource
                owner
                logicalRun
                physicalUserMsg
                providerRun
                (projectionOf projection)
        with
        | Some view ->
            match view.State with
            | StrengthCandidateState.Closed closed when closed.Reason = DelegationClosedReason.Superseded -> null
            | StrengthCandidateState.Closed _
            | StrengthCandidateState.Requested
            | StrengthCandidateState.Bound
            | StrengthCandidateState.Prepared
            | StrengthCandidateState.Promoted
            | StrengthCandidateState.Traced -> box (ReadonlyRoundBudget.value view.Request.RequestedRounds)
            | StrengthCandidateState.Abandoned -> null
        | None -> null

    /// DELEGATE-6.3: the requested rounds are read from the immutable
    /// projection; no layer keeps its own copy of the budget.
    let projectionRequestedRounds (decision: string) (projection: obj) =
        match StrengthProjection.requestedRounds (StrengthDecisionId.create decision) (projectionOf projection) with
        | Some rounds -> box (ReadonlyRoundBudget.value rounds)
        | None -> null

    let projectionTraceRange (decision: string) (projection: obj) =
        match StrengthProjection.tryTraceRange (StrengthDecisionId.create decision) (projectionOf projection) with
        | Some range ->
            box
                {| startInclusive = range.StartInclusive
                   endExclusive = range.EndExclusive |}
        | None -> null

    /// DELEGATE-015: evidence-only imported history, folded by import identity.
    /// The fold keeps the causal position, the digest and the trace coverage as
    /// evidence, and never yields a runnable delegation (WHY [015]).
    let private importedHistoryToJs (imported: DelegationHistoryImported) : obj =
        box
            {| decisionId = StrengthDecisionId.value imported.DecisionId
               sourceStreamId = imported.SourceStreamId
               sourceEventId = imported.SourceEventId
               importId = imported.ImportId
               oldBudgetEvidence = imported.OldBudgetEvidence |> optionToObj
               outcome = importOutcomeToJs imported.Outcome |}

    let projectionImported (importId: string) (projection: obj) =
        match StrengthProjection.tryImported importId (projectionOf projection) with
        | Some imported -> importedHistoryToJs imported
        | None -> null

    let storeToEnvelope (sha256: string -> string) (event: obj) : obj =
        match eventOf event with
        | Error error -> box {| ok = false; error = error |}
        | Ok event -> EnvelopeHandle(StrengthStore.toEnvelope sha256 event) :> obj

    let private envelopeOf value =
        unbox<EnvelopeHandle> value |> fun handle -> handle.Value

    let envelopeView (value: obj) : obj =
        let envelope = envelopeOf value

        box
            {| id = EventId.value envelope.EventId
               stream = EventStreamId.value envelope.StreamId
               eventType = envelope.EventType
               parents = envelope.Parents |> List.map EventId.value |> List.toArray
               payloadRefs = envelope.PayloadRefs |> List.map PayloadRef.value |> List.toArray |}

    let storeTryDecodeEnvelope (value: obj) : obj =
        match StrengthStore.tryDecodeEnvelope (envelopeOf value) with
        | Ok event ->
            box
                {| ok = true
                   value = eventView (EventHandle event :> obj) |}
        | Error error -> box {| ok = false; error = error |}

    // DELEGATE-015: offline migration of pre-delegation Strength history,
    // projected JS-native. The planner and the classification live in the
    // Migration module; here tests only get plain-object boundary shapes.

    let private legacyEnvelopeToJs (envelope: LegacyEnvelope) : obj =
        box
            {| eventId = envelope.EventId
               sourceStreamId = envelope.SourceStreamId
               eventType = envelope.EventType
               decisionId = envelope.DecisionId
               budgetEvidence = envelope.BudgetEvidence |> optionToObj
               targetProviderRun = envelope.TargetProviderRun |> optionToObj
               frameDigest = envelope.FrameDigest |> optionToObj
               byteLength = envelope.ByteLength |> optionToObj
               tracedStartInclusive = envelope.TracedStartInclusive |> optionToObj
               tracedEndExclusive = envelope.TracedEndExclusive |> optionToObj
               materialPayloads = envelope.MaterialPayloads |}

    /// host-boundary-030: a traced range crossing this boundary is a JS bigint
    /// (a decoded Thoth int64 arrives as one, and `Encode.int64` writes it back
    /// as text). Thoth's own `Decode.int64` only inspects `number` and `string`,
    /// so a bigint would fail it as a bad primitive; accept the bigint here and
    /// leave every other shape to the stock decoder, which fails closed.
    let private int64View: Decoder<int64> =
        fun path value ->
            if emitJsExpr value "typeof $0 === 'bigint'" then
                // In Fable a JS bigint already is an int64; no conversion.
                Ok(unbox<int64> value)
            else
                Decode.int64 path value

    let private legacyEnvelopeViewDecoder: Decoder<LegacyEnvelope> =
        Decode.object (fun get ->
            { LegacyEnvelope.EventId = get.Required.Field "eventId" Decode.string
              SourceStreamId = get.Required.Field "sourceStreamId" Decode.string
              EventType = get.Required.Field "eventType" Decode.string
              DecisionId = get.Required.Field "decisionId" Decode.string
              BudgetEvidence = get.Optional.Field "budgetEvidence" Decode.string
              TargetProviderRun = get.Optional.Field "targetProviderRun" Decode.string
              FrameDigest = get.Optional.Field "frameDigest" Decode.string
              ByteLength = get.Optional.Field "byteLength" Decode.int
              TracedStartInclusive = get.Optional.Field "tracedStartInclusive" int64View
              TracedEndExclusive = get.Optional.Field "tracedEndExclusive" int64View
              MaterialPayloads =
                get.Optional.Field "materialPayloads" (Decode.list Decode.string)
                |> Option.defaultValue []
                |> List.toArray })

    let private importFactToJs (fact: ImportFact) : obj =
        box
            {| decisionId = fact.DecisionId
               sourceStreamId = fact.SourceStreamId
               sourceEventId = fact.SourceEventId
               importId = fact.ImportId
               oldBudgetEvidence = fact.OldBudgetEvidence |> optionToObj
               outcomeKind = fact.OutcomeKind
               targetProviderRun = fact.TargetProviderRun |> optionToObj
               frameDigest = fact.FrameDigest |> optionToObj
               byteLength = fact.ByteLength |> optionToObj
               tracedStartInclusive = fact.TracedStartInclusive |> optionToObj
               tracedEndExclusive = fact.TracedEndExclusive |> optionToObj
               materialPayloads = fact.MaterialPayloads
               relinquishReason = fact.RelinquishReason |> optionToObj |}

    let private importFactViewDecoder: Decoder<ImportFact> =
        Decode.object (fun get ->
            { ImportFact.DecisionId = get.Required.Field "decisionId" Decode.string
              SourceStreamId = get.Required.Field "sourceStreamId" Decode.string
              SourceEventId = get.Required.Field "sourceEventId" Decode.string
              ImportId = get.Required.Field "importId" Decode.string
              OldBudgetEvidence = get.Optional.Field "oldBudgetEvidence" Decode.string
              OutcomeKind = get.Required.Field "outcomeKind" Decode.string
              TargetProviderRun = get.Optional.Field "targetProviderRun" Decode.string
              FrameDigest = get.Optional.Field "frameDigest" Decode.string
              ByteLength = get.Optional.Field "byteLength" Decode.int
              TracedStartInclusive = get.Optional.Field "tracedStartInclusive" int64View
              TracedEndExclusive = get.Optional.Field "tracedEndExclusive" int64View
              MaterialPayloads =
                get.Optional.Field "materialPayloads" (Decode.list Decode.string)
                |> Option.defaultValue []
                |> List.toArray
              RelinquishReason = get.Optional.Field "relinquishReason" Decode.string })

    /// Recognize one envelope payload as legacy, current, or not Strength.
    /// Canonical JSON text in; flat verdict out.
    let migrationClassifyEnvelope (eventType: string) (payloadJson: string) : obj =
        let classification =
            LegacyProtocolClassifier.classifyEnvelopeJson eventType payloadJson

        box
            {| kind = classification.Kind
               reason = classification.Reason |> optionToObj |}

    /// Extract the legacy view of one envelope, or `null` when the envelope is
    /// not one of the four pre-delegation Strength fact types.
    let migrationReadLegacyEnvelope (envelopeJson: string) : obj =
        match DelegationHistoryMigration.readLegacyEnvelope envelopeJson with
        | Some envelope -> legacyEnvelopeToJs envelope
        | None -> null

    /// Plan the import facts of one legacy decision. Envelopes arrive in causal
    /// order as JS-native views; the planner is the Migration module's.
    let migrationPlanDecision (sha256: string -> string) (contractRevision: int) (envelopes: obj array) : obj =
        let decoded =
            envelopes
            |> Array.map (fun envelope ->
                match Decode.fromValue "$" legacyEnvelopeViewDecoder envelope with
                | Ok value -> Ok value
                | Error error -> Error error)

        match
            decoded
            |> Array.tryPick (function
                | Error error -> Some error
                | Ok _ -> None)
        with
        | Some error -> box {| ok = false; error = error |}
        | None ->
            let legacy =
                decoded
                |> Array.choose (function
                    | Ok value -> Some value
                    | Error _ -> None)

            try
                let facts = DelegationHistoryMigration.planDecision sha256 contractRevision legacy

                box
                    {| ok = true
                       value = facts |> Array.map importFactToJs |}
            with error ->
                box {| ok = false; error = error.Message |}

    /// Render one planned import fact as the JS-native event the EventStore
    /// surface appends; the same encoder as the runtime decode path.
    let migrationImportEvent (sha256: string -> string) (value: obj) : obj =
        match Decode.fromValue "$" importFactViewDecoder value with
        | Ok fact ->
            box
                {| ok = true
                   value = DelegationHistoryMigration.importEventJs sha256 fact |}
        | Error error -> box {| ok = false; error = error |}

    let private appendErrorName error =
        match error with
        | AppendError.StorageInvalid invalid ->
            match invalid with
            | StorageInvalid.IdentityCollision _ -> "IdentityCollision"
            | StorageInvalid.NonCanonical _ -> "NonCanonical"
            | StorageInvalid.MalformedEnvelope _ -> "MalformedEnvelope"
            | StorageInvalid.MissingParent _ -> "MissingParent"
            | StorageInvalid.CyclicParents -> "CyclicParents"
            | StorageInvalid.MissingPayload _ -> "MissingPayload"
            | StorageInvalid.UnknownEventType _ -> "UnknownEventType"
        | AppendError.SemanticCut _ -> "SemanticCut"
        | AppendError.AppendFailed _ -> "AppendFailed"
        | AppendError.AppendNotAttempted _ -> "AppendNotAttempted"
        | AppendError.CommitUnknown _ -> "CommitUnknown"
        | AppendError.NoNewWriteReleaseFailed _ -> "NoNewWriteReleaseFailed"

    let private appendEnvelopeToJs (envelope: EventEnvelope) : obj =
        box
            {| id = EventId.value envelope.EventId
               stream = EventStreamId.value envelope.StreamId
               ``type`` = envelope.EventType
               parents = envelope.Parents |> List.map EventId.value |> List.toArray
               payload = envelope.Payload |> Encode.toString 0 |> JS.JSON.parse
               payloadRefs = envelope.PayloadRefs |> List.map PayloadRef.value |> List.toArray |}

    let private appendCutToJs (cut: SemanticCut) : obj =
        box
            {| failedEventId = EventId.value cut.FailedEventId
               rule = cut.Rule
               cutEventId = EventId.value cut.CutEventId
               reason = cut.Reason |}

    let private preparedAppendToJs (prepared: PreparedAppend) : obj =
        box
            {| durableEvents = prepared.DurableEvents |> List.map appendEnvelopeToJs |> List.toArray
               cuts = prepared.Cuts |> List.map appendCutToJs |> List.toArray |}

    let private appendFaultToJs (fault: AppendFault) : obj =
        box
            {| phase = sprintf "%A" fault.Phase
               cause = fault.Cause |}

    let private appendRejectionToJs rejection : obj =
        match rejection with
        | AppendPreWriteRejection.PreparationRejected reason ->
            box
                {| code = "AppendFailed"
                   reason = reason |}
        | AppendPreWriteRejection.StorageInvalid invalid ->
            let detail =
                match invalid with
                | StorageInvalid.IdentityCollision eventId
                | StorageInvalid.MissingParent eventId ->
                    box
                        {| code = appendErrorName (AppendError.StorageInvalid invalid)
                           eventId = EventId.value eventId |}
                | StorageInvalid.NonCanonical reason
                | StorageInvalid.MalformedEnvelope reason ->
                    box
                        {| code = appendErrorName (AppendError.StorageInvalid invalid)
                           reason = reason |}
                | StorageInvalid.CyclicParents -> box {| code = "CyclicParents" |}
                | StorageInvalid.MissingPayload payloadRef ->
                    box
                        {| code = "MissingPayload"
                           payloadRef = PayloadRef.value payloadRef |}
                | StorageInvalid.UnknownEventType eventType ->
                    box
                        {| code = "UnknownEventType"
                           eventType = eventType |}

            box
                {| code = "StorageInvalid"
                   error = detail |}

    let private appendSettlementToJs failure : obj =
        let view phase cause cleanup requested prepared prior : obj =
            box
                {| code = appendErrorName failure
                   phase = phase
                   cause = cause
                   cleanupFailures = cleanup |> List.map appendFaultToJs |> List.toArray
                   requested = requested |> List.map appendEnvelopeToJs |> List.toArray
                   prepared = prepared |> Option.map preparedAppendToJs |> Option.defaultValue null
                   priorRejection = prior |> Option.map appendRejectionToJs |> Option.defaultValue null |}

        match failure with
        | AppendError.AppendNotAttempted evidence ->
            view
                (sprintf "%A" evidence.Primary.Phase)
                evidence.Primary.Cause
                evidence.CleanupFailures
                evidence.Requested
                evidence.Prepared
                evidence.PriorRejection
        | AppendError.CommitUnknown evidence ->
            view
                (sprintf "%A" evidence.Primary.Phase)
                evidence.Primary.Cause
                evidence.CleanupFailures
                evidence.Requested
                (Some evidence.Prepared)
                None
        | AppendError.NoNewWriteReleaseFailed evidence ->
            view "StoreRelease" evidence.Cause [] evidence.Requested evidence.Prepared None
        | AppendError.StorageInvalid _
        | AppendError.SemanticCut _
        | AppendError.AppendFailed _ -> invalidArg "failure" "expected an append settlement failure"

    let private settlementFailureToJs eventId failure : obj =
        box
            {| ok = false
               error = appendErrorName failure
               eventId = EventId.value eventId
               settlement = appendSettlementToJs failure |}

    let storeAppend (store: obj) (sha256: string -> string) (event: obj) : Task<obj> =
        task {
            match eventOf event with
            | Error error -> return box {| ok = false; error = error |}
            | Ok event ->
                let! result = StrengthStore.append (EventStoreStrengthSurface.storeOf store) sha256 event

                return
                    match result with
                    | Ok() -> box {| ok = true |}
                    | Error(eventId,
                            ((AppendError.AppendNotAttempted _ | AppendError.CommitUnknown _ | AppendError.NoNewWriteReleaseFailed _) as failure)) ->
                        settlementFailureToJs eventId failure
                    | Error(_, error) ->
                        box
                            {| ok = false
                               error = appendErrorName error |}
        }

    let storeWritePayload (store: obj) (bytes: byte array) : Task<obj> =
        task {
            let! result = EventStoreStrengthSurface.writePayload store bytes

            return
                match result with
                | Ok value ->
                    box
                        {| ok = true
                           value = PayloadRef.value value |}
                | Error error -> box {| ok = false; error = error |}
        }

    let storeReadPayload (store: obj) (reference: string) : Task<obj> =
        task {
            let! result = EventStoreStrengthSurface.readPayload store (PayloadRef.create reference)

            return
                match result with
                | Ok(Some bytes) -> box {| ok = true; value = bytes |}
                | Ok None -> box {| ok = true; value = null |}
                | Error error -> box {| ok = false; error = error |}
        }

    let storeCurrent (store: obj) : obj =
        match EventStoreStrengthSurface.current store "Strength" with
        | Some value -> ProjectionHandle(unbox<StrengthProjection> value) :> obj
        | None -> ProjectionHandle StrengthProjection.empty :> obj

    let durabilityCreate (store: obj) : obj =
        DurabilityHandle(EventStoreStrengthSurface.durability store) :> obj

    let private durabilityOf value =
        unbox<DurabilityHandle> value |> fun handle -> handle.Value

    let durabilityLoadProjection (durability: obj) : Task<obj> =
        task {
            let! result = (durabilityOf durability).LoadProjection()

            return
                match result with
                | Ok projection ->
                    box
                        {| ok = true
                           value = (ProjectionHandle projection :> obj) |}
                | Error error -> box {| ok = false; error = error |}
        }

    let durabilityLoadBundleForDecision (durability: obj) (projection: obj) (decision: string) : Task<obj> =
        task {
            match StrengthProjection.tryCandidate (StrengthDecisionId.create decision) (projectionOf projection) with
            | None ->
                return
                    box
                        {| ok = false
                           error = "missing candidate" |}
            | Some view ->
                match view.Prepared with
                | None ->
                    return
                        box
                            {| ok = false
                               error = "missing candidate" |}
                | Some prepared ->
                    let! result = (durabilityOf durability).LoadFrameBundle prepared

                    return
                        match result with
                        | Ok bundle ->
                            box
                                {| ok = true
                                   value = bundleToJs bundle |}
                        | Error error -> box {| ok = false; error = error |}
        }

    let durabilityAppend (durability: obj) (event: obj) : Task<obj> =
        task {
            match eventOf event with
            | Error error -> return box {| ok = false; error = error |}
            | Ok event ->
                let! result = (durabilityOf durability).Append event

                return
                    match result with
                    | StrengthDurableAppend.Applied -> box {| ok = true |}
                    | StrengthDurableAppend.SemanticRejected reason -> box {| ok = false; error = reason |}
                    | StrengthDurableAppend.StorageInvalid _ ->
                        box
                            {| ok = false
                               error = "StorageInvalid" |}
                    | StrengthDurableAppend.StorageFailed reason -> box {| ok = false; error = reason |}
                    | StrengthDurableAppend.SettlementFailed(eventId, failure) -> settlementFailureToJs eventId failure
        }

    /// DELEGATE-6.6/STRENGTH-006: publish the durable candidate for one Bound
    /// decision. The request carries no budget: RequestedRounds belongs to the
    /// DelegationRequested fact alone. The frame bundle arrives in the same JS
    /// shape every other bundle crosses this surface; its payload refs were
    /// written by the caller beforehand.
    let durabilityPublishPrepared (durability: obj) (request: obj) : Task<obj> =
        task {
            let! result =
                (durabilityOf durability).PublishPrepared
                    { OwnerSessionId = SessionId.create (textOf request?ownerSessionId)
                      DecisionId = StrengthDecisionId.create (textOf request?decisionId)
                      TargetProviderRun = ProviderRunIdentity.create (textOf request?targetProviderRun)
                      ReplicaSessionId = SessionId.create (textOf request?replicaSessionId)
                      AnchorDigest = textOf request?anchorDigest
                      Bundle = bundleOf request?bundle }

            return
                match result with
                | StrengthPreparedPublish.Published -> box {| kind = "Published" |}
                | StrengthPreparedPublish.StorageInvalid error ->
                    box
                        {| kind = "StorageInvalid"
                           error = error |}
                | StrengthPreparedPublish.Rejected error -> box {| kind = "Rejected"; error = error |}
                | StrengthPreparedPublish.SettlementFailed(eventId, failure) ->
                    box
                        {| kind = "SettlementFailed"
                           eventId = EventId.value eventId
                           settlement = appendSettlementToJs failure |}
        }

    let traceExpectedParts (bundle: obj) : obj array =
        StrengthTraceRecovery.expectedParts (bundleOf bundle)
        |> List.map (fun (kind, toolName, body) ->
            box
                {| kind = kind
                   toolName = toolName |> optionToObj
                   body = body |})
        |> List.toArray

    let traceRecoverRange (bundle: obj) (observed: obj array) : obj =
        let parts: StrengthTraceObservedPart list =
            observed
            |> Array.toList
            |> List.map (fun value ->
                ({ CursorSequence = int64 (int (textOf value?cursorSequence))
                   Kind = textOf value?kind
                   ToolName = optionalText value?toolName
                   Body = textOf value?body }
                : StrengthTraceObservedPart))

        match StrengthTraceRecovery.recoverRange (bundleOf bundle) parts with
        | Ok None -> box {| ok = true; value = (null: obj) |}
        | Ok(Some range) ->
            box
                {| ok = true
                   value =
                    box
                        {| startInclusive = range.StartInclusive
                           endExclusive = range.EndExclusive |} |}
        | Error error -> box {| ok = false; error = error |}

    let turnEvidenceClassify (parts: obj array) : obj =
        let parsed =
            parts
            |> Array.toList
            |> List.fold
                (fun state value ->
                    match state, partResult value with
                    | Ok current, Ok part -> Ok(part :: current)
                    | Error error, _ -> Error error
                    | _, Error error -> Error error)
                (Ok [])
            |> Result.map (List.rev >> List.toArray)

        match parsed with
        | Error error -> box {| ok = false; error = error |}
        | Ok parts ->
            let evidence = StrengthTurnEvidence.classifyParts parts

            let value =
                match evidence with
                | StrengthProviderOutputEvidence.RealOutput -> "RealOutput"
                | StrengthProviderOutputEvidence.TransportOnly -> "TransportOnly"
                | StrengthProviderOutputEvidence.NoOutput -> "NoOutput"

            box value

    let private turnOutcomeResult (value: obj) : Result<ReconcileProgram.TurnOutcome, string> =
        match textOf value with
        | "completed" -> Ok ReconcileProgram.TurnCompleted
        | "needs-continuation" -> Ok(ReconcileProgram.TurnNeedsContinuation "needs-continuation")
        | "aborted" -> Ok(ReconcileProgram.TurnAborted "aborted")
        | "failed" -> Ok(ReconcileProgram.TurnFailed "failed")
        | unknown -> Error(sprintf "unknown turn outcome: %s" unknown)

    let private reconciledTurnOf (value: obj) : Result<ReconciledTurn, string> =
        let parts =
            arrayOf value?parts
            |> Array.toList
            |> List.fold
                (fun state item ->
                    match state, partResult item with
                    | Ok current, Ok part -> Ok(part :: current)
                    | Error error, _ -> Error error
                    | _, Error error -> Error error)
                (Ok [])
            |> Result.map (List.rev >> List.toArray)

        match parts, turnOutcomeResult value?outcome with
        | Ok parts, Ok outcome ->
            Ok
                { SessionId = SessionId.create (textOf value?sessionId)
                  PhysicalUserMessageId = PhysicalUserMessageId.create (textOf value?physicalUserMessageId)
                  AuthorityRootUserMessageId =
                    AuthorityRootUserMessageId.create (textOf value?authorityRootUserMessageId)
                  ProviderRun = ProviderRunIdentity.create (textOf value?providerRun)
                  Role = None
                  Directory = None
                  Parts = parts
                  Finish = None
                  ErrorName = None
                  Model = None
                  Outcome = outcome
                  Observation = None }
        | Error error, _
        | _, Error error -> Error error

    let lifecycleReconcileEvent (projection: obj) (turn: obj) : obj =
        match reconciledTurnOf turn with
        | Error error -> box {| ok = false; error = error |}
        | Ok turn ->
            match StrengthLifecycle.reconcileEvent (projectionOf projection) turn with
            | Some event -> eventView (EventHandle event :> obj)
            | None -> null

    let lifecycleReconcileHandle (projection: obj) (turn: obj) : obj =
        match reconciledTurnOf turn with
        | Error error -> box {| ok = false; error = error |}
        | Ok turn ->
            match StrengthLifecycle.reconcileEvent (projectionOf projection) turn with
            | Some event ->
                box
                    {| event = (EventHandle event :> obj)
                       view = eventView (EventHandle event :> obj) |}
            | None -> box {| event = null; view = null |}

    let lifecycleReconcileCompletedRequest (owner: string) (projection: obj) (message: obj) : obj =
        SessionSnapshotPort.projectMessage message
        |> Option.bind (StrengthLifecycle.reconcileCompletedRequest (SessionId.create owner) (projectionOf projection))
        |> Option.map (fun event -> eventView (EventHandle event :> obj))
        |> Option.defaultValue null

    let private planToJs (plan: StrengthReplayPlan) : obj =
        box
            {| prepared = preparedToJs plan.Prepared
               bundle = bundleToJs plan.Bundle
               beforeMessageIndex = plan.BeforeMessageIndex
               existingTraceRange =
                plan.ExistingTraceRange
                |> Option.map (fun range ->
                    box
                        {| startInclusive = range.StartInclusive
                           endExclusive = range.EndExclusive |})
                |> optionToObj |}

    let private planOf (value: obj) : StrengthReplayPlan =
        let prepared =
            { OwnerSessionId = SessionId.create (textOf value?prepared?ownerSessionId)
              DecisionId = StrengthDecisionId.create (textOf value?prepared?decisionId)
              TargetProviderRun = ProviderRunIdentity.create (textOf value?prepared?targetProviderRun)
              ReplicaSessionId = SessionId.create (textOf value?prepared?replicaSessionId)
              AnchorDigest = textOf value?prepared?anchorDigest
              FrameDigest = textOf value?prepared?frameDigest
              ByteLength = int value?prepared?byteLength
              MaterialPayloads =
                arrayOf value?prepared?materialPayloads
                |> Array.toList
                |> List.map (string >> PayloadRef.create) }

        let traceRange =
            if isNullish value?existingTraceRange then
                None
            else
                Some
                    { StartInclusive = int64 (int (textOf value?existingTraceRange?startInclusive))
                      EndExclusive = int64 (int (textOf value?existingTraceRange?endExclusive)) }

        { Prepared = prepared
          Bundle = bundleOf value?bundle
          BeforeMessageIndex = int (textOf value?beforeMessageIndex)
          ExistingTraceRange = traceRange }

    let lifecycleReplayPlans (owner: string) (messages: obj array) (bundle: obj) (projection: obj) : Task<obj> =
        let messageIds =
            messages |> Array.toList |> List.map (fun value -> optionalText value?id)

        let load _ = Task.FromResult(Ok(bundleOf bundle))

        task {
            let! result =
                StrengthLifecycle.replayPlans (SessionId.create owner) id messageIds load (projectionOf projection)

            return
                match result with
                | Ok plans ->
                    box
                        {| ok = true
                           value = plans |> List.map planToJs |> List.toArray |}
                | Error error -> box {| ok = false; error = error |}
        }

    let lifecycleReplayPlansObserved
        (owner: string)
        (messages: obj array)
        (loadResponses: obj array)
        (projection: obj)
        : Task<obj> =
        let messageIds =
            messages |> Array.toList |> List.map (fun value -> optionalText value?id)

        // DSL-MUTABLE: algorithm-scratch — records observable load-port invocations
        let loadedDecisionIds = ResizeArray<string>()

        let load (prepared: StrengthCandidatePrepared) =
            let decisionId = StrengthDecisionId.value prepared.DecisionId
            loadedDecisionIds.Add(decisionId)

            match
                loadResponses
                |> Array.tryFind (fun response -> textOf response?decisionId = decisionId)
            with
            | None -> Task.FromResult(Error(sprintf "Strength bundle load unavailable: decision=%s" decisionId))
            | Some response when not (isNullish response?error) -> Task.FromResult(Error(textOf response?error))
            | Some response when isNullish response?bundle ->
                Task.FromResult(Error(sprintf "Strength bundle load unavailable: decision=%s" decisionId))
            | Some response -> Task.FromResult(Ok(bundleOf response?bundle))

        task {
            let! result =
                StrengthLifecycle.replayPlans (SessionId.create owner) id messageIds load (projectionOf projection)

            let loads = loadedDecisionIds.ToArray()

            return
                match result with
                | Ok plans ->
                    box
                        {| ok = true
                           value = plans |> List.map planToJs |> List.toArray
                           loadedDecisionIds = loads |}
                | Error error ->
                    box
                        {| ok = false
                           error = error
                           loadedDecisionIds = loads |}
        }

    let lifecycleNeedsRawReplay (coveredThrough: obj) (plan: obj) : bool =
        let covered =
            if isNullish coveredThrough then
                None
            else
                Some(int64 (int (textOf coveredThrough)))

        StrengthLifecycle.needsRawReplay covered (planOf plan)

    let lifecycleReplayIntents (sha256: string -> string) (plans: obj array) (ownerRole: string) : obj =
        let display =
            match Roles.tryParseRole ownerRole with
            | Some role ->
                fun toolName ->
                    if toolName = "js-predictor" then
                        "js-" + Roles.roleLabel role
                    else
                        toolName
            | None -> id

        match
            plans
            |> Array.toList
            |> List.map planOf
            |> StrengthLifecycle.replayIntents sha256 display
        with
        | Ok intents ->
            box
                {| ok = true
                   value = intents |> List.map ProjectionSurface.intentToSurfaceValue |> List.toArray
                   error = null |}
        | Error error ->
            box
                {| ok = false
                   value = null
                   error = projectionIntentErrorName error |}

    type private ScopeHandle(scope: PluginStrengthScope) =
        member _.Value = scope

    let scopeCreate () : obj =
        ScopeHandle(PluginStrengthScope(None)) :> obj

    let scopeAcquireShared (key: string) : obj =
        ScopeHandle(SharedPredictorScope.acquire key) :> obj

    let private scopeOf value =
        unbox<ScopeHandle> value |> fun handle -> handle.Value

    let scopeReleaseShared (scope: obj) : unit =
        SharedPredictorScope.release (scopeOf scope)

    let scopeFuseReason (scope: obj) =
        match (scopeOf scope).StrengthFuseReason with
        | Some reason -> reason
        | None -> null

    let scopeTripFuse (scope: obj) (reason: string) = (scopeOf scope).TripStrengthFuse reason
    let scopeClearSession (scope: obj) (session: string) = (scopeOf scope).ClearSession session
    let scopeDispose (scope: obj) = (scopeOf scope).Dispose()

    let private bindingOf (value: obj) : Result<StrengthReplicaBinding, string> =
        match roleResult value?canonicalRole, roundsResult value?requestedRounds with
        | Ok role, Ok requestedRounds ->
            let requestKind = ProviderRequestKind.StrengthReplica

            Ok
                { OwnerSessionId = SessionId.create (textOf value?ownerSessionId)
                  ReplicaSessionId = SessionId.create (textOf value?replicaSessionId)
                  DecisionId = StrengthDecisionId.create (textOf value?decisionId)
                  TargetProviderRun = ProviderRunIdentity.create (textOf value?targetProviderRun)
                  CanonicalRole = role
                  RequestedRounds = requestedRounds
                  SemanticDigest = textOf value?semanticDigest
                  LocalizedMirrorMessages = messagesOf value?localizedMirrorMessages
                  SynchronizedTextMessages =
                    arrayOf value?synchronizedTextMessages |> Array.map unbox<int> |> Set.ofArray
                  ToolCapabilitySet = PromptAuthority.toolCapabilitiesFor role requestKind }
        | Error error, _
        | _, Error error -> Error error

    let runtimeCreate () : obj = RuntimeHandle(StrengthRuntime()) :> obj

    let private runtimeOf value =
        unbox<RuntimeHandle> value |> fun handle -> handle.Value

    let runtimeBinding
        (owner: string)
        (replica: string)
        (decision: string)
        (target: string)
        (role: string)
        (requestedRounds: int)
        (semanticDigest: string)
        (localizedMirrorMessages: obj array)
        : obj =
        box
            {| ownerSessionId = owner
               replicaSessionId = replica
               decisionId = decision
               targetProviderRun = target
               canonicalRole = role
               requestedRounds = requestedRounds
               semanticDigest = semanticDigest
               localizedMirrorMessages = localizedMirrorMessages |}

    let runtimeRegister (runtime: obj) (binding: obj) : obj =
        match bindingOf binding with
        | Error error -> box {| ok = false; error = error |}
        | Ok binding ->
            match (runtimeOf runtime).Register(binding) with
            | Ok() -> box {| ok = true |}
            | Error error ->
                let name =
                    match error with
                    | StrengthRuntimeRegisterError.OwnerAlreadyHasReplica _ -> "OwnerAlreadyHasReplica"
                    | StrengthRuntimeRegisterError.ReplicaAlreadyBound _ -> "ReplicaAlreadyBound"
                    | StrengthRuntimeRegisterError.RoleIneligible _ -> "RoleIneligible"
                    | StrengthRuntimeRegisterError.EmptyBudget -> "EmptyBudget"

                box {| ok = false; error = name |}

    let private bindingToJs (binding: StrengthReplicaBinding) =
        box
            {| ownerSessionId = SessionId.value binding.OwnerSessionId
               replicaSessionId = SessionId.value binding.ReplicaSessionId
               decisionId = StrengthDecisionId.value binding.DecisionId
               targetProviderRun = ProviderRunIdentity.value binding.TargetProviderRun
               canonicalRole = roleLabel binding.CanonicalRole
               requestedRounds = ReadonlyRoundBudget.value binding.RequestedRounds
               semanticDigest = binding.SemanticDigest
               localizedMirrorMessages = binding.LocalizedMirrorMessages |> List.map wireMessageToJs |> List.toArray |}

    let runtimeFindByReplica (runtime: obj) (replica: string) =
        match (runtimeOf runtime).TryFindByReplica(SessionId.create replica) with
        | Some binding -> bindingToJs binding
        | None -> null

    let runtimeRetire (runtime: obj) (replica: string) =
        match (runtimeOf runtime).Retire(SessionId.create replica) with
        | Some binding -> bindingToJs binding
        | None -> null

    let private emptySessionPort (aborted: ResizeArray<string>) : ISessionHostPort =
        { new ISessionHostPort with
            member _.SubscribeTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SubscribeFutureTerminal(_, _) =
                { new IDisposable with
                    member _.Dispose() = () }

            member _.SendPrompt(_, _, _) =
                Task.FromResult(Outcome.Retryable "unused")

            member _.AbortSession(sessionId) =
                aborted.Add(SessionId.value sessionId)
                Task.FromResult(Ok())

            member _.InterruptAttempt(_) = Task.FromResult(Ok())
            member _.IsManagedChild(_) = true
            member _.AbortChildren(_) = AsyncSupport.completedTask ()
            member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "unused")
            member _.TryGetParentSession(_) = Task.FromResult(Ok None)
            member _.CreateChildSession(_, _) = Task.FromResult(Error "unused")
            member _.ListChildren(_) = Task.FromResult(Ok [])
            member _.FamilyRootOf(sessionId) = sessionId }

    let private transformOutcomeToJs outcome output aborted =
        let abortedIds = aborted |> Seq.toArray

        match outcome with
        | StrengthReplicaTransformOutcome.NotReplica ->
            box
                {| kind = "NotReplica"
                   batches = [||]
                   output = output
                   aborted = abortedIds |}
        | StrengthReplicaTransformOutcome.Ready values ->
            box
                {| kind = "Ready"
                   batches = values |> List.map batchToJs |> List.toArray
                   output = output
                   aborted = abortedIds |}
        | StrengthReplicaTransformOutcome.Retired(reason, values) ->
            box
                {| kind = "Retired"
                   reason = reason
                   batches = values |> List.map batchToJs |> List.toArray
                   output = output
                   aborted = abortedIds |}

    /// Mirror one outbound request through the real transform program.
    /// DELEGATE-5.3: `outboundRequest` marks a real provider request boundary;
    /// the live registry then owns the admission verdict, and an unadmitted
    /// request retires before any physical send instead of being gated by the
    /// visible batch count. `false` re-mirrors an already-admitted request
    /// without consuming another round.
    let transformApply (sha256: string -> string) (runtime: obj) (output: obj) (outboundRequest: bool) : Task<obj> =
        // DSL-MUTABLE: algorithm-scratch — aborted id accumulator
        let aborted = ResizeArray<string>()

        task {
            let! outcome =
                StrengthReplicaTransform.apply
                    sha256
                    (runtimeOf runtime)
                    (emptySessionPort aborted)
                    output
                    outboundRequest

            let messages =
                if isNullish output?messages then
                    [||]
                else
                    unbox<obj array> output?messages

            return transformOutcomeToJs outcome (box messages) aborted
        }

    let private registerErrorName error =
        match error with
        | StrengthRuntimeRegisterError.OwnerAlreadyHasReplica _ -> "OwnerAlreadyHasReplica"
        | StrengthRuntimeRegisterError.ReplicaAlreadyBound _ -> "ReplicaAlreadyBound"
        | StrengthRuntimeRegisterError.RoleIneligible _ -> "RoleIneligible"
        | StrengthRuntimeRegisterError.EmptyBudget -> "EmptyBudget"

    /// Register a binding into the real scope live registry (orphan setup).
    let scopeRuntimeRegister (scope: obj) (binding: obj) : obj =
        match bindingOf binding with
        | Error error -> box {| ok = false; error = error |}
        | Ok binding ->
            match (scopeOf scope).StrengthRuntime.Register(binding) with
            | Ok() -> box {| ok = true |}
            | Error error ->
                box
                    {| ok = false
                       error = registerErrorName error |}

    /// Live-registry lookup on the real scope (orphan assertions).
    let scopeRuntimeFindByReplica (scope: obj) (replica: string) =
        match (scopeOf scope).StrengthRuntime.TryFindByReplica(SessionId.create replica) with
        | Some binding -> bindingToJs binding
        | None -> null

    /// Opaque handle over a real StrengthReplicaRuntime whose only stubbed
    /// ports are the Host physical ones (session abort/child records and the
    /// model lease tracker). Attach/turn/transform/delete/dispose paths are
    /// the production paths; Start* bootstrap is out of scope for the handle
    /// because it needs a journal-backed dispatcher.
    type private ReplicaHandle(runtime: StrengthReplicaRuntime, live: StrengthRuntime) =
        member _.Runtime = runtime
        member _.Live = live

    let private replicaOf value = unbox<ReplicaHandle> value

    let private preparationProfile owner role =
        let identity =
            ParticipantIdentity.resolveAtRoot role
            |> Result.defaultWith (fun error -> invalidArg "role" (sprintf "%A" error))

        PromptAuthority.createAuthorityExecutionProfile
            owner
            (LogicalRunId.create "preparation-logical-run")
            (AuthorityRootUserMessageId.create "preparation-authority-root")
            PromptAuthority.RootAuthorityKind.HumanRoot
            identity
        |> Result.defaultWith invalidOp

    let private preparationJournal owner profile : IPromptJournal =
        { new IPromptJournal with
            member _.RuntimeId = RuntimeId.create "preparation-probe"

            member _.ProjectionFor session =
                if session = owner then
                    { PromptAuthority.empty with
                        ActiveLogicalRun = Some profile }
                else
                    PromptAuthority.empty

            member _.Append _ _ _ =
                invalidOp "Preparation probe cannot replace a durable dispatch journal"

            member _.HandleForChild _ = None

            member _.ChatAcceptancePersistence() =
                invalidOp "Preparation probe cannot accept chat execution" }

    let private childInfoFromJs (value: obj) : OpenCodeChildInfo =
        { SessionId = SessionId.create (textOf value?sessionId)
          ParentSessionId = None
          Agent = optionalText value?agent
          Title = optionalText value?title }

    let private childListingFromJs (value: obj) =
        if unbox<bool> value?ok then
            Ok(arrayOf value?children |> Array.map childInfoFromJs |> Array.toList)
        else
            Error(textOf value?error)

    let private preparationSessionPort owner (events: IEventObservationPort) (ports: obj) : ISessionHostPort =
        // DSL-MUTABLE: resource — Host-proved children owned by this controlled transport
        let children = Collections.Generic.HashSet<SessionId>()

        let abort session =
            task {
                do! emitJsExpr (ports?abort, SessionId.value session) "$0($1)" |> unbox<Task>
                return Ok()
            }

        let subscribe future session callback =
            let listener observed outcome =
                if observed = session then
                    callback observed outcome

            if future then
                events.SubscribeFutureTerminalListener listener
            else
                events.SubscribeTerminalListener listener

        { new ISessionHostPort with
            member _.SubscribeTerminal(session, callback) = subscribe false session callback
            member _.SubscribeFutureTerminal(session, callback) = subscribe true session callback

            member _.CreateChildSession(parent, options) =
                task {
                    let descriptor =
                        box
                            {| agent = options.Agent |> Option.toObj
                               title = options.Title |> Option.toObj |}

                    let! created =
                        emitJsExpr (ports?create, SessionId.value parent, descriptor) "$0($1,$2)"
                        |> unbox<Task<string>>

                    let child = SessionId.create created
                    children.Add child |> ignore
                    return Ok child
                }

            member _.ListChildren parent =
                task {
                    let! listed = emitJsExpr (ports?list, SessionId.value parent) "$0($1)" |> unbox<Task<obj>>
                    return childListingFromJs listed
                }

            member _.AbortSession session = abort session
            member _.InterruptAttempt session = abort session

            member _.AbortChildren _ =
                task {
                    for child in children do
                        let! _ = abort child
                        ()
                }

            member _.IsManagedChild session = children.Contains session
            member _.FamilyRootOf _ = owner

            member _.TryGetParentSession session =
                if children.Contains session then
                    Task.FromResult(Ok(Some owner))
                else
                    Task.FromResult(Ok None)

            member _.CreateSiblingSession(_, _, _) =
                invalidOp "Preparation probe does not create work siblings"

            member _.SendPrompt(_, _, _) =
                invalidOp "Use the real Host canary to dispatch prepared prompts" }

    let private acquireModelFromJs callback session role =
        let value = emitJsExpr (callback, SessionId.value session, role) "$0($1,$2)"

        if isNullish value then
            None
        else
            Some
                { providerID = textOf value?providerID
                  modelID = textOf value?modelID
                  variant = optionalText value?variant }

    /// Construct the real preparation coordinator over controlled physical ports.
    /// The journal is a frozen owner authority; actual dispatch uses the real Host canary.
    let replicaPreparationCreate (owner: string) (role: string) (eventPort: obj) (ports: obj) : obj =
        let ownerId = SessionId.create owner
        let events = unbox<IEventObservationPort> eventPort
        let profile = preparationProfile ownerId role
        let sessions = preparationSessionPort ownerId events ports
        let dispatcher = PromptDispatcher.forPrompts (preparationJournal ownerId profile)
        let live = StrengthRuntime()

        let snapshot =
            if isNullish ports?getMessages then
                None
            else
                Some
                    { new ISessionSnapshotPort with
                        member _.GetMessages sessionId =
                            task {
                                try
                                    let! raw =
                                        emitJsExpr (ports?getMessages, SessionId.value sessionId) "$0($1)"
                                        |> unbox<Task<obj array>>

                                    return Ok(SessionSnapshotPort.projectMessages raw)
                                with ex ->
                                    return Error ex.Message
                            } }

        let acquire =
            if isNullish ports?acquireModel then
                None
            else
                Some(acquireModelFromJs ports?acquireModel)

        let runtime =
            new StrengthReplicaRuntime(
                sessions,
                dispatcher,
                live,
                (fun _ _ _ -> ()),
                ?tryAcquireModel = acquire,
                ?snapshotPort = snapshot
            )

        ReplicaHandle(runtime, live) :> obj

    let private preparedResultToJs result =
        match result with
        | Error error -> box {| ok = false; error = error |}
        | Ok(prepared: StrengthReplicaPreparation) ->
            box
                {| ok = true
                   value =
                    box
                        {| replicaSessionId = SessionId.value prepared.ReplicaSessionId
                           completion = prepared.Completion |} |}

    let private prepareBinding (handle: ReplicaHandle) (binding: StrengthReplicaBinding) =
        task {
            let! prepared =
                handle.Runtime.PrepareReplicaStart(
                    binding.OwnerSessionId,
                    binding.DecisionId,
                    binding.TargetProviderRun,
                    binding.RequestedRounds,
                    Roles.roleLabel binding.CanonicalRole,
                    binding.LocalizedMirrorMessages,
                    binding.SynchronizedTextMessages,
                    binding.SemanticDigest
                )

            return preparedResultToJs prepared
        }

    let replicaPrepare (handle: obj) (request: obj) : Task<obj> =
        match bindingOf request with
        | Error error -> Task.FromResult(box {| ok = false; error = error |})
        | Ok binding -> prepareBinding (replicaOf handle) binding

    let replicaSendPrepared (handle: obj) (replica: string) : Task<obj> =
        task {
            let! sent = (replicaOf handle).Runtime.SendPreparedPrompt(SessionId.create replica)

            return
                match sent with
                | Ok() -> box {| ok = true |}
                | Error error -> box {| ok = false; error = error |}
        }

    let replicaDecisionOutcome (handle: obj) (replica: string) (decision: string) : obj =
        (replicaOf handle)
            .Runtime.TryDecisionOutcome(SessionId.create replica, StrengthDecisionId.create decision)
        |> Option.map box
        |> Option.toObj

    let replicaReleaseDecisionOutcome (handle: obj) (decision: string) =
        (replicaOf handle)
            .Runtime.ReleaseDecisionOutcome(StrengthDecisionId.create decision)

    let replicaRuntimeCreate () : obj =
        let sessions =
            { new ISessionHostPort with
                member _.SubscribeTerminal(_, _) =
                    { new IDisposable with
                        member _.Dispose() = () }

                member _.SubscribeFutureTerminal(_, _) =
                    { new IDisposable with
                        member _.Dispose() = () }

                member _.SendPrompt(_, _, _) =
                    Task.FromResult(Outcome.Retryable "unused")

                member _.AbortSession(_) = Task.FromResult(Ok())

                member _.InterruptAttempt(_) = Task.FromResult(Ok())
                member _.IsManagedChild(_) = true
                member _.AbortChildren(_) = AsyncSupport.completedTask ()
                member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "unused")
                member _.TryGetParentSession(_) = Task.FromResult(Ok None)
                member _.CreateChildSession(_, _) = Task.FromResult(Error "unused")
                member _.ListChildren(_) = Task.FromResult(Ok [])
                member _.FamilyRootOf(sessionId) = sessionId }

        // Never invoked on attach/turn/transform/delete/dispose paths; the
        // handle exists so tests drive those real paths with only Host
        // physical ports stubbed.
        let dispatcher: Wanxiangshu.Interaction.Dispatch.PromptDispatcher.Runtime =
            Unchecked.defaultof<_>

        let live = StrengthRuntime()

        let runtime =
            new StrengthReplicaRuntime(
                sessions,
                dispatcher,
                live,
                (fun _ _ _ -> ()),
                ?releaseModel = Some(fun sessionId -> ())
            )

        ReplicaHandle(runtime, live) :> obj

    /// Attach an already-live binding to the real coordinator. Returns the
    /// immutable-outcome completion task (JS-awaitable) on success.
    let replicaAttach (handle: obj) (binding: obj) : obj =
        let h = replicaOf handle

        match bindingOf binding with
        | Error error -> box {| ok = false; error = error |}
        | Ok binding ->
            match h.Runtime.AttachLiveDecision binding with
            | Ok completion ->
                box
                    {| ok = true
                       value = box {| completion = completion |} |}
            | Error error -> box {| ok = false; error = error |}

    /// Register a binding directly into the handle live registry (orphan
    /// setup: binding present, no local decision state).
    let replicaLiveRegister (handle: obj) (binding: obj) : obj =
        match bindingOf binding with
        | Error error -> box {| ok = false; error = error |}
        | Ok binding ->
            match (replicaOf handle).Live.Register(binding) with
            | Ok() -> box {| ok = true |}
            | Error error ->
                box
                    {| ok = false
                       error = registerErrorName error |}

    /// Live-registry lookup on the handle (orphan assertions).
    let replicaLiveFind (handle: obj) (replica: string) =
        match (replicaOf handle).Live.TryFindByReplica(SessionId.create replica) with
        | Some binding -> bindingToJs binding
        | None -> null

    let private terminalToJs terminal =
        match terminal with
        | StrengthReplicaTerminal.BudgetReached ->
            box
                {| kind = "BudgetReached"
                   reason = null |}
        | StrengthReplicaTerminal.TextCompleted ->
            box
                {| kind = "TextCompleted"
                   reason = null |}
        | StrengthReplicaTerminal.Cancelled -> box {| kind = "Cancelled"; reason = null |}
        | StrengthReplicaTerminal.Failed reason -> box {| kind = "Failed"; reason = reason |}
        | StrengthReplicaTerminal.InvalidFrame reason ->
            box
                {| kind = "InvalidFrame"
                   reason = reason |}

    let private outcomeToJs (outcome: StrengthReplicaOutcome) : obj =
        box
            {| replicaSessionId = SessionId.value outcome.ReplicaSessionId
               requestsAdmitted = outcome.RequestsAdmitted
               batches = outcome.Batches |> List.map batchToJs |> List.toArray
               terminal = terminalToJs outcome.Terminal |}

    /// Await an attach completion task and map the immutable outcome to JS.
    let replicaAwaitOutcome (completion: obj) : Task<obj> =
        task {
            let! outcome = unbox<Task<StrengthReplicaOutcome>> completion
            return outcomeToJs outcome
        }

    let private replicaTurnOf (value: obj) : Result<ReconciledTurn, string> =
        let parts =
            if isNullish value?parts then
                Ok [||]
            else
                arrayOf value?parts
                |> Array.toList
                |> List.fold
                    (fun state item ->
                        match state, partResult item with
                        | Ok current, Ok part -> Ok(part :: current)
                        | Error error, _ -> Error error
                        | _, Error error -> Error error)
                    (Ok [])
                |> Result.map (List.rev >> List.toArray)

        let textOr fallback v =
            if isNullish v then fallback else textOf v

        match parts, turnOutcomeResult value?outcome with
        | Ok parts, Ok outcome ->
            Ok
                { SessionId = SessionId.create (textOf value?sessionId)
                  PhysicalUserMessageId =
                    PhysicalUserMessageId.create (textOr "replica-test-physical" value?physicalUserMessageId)
                  AuthorityRootUserMessageId =
                    AuthorityRootUserMessageId.create (textOr "replica-test-root" value?authorityRootUserMessageId)
                  ProviderRun = ProviderRunIdentity.create (textOr "replica-test-run" value?providerRun)
                  Role = None
                  Directory = None
                  Parts = parts
                  Finish = optionalText value?finish
                  ErrorName = optionalText value?errorName
                  Model = None
                  Outcome = outcome
                  Observation = None }
        | Error error, _
        | _, Error error -> Error error

    /// Drive a turn through the real HandleTurn path. Returns handled bool.
    let replicaHandleTurn (handle: obj) (turn: obj) : obj =
        match replicaTurnOf turn with
        | Error error -> box {| ok = false; error = error |}
        | Ok turn -> box ((replicaOf handle).Runtime.HandleTurn turn)

    /// Drive transform output through the real HandleTransform path.
    /// Returns handled bool; admission counts are visible via replicaPeek.
    let replicaHandleTransform (handle: obj) (output: obj) : Task<obj> =
        task {
            let! handled = (replicaOf handle).Runtime.HandleTransform output
            return box handled
        }

    /// Drive the real HandleSessionDeleted path (live state or orphan).
    let replicaSessionDeleted (handle: obj) (session: string) =
        (replicaOf handle).Runtime.HandleSessionDeleted(SessionId.create session)

    /// Drive the real CancelOwner path.
    let replicaCancelOwner (handle: obj) (owner: string) : Task =
        task { do! (replicaOf handle).Runtime.CancelOwner(SessionId.create owner) }

    /// Read-only peek at live decision state; null once physically retired.
    let replicaPeek (handle: obj) (replica: string) : obj =
        match (replicaOf handle).Runtime.TryPeek(SessionId.create replica) with
        | None -> null
        | Some peek ->
            box
                {| requestsAdmitted = peek.RequestsAdmitted
                   batches = peek.Batches |> List.map batchToJs |> List.toArray
                   terminal = peek.SemanticTerminal |> Option.map terminalToJs |> optionToObj |}

    let replicaIsReplica (handle: obj) (session: string) : bool =
        (replicaOf handle).Runtime.IsReplica(SessionId.create session)

    let replicaDispose (handle: obj) = (replicaOf handle).Runtime.Dispose()

    /// DELEGATE-011: cumulative physical-tail cleanup ledger. `replicaReleased`
    /// is every replica whose lease was really released and whose child was
    /// terminated, whatever its terminal was; `replicaAborted` is the subset
    /// whose end was abnormal (failure, cancellation, deletion, dispose, or a
    /// binding that never became a decision). Both accumulate and are idempotent:
    /// the same physical tail observed twice yields the same array.
    let replicaReleased (handle: obj) : string array = (replicaOf handle).Runtime.Released()

    let replicaAborted (handle: obj) : string array = (replicaOf handle).Runtime.Aborted()

    /// WHAT[016]: the estimate protocol answered in JS-native values.
    /// `classifyTool` goes through the contract's own `policyCode` and
    /// `parseParticipatingArguments` through its `errorCode`, so neither the
    /// union nor the Result crosses the boundary; the self note keeps its
    /// original text and becomes null when the contract yields None.
    let protocolRevision: int = InvestigationEstimateContract.ProtocolRevision

    let classifyTool (toolName: string) : string =
        InvestigationEstimateContract.classifyTool toolName
        |> InvestigationEstimateContract.policyCode

    let parseParticipatingArguments (arguments: obj) : obj =
        match InvestigationEstimateContract.parseParticipatingArguments arguments with
        | Ok(rounds, selfNote) ->
            box
                {| ok = true
                   rounds = InvestigationEstimateContract.EstimatedReadonlyRounds.value rounds
                   selfNote = selfNote |> optionToObj |}
        | Error error ->
            box
                {| ok = false
                   error = InvestigationEstimateContract.errorCode error |}

    /// WHAT[016] §6: one stable error code answered as its natural-language
    /// explanation. The language comes from `ProviderLanguage.parse` and the
    /// classification from the contract's own `tryFromCode`, so the prose and
    /// the code vocabulary each keep exactly one owner; an unrecognized code is
    /// refused outright rather than answered with a default sentence.
    let investigationArgumentErrorText (lang: string) (errorCode: string) : string =
        let language = Wanxiangshu.Participant.Provider.ProviderLanguage.parse lang

        match InvestigationEstimateContract.tryFromCode errorCode with
        | Some error -> InvestigationEstimateContract.describeArgumentError language error
        | None -> failwithf "unknown investigation argument error code: %s" errorCode

    /// DELEGATE-5.3: the budget is the owner's own integer. Construction refuses
    /// a negative value instead of silently normalizing it to zero.
    let budgetTryCreate (value: int) : obj =
        match ReadonlyRoundBudget.tryCreate value with
        | Ok rounds ->
            box
                {| ok = true
                   value = ReadonlyRoundBudget.value rounds |}
        | Error error -> box {| ok = false; error = error |}

    /// Collapse one batch of the owner's integers to its maximum. Illegal input
    /// is refused, never normalized; a null value means the batch grants no
    /// authorization opportunity at all, which is different from zero rounds.
    let budgetMaxOf (values: int array) : obj =
        let parsed =
            values
            |> Array.toList
            |> List.fold
                (fun state item ->
                    match state, ReadonlyRoundBudget.tryCreate item with
                    | Ok current, Ok rounds -> Ok(rounds :: current)
                    | Error error, _ -> Error error
                    | _, Error error -> Error error)
                (Ok [])
            |> Result.map List.rev

        match parsed with
        | Error error -> box {| ok = false; error = error |}
        | Ok [] -> box {| ok = true; value = null |}
        | Ok budgets ->
            match ReadonlyRoundBudget.maxOf budgets with
            | Some rounds ->
                box
                    {| ok = true
                       value = ReadonlyRoundBudget.value rounds |}
            | None -> box {| ok = true; value = null |}

    /// The lifecycle handle is the JS-visible authorization session: every
    /// legal transition advances this same handle, so an alias obtained earlier
    /// still observes the current state. The domain lifecycle stays a pure
    /// value; only this handle is mutable.
    type private LifecycleHandle(lifecycle: DelegationLifecycle) =
        // DSL-MUTABLE: resource — 句柄内部可变游标：随 Bound/Prepared/终态推进，承载 JS 可见会话状态而非工作流状态
        let mutable current = lifecycle

        member _.Value
            with get () = current
            and set value = current <- value

    let private lifecycleOf value =
        unbox<LifecycleHandle> value |> fun handle -> handle.Value

    let private closedFromResult (value: obj) : Result<DelegationClosedFrom, string> =
        match textOf value with
        | "Requested" -> Ok DelegationClosedFrom.Requested
        | "Bound" -> Ok DelegationClosedFrom.Bound
        | unknown -> Error(sprintf "unknown closed-from: %s" unknown)

    let private closedReasonResult (value: obj) : Result<DelegationClosedReason, string> =
        match textOf value with
        | "NoMaterial" -> Ok DelegationClosedReason.NoMaterial
        | "CannotContinue" -> Ok DelegationClosedReason.CannotContinue
        | "Cancelled" -> Ok DelegationClosedReason.Cancelled
        | "Superseded" -> Ok DelegationClosedReason.Superseded
        | "RecoveryAbandoned" -> Ok DelegationClosedReason.RecoveryAbandoned
        | unknown -> Error(sprintf "unknown closed reason: %s" unknown)

    let private importedInt64Option (value: obj) : int64 option =
        if isNullish value then
            None
        else
            // The canonical payload writes int64 as text; JS bigint and number
            // shapes reach here too, and BigInt accepts all three.
            Some(unbox<int64> (emitJsExpr value "BigInt($0)"))

    /// DELEGATE-015: the import outcome decodes from the canonical payload
    /// view; kind selects the closed union and each kind carries its own
    /// evidence fields.
    let private importOutcomeResult (value: obj) : Result<DelegationImportOutcome, string> =
        match textOf value?kind with
        | "adopted" ->
            Ok(
                DelegationImportOutcome.Adopted
                    { TargetProviderRun = ProviderRunIdentity.create (textOf value?target_provider_run)
                      FrameDigest = textOf value?frame_digest
                      ByteLength = unbox<int> value?byte_length
                      MaterialPayloads =
                        arrayOf value?payload_refs
                        |> Array.map (fun reference -> PayloadRef.create (textOf reference))
                        |> Array.toList
                      TracedStartInclusive = importedInt64Option value?traced_start_inclusive
                      TracedEndExclusive = importedInt64Option value?traced_end_exclusive }
            )
        | "relinquished" ->
            Ok(
                DelegationImportOutcome.Relinquished
                    { TargetProviderRun =
                        if isNullish value?target_provider_run then
                            None
                        else
                            Some(ProviderRunIdentity.create (textOf value?target_provider_run))
                      Reason = textOf value?reason }
            )
        | unknown -> Error(sprintf "unknown import outcome kind: %s" unknown)

    let private delegationBindingOf (value: obj) : DelegationBinding =
        { DecisionId = StrengthDecisionId.create (textOf value?decisionId)
          TargetProviderRun = ProviderRunIdentity.create (textOf value?targetProviderRun)
          ReplicaSessionId = SessionId.create (textOf value?replicaSessionId)
          AnchorDigest = textOf value?anchorDigest }

    let private delegationClosedOf (value: obj) : Result<DelegationClosed, string> =
        match closedFromResult value?from, closedReasonResult value?reason with
        | Ok closedFrom, Ok reason ->
            Ok
                { DecisionId = StrengthDecisionId.create (textOf value?decisionId)
                  From = closedFrom
                  Reason = reason }
        | Error error, _
        | _, Error error -> Error error

    /// A successful transition advances the handle in place and hands the
    /// same handle back, so a caller that kept the original reference observes
    /// the advanced lifecycle; a refused transition leaves the handle untouched.
    let private transitionResultToJs
        (handle: LifecycleHandle)
        (result: Result<DelegationLifecycle, DelegationTransitionError>)
        =
        match result with
        | Ok lifecycle ->
            handle.Value <- lifecycle
            box {| ok = true; value = (handle :> obj) |}
        | Error error ->
            let name =
                match error with
                | DelegationTransitionError.IllegalFrom _ -> "IllegalFrom"
                | DelegationTransitionError.Conflict _ -> "Conflict"

            box {| ok = false; error = name |}

    let private delegationRequestOf (value: obj) : Result<DelegationRequest, string> =
        match requestedRoundsOptionResult value?requestedRounds with
        | Error error -> Error error
        | Ok None -> Error "missing requested rounds"
        | Ok(Some requestedRounds) ->
            match ownerLogicalRunOf value?ownerLogicalRun with
            | Error error -> Error error
            | Ok ownerLogicalRun ->
                Ok
                    { DecisionId = StrengthDecisionId.create (textOf value?decisionId)
                      OwnerSessionId = SessionId.create (textOf value?ownerSessionId)
                      OwnerLogicalRun = ownerLogicalRun
                      SourcePhysicalUserMessageId =
                        PhysicalUserMessageId.create (textOf value?sourcePhysicalUserMessageId)
                      SourceProviderRun = ProviderRunIdentity.create (textOf value?sourceProviderRun)
                      SourceToolCallIds =
                        arrayOf value?sourceToolCallIds
                        |> Array.toList
                        |> List.map (ToolCallId.create << textOf)
                      RequestedRounds = requestedRounds
                      ContractRevision = DelegationContractRevisions.create (int value?contractRevision) }

    /// DELEGATE-6.1: derive the DecisionId deterministically from the contract
    /// version, the owner logical run and the source provider run. A retry that
    /// changes target cannot mint a second budget.
    let delegationDeriveDecisionId
        (sha256: string -> string)
        (contractRevision: int)
        (logicalRunId: string)
        (authorityRootUserMessageId: string)
        (sourceProviderRun: string)
        : string =
        Delegation.deriveDecisionId
            sha256
            (DelegationContractRevisions.create contractRevision)
            { LogicalRunId = LogicalRunId.create logicalRunId
              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRootUserMessageId }
            (ProviderRunIdentity.create sourceProviderRun)
        |> StrengthDecisionId.value

    /// Fold one frozen request into the lifecycle: the entry point of every
    /// legal transition sequence.
    let delegationRequest (value: obj) : obj =
        match delegationRequestOf value with
        | Error error -> box {| ok = false; error = error |}
        | Ok request -> LifecycleHandle(Delegation.request request) :> obj

    let delegationBind (lifecycle: obj) (binding: obj) : obj =
        let handle = unbox<LifecycleHandle> lifecycle
        let declared = delegationBindingOf binding
        // The child carries no decision of its own: the binding's identity
        // comes from the authorization this handle currently holds.
        let completed =
            { declared with
                DecisionId = Delegation.decisionId handle.Value }

        transitionResultToJs handle (Delegation.tryBind handle.Value completed)

    let delegationPrepare (lifecycle: obj) : obj =
        let handle = unbox<LifecycleHandle> lifecycle
        transitionResultToJs handle (Delegation.tryPrepare handle.Value)

    let delegationPromote (lifecycle: obj) : obj =
        let handle = unbox<LifecycleHandle> lifecycle
        transitionResultToJs handle (Delegation.tryPromote handle.Value)

    let delegationTrace (lifecycle: obj) : obj =
        let handle = unbox<LifecycleHandle> lifecycle
        transitionResultToJs handle (Delegation.tryTrace handle.Value)

    let delegationClose (lifecycle: obj) (closed: obj) : obj =
        match delegationClosedOf closed with
        | Error error -> box {| ok = false; error = error |}
        | Ok declared ->
            let handle = unbox<LifecycleHandle> lifecycle

            let completed =
                { declared with
                    DecisionId = Delegation.decisionId handle.Value }

            transitionResultToJs handle (Delegation.tryClose handle.Value completed)

    let delegationAbandon (lifecycle: obj) : obj =
        let handle = unbox<LifecycleHandle> lifecycle
        transitionResultToJs handle (Delegation.tryAbandon handle.Value)

    let delegationDecisionId (lifecycle: obj) : string =
        Delegation.decisionId (lifecycleOf lifecycle) |> StrengthDecisionId.value

    let eventRequested (value: obj) : obj =
        match delegationRequestOf value with
        | Error error -> box {| ok = false; error = error |}
        | Ok request ->
            EventHandle(
                StrengthEvents.requested
                    request.DecisionId
                    request.OwnerSessionId
                    request.OwnerLogicalRun
                    request.SourcePhysicalUserMessageId
                    request.SourceProviderRun
                    request.SourceToolCallIds
                    request.RequestedRounds
                    request.ContractRevision
            )
            :> obj

    /// DELEGATE-6.2: Bound fixes target, replica session and anchor digest
    /// before the first outbound request.
    let eventBound (decision: string) (target: string) (replica: string) (anchorDigest: string) : obj =
        EventHandle(
            StrengthEvents.bound
                (StrengthDecisionId.create decision)
                (ProviderRunIdentity.create target)
                (SessionId.create replica)
                anchorDigest
        )
        :> obj

    /// DELEGATE-6.3: closing names the legal predecessor it closes from.
    let eventClosed (decision: string) (closedFrom: string) (reason: string) : obj =
        match closedFromResult (box closedFrom), closedReasonResult (box reason) with
        | Ok from, Ok reason ->
            EventHandle(StrengthEvents.closed (StrengthDecisionId.create decision) from reason) :> obj
        | Error error, _
        | _, Error error -> box {| ok = false; error = error |}

    /// DELEGATE-015: imported history is evidence, never an admission. The
    /// constructor accepts the canonical payload view (snake_case fields inside
    /// the outcome object), validates it into the closed import union, and
    /// returns an event handle the projection fold can consume.
    let eventHistoryImported
        (decision: string)
        (sourceStreamId: string)
        (sourceEventId: string)
        (importId: string)
        (oldBudgetEvidence: string)
        (outcome: obj)
        : obj =
        match importOutcomeResult outcome with
        | Error error -> box {| ok = false; error = error |}
        | Ok importedOutcome ->
            EventHandle(
                StrengthEvents.historyImported
                    (StrengthDecisionId.create decision)
                    sourceStreamId
                    sourceEventId
                    importId
                    (if isNullish oldBudgetEvidence then
                         None
                     else
                         Some oldBudgetEvidence)
                    importedOutcome
            )
            :> obj

    let TwinBijectionSurface_restore
        (child: obj array)
        (owner: obj array)
        (synchronizedTextMessages: obj array)
        : obj array =
        TwinBijectionSurface.restore child owner synchronizedTextMessages

    let TwinBijectionSurface_preservesOwnerOrder (child: obj array) (owner: obj array) : bool =
        TwinBijectionSurface.preservesOwnerOrder child owner

    let TwinBijectionSurface_introducesNothing (child: obj array) (owner: obj array) : bool =
        TwinBijectionSurface.introducesNothing child owner

    let TwinBijectionSurface_dropsNoSpeech (child: obj array) (owner: obj array) : bool =
        TwinBijectionSurface.dropsNoSpeech child owner

    let TwinBijectionSurface_extensionIsPrefix
        (childBefore: obj array)
        (ownerBefore: obj array)
        (childAfter: obj array)
        (ownerAfter: obj array)
        : bool =
        TwinBijectionSurface.extensionIsPrefix childBefore ownerBefore childAfter ownerAfter
