namespace Wanxiangshu.Strength.OpenCode

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Strength
open Wanxiangshu.Strength.Persistence
open Wanxiangshu.Strength.Projection
open Wanxiangshu.Strength.Replica

/// DELEGATE-7: Host boundary wiring for explicit read-only delegation.
///
/// Two phases share one owner transform call and pass evidence through local
/// values only — never a cross-callback cache. Capture freezes the authorization
/// from the real completed owner batch and persists DelegationRequested before
/// any compaction or message replacement can lose batch metadata. Start reads
/// the pending request back from canonical Current, freezes target and mirror,
/// and runs the replica through its prepared stages with DelegationBound
/// persisted in between. All policy math stays in Domain (Policy/Delegation);
/// this adapter only freezes Host evidence, invokes the decision-local Replica,
/// publishes Prepared, and applies the insertion intent after publication.
[<RequireQualifiedAccess>]
module StrengthDelegate =

    /// WHAT-014: stable code-level contract version of the delegation field set.
    /// It never changes with environment, configuration or rollout state.
    let private contractRevision =
        DelegationContractRevisions.create InvestigationEstimateContract.ProtocolRevision

    let private failClosed (strengthScope: PluginStrengthScope) (reason: string) : 'a =
        strengthScope.TripStrengthFuse reason
        raise (InvalidOperationException reason)

    let private isRootWorkEntry (entry: SessionAssociation) : bool =
        entry.ParentSessionId.IsNone
        && match SessionOwnershipClassification.classifyLegacy entry with
           | SessionExecutionClass.Work, Some SessionOwnership.Root -> true
           | _ -> false

    let private rootWork (sessionId: SessionId) (associations: Map<SessionId, SessionAssociation>) : bool =
        SessionAssociationProjection.tryFind sessionId associations
        |> Option.exists isRootWorkEntry

    let private renderCandidate
        (owner: SessionId)
        (target: ProviderRunIdentity)
        (decision: StrengthDecisionId)
        (bundle: StrengthFrameBundle)
        (output: obj)
        : Result<unit, string> =
        result {
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput output
            let wire = ProviderWireCapture.decodeMessageView rawMessages

            let! intent =
                StrengthProjectionIntent.candidate HostDigest.sha256Hex owner decision target target bundle
                |> Result.mapError (fun error -> sprintf "Strength Candidate intent refused: %A" error)

            let snapshot = { CurrentProjection = ProviderProjection.toSemantic wire }

            let rendered =
                ProjectionRenderer.renderMessagesWithHostIds snapshot wire.Messages [ intent ]

            let! projected =
                ProjectionMessageEdit.tryApplyRenderedInsertionsPreservingBase
                    (SessionId.value owner)
                    HostDigest.sha256Hex
                    rawMessages
                    rendered

            HostMessageProjection.replaceMessagesInPlace output projected
            return ()
        }

    let private wireAnchorDigest (rawMessages: obj list) =
        ProviderWireCapture.decodeMessageView rawMessages
        |> ProviderProjection.toSemantic
        |> ProviderProjection.renderSemantic
        |> HostDigest.sha256Hex

    type private BoundPorts =
        { Snapshots: ISessionSnapshotPort
          Durable: AgentJournal
          Runtime: StrengthReplicaRuntime
          Durability: StrengthDurabilityPort }

    type private OwnerSurface =
        { Owner: SessionId
          Target: ProviderRunIdentity
          Authority: PromptAuthority.AuthorityExecutionProfile
          Projections: ProjectionSet
          RawMessages: obj list
          Output: obj
          Ports: BoundPorts
          Wire: ProviderProjection.ProviderWireProjection
          AnchorDigest: string
          SourcePhysicalUserMessageId: PhysicalUserMessageId
          RequestKind: ProviderRequestKind
          HasPrefixProbe: bool
          IsRootWork: bool
          DurableProjection: StrengthProjection }

    let private hasPrefixProbeChoice (choice: Wanxiangshu.Context.Prefix.XProjectionChoice) : bool =
        match choice with
        | Wanxiangshu.Context.Prefix.XProjectionChoice.UsePrefixProbe _ -> true
        | Wanxiangshu.Context.Prefix.XProjectionChoice.UseCommittedEpoch -> false

    let private planEvidence
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (owner: SessionId)
        (target: ProviderRunIdentity)
        =
        match tryAttemptPlan owner target with
        | Some plan ->
            let hasPrefixProbe = hasPrefixProbeChoice plan.Profile.ProjectionChoice
            plan.Profile.RequestKind, hasPrefixProbe
        | None -> ProviderRequestKind.WorkMain, false

    let private tryBind
        (journal: AgentJournal option)
        (snapshotPort: ISessionSnapshotPort option)
        (strengthDurability: StrengthDurabilityPort option)
        (strengthScope: PluginStrengthScope)
        (output: obj)
        : Result<BoundPorts * SessionId, string> =
        let candidates =
            journal,
            snapshotPort,
            strengthScope.StrengthReplicaRuntime,
            strengthDurability,
            ProviderWireDecode.projectionSessionIdFromMessages output

        match candidates with
        | Some durable, Some snapshots, Some runtime, Some durability, Some sessionIdText ->
            Ok(
                { Snapshots = snapshots
                  Durable = durable
                  Runtime = runtime
                  Durability = durability },
                SessionId.create sessionIdText
            )
        | _ -> Error "Strength ports are not fully bound"

    let private tryResolvePhysicalUserMessage (rawMessages: obj list) : Result<PhysicalUserMessageId, string> =
        match ProviderWireCapture.lastUserMessageId rawMessages with
        | Some physical -> Ok physical
        | None -> Error "owner transform has no physical user message"

    let private tryResolveAssistantRun
        (physical: PhysicalUserMessageId)
        (messages: SessionMessage list)
        : Result<ProviderRunIdentity, string> =
        match ProviderRunBinding.bindableRun (PhysicalUserMessageId.value physical) messages with
        | Ok assistant -> Ok(ProviderRunIdentity.create assistant.Id)
        | Error _ -> Error "owner provider run is not uniquely bound"

    let private tryResolveAuthorityProfile
        (owner: SessionId)
        (projections: ProjectionSet)
        : Result<PromptAuthority.AuthorityExecutionProfile, string> =
        match PromptAuthorityProjectionQueries.activeProfile owner projections.AgentProjections with
        | Some authority -> Ok authority
        | None -> Error "owner has no active authority profile"

    let private resolveSurface
        (bound: BoundPorts * SessionId)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (durableStrength: StrengthProjection)
        (output: obj)
        : Task<Result<OwnerSurface, string>> =
        taskResult {
            let ports, owner = bound
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput output
            let! physical = tryResolvePhysicalUserMessage rawMessages
            let! messages = ports.Snapshots.GetMessages owner
            let! target = tryResolveAssistantRun physical messages
            let projections = AgentJournal.snapshot ports.Durable
            let! authority = tryResolveAuthorityProfile owner projections
            let requestKind, hasPrefixProbe = planEvidence tryAttemptPlan owner target

            let attached =
                syncDelegateRuntime
                |> Option.bind (fun sd -> sd.TryFindDelegateOwner owner)
                |> Option.isSome

            let wire = ProviderWireCapture.decodeMessageView rawMessages

            return
                { Owner = owner
                  Target = target
                  Authority = authority
                  Projections = projections
                  RawMessages = rawMessages
                  Output = output
                  Ports = ports
                  Wire = wire
                  AnchorDigest = wireAnchorDigest rawMessages
                  SourcePhysicalUserMessageId = physical
                  RequestKind = requestKind
                  HasPrefixProbe = hasPrefixProbe
                  IsRootWork =
                    authority.AuthorityKind = PromptAuthority.RootAuthorityKind.HumanRoot
                    && rootWork owner projections.AgentProjections.Associations
                    && not attached
                  DurableProjection = durableStrength }
        }

    // ---- source batch evidence -------------------------------------------------

    let private isToolPartCompleted (part: obj) : bool =
        let state = ProviderWireDecode.readField part "state"

        if isNull state then
            false
        else
            let status = if isNull state?status then "" else string state?status

            let hasInput =
                not (isNull state?input)
                || not (isNull part?args)
                || not (isNull part?arguments)

            let hasOutput =
                not (isNull state?output)
                || not (isNull state?result)
                || not (isNull state?content)

            status = "completed" && hasInput && hasOutput

    let private tryExtractToolCallArgs (state: obj) (part: obj) : string option =
        if not (isNull state) && not (isNull state?input) then
            Some(emitJsExpr state?input "JSON.stringify($0)")
        elif not (isNull part?args) then
            Some(emitJsExpr part?args "JSON.stringify($0)")
        elif not (isNull part?arguments) then
            Some(emitJsExpr part?arguments "JSON.stringify($0)")
        else
            None

    type private SourceToolCall =
        { CallId: ToolCallId
          ToolName: string
          CanonicalArguments: string }

    let private tryExtractToolCall (part: obj) : SourceToolCall option =
        let state = ProviderWireDecode.readField part "state"

        let callId =
            ProviderWireDecode.firstString part [ "callID"; "callId"; "id" ]
            |> Option.map ToolCallId.create

        let toolName =
            ProviderWireDecode.firstString part [ "tool"; "name"; "toolName"; "tool_name"; "toolID" ]
            |> Option.map (fun s -> s.Trim().ToLowerInvariant())

        let args = tryExtractToolCallArgs state part

        match callId, toolName, args with
        | Some cid, Some name, Some a when not (String.IsNullOrWhiteSpace name) ->
            Some
                { CallId = cid
                  ToolName = name
                  CanonicalArguments = a }
        | _ -> None

    let private validateExtractedToolCalls
        (expectedCount: int)
        (calls: SourceToolCall list)
        : SourceToolCall list option =
        let ids = calls |> List.map (fun call -> call.CallId)

        if calls.Length = expectedCount && Set.count (Set.ofList ids) = ids.Length then
            Some calls
        else
            None

    let private tryExtractUniqueCalls (toolParts: obj list) : SourceToolCall list option =
        if List.forall isToolPartCompleted toolParts then
            let calls = toolParts |> List.choose tryExtractToolCall
            validateExtractedToolCalls toolParts.Length calls
        else
            None

    let private tryExtractRawAssistantBatch (assistant: obj) : SourceToolCall list option =
        let parts = ProviderWireDecode.rawPartsOf assistant

        let toolParts =
            parts
            |> List.filter (fun part ->
                let kind =
                    ProviderWireDecode.firstString part [ "type" ]
                    |> Option.defaultValue ""
                    |> fun s -> s.ToLowerInvariant()

                kind = "tool" || kind = "tool-call" || kind = "tool_call")

        if List.isEmpty toolParts then
            None
        else
            tryExtractUniqueCalls toolParts

    let private extractWireToolCalls (parts: ProviderProjection.WirePart list) : SourceToolCall list =
        parts
        |> List.choose (function
            | ProviderProjection.WireToolCall(callId, name, arguments) ->
                let toolName = name.Trim().ToLowerInvariant()

                if String.IsNullOrWhiteSpace toolName then
                    None
                else
                    Some
                        { CallId = callId
                          ToolName = toolName
                          CanonicalArguments = arguments }
            | _ -> None)

    let private tryExtractWireAssistantCalls (message: ProviderProjection.WireMessage) : SourceToolCall list option =
        if not (String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) then
            None
        else
            let calls = extractWireToolCalls message.Parts
            if List.isEmpty calls then None else Some calls

    let private matchBatchWithTailCalls
        (wireMessages: ProviderProjection.WireMessage list)
        (tailCalls: SourceToolCall list)
        : SourceToolCall list option =
        match List.tryLast (StrengthBatchCollector.collectCompleteBatches wireMessages) with
        | Some batch ->
            let batchSignatures =
                batch.Exchanges
                |> List.map (fun exchange -> exchange.ToolName, exchange.CanonicalArguments)

            let callSignatures =
                tailCalls |> List.map (fun call -> call.ToolName, call.CanonicalArguments)

            if batchSignatures = callSignatures then
                Some tailCalls
            else
                None
        | None -> None

    let private tryExtractWireCompletedBatch
        (wire: ProviderProjection.ProviderWireProjection)
        : SourceToolCall list option =
        let assistantBatches = wire.Messages |> List.choose tryExtractWireAssistantCalls

        match assistantBatches with
        | [] -> None
        | batches -> matchBatchWithTailCalls wire.Messages (List.last batches)

    /// Resolves the completed source batch of the tail assistant message.
    /// Supports both:
    /// 1. Host session-shaped tool parts (single assistant message where all tool parts are completed with output)
    /// 2. Wire-level multi-message parts (assistant WireToolCall messages followed by WireToolResult messages)
    let private resolveCompletedSourceBatch
        (rawMessages: obj list)
        (wire: ProviderProjection.ProviderWireProjection)
        : SourceToolCall list option =
        let fromRaw =
            rawMessages
            |> List.choose (fun raw ->
                let info = ProviderWireDecode.infoObject raw

                let role =
                    ProviderWireDecode.firstString info [ "role" ]
                    |> Option.orElse (ProviderWireDecode.firstString raw [ "role" ])
                    |> Option.defaultValue ""

                if String.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) then
                    Some raw
                else
                    None)
            |> List.tryLast
            |> Option.bind tryExtractRawAssistantBatch

        match fromRaw with
        | Some calls -> Some calls
        | None -> tryExtractWireCompletedBatch wire

    // ---- call budget parsing ---------------------------------------------------

    let private tryParseCallArguments (callId: ToolCallId) (arguments: string) : Result<obj, string> =
        try
            Ok(emitJsExpr arguments "JSON.parse($0)")
        with _ ->
            Error(sprintf "delegation arguments of call %s are not valid JSON" (ToolCallId.value callId))

    [<RequireQualifiedAccess>]
    type private BatchAggregation =
        | NoEstimateOpportunity
        | EstimatedZero
        | PositiveEstimate of budget: ReadonlyRoundBudget
        | ArgumentError of reason: string

    let private aggregateBatchEstimate (language: ProviderLanguage) (calls: SourceToolCall list) : BatchAggregation =
        let estimateCalls =
            calls
            |> List.filter (fun call ->
                match InvestigationEstimateContract.classifyTool call.ToolName with
                | InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall -> true
                | _ -> false)

        match estimateCalls with
        | [] -> BatchAggregation.NoEstimateOpportunity
        | _ ->
            let parsedResults =
                estimateCalls
                |> List.map (fun call ->
                    match tryParseCallArguments call.CallId call.CanonicalArguments with
                    | Error err -> Error err
                    | Ok parsedObj ->
                        match InvestigationEstimateContract.parseParticipatingArguments parsedObj with
                        | Ok(rounds, _noteOpt) -> Ok rounds
                        | Error err ->
                            let explanation = InvestigationEstimateContract.formatArgumentError language err

                            Error(
                                sprintf
                                    "delegation arguments of call %s rejected: %s"
                                    (ToolCallId.value call.CallId)
                                    explanation
                            ))

            let firstError =
                parsedResults
                |> List.tryPick (function
                    | Error err -> Some err
                    | Ok _ -> None)

            match firstError with
            | Some reason -> BatchAggregation.ArgumentError reason
            | None ->
                let roundsList =
                    parsedResults
                    |> List.choose (function
                        | Ok rounds -> Some rounds
                        | Error _ -> None)

                let maxRounds =
                    roundsList
                    |> List.maxBy InvestigationEstimateContract.EstimatedReadonlyRounds.value

                let maxValue = InvestigationEstimateContract.EstimatedReadonlyRounds.value maxRounds

                if maxValue = 0 then
                    BatchAggregation.EstimatedZero
                else
                    let budget =
                        InvestigationEstimateContract.EstimatedReadonlyRounds.toExecutionBudget maxRounds

                    BatchAggregation.PositiveEstimate budget

    // ---- phase one: capture the authorization ----------------------------------

    type CaptureOutcome =
        | Captured of DelegationRequest
        | Skipped of reason: string

    let private checkCaptureEligibility (surface: OwnerSurface) : Result<unit, string> =
        if surface.RequestKind <> ProviderRequestKind.WorkMain then
            Error "not-work-main"
        elif surface.Ports.Runtime.IsReplica surface.Owner || not surface.IsRootWork then
            Error "not-root-owner-work"
        else
            Ok()

    let private tryResolveSourceCalls (surface: OwnerSurface) : Result<SourceToolCall list, string> =
        match resolveCompletedSourceBatch surface.RawMessages surface.Wire with
        | Some calls -> Ok calls
        | None -> Error "no-completed-source-batch"

    let private tryResolveCaptureCallsAndBudget
        (surface: OwnerSurface)
        : Result<SourceToolCall list * ReadonlyRoundBudget, string> =
        result {
            let! calls = tryResolveSourceCalls surface

            let language =
                ProviderLanguageBinding.forSessionText (SessionId.value surface.Owner)

            match aggregateBatchEstimate language calls with
            | BatchAggregation.PositiveEstimate budget -> return calls, budget
            | BatchAggregation.NoEstimateOpportunity -> return! Error "no-estimate-opportunity"
            | BatchAggregation.EstimatedZero -> return! Error "estimated-zero"
            | BatchAggregation.ArgumentError reason -> return! Error reason
        }

    let private buildDelegationRequest
        (surface: OwnerSurface)
        (calls: SourceToolCall list)
        (budget: ReadonlyRoundBudget)
        : DelegationRequest =
        let ownerLogicalRun =
            { LogicalRunId = surface.Authority.LogicalRunId
              AuthorityRootUserMessageId = surface.Authority.AuthorityRootUserMessageId }

        let decisionId =
            Delegation.deriveDecisionId HostDigest.sha256Hex contractRevision ownerLogicalRun surface.Target

        let sourceToolCallIds = calls |> List.map (fun call -> call.CallId)

        { DecisionId = decisionId
          OwnerSessionId = surface.Owner
          OwnerLogicalRun = ownerLogicalRun
          SourcePhysicalUserMessageId = surface.SourcePhysicalUserMessageId
          SourceProviderRun = surface.Target
          SourceToolCallIds = sourceToolCallIds
          RequestedRounds = budget
          ContractRevision = contractRevision }

    [<RequireQualifiedAccess>]
    type private CaptureDisposition =
        | Replay of DelegationRequest
        | Conflict
        | Fresh of DelegationRequest

    let private tryFindExistingBySourceQuadruple
        (durableProjection: StrengthProjection)
        (owner: SessionId)
        (logicalRun: OwnerLogicalRunIdentity)
        (physicalUserMessageId: PhysicalUserMessageId)
        (sourceProviderRun: ProviderRunIdentity)
        : StrengthDelegationView option =
        durableProjection.ByDecision
        |> Map.toList
        |> List.map snd
        |> List.tryFind (fun view ->
            view.Request.OwnerSessionId = owner
            && view.Request.OwnerLogicalRun = logicalRun
            && view.Request.SourcePhysicalUserMessageId = physicalUserMessageId
            && view.Request.SourceProviderRun = sourceProviderRun)

    let private evaluateCaptureDisposition
        (durableProjection: StrengthProjection)
        (request: DelegationRequest)
        : CaptureDisposition =
        match
            tryFindExistingBySourceQuadruple
                durableProjection
                request.OwnerSessionId
                request.OwnerLogicalRun
                request.SourcePhysicalUserMessageId
                request.SourceProviderRun
        with
        | Some existing when Delegation.sameRequest existing.Request request ->
            CaptureDisposition.Replay existing.Request
        | Some _ -> CaptureDisposition.Conflict
        | None ->
            match StrengthProjection.tryCandidate request.DecisionId durableProjection with
            | Some existing when Delegation.sameRequest existing.Request request ->
                CaptureDisposition.Replay existing.Request
            | Some _ -> CaptureDisposition.Conflict
            | None -> CaptureDisposition.Fresh request

    let private tryAdmitDelegationRequest
        (surface: OwnerSurface)
        (predictorConfigured: bool)
        (request: DelegationRequest)
        : Result<DelegationRequest, string> =
        let opportunity =
            { OwnerSessionId = surface.Owner
              OwnerLogicalRun = request.OwnerLogicalRun
              SourcePhysicalUserMessageId = surface.SourcePhysicalUserMessageId
              SourceProviderRun = surface.Target
              SourceToolCallIds = request.SourceToolCallIds
              RequestedRounds = Some request.RequestedRounds
              ContractRevision = contractRevision
              IsRootWork = surface.IsRootWork
              RequestKind = surface.RequestKind
              CanonicalRole = surface.Authority.CanonicalRole
              HasPrefixProbe = surface.HasPrefixProbe
              IsReplicaOrInternalLeaf = not surface.IsRootWork
              IsInteractionRepair = surface.RequestKind = ProviderRequestKind.InteractionRepair
              IsExplicitRecoveryBranch = false
              OwnerCancelled = false
              TargetProviderRunBound = true
              EventStoreHealthy = true
              HostBoundaryHealthy = true
              ProcessFuseHealthy = true
              OwnerLogicalRunSuperseded = false
              PendingRequested = true
              PredictorConfigured = predictorConfigured }

        StrengthPolicy.tryRequest HostDigest.sha256Hex opportunity

    let private handleCaptureAppendOutcome
        (strengthScope: PluginStrengthScope)
        (admitted: DelegationRequest)
        (appendResult: StrengthDurableAppend)
        : CaptureOutcome =
        match appendResult with
        | StrengthDurableAppend.Applied -> CaptureOutcome.Captured admitted
        | StrengthDurableAppend.SemanticRejected reason ->
            failClosed strengthScope ("Strength DelegationRequested rejected: " + reason)
        | StrengthDurableAppend.StorageInvalid reason ->
            failClosed strengthScope ("Strength DelegationRequested storage invalid: " + reason)
        | StrengthDurableAppend.StorageFailed reason ->
            failClosed strengthScope ("Strength DelegationRequested append failed: " + reason)

    let private persistNewDelegationRequest
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (predictorConfigured: bool)
        (request: DelegationRequest)
        : Task<CaptureOutcome> =
        task {
            match tryAdmitDelegationRequest surface predictorConfigured request with
            | Error reason -> return CaptureOutcome.Skipped reason
            | Ok admitted ->
                let! appendResult =
                    surface.Ports.Durability.Append(
                        StrengthEvents.requested
                            admitted.DecisionId
                            admitted.OwnerSessionId
                            admitted.OwnerLogicalRun
                            admitted.SourcePhysicalUserMessageId
                            admitted.SourceProviderRun
                            admitted.SourceToolCallIds
                            admitted.RequestedRounds
                            admitted.ContractRevision
                    )

                return handleCaptureAppendOutcome strengthScope admitted appendResult
        }

    let private executeCaptureRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        : Task<CaptureOutcome> =
        match evaluateCaptureDisposition surface.DurableProjection request with
        | CaptureDisposition.Replay existing -> Task.FromResult(CaptureOutcome.Captured existing)
        | CaptureDisposition.Conflict -> Task.FromResult(CaptureOutcome.Skipped "delegation-request-conflict")
        | CaptureDisposition.Fresh freshRequest ->
            persistNewDelegationRequest strengthScope surface predictorConfigured freshRequest

    let private planCaptureRequest (surface: OwnerSurface) : Result<DelegationRequest, string> =
        result {
            do! checkCaptureEligibility surface
            let! calls, budget = tryResolveCaptureCallsAndBudget surface
            return buildDelegationRequest surface calls budget
        }

    let private captureOnSurface
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        : Task<CaptureOutcome> =
        match planCaptureRequest surface with
        | Error reason -> Task.FromResult(CaptureOutcome.Skipped reason)
        | Ok request -> executeCaptureRequest strengthScope predictorConfigured surface request

    let private loadDurableProjectionOrThrow
        (ports: BoundPorts)
        (strengthScope: PluginStrengthScope)
        (phaseName: string)
        : Task<StrengthProjection> =
        task {
            match! ports.Durability.LoadProjection() with
            | Ok durableStrength -> return durableStrength
            | Error reason ->
                return
                    failClosed strengthScope (sprintf "Strength %s cannot prove EventStore health: %s" phaseName reason)
        }

    let private tryExecuteCaptureOnBound
        (bound: BoundPorts * SessionId)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (output: obj)
        : Task<CaptureOutcome> =
        task {
            let ports, _ = bound
            let! durableStrength = loadDurableProjectionOrThrow ports strengthScope "capture"

            match! resolveSurface bound strengthScope tryAttemptPlan syncDelegateRuntime durableStrength output with
            | Error reason -> return CaptureOutcome.Skipped reason
            | Ok resolved -> return! captureOnSurface strengthScope predictorConfigured resolved
        }

    let tryCapture
        (snapshotPort: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (strengthDurability: StrengthDurabilityPort option)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (output: obj)
        : Task<CaptureOutcome> =
        task {
            match tryBind journal snapshotPort strengthDurability strengthScope output with
            | Error reason -> return CaptureOutcome.Skipped reason
            | Ok _ when strengthScope.StrengthFuseReason |> Option.isSome ->
                return CaptureOutcome.Skipped "strength-fuse-tripped"
            | Ok bound ->
                return!
                    tryExecuteCaptureOnBound
                        bound
                        strengthScope
                        tryAttemptPlan
                        syncDelegateRuntime
                        predictorConfigured
                        output
        }

    // ---- phase two: start / consume --------------------------------------------

    let private appendClosed
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (decisionId: StrengthDecisionId)
        (closedFrom: DelegationClosedFrom)
        (closedReason: DelegationClosedReason)
        : Task<unit> =
        task {
            match! surface.Ports.Durability.Append(StrengthEvents.closed decisionId closedFrom closedReason) with
            | StrengthDurableAppend.Applied -> return ()
            | StrengthDurableAppend.SemanticRejected reason ->
                return failClosed strengthScope ("Strength DelegationClosed rejected: " + reason)
            | StrengthDurableAppend.StorageInvalid reason ->
                return failClosed strengthScope ("Strength DelegationClosed storage invalid: " + reason)
            | StrengthDurableAppend.StorageFailed reason ->
                return failClosed strengthScope ("Strength DelegationClosed append failed: " + reason)
        }

    let private renderCandidateOrThrow
        (strengthScope: PluginStrengthScope)
        (owner: SessionId)
        (target: ProviderRunIdentity)
        (decisionId: StrengthDecisionId)
        (bundle: StrengthFrameBundle)
        (output: obj)
        : unit =
        match renderCandidate owner target decisionId bundle output with
        | Ok() -> ()
        | Error error -> failClosed strengthScope ("Strength Candidate render failed closed: " + error)

    let private publishAndRender
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (decisionId: StrengthDecisionId)
        (bundle: StrengthFrameBundle)
        (replicaSessionId: SessionId)
        : Task<unit> =
        task {
            let! published =
                surface.Ports.Durability.PublishPrepared
                    { OwnerSessionId = surface.Owner
                      DecisionId = decisionId
                      TargetProviderRun = surface.Target
                      ReplicaSessionId = replicaSessionId
                      AnchorDigest = surface.AnchorDigest
                      Bundle = bundle }

            match published with
            | StrengthPreparedPublish.StorageInvalid error ->
                return failClosed strengthScope ("Strength Prepared storage invalid: " + error)
            | StrengthPreparedPublish.Rejected _ -> return ()
            | StrengthPreparedPublish.Published ->
                renderCandidateOrThrow strengthScope surface.Owner surface.Target decisionId bundle surface.Output
                return ()
        }

    let private verifyPreparedAnchor
        (strengthScope: PluginStrengthScope)
        (rawMessages: obj list)
        (expectedDigest: string)
        : unit =
        if wireAnchorDigest rawMessages <> expectedDigest then
            failClosed strengthScope "Strength Prepared recovery anchor digest changed before target consumption"

    let private consumePreparedBundle
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (prepared: StrengthCandidatePrepared)
        : Task<unit> =
        task {
            verifyPreparedAnchor strengthScope surface.RawMessages prepared.AnchorDigest

            match! surface.Ports.Durability.LoadFrameBundle prepared with
            | Error error -> return failClosed strengthScope ("Strength Prepared frame load failed: " + error)
            | Ok bundle ->
                renderCandidateOrThrow
                    strengthScope
                    surface.Owner
                    surface.Target
                    prepared.DecisionId
                    bundle
                    surface.Output
        }

    let private consumePreparedCandidate
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (preparedOpt: StrengthCandidatePrepared option)
        : Task<unit> =
        match preparedOpt with
        | Some prepared -> consumePreparedBundle strengthScope surface prepared
        | None -> Task.FromResult()

    let private consumeBoundCandidate
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (bindingOpt: DelegationBinding option)
        : Task<unit> =
        match bindingOpt with
        | Some binding when not (surface.Ports.Runtime.IsReplica binding.ReplicaSessionId) ->
            appendClosed
                strengthScope
                surface
                binding.DecisionId
                DelegationClosedFrom.Bound
                DelegationClosedReason.RecoveryAbandoned
        | _ -> Task.FromResult()

    /// DELEGATE-7/10: the target run already owns a durable decision. A Prepared
    /// candidate re-renders the exact same material without re-running the
    /// readonly tools; a Bound-but-empty execution whose local child is gone
    /// loses this investigation opportunity and closes; settled states wait.
    let private consumeBoundDecision
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (view: StrengthDelegationView)
        : Task<unit> =
        match view.State with
        | StrengthCandidateState.Prepared -> consumePreparedCandidate strengthScope surface view.Prepared
        | StrengthCandidateState.Bound -> consumeBoundCandidate strengthScope surface view.Binding
        | StrengthCandidateState.Promoted
        | StrengthCandidateState.Traced
        | StrengthCandidateState.Closed _
        | StrengthCandidateState.Abandoned
        | StrengthCandidateState.Requested -> Task.FromResult()

    let private tryBuildBundleOrThrow (strengthScope: PluginStrengthScope) batches : StrengthFrameBundle =
        match StrengthFrame.tryBuild HostDigest.sha256Hex batches with
        | Ok bundle -> bundle
        | Error error -> failClosed strengthScope (sprintf "Strength Replica bundle invalid: %A" error)

    let private handleReplicaCompletion
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (decisionId: StrengthDecisionId)
        (completed: StrengthReplicaOutcome)
        : Task<unit> =
        match completed.Terminal with
        | StrengthReplicaTerminal.InvalidFrame reason ->
            failClosed strengthScope ("Strength Replica invalid frame: " + reason)
        | _ when List.isEmpty completed.Batches ->
            appendClosed strengthScope surface decisionId DelegationClosedFrom.Bound DelegationClosedReason.NoMaterial
        | _ ->
            let bundle = tryBuildBundleOrThrow strengthScope completed.Batches
            publishAndRender strengthScope surface decisionId bundle completed.ReplicaSessionId

    let private executeBoundReplica
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (decisionId: StrengthDecisionId)
        (preparation: StrengthReplicaPreparation)
        : Task<unit> =
        task {
            match! surface.Ports.Runtime.SendPreparedPrompt preparation.ReplicaSessionId with
            | Error _ ->
                return!
                    appendClosed
                        strengthScope
                        surface
                        decisionId
                        DelegationClosedFrom.Bound
                        DelegationClosedReason.CannotContinue
            | Ok() ->
                let! completed = preparation.Completion
                return! handleReplicaCompletion strengthScope surface decisionId completed
        }

    let private appendBoundAndExecute
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        (preparation: StrengthReplicaPreparation)
        : Task<unit> =
        task {
            match!
                surface.Ports.Durability.Append(
                    StrengthEvents.bound
                        request.DecisionId
                        surface.Target
                        preparation.ReplicaSessionId
                        surface.AnchorDigest
                )
            with
            | StrengthDurableAppend.SemanticRejected reason ->
                do! surface.Ports.Runtime.CancelOwner surface.Owner
                return! failClosed strengthScope ("Strength DelegationBound rejected: " + reason)
            | StrengthDurableAppend.StorageInvalid reason ->
                do! surface.Ports.Runtime.CancelOwner surface.Owner
                return! failClosed strengthScope ("Strength DelegationBound storage invalid: " + reason)
            | StrengthDurableAppend.StorageFailed reason ->
                do! surface.Ports.Runtime.CancelOwner surface.Owner
                return! failClosed strengthScope ("Strength DelegationBound append failed: " + reason)
            | StrengthDurableAppend.Applied ->
                return! executeBoundReplica strengthScope surface request.DecisionId preparation
        }

    let private startWithMirror
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        (replicaAgent: string)
        (replicaMirror: ProviderProjection.WireMessage list)
        : Task<unit> =
        task {
            match!
                surface.Ports.Runtime.PrepareReplicaStart(
                    surface.Owner,
                    request.DecisionId,
                    surface.Target,
                    request.RequestedRounds,
                    replicaAgent,
                    replicaMirror,
                    surface.AnchorDigest
                )
            with
            | Error _ ->
                return!
                    appendClosed
                        strengthScope
                        surface
                        request.DecisionId
                        DelegationClosedFrom.Requested
                        DelegationClosedReason.CannotContinue
            | Ok preparation -> return! appendBoundAndExecute strengthScope surface request preparation
        }

    let private prepareAndStartReplica
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        : Task<unit> =
        let replicaAgent = Roles.roleLabel surface.Authority.CanonicalRole

        let mirrorResult =
            StrengthFrame.tryLocalizeMirror
                HostDigest.sha256Hex
                request.DecisionId
                surface.AnchorDigest
                surface.Wire.Messages

        match mirrorResult with
        | Error _ -> Task.FromResult()
        | Ok replicaMirror -> startWithMirror strengthScope surface request replicaAgent replicaMirror

    let private startRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        : Task<unit> =
        if
            request.OwnerLogicalRun.AuthorityRootUserMessageId
            <> surface.Authority.AuthorityRootUserMessageId
        then
            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.Superseded
        elif strengthScope.StrengthFuseReason |> Option.isSome then
            failClosed strengthScope "Strength fuse is tripped; delegation is closed for this process"
        elif request.ContractRevision <> contractRevision then
            // DELEGATE_REVISE §12.4: v1 Requested that was not Bound is explicitly closed
            // upon contract revision upgrade; owner continues normally without launching old protocol child.
            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        elif not predictorConfigured then
            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        elif not (Set.contains surface.Authority.CanonicalRole StrengthPolicy.eligibleRoles) then
            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        else
            prepareAndStartReplica strengthScope surface request

    /// DELEGATE-10: recovery reads the pending request from persisted facts. A
    /// new user input or authority replacement closes the old request; the
    /// request never rescans arbitrary history for a positive budget.
    let private startPendingRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        : Task<unit> =
        task {
            let pending =
                surface.DurableProjection.ByDecision
                |> Map.toList
                |> List.map snd
                |> List.filter (fun view ->
                    view.State = StrengthCandidateState.Requested
                    && view.Request.OwnerSessionId = surface.Owner
                    && view.Request.OwnerLogicalRun.LogicalRunId = surface.Authority.LogicalRunId)
                |> List.sortBy (fun view -> StrengthDecisionId.value view.Request.DecisionId)

            match pending with
            | [] -> return ()
            | view :: _ -> return! startRequest strengthScope predictorConfigured surface view.Request
        }

    [<RequireQualifiedAccess>]
    type private SurfaceApplication =
        | Skip
        | ConsumeBound of StrengthDelegationView
        | StartPending

    let private decideTargetAction (target: ProviderRunIdentity) (durable: StrengthProjection) : SurfaceApplication =
        match StrengthProjection.tryDecisionForTarget target durable with
        | None -> SurfaceApplication.StartPending
        | Some decisionId ->
            StrengthProjection.tryCandidate decisionId durable
            |> Option.map SurfaceApplication.ConsumeBound
            |> Option.defaultValue SurfaceApplication.Skip

    let private planSurfaceApplication (surface: OwnerSurface) : SurfaceApplication =
        if surface.Ports.Runtime.IsReplica surface.Owner || not surface.IsRootWork then
            SurfaceApplication.Skip
        else
            decideTargetAction surface.Target surface.DurableProjection

    let private applyOnSurface
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        : Task<unit> =
        match planSurfaceApplication surface with
        | SurfaceApplication.Skip -> Task.FromResult()
        | SurfaceApplication.ConsumeBound view -> consumeBoundDecision strengthScope surface view
        | SurfaceApplication.StartPending -> startPendingRequest strengthScope predictorConfigured surface

    let private executeApplyOnBound
        (bound: BoundPorts * SessionId)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (output: obj)
        : Task<unit> =
        task {
            let ports, _ = bound
            let! durableStrength = loadDurableProjectionOrThrow ports strengthScope "start"

            match! resolveSurface bound strengthScope tryAttemptPlan syncDelegateRuntime durableStrength output with
            | Error _ -> return ()
            | Ok surface -> return! applyOnSurface strengthScope predictorConfigured surface
        }

    let tryApply
        (snapshotPort: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (strengthDurability: StrengthDurabilityPort option)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (output: obj)
        : Task<unit> =
        match tryBind journal snapshotPort strengthDurability strengthScope output with
        | Error _ -> Task.FromResult()
        | Ok bound ->
            executeApplyOnBound bound strengthScope tryAttemptPlan syncDelegateRuntime predictorConfigured output
