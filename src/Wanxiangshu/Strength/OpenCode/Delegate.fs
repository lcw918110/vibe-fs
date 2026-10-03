namespace Wanxiangshu.Strength.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
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

    /// WHAT[002]: the source exclusion is "not the Replica and not another
    /// InternalLeaf" — a durable association that classifies as InternalLeaf
    /// (Blogger companions, satellites). A Work child delegated by its owner
    /// (DevOps, Engineer) is a legal delegation source; its authority kind
    /// (HumanRoot or AgentOwnerRoot) decides nothing here.
    let private isInternalLeafEntry (entry: SessionAssociation) : bool =
        match SessionOwnershipClassification.classifyLegacy entry with
        | SessionExecutionClass.InternalLeaf, _ -> true
        | _ -> false

    let private internalLeafOwner (sessionId: SessionId) (associations: Map<SessionId, SessionAssociation>) : bool =
        SessionAssociationProjection.tryFind sessionId associations
        |> Option.exists isInternalLeafEntry

    let private renderCandidate
        (owner: SessionId)
        (ownerRole: Role)
        (target: ProviderRunIdentity)
        (decision: StrengthDecisionId)
        (bundle: StrengthFrameBundle)
        (output: obj)
        : Result<unit, string> =
        result {
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput output
            let wire = ProviderWireCapture.decodeMessageView rawMessages

            let displayName tool =
                if tool = "js-predictor" then
                    "js-" + Roles.roleLabel ownerRole
                else
                    tool

            let! intent =
                StrengthProjectionIntent.candidate HostDigest.sha256Hex owner decision target target displayName bundle
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
          Durability: StrengthDurabilityPort
          Timer: ITimerPort option }

    type private OwnerSurface =
        { Owner: SessionId
          Target: ProviderRunIdentity
          Authority: PromptAuthority.AuthorityExecutionProfile
          Projections: ProjectionSet
          RawMessages: obj list
          HostMessages: SessionMessage list
          Output: obj
          Ports: BoundPorts
          Wire: ProviderProjection.ProviderWireProjection
          AnchorDigest: string
          SourcePhysicalUserMessageId: PhysicalUserMessageId
          RequestKind: ProviderRequestKind
          HasPrefixProbe: bool
          IsInternalLeaf: bool
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
        (projectionSessionIdOpt: string option)
        (timerPort: ITimerPort option)
        (output: obj)
        : Result<BoundPorts * SessionId, string> =
        let candidates =
            journal,
            snapshotPort,
            strengthScope.StrengthReplicaRuntime,
            strengthDurability,
            (projectionSessionIdOpt
             |> Option.orElseWith (fun () -> ProviderWireDecode.projectionSessionIdFromMessages output))

        match candidates with
        | Some durable, Some snapshots, Some runtime, Some durability, Some sessionIdText ->
            Ok(
                { Snapshots = snapshots
                  Durable = durable
                  Runtime = runtime
                  Durability = durability
                  Timer = timerPort },
                SessionId.create sessionIdText
            )
        | _ -> Error "Strength ports are not fully bound"

    let private tryResolvePhysicalUserMessage (rawMessages: obj list) : Result<PhysicalUserMessageId, string> =
        match ProviderWireCapture.lastUserMessageId rawMessages with
        | Some physical -> Ok physical
        | None -> Error "owner transform has no physical user message"

    let private observeBoundAssistant
        (userMessageId: string)
        (currentMessages: SessionMessage list)
        : SessionMessage option =
        match ProviderRunBinding.observeBindableRun userMessageId currentMessages with
        | ProviderRunBinding.Observation.Bound assistant -> Some assistant
        | _ -> None

    let private resolveBindableAssistant
        (physicalId: PhysicalUserMessageId)
        (currentMessages: SessionMessage list)
        : SessionMessage option =
        match observeBoundAssistant (PhysicalUserMessageId.value physicalId) currentMessages with
        | Some assistant -> Some assistant
        | None ->
            // Fallback: If physicalId is a content-hash while snapshot user messages have native host IDs,
            // match using the latest user message from the snapshot whose child is an uncompleted assistant.
            currentMessages
            |> List.filter (fun m -> m.Role = "user")
            |> List.tryLast
            |> Option.bind (fun userMsg -> observeBoundAssistant userMsg.Id currentMessages)

    let private awaitProjectionCatchup (timer: ITimerPort option) : Task<unit> =
        task {
            match timer with
            | None -> return ()
            | Some port ->
                let deadline = port.Delay ProviderRunBinding.projectionCatchupDelayMilliseconds
                do! deadline.Delay
        }

    let private rereadOwnerMessagesAfterCatchup
        (ports: BoundPorts)
        (owner: SessionId)
        (readsLeft: int)
        (loop: int -> SessionMessage list -> Task<Result<SessionMessage list * ProviderRunIdentity, string>>)
        : Task<Result<SessionMessage list * ProviderRunIdentity, string>> =
        task {
            do! awaitProjectionCatchup ports.Timer

            match! ports.Snapshots.GetMessages owner with
            | Ok reloaded -> return! loop (readsLeft - 1) reloaded
            | Error err -> return Error(sprintf "owner snapshot reread failed: %s" err)
        }

    let private tryResolveAssistantRun
        (ports: BoundPorts)
        (owner: SessionId)
        (physical: PhysicalUserMessageId)
        (initialMessages: SessionMessage list)
        : Task<Result<SessionMessage list * ProviderRunIdentity, string>> =
        task {
            let rec loop (readsLeft: int) (currentMessages: SessionMessage list) =
                task {
                    match resolveBindableAssistant physical currentMessages with
                    | Some assistant -> return Ok(currentMessages, ProviderRunIdentity.create assistant.Id)
                    | None when readsLeft > 0 -> return! rereadOwnerMessagesAfterCatchup ports owner readsLeft loop
                    | None ->
                        let roles =
                            currentMessages
                            |> List.map (fun m ->
                                sprintf "%s(id=%s,parent=%A,comp=%b)" m.Role m.Id m.ParentId m.IsCompaction)
                            |> String.concat "; "

                        return
                            Error(
                                sprintf
                                    "owner provider run is not uniquely bound (NoCandidate for %s, msgs: [%s])"
                                    (PhysicalUserMessageId.value physical)
                                    roles
                            )
                }

            return! loop ProviderRunBinding.projectionCatchupMaxReads initialMessages
        }

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
            let! initialMessages = ports.Snapshots.GetMessages owner
            let! messages, target = tryResolveAssistantRun ports owner physical initialMessages
            let projections = AgentJournal.snapshot ports.Durable
            let! authority = tryResolveAuthorityProfile owner projections
            let requestKind, hasPrefixProbe = planEvidence tryAttemptPlan owner target

            let wire = ProviderWireCapture.decodeMessageView rawMessages

            return
                { Owner = owner
                  Target = target
                  Authority = authority
                  Projections = projections
                  RawMessages = rawMessages
                  HostMessages = messages
                  Output = output
                  Ports = ports
                  Wire = wire
                  AnchorDigest = wireAnchorDigest rawMessages
                  SourcePhysicalUserMessageId = physical
                  RequestKind = requestKind
                  HasPrefixProbe = hasPrefixProbe
                  IsInternalLeaf = internalLeafOwner owner projections.AgentProjections.Associations
                  DurableProjection = durableStrength }
        }

    // ---- source batch evidence -------------------------------------------------

    let private isToolStatusComplete (status: string) : bool =
        match status.Trim().ToLowerInvariant() with
        | "pending" -> false
        | "completed"
        | "complete" -> true
        | _ -> false

    let private readToolPartStatus (state: obj) : string =
        if isNull state?status then "" else string state?status

    let private toolStatusAllowsCompletion (status: string) : bool =
        if String.IsNullOrWhiteSpace status then
            true
        else
            isToolStatusComplete status

    let private isToolPartCompleted (part: obj) : bool =
        let state = ProviderWireDecode.readField part "state"

        let hasInput =
            (not (isNull state) && not (isNull state?input))
            || not (isNull part?args)
            || not (isNull part?arguments)

        let hasOutput =
            (not (isNull state)
             && (not (isNull state?output)
                 || not (isNull state?result)
                 || not (isNull state?content)))
            || not (isNull part?output)
            || not (isNull part?result)
            || not (isNull part?content)

        if isNull state then
            hasInput && hasOutput
        else
            toolStatusAllowsCompletion (readToolPartStatus state) && hasInput && hasOutput

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

    let private isWireRequestBoundary (message: ProviderProjection.WireMessage) =
        String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        || String.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)

    let private wireResultParts (message: ProviderProjection.WireMessage) =
        message.Parts
        |> List.choose (function
            | ProviderProjection.WireToolResult(callId, result) -> Some(callId, result)
            | _ -> None)

    let private addWireResult
        (callIds: Set<string>)
        (state: Result<Map<string, string>, unit>)
        ((callId, result): ToolCallId * string)
        : Result<Map<string, string>, unit> =
        state
        |> Result.bind (fun current ->
            let key = ToolCallId.value callId

            if not (Set.contains key callIds) || Map.containsKey key current then
                Error()
            else
                Ok(Map.add key result current))

    let private collectNextWireResults
        (all: ProviderProjection.WireMessage array)
        (callIds: Set<string>)
        (index: int)
        (results: Map<string, string>)
        : Result<Map<string, string>, unit> =
        wireResultParts all.[index] |> List.fold (addWireResult callIds) (Ok results)

    let private collectWireResults
        (all: ProviderProjection.WireMessage array)
        (callIds: Set<string>)
        (startIndex: int)
        : Result<Map<string, string> * int, unit> =
        let rec loop index results =
            if index >= all.Length || isWireRequestBoundary all.[index] then
                Ok(results, index)
            else
                collectNextWireResults all callIds index results
                |> Result.bind (fun current -> loop (index + 1) current)

        loop startIndex Map.empty

    let private callIdSet (calls: SourceToolCall list) =
        calls |> List.map (fun call -> ToolCallId.value call.CallId) |> Set.ofList

    /// One scan step over the wire: a non-assistant message yields an empty
    /// batch and advances; an assistant message with no calls stops the scan;
    /// an assistant message with calls yields its complete batch and the next
    /// index. `Error` means the wire is not a completed batch, so scanning stops.
    let rec private wireBatchStep
        (all: ProviderProjection.WireMessage array)
        (index: int)
        : Result<SourceToolCall list * int, unit> =
        let message = all.[index]

        let isAssistant =
            String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)

        let calls =
            if isAssistant then
                extractWireToolCalls message.Parts
            else
                []

        match isAssistant, calls with
        | false, _ -> Ok([], index + 1)
        | true, [] -> Error()
        | true, calls -> completeWireBatch all index calls

    and private completeWireBatch
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (calls: SourceToolCall list)
        : Result<SourceToolCall list * int, unit> =
        match collectWireResults all (callIdSet calls) (index + 1) with
        | Error() -> Error()
        | Ok(results, _) when Map.count results <> List.length calls -> Error()
        | Ok(_, nextIndex) -> Ok(calls, nextIndex)

    let private collectWireCompletedBatches (messages: ProviderProjection.WireMessage list) : SourceToolCall list list =
        let all = List.toArray messages

        let rec loop index collected =
            let step =
                if index >= all.Length then
                    Error()
                else
                    wireBatchStep all index

            match step with
            | Error() -> List.rev collected
            | Ok([], nextIndex) -> loop nextIndex collected
            | Ok(calls, nextIndex) -> loop nextIndex (calls :: collected)

        loop 0 []

    let private signaturePair (batch: StrengthRequestBatch) (calls: SourceToolCall list) =
        let exchanges =
            batch.Exchanges
            |> List.map (fun exchange -> exchange.ToolName, exchange.CanonicalArguments)

        let calls = calls |> List.map (fun call -> call.ToolName, call.CanonicalArguments)
        exchanges, calls

    let private matchesTailCalls
        (batch: StrengthRequestBatch)
        (completedCalls: SourceToolCall list)
        (tailCalls: SourceToolCall list)
        : bool =
        let batchSignatures, callSignatures = signaturePair batch tailCalls
        batchSignatures = callSignatures && completedCalls = tailCalls

    let private matchBatchWithTailCalls
        (wireMessages: ProviderProjection.WireMessage list)
        (tailCalls: SourceToolCall list)
        : SourceToolCall list option =
        let completeBatches = StrengthBatchCollector.collectCompleteBatches wireMessages
        let completedCallBatches = collectWireCompletedBatches wireMessages

        match List.tryLast completeBatches, List.tryLast completedCallBatches with
        | Some batch, Some completedCalls when matchesTailCalls batch completedCalls tailCalls -> Some tailCalls
        | _ -> None

    let private tryExtractWireCompletedBatch
        (wire: ProviderProjection.ProviderWireProjection)
        : SourceToolCall list option =
        let assistantBatches = wire.Messages |> List.choose tryExtractWireAssistantCalls

        match assistantBatches with
        | [] -> None
        | batches -> matchBatchWithTailCalls wire.Messages (List.last batches)

    let private rawToolParts (assistant: obj) : obj list =
        let parts = ProviderWireDecode.rawPartsOf assistant

        parts
        |> List.filter (fun part ->
            let kind =
                ProviderWireDecode.firstString part [ "type" ]
                |> Option.defaultValue ""
                |> fun s -> s.ToLowerInvariant()

            kind = "tool" || kind = "tool-call" || kind = "tool_call")

    let private tryLastWireAssistant (messages: ProviderProjection.WireMessage list) =
        let all = List.toArray messages

        let rec loop index lastFound =
            if index >= all.Length then
                lastFound
            else
                let isAssistant =
                    String.Equals(all.[index].Role, "assistant", StringComparison.OrdinalIgnoreCase)

                let nextFound = if isAssistant then Some(index, all.[index]) else lastFound
                loop (index + 1) nextFound

        loop 0 None

    let private verifyWireResults
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (calls: SourceToolCall list)
        : bool =
        match collectWireResults all (callIdSet calls) (index + 1) with
        | Error() -> false
        | Ok(results, _) -> Map.count results = List.length calls

    let private checkWireAssistantCalls
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (calls: SourceToolCall list)
        : bool =
        match calls with
        | [] -> false
        | _ -> verifyWireResults all index calls

    let private checkWireTailComplete (wire: ProviderProjection.ProviderWireProjection) : bool =
        let all = List.toArray wire.Messages

        match tryLastWireAssistant wire.Messages with
        | None -> false
        | Some(index, msg) -> checkWireAssistantCalls all index (extractWireToolCalls msg.Parts)

    let private checkRawTailComplete (rawTools: obj list) (wire: ProviderProjection.ProviderWireProjection) : bool =
        match List.isEmpty rawTools with
        | false -> List.forall isToolPartCompleted rawTools
        | true -> checkWireTailComplete wire

    let private isAssistantRawMessage (raw: obj) : bool =
        let info = ProviderWireDecode.infoObject raw

        let role =
            ProviderWireDecode.firstString info [ "role" ]
            |> Option.orElse (ProviderWireDecode.firstString raw [ "role" ])
            |> Option.defaultValue ""

        String.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)

    /// WHAT[speculative-investigation-002]: source identity is the emitting
    /// assistant that completed the batch, never the next outbound placeholder.
    /// Trailing assistants with no tool parts are placeholders and are skipped.
    /// An assistant whose tools are present but incomplete fails closed and must
    /// not fall back to an earlier completed batch (013 trailing-integrity).
    [<RequireQualifiedAccess>]
    type private SourceAssistantTail =
        | Completed of raw: obj
        | Incomplete
        | Absent

    let private decideSourceAssistantTail
        (last: obj)
        (continueEarlier: unit -> SourceAssistantTail)
        : SourceAssistantTail =
        let tools = rawToolParts last

        if List.isEmpty tools then
            continueEarlier ()
        elif List.forall isToolPartCompleted tools then
            SourceAssistantTail.Completed last
        else
            SourceAssistantTail.Incomplete

    let private classifySourceAssistantTail (rawMessages: obj list) : SourceAssistantTail =
        let assistants = rawMessages |> List.filter isAssistantRawMessage |> List.rev

        let rec loop remaining =
            match remaining with
            | [] -> SourceAssistantTail.Absent
            | last :: earlier -> decideSourceAssistantTail last (fun () -> loop earlier)

        loop assistants

    let private tailBatchIsComplete (rawMessages: obj list) (wire: ProviderProjection.ProviderWireProjection) : bool =
        match classifySourceAssistantTail rawMessages with
        | SourceAssistantTail.Completed lastRaw -> checkRawTailComplete (rawToolParts lastRaw) wire
        | SourceAssistantTail.Incomplete -> false
        | SourceAssistantTail.Absent -> checkWireTailComplete wire

    let private pairCompletedCallsWithRun
        (calls: SourceToolCall list option)
        (run: ProviderRunIdentity option)
        : (SourceToolCall list * ProviderRunIdentity) option =
        match calls, run with
        | Some complete, Some source -> Some(complete, source)
        | _ -> None

    let private providerRunOfRawAssistant (raw: obj) : ProviderRunIdentity option =
        ProviderWireCapture.decodeCapturedMessage raw
        |> Option.bind (fun captured -> captured.ProviderRun)

    /// No host-session emitting tool batch: fall back to the last raw assistant
    /// (if any) solely for ProviderRun, paired with a wire-shaped completed batch.
    /// Host completed parts never appear as WireToolCall, so this path only serves
    /// multi-message wire history.
    let private batchFromAbsentAssistantTail
        (rawMessages: obj list)
        (wire: ProviderProjection.ProviderWireProjection)
        : (SourceToolCall list * ProviderRunIdentity) option =
        match rawMessages |> List.filter isAssistantRawMessage |> List.tryLast with
        | None -> None
        | Some raw -> pairCompletedCallsWithRun (tryExtractWireCompletedBatch wire) (providerRunOfRawAssistant raw)

    let private batchFromCompletedAssistantTail
        (raw: obj)
        (wire: ProviderProjection.ProviderWireProjection)
        : (SourceToolCall list * ProviderRunIdentity) option =
        let calls =
            tryExtractRawAssistantBatch raw
            |> Option.orElseWith (fun () -> tryExtractWireCompletedBatch wire)

        pairCompletedCallsWithRun calls (providerRunOfRawAssistant raw)

    let private extractCompletedSourceBatch
        (rawMessages: obj list)
        (wire: ProviderProjection.ProviderWireProjection)
        : (SourceToolCall list * ProviderRunIdentity) option =
        match classifySourceAssistantTail rawMessages with
        | SourceAssistantTail.Incomplete -> None
        | SourceAssistantTail.Absent -> batchFromAbsentAssistantTail rawMessages wire
        | SourceAssistantTail.Completed raw -> batchFromCompletedAssistantTail raw wire

    /// Resolves the completed source batch of the emitting assistant.
    /// Supports both:
    /// 1. Host session-shaped tool parts (single assistant message where all tool parts are completed with output)
    /// 2. Wire-level multi-message parts (assistant WireToolCall messages followed by WireToolResult messages)
    let private resolveCompletedSourceBatch
        (rawMessages: obj list)
        (wire: ProviderProjection.ProviderWireProjection)
        : (SourceToolCall list * ProviderRunIdentity) option =
        match tailBatchIsComplete rawMessages wire with
        | false -> None
        | true -> extractCompletedSourceBatch rawMessages wire

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
        let ofRounds (roundsList: InvestigationEstimateContract.EstimatedReadonlyRounds list) =
            let maxRounds =
                roundsList
                |> List.maxBy InvestigationEstimateContract.EstimatedReadonlyRounds.value

            let maxValue = InvestigationEstimateContract.EstimatedReadonlyRounds.value maxRounds

            if maxValue = 0 then
                BatchAggregation.EstimatedZero
            else
                BatchAggregation.PositiveEstimate(
                    InvestigationEstimateContract.EstimatedReadonlyRounds.toExecutionBudget maxRounds
                )

        let ofParsedResults
            (parsedResults: Result<InvestigationEstimateContract.EstimatedReadonlyRounds, string> list)
            =
            let firstError =
                parsedResults
                |> List.tryPick (function
                    | Error err -> Some err
                    | Ok _ -> None)

            match firstError with
            | Some reason -> BatchAggregation.ArgumentError reason
            | None ->
                ofRounds (
                    parsedResults
                    |> List.choose (function
                        | Ok rounds -> Some rounds
                        | Error _ -> None)
                )

        let roundsOfParsedObject
            (call: SourceToolCall)
            (parsedObj: obj)
            : Result<InvestigationEstimateContract.EstimatedReadonlyRounds, string> =
            match InvestigationEstimateContract.parseParticipatingArguments parsedObj with
            | Ok(rounds, _noteOpt) -> Ok rounds
            | Error err ->
                let explanation = InvestigationEstimateContract.formatArgumentError language err

                Error(sprintf "delegation arguments of call %s rejected: %s" (ToolCallId.value call.CallId) explanation)

        let parseCall (call: SourceToolCall) : Result<InvestigationEstimateContract.EstimatedReadonlyRounds, string> =
            match tryParseCallArguments call.CallId call.CanonicalArguments with
            | Error err -> Error err
            | Ok parsedObj -> roundsOfParsedObject call parsedObj

        let estimateCalls =
            calls
            |> List.filter (fun call ->
                match InvestigationEstimateContract.classifyTool call.ToolName with
                | InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall -> true
                | _ -> false)

        match estimateCalls with
        | [] -> BatchAggregation.NoEstimateOpportunity
        | _ -> estimateCalls |> List.map parseCall |> ofParsedResults

    // ---- phase one: capture the authorization ----------------------------------

    type CaptureOutcome =
        | Captured of DelegationRequest
        | Skipped of reason: string

    let captureOutcomeCode (outcome: CaptureOutcome) : string =
        match outcome with
        | Captured _ -> "Captured"
        | Skipped _ -> "Skipped"

    let private checkCaptureEligibility (surface: OwnerSurface) : Result<unit, string> =
        if surface.RequestKind <> ProviderRequestKind.WorkMain then
            Error "not-work-main"
        elif surface.Ports.Runtime.IsReplica surface.Owner || surface.IsInternalLeaf then
            Error "replica-or-internal-leaf"
        else
            Ok()

    let private tryResolveSourceCalls
        (surface: OwnerSurface)
        : Result<SourceToolCall list * ProviderRunIdentity, string> =
        match resolveCompletedSourceBatch surface.RawMessages surface.Wire with
        | Some(calls, sourceRun) -> Ok(calls, sourceRun)
        | None ->
            let assistants = surface.RawMessages |> List.filter isAssistantRawMessage

            let summary =
                assistants
                |> List.mapi (fun index raw ->
                    let tools = rawToolParts raw
                    let completed = tools |> List.filter isToolPartCompleted |> List.length

                    let run =
                        ProviderWireCapture.decodeCapturedMessage raw
                        |> Option.bind (fun captured -> captured.ProviderRun)
                        |> Option.isSome

                    sprintf "a%d-t%d-c%d-r%b" index tools.Length completed run)
                |> String.concat ","

            Error(sprintf "no-completed-source-batch(%s;w%d)" summary surface.Wire.Messages.Length)

    let private tryResolveCaptureCallsAndBudget
        (surface: OwnerSurface)
        : Result<SourceToolCall list * ProviderRunIdentity * ReadonlyRoundBudget, string> =
        result {
            let! calls, sourceProviderRun = tryResolveSourceCalls surface

            do!
                match
                    surface.HostMessages
                    |> List.tryFind (fun message -> message.Id = ProviderRunIdentity.value sourceProviderRun)
                with
                | Some source when
                    source.ParentId = Some(PhysicalUserMessageId.value surface.SourcePhysicalUserMessageId)
                    ->
                    Ok()
                | _ -> Error "source-batch-not-from-current-physical-request"

            let language =
                ProviderLanguageBinding.forSessionText (SessionId.value surface.Owner)

            match aggregateBatchEstimate language calls with
            | BatchAggregation.PositiveEstimate budget -> return calls, sourceProviderRun, budget
            | BatchAggregation.NoEstimateOpportunity -> return! Error "no-estimate-opportunity"
            | BatchAggregation.EstimatedZero -> return! Error "estimated-zero"
            | BatchAggregation.ArgumentError reason -> return! Error reason
        }

    let private buildDelegationRequest
        (surface: OwnerSurface)
        (calls: SourceToolCall list)
        (sourceRun: ProviderRunIdentity)
        (budget: ReadonlyRoundBudget)
        : DelegationRequest =
        let ownerLogicalRun =
            { LogicalRunId = surface.Authority.LogicalRunId
              AuthorityRootUserMessageId = surface.Authority.AuthorityRootUserMessageId }

        let decisionId =
            Delegation.deriveDecisionId HostDigest.sha256Hex contractRevision ownerLogicalRun sourceRun

        let sourceToolCallIds = calls |> List.map (fun call -> call.CallId)

        { DecisionId = decisionId
          OwnerSessionId = surface.Owner
          OwnerLogicalRun = ownerLogicalRun
          SourcePhysicalUserMessageId = surface.SourcePhysicalUserMessageId
          SourceProviderRun = sourceRun
          SourceToolCallIds = sourceToolCallIds
          RequestedRounds = budget
          ContractRevision = contractRevision }

    [<RequireQualifiedAccess>]
    type private CaptureDisposition =
        | Replay of DelegationRequest
        | Conflict
        | Fresh of DelegationRequest

    let private evaluateCaptureDisposition
        (durableProjection: StrengthProjection)
        (request: DelegationRequest)
        : CaptureDisposition =
        let resolveExisting (existing: StrengthDelegationView) =
            if not (Delegation.sameRequest existing.Request request) then
                CaptureDisposition.Conflict
            else
                CaptureDisposition.Replay existing.Request

        match
            StrengthProjection.tryCandidateBySource
                request.OwnerSessionId
                request.OwnerLogicalRun
                request.SourcePhysicalUserMessageId
                request.SourceProviderRun
                durableProjection
        with
        | Some existing -> resolveExisting existing
        | None ->
            match StrengthProjection.tryCandidate request.DecisionId durableProjection with
            | Some existing -> resolveExisting existing
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
              SourceProviderRun = request.SourceProviderRun
              SourceToolCallIds = request.SourceToolCallIds
              RequestedRounds = Some request.RequestedRounds
              ContractRevision = contractRevision
              IsRootWork = not surface.IsInternalLeaf
              RequestKind = surface.RequestKind
              CanonicalRole = surface.Authority.CanonicalRole
              HasPrefixProbe = surface.HasPrefixProbe
              IsReplicaOrInternalLeaf = surface.IsInternalLeaf
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
            let! calls, sourceRun, budget = tryResolveCaptureCallsAndBudget surface
            return buildDelegationRequest surface calls sourceRun budget
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
        (projectionSessionIdOpt: string option)
        (timerPort: ITimerPort option)
        (output: obj)
        : Task<CaptureOutcome> =
        task {
            match
                tryBind journal snapshotPort strengthDurability strengthScope projectionSessionIdOpt timerPort output
            with
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
        (ownerRole: Role)
        (target: ProviderRunIdentity)
        (decisionId: StrengthDecisionId)
        (bundle: StrengthFrameBundle)
        (output: obj)
        : unit =
        match renderCandidate owner ownerRole target decisionId bundle output with
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
                renderCandidateOrThrow
                    strengthScope
                    surface.Owner
                    surface.Authority.CanonicalRole
                    surface.Target
                    decisionId
                    bundle
                    surface.Output

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
                    surface.Authority.CanonicalRole
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
        | StrengthReplicaTerminal.Cancelled ->
            appendClosed strengthScope surface decisionId DelegationClosedFrom.Bound DelegationClosedReason.Cancelled
        | _ when List.isEmpty completed.Batches ->
            appendClosed strengthScope surface decisionId DelegationClosedFrom.Bound DelegationClosedReason.NoMaterial
        | _ ->
            let bundle = tryBuildBundleOrThrow strengthScope completed.Batches
            publishAndRender strengthScope surface decisionId bundle completed.ReplicaSessionId

    let private consumeRetiredBinding strengthScope surface (binding: DelegationBinding) =
        task {
            let! current = loadDurableProjectionOrThrow surface.Ports strengthScope "bound-recovery"

            match StrengthProjection.tryCandidate binding.DecisionId current with
            | Some view when view.State = StrengthCandidateState.Prepared ->
                return! consumePreparedCandidate strengthScope surface view.Prepared
            | Some view when view.State = StrengthCandidateState.Bound ->
                return!
                    appendClosed
                        strengthScope
                        surface
                        binding.DecisionId
                        DelegationClosedFrom.Bound
                        DelegationClosedReason.RecoveryAbandoned
            | _ -> return ()
        }

    let private consumeBoundOutcome
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (binding: DelegationBinding)
        (pendingOpt: Task<StrengthReplicaOutcome> option)
        : Task<unit> =
        task {
            match pendingOpt with
            | Some pending ->
                let! completed = pending
                return! handleReplicaCompletion strengthScope surface binding.DecisionId completed
            | None -> return! consumeRetiredBinding strengthScope surface binding
        }

    let private consumeBoundCandidate
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (bindingOpt: DelegationBinding option)
        : Task<unit> =
        match bindingOpt with
        | None -> Task.FromResult()
        | Some binding ->
            let pending =
                surface.Ports.Runtime.TryDecisionOutcome(binding.ReplicaSessionId, binding.DecisionId)

            consumeBoundOutcome strengthScope surface binding pending

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

    let private executeBoundReplica
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (decisionId: StrengthDecisionId)
        (preparation: StrengthReplicaPreparation)
        : Task<unit> =
        task {
            match! surface.Ports.Runtime.SendPreparedPrompt preparation.ReplicaSessionId with
            | Error err ->
                Diagnostic.emit
                    "strength-replica-prepare-failed"
                    [ "session_id", SessionId.value surface.Owner
                      "replica_session_id", SessionId.value preparation.ReplicaSessionId
                      "result", "send-error:" + err ]

                return!
                    appendClosed
                        strengthScope
                        surface
                        decisionId
                        DelegationClosedFrom.Bound
                        DelegationClosedReason.CannotContinue
            | Ok() ->
                Diagnostic.emit
                    "strength-replica-prepare-failed"
                    [ "session_id", SessionId.value surface.Owner
                      "replica_session_id", SessionId.value preparation.ReplicaSessionId
                      "result", "send-ok-awaiting-completion" ]

                let! completed = preparation.Completion

                Diagnostic.emit
                    "strength-replica-prepare-failed"
                    [ "session_id", SessionId.value surface.Owner
                      "replica_session_id", SessionId.value preparation.ReplicaSessionId
                      "result", sprintf "completion-batches=%d" completed.Batches.Length ]

                return! handleReplicaCompletion strengthScope surface decisionId completed
        }

    let private appendBoundAndExecute
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        (preparation: StrengthReplicaPreparation)
        : Task<unit> =
        task {
            try
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
            finally
                surface.Ports.Runtime.ReleaseDecisionOutcome request.DecisionId
        }

    let private startWithMirror
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        (replicaAgent: string)
        (replicaMirror: ProviderProjection.WireMessage list)
        (synchronizedTextMessages: Set<int>)
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
                    synchronizedTextMessages,
                    surface.AnchorDigest
                )
            with
            | Error reason ->
                Diagnostic.emit
                    "strength-replica-prepare-failed"
                    [ "session_id", SessionId.value surface.Owner
                      "result", "prepare-error:" + reason ]

                return!
                    appendClosed
                        strengthScope
                        surface
                        request.DecisionId
                        DelegationClosedFrom.Requested
                        DelegationClosedReason.CannotContinue
            | Ok preparation ->
                Diagnostic.emit
                    "strength-replica-prepare-failed"
                    [ "session_id", SessionId.value surface.Owner
                      "replica_session_id", SessionId.value preparation.ReplicaSessionId
                      "result", "prepare-ok-binding" ]

                return! appendBoundAndExecute strengthScope surface request preparation
        }

    let private synchronizedTextMessages (surface: OwnerSurface) : Task<Result<Set<int>, string>> =
        taskResult {
            let prepared =
                surface.DurableProjection.ByDecision
                |> Map.toList
                |> List.choose (fun (_, view) ->
                    match view.State, view.Prepared with
                    | (StrengthCandidateState.Promoted | StrengthCandidateState.Traced), Some prepared when
                        prepared.OwnerSessionId = surface.Owner
                        ->
                        Some prepared
                    | _ -> None)

            let rec collect remaining hostIds =
                taskResult {
                    match remaining with
                    | [] -> return hostIds
                    | prepared :: tail ->
                        let! bundle = surface.Ports.Durability.LoadFrameBundle prepared

                        let ids =
                            bundle.Batches
                            |> List.filter (fun batch -> not (List.isEmpty batch.AssistantText))
                            |> List.map (fun batch ->
                                StrengthFrame.hostMessageId
                                    HostDigest.sha256Hex
                                    surface.Owner
                                    prepared.DecisionId
                                    batch.RequestOrdinal
                                    "call"
                                    bundle.Digest)

                        return! collect tail (Set.union hostIds (Set.ofList ids))
                }

            let! hostIds = collect prepared Set.empty

            return
                surface.RawMessages
                |> List.choose (fun raw ->
                    ProviderWireCapture.decodeMessage raw
                    |> Option.map (fun _ -> ProviderWireDecode.hostMessageId raw))
                |> List.mapi (fun index id -> index, id)
                |> List.choose (fun (index, id) ->
                    if id |> Option.exists (fun value -> Set.contains value hostIds) then
                        Some index
                    else
                        None)
                |> Set.ofList
        }

    let private startSynchronizedMirror strengthScope surface request replicaAgent replicaMirror =
        task {
            match! synchronizedTextMessages surface with
            | Error error -> return failClosed strengthScope ("Strength mirror synchronization failed: " + error)
            | Ok synchronized ->
                return! startWithMirror strengthScope surface request replicaAgent replicaMirror synchronized
        }

    let private prepareAndStartReplica
        (strengthScope: PluginStrengthScope)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        : Task<unit> =
        let replicaAgent = Roles.roleLabel surface.Authority.CanonicalRole

        Diagnostic.emit
            "strength-replica-prepare-failed"
            [ "session_id", SessionId.value surface.Owner
              "result", sprintf "prepare-enter agent=%s wire=%d" replicaAgent surface.Wire.Messages.Length ]

        let mirrorResult =
            StrengthFrame.tryLocalizeMirror
                HostDigest.sha256Hex
                request.DecisionId
                surface.AnchorDigest
                surface.Wire.Messages

        match mirrorResult with
        | Error err ->
            Diagnostic.emit
                "strength-replica-prepare-failed"
                [ "session_id", SessionId.value surface.Owner; "result", "mirror-failed" ]

            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        | Ok replicaMirror ->
            Diagnostic.emit
                "strength-replica-prepare-failed"
                [ "session_id", SessionId.value surface.Owner
                  "result", sprintf "mirror-ok msgs=%d" replicaMirror.Length ]

            startSynchronizedMirror strengthScope surface request replicaAgent replicaMirror

    let private startRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (surface: OwnerSurface)
        (request: DelegationRequest)
        : Task<unit> =
        Diagnostic.emit
            "strength-start-request-entered"
            [ "session_id", SessionId.value surface.Owner
              "role", Roles.roleLabel surface.Authority.CanonicalRole
              "result", StrengthDecisionId.value request.DecisionId ]

        if
            request.OwnerLogicalRun.AuthorityRootUserMessageId
            <> surface.Authority.AuthorityRootUserMessageId
        then
            Diagnostic.emit
                "strength-start-request-superseded"
                [ "session_id", SessionId.value surface.Owner
                  "result",
                  sprintf
                      "req=%s surf=%s"
                      (AuthorityRootUserMessageId.value request.OwnerLogicalRun.AuthorityRootUserMessageId)
                      (AuthorityRootUserMessageId.value surface.Authority.AuthorityRootUserMessageId) ]

            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.Superseded
        elif strengthScope.StrengthFuseReason |> Option.isSome then
            failClosed strengthScope "Strength fuse is tripped; delegation is closed for this process"
        elif request.ContractRevision <> contractRevision then
            // v1 Requested that was not Bound is explicitly closed
            // upon contract revision upgrade; owner continues normally without launching old protocol child.
            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        elif not predictorConfigured then
            Diagnostic.emit
                "strength-replica-prepare-failed"
                [ "session_id", SessionId.value surface.Owner
                  "result", "predictor-not-configured" ]

            appendClosed
                strengthScope
                surface
                request.DecisionId
                DelegationClosedFrom.Requested
                DelegationClosedReason.CannotContinue
        elif not (Set.contains surface.Authority.CanonicalRole StrengthPolicy.eligibleRoles) then
            Diagnostic.emit
                "strength-replica-prepare-failed"
                [ "session_id", SessionId.value surface.Owner; "result", "role-not-eligible" ]

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
            let isTerminalState state =
                match state with
                | StrengthCandidateState.Traced
                | StrengthCandidateState.Closed _
                | StrengthCandidateState.Abandoned -> true
                | StrengthCandidateState.Requested
                | StrengthCandidateState.Bound
                | StrengthCandidateState.Prepared
                | StrengthCandidateState.Promoted -> false

            let isSourceAlreadyTerminal (req: DelegationRequest) =
                surface.DurableProjection.ByDecision
                |> Map.toList
                |> List.map snd
                |> List.exists (fun other ->
                    other.Request.OwnerSessionId = req.OwnerSessionId
                    && other.Request.OwnerLogicalRun = req.OwnerLogicalRun
                    && other.Request.SourcePhysicalUserMessageId = req.SourcePhysicalUserMessageId
                    && other.Request.SourceProviderRun = req.SourceProviderRun
                    && isTerminalState other.State)

            let pending =
                surface.DurableProjection.ByDecision
                |> Map.toList
                |> List.map snd
                |> List.filter (fun view ->
                    view.State = StrengthCandidateState.Requested
                    && view.Request.OwnerSessionId = surface.Owner
                    && view.Request.OwnerLogicalRun.LogicalRunId = surface.Authority.LogicalRunId
                    && not (isSourceAlreadyTerminal view.Request))
                |> List.sortBy (fun view -> StrengthDecisionId.value view.Request.DecisionId)

            Diagnostic.emit
                "strength-delegation-skip"
                [ "session_id", SessionId.value surface.Owner
                  "result", sprintf "start-pending-count:%d" pending.Length ]

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
        // A decision is already bound to THIS target but has produced no
        // candidate yet. That is the state of every decision captured moments
        // ago in this same transform: its target is this request's target, so
        // the target lookup short-circuits before the pending scan can ever see
        // it. Answering Skip here is what stranded captured-but-unstarted
        // decisions across requests; answering StartPending lets the start run
        // in the same request that captured it, which is also where its mirror
        // is the request actually being sent.
        StrengthProjection.tryDecisionForTarget target durable
        |> Option.bind (fun decisionId -> StrengthProjection.tryCandidate decisionId durable)
        |> function
            | Some candidate -> SurfaceApplication.ConsumeBound candidate
            | None -> SurfaceApplication.StartPending

    let private planSurfaceApplication (surface: OwnerSurface) : SurfaceApplication =
        if
            surface.RequestKind <> ProviderRequestKind.WorkMain
            || surface.Ports.Runtime.IsReplica surface.Owner
            || surface.IsInternalLeaf
        then
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
            | Error err ->
                Diagnostic.emit
                    "strength-delegation-skip"
                    [ "session_id", SessionId.value (snd bound)
                      "result", "apply-surface-failed:" + err ]

                return ()
            | Ok surface -> return! applyOnSurface strengthScope predictorConfigured surface
        }

    let private startCanonicalRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (request: DelegationRequest)
        (durableStrength: StrengthProjection)
        (surface: OwnerSurface)
        : Task<unit> =
        match StrengthProjection.tryCandidate request.DecisionId durableStrength with
        | Some view when view.State = StrengthCandidateState.Requested ->
            startRequest strengthScope predictorConfigured surface request
        | None ->
            // If the durable event was just appended during this same turn but projection hasn't folded yet or was freshly created
            startRequest strengthScope predictorConfigured surface request
        | Some view when
            view.Binding
            |> Option.exists (fun binding -> binding.TargetProviderRun = surface.Target)
            ->
            consumeBoundDecision strengthScope surface view
        | _ -> Task.FromResult()

    /// Capture and start in ONE step at the end of the transform.
    ///
    /// The authorization metadata is frozen from the completed source batch of
    /// the tail assistant message; the start runs immediately in the same call,
    /// so the mirror is the FINAL outgoing request (post-sanitization) and no
    /// decision is ever left pending across requests.
    let private tryStartBoundRequest
        (strengthScope: PluginStrengthScope)
        (predictorConfigured: bool)
        (request: DelegationRequest)
        (bound: BoundPorts * SessionId)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (output: obj)
        : Task<unit> =
        task {
            let! durableStrength = loadDurableProjectionOrThrow (fst bound) strengthScope "start"

            let! surfaceResult =
                resolveSurface bound strengthScope tryAttemptPlan syncDelegateRuntime durableStrength output

            match surfaceResult with
            | Error err ->
                Diagnostic.emit
                    "strength-start-surface-failed"
                    [ "session_id", SessionId.value request.OwnerSessionId; "result", err ]

                return ()
            | Ok surface ->
                return! startCanonicalRequest strengthScope predictorConfigured request durableStrength surface
        }

    let private executeSkippedRecovery
        (boundResult: Result<BoundPorts * SessionId, string>)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (output: obj)
        : Task<unit> =
        match boundResult with
        | Ok bound when strengthScope.StrengthFuseReason.IsNone ->
            executeApplyOnBound bound strengthScope tryAttemptPlan syncDelegateRuntime predictorConfigured output
        | Ok(_, sessionId) ->
            Diagnostic.emit
                "strength-delegation-skip"
                [ "session_id", SessionId.value sessionId; "result", "skipped-recovery-fuse" ]

            Task.FromResult()
        | Error err ->
            Diagnostic.emit "strength-delegation-skip" [ "session_id", ""; "result", "skipped-recovery-bind:" + err ]
            Task.FromResult()

    let private tryStartCapturedRequest
        (snapshotPort: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (strengthDurability: StrengthDurabilityPort option)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (projectionSessionIdOpt: string option)
        (timerPort: ITimerPort option)
        (output: obj)
        (request: DelegationRequest)
        : Task<unit> =
        Diagnostic.emit
            "strength-start-captured-entry"
            [ "session_id", SessionId.value request.OwnerSessionId
              "result", StrengthDecisionId.value request.DecisionId ]

        match tryBind journal snapshotPort strengthDurability strengthScope projectionSessionIdOpt timerPort output with
        | Error err ->
            Diagnostic.emit
                "strength-start-captured-bind-failed"
                [ "session_id", SessionId.value request.OwnerSessionId; "result", err ]

            Task.FromResult()
        | Ok bound ->
            Diagnostic.emit
                "strength-start-captured-bound"
                [ "session_id", SessionId.value request.OwnerSessionId
                  "result", StrengthDecisionId.value request.DecisionId ]

            tryStartBoundRequest
                strengthScope
                predictorConfigured
                request
                bound
                tryAttemptPlan
                syncDelegateRuntime
                output

    let tryCaptureAndStart
        (snapshotPort: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (strengthDurability: StrengthDurabilityPort option)
        (strengthScope: PluginStrengthScope)
        (tryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (predictorConfigured: bool)
        (projectionSessionIdOpt: string option)
        (timerPort: ITimerPort option)
        (output: obj)
        : Task<unit> =
        task {
            let! captured =
                tryCapture
                    snapshotPort
                    journal
                    strengthDurability
                    strengthScope
                    tryAttemptPlan
                    syncDelegateRuntime
                    predictorConfigured
                    projectionSessionIdOpt
                    timerPort
                    output

            match captured with
            | CaptureOutcome.Skipped reason ->
                let sessionId =
                    projectionSessionIdOpt
                    |> Option.orElseWith (fun () -> ProviderWireDecode.projectionSessionIdFromMessages output)
                    |> Option.defaultValue ""

                Diagnostic.emit "strength-delegation-skip" [ "session_id", sessionId; "result", reason ]

                let boundResult =
                    tryBind
                        journal
                        snapshotPort
                        strengthDurability
                        strengthScope
                        projectionSessionIdOpt
                        timerPort
                        output

                return!
                    executeSkippedRecovery
                        boundResult
                        strengthScope
                        tryAttemptPlan
                        syncDelegateRuntime
                        predictorConfigured
                        output
            | CaptureOutcome.Captured request ->
                Diagnostic.emit
                    "strength-delegation-requested"
                    [ "session_id", SessionId.value request.OwnerSessionId
                      "decision_id", StrengthDecisionId.value request.DecisionId
                      "result", string (ReadonlyRoundBudget.value request.RequestedRounds) ]

                return!
                    tryStartCapturedRequest
                        snapshotPort
                        journal
                        strengthDurability
                        strengthScope
                        tryAttemptPlan
                        syncDelegateRuntime
                        predictorConfigured
                        projectionSessionIdOpt
                        timerPort
                        output
                        request
        }
