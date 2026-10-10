namespace Wanxiangshu.Context.Companion

open System
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.Execution.Session
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt.Fallback

/// Context-owned fold oracle for durable recovery laws.
/// It accepts plain fact/envelope data and returns plain projection summaries;
/// the typed fold and all Fable collections remain inside the production boundary.
[<RequireQualifiedAccess>]
module ContextFoldSurface =

    let private text (value: obj) =
        if isNull value then "" else string value

    let private sessionId (value: obj) = SessionId.create (text value)
    let private providerRun (value: obj) = ProviderRunIdentity.create (text value)
    let private blobRef (value: obj) = BlobRef.create (text value)
    let private blobDigest (value: obj) = BlobDigest.create (text value)

    let private frameEpoch (value: obj) =
        FrameEpochId.create (Int64.Parse(text value))

    let private prefixEpoch (value: obj) =
        PrefixEpochId.create (Int64.Parse(text value))

    let private bloggerRequest (value: obj) = BloggerRequestId.create (text value)
    let private promptKey (value: obj) = PromptKey.create (text value)

    let private optionalText (value: obj) =
        if isNull value then None else Some(text value)

    let private optionalBlobRef (value: obj) =
        if isNull value then None else Some(blobRef value)

    let private optionalProviderRun (value: obj) =
        if isNull value then None else Some(providerRun value)

    let private optionalToolCall (value: obj) =
        if isNull value then
            None
        else
            Some(ToolCallId.create (text value))

    let private optionalHostToolPart (value: obj) =
        if isNull value then
            None
        else
            Some(HostToolPartId.create (text value))

    let private int64Value (value: obj) = unbox<int64> value
    let private intValue (value: obj) = unbox<int> value

    let private stringList (value: obj) =
        if isNull value then
            []
        else
            unbox<string array> value |> Array.toList

    let private toolCallList (value: obj) =
        if isNull value then
            []
        else
            unbox<string array> value |> Array.toList |> List.map ToolCallId.create

    let private companionFact (caseName: string) (payload: obj) : AgentFact =
        match caseName with
        | "CompanionBloggerLinked" ->
            AgentFact.Companion(
                CompanionFactCases.CompanionBloggerLinked
                    {| SessionId = sessionId (payload?SessionId)
                       BloggerSessionId = sessionId (payload?BloggerSessionId)
                       BloggerAgent = text (payload?BloggerAgent) |}
            )
        | "CompanionBloggerClosed" ->
            AgentFact.Companion(
                CompanionFactCases.CompanionBloggerClosed {| SessionId = sessionId (payload?SessionId) |}
            )
        | "OpeningPromptCaptured" ->
            AgentFact.Companion(
                CompanionFactCases.OpeningPromptCaptured
                    {| SessionId = sessionId (payload?SessionId)
                       AssignmentText = text (payload?AssignmentText)
                       AuthoritativeRequirements = stringList (payload?AuthoritativeRequirements)
                       ProviderRun = optionalProviderRun (payload?ProviderRun) |}
            )
        | "XTracePartAppended" ->
            AgentFact.Companion(
                CompanionFactCases.XTracePartAppended
                    {| SessionId = sessionId (payload?SessionId)
                       CursorSequence = int64Value (payload?CursorSequence)
                       Role = text (payload?Role)
                       Turn = intValue (payload?Turn)
                       PartIndex = intValue (payload?PartIndex)
                       Kind = text (payload?Kind)
                       ToolName = optionalText (payload?ToolName)
                       TextRef = blobRef (payload?TextRef)
                       TextDigest = blobDigest (payload?TextDigest)
                       Provenance = text (payload?Provenance)
                       ProviderRun = optionalProviderRun (payload?ProviderRun)
                       ToolCallId = optionalToolCall (payload?ToolCallId)
                       HostToolPartId = optionalHostToolPart (payload?HostToolPartId) |}
            )
        | "TerminalOutputCaptured" ->
            AgentFact.Companion(
                CompanionFactCases.TerminalOutputCaptured
                    {| SessionId = sessionId (payload?SessionId)
                       TextRef = blobRef (payload?TextRef)
                       TextDigest = blobDigest (payload?TextDigest)
                       ProviderRun = providerRun (payload?ProviderRun) |}
            )
        | other -> failwith $"ContextFoldSurface: unknown Companion fact '{other}'"

    let private contextFact (caseName: string) (payload: obj) : AgentFact =
        match caseName with
        | "BlogObservationCommitted" ->
            AgentFact.Context(
                ContextFactCases.BlogObservationCommitted
                    {| SessionId = sessionId (payload?SessionId)
                       BloggerSessionId = sessionId (payload?BloggerSessionId)
                       RequestId = bloggerRequest (payload?RequestId)
                       FrameEpochId = frameEpoch (payload?FrameEpochId)
                       PreviousIngestedThroughSequence = int64Value (payload?PreviousIngestedThroughSequence)
                       NextIngestedThroughSequence = int64Value (payload?NextIngestedThroughSequence)
                       PreviousCoverableTurnCutoffExclusive = intValue (payload?PreviousCoverableTurnCutoffExclusive)
                       NextCoverableTurnCutoffExclusive = intValue (payload?NextCoverableTurnCutoffExclusive)
                       NextCoveredPrefixDigest = text (payload?NextCoveredPrefixDigest)
                       TextRef = blobRef (payload?TextRef)
                       TextDigest = blobDigest (payload?TextDigest)
                       ProviderRun = providerRun (payload?ProviderRun)
                       ToolCallIds = toolCallList (payload?ToolCallIds)
                       TipRuleId = text (payload?TipRuleId)
                       FieldNameAtCommit = optionalText (payload?FieldNameAtCommit)
                       EvidenceRef = optionalBlobRef (payload?EvidenceRef)
                       ObservedPrefixEpochId = prefixEpoch (payload?ObservedPrefixEpochId) |}
            )
        | "BlogObservationsSquashed" ->
            AgentFact.Context(
                ContextFactCases.BlogObservationsSquashed
                    {| SessionId = sessionId (payload?SessionId)
                       BloggerSessionId = sessionId (payload?BloggerSessionId)
                       RequestId = bloggerRequest (payload?RequestId)
                       PreviousFrameEpochId = frameEpoch (payload?PreviousFrameEpochId)
                       NextFrameEpochId = frameEpoch (payload?NextFrameEpochId)
                       CoveredFrameCount = intValue (payload?CoveredFrameCount)
                       TextRef = blobRef (payload?TextRef)
                       TextDigest = blobDigest (payload?TextDigest)
                       ProviderRun = providerRun (payload?ProviderRun) |}
            )
        | "PrefixRebaseCommitted" ->
            AgentFact.Context(
                ContextFactCases.PrefixRebaseCommitted
                    {| SessionId = sessionId (payload?SessionId)
                       PreviousEpochId = prefixEpoch (payload?PreviousEpochId)
                       NextEpochId = prefixEpoch (payload?NextEpochId)
                       FrozenRecordPrefixRef = blobRef (payload?FrozenRecordPrefixRef)
                       FrozenRecordPrefixDigest = blobDigest (payload?FrozenRecordPrefixDigest)
                       CutoffExclusive = intValue (payload?CutoffExclusive)
                       CoveredPrefixDigest = text (payload?CoveredPrefixDigest)
                       SealRoot = text (payload?SealRoot)
                       SyntheticMessageId = text (payload?SyntheticMessageId)
                       ProbeId = text (payload?ProbeId)
                       SolvingProviderRun = providerRun (payload?SolvingProviderRun) |}
            )
        | "ContextReanchored" ->
            AgentFact.Context(
                ContextFactCases.ContextReanchored
                    {| SessionId = sessionId (payload?SessionId)
                       PreviousEpochId = prefixEpoch (payload?PreviousEpochId)
                       NextEpochId = prefixEpoch (payload?NextEpochId)
                       ObservedCompactionRun = providerRun (payload?ObservedCompactionRun) |}
            )
        | "TenureReanchored" ->
            AgentFact.Context(
                ContextFactCases.TenureReanchored
                    {| SessionId = sessionId (payload?SessionId)
                       PreviousEpochId = prefixEpoch (payload?PreviousEpochId)
                       NextEpochId = prefixEpoch (payload?NextEpochId)
                       WorkId = text (payload?WorkId)
                       IncumbencyId = text (payload?IncumbencyId) |}
            )
        | "TodoCheckpointCommitted" ->
            AgentFact.Context(
                ContextFactCases.TodoCheckpointCommitted
                    {| SessionId = sessionId (payload?SessionId)
                       ToolCallId = ToolCallId.create (text (payload?ToolCallId)) |}
            )
        | other -> failwith $"ContextFoldSurface: unknown context fact '{other}'"

    let private agentFactOfJs (value: obj) : Fact =
        let family = text (value?family)
        let caseName = text (value?case)
        let payload = unbox<obj> (value?payload)

        match family with
        | "Companion" -> Fact.Agent(companionFact caseName payload)
        | "Context" -> Fact.Agent(contextFact caseName payload)
        | _ -> failwith $"ContextFoldSurface: unknown fact family '{family}'"

    let private streamOfJs (value: obj) =
        StreamId.Session(sessionId (value?session))

    let private envelopeOfJs (value: obj) : Envelope =
        let fact = agentFactOfJs (value?fact)

        { RuntimeId = RuntimeId.create (text (value?runtime))
          LocalSeq = LocalSeq.create (int64Value (value?seq))
          ObservedAt = DateTimeOffset.Parse(text (value?observedAt))
          EventId = EventId.create (text (value?id))
          Stream = streamOfJs value
          ProviderRun = optionalProviderRun (value?run)
          Fact = fact }

    let private prefixSnapshotToJs snapshot =
        match snapshot with
        | None -> null
        | Some value ->
            box
                {| FrozenRecordPrefixRef = BlobRef.value value.FrozenRecordPrefixRef
                   FrozenRecordPrefixDigest = BlobDigest.value value.FrozenRecordPrefixDigest
                   CutoffExclusive = value.CutoffExclusive
                   CoveredPrefixDigest = value.CoveredPrefixDigest
                   SealRoot = value.SealRoot
                   SyntheticMessageId = value.SyntheticMessageId |}

    // Explicit annotation: `open ...Blogger.Runtime` also brings OpenBloggerRequest
    // (same FrameEpochId field) into scope, and unannotated record-field inference
    // would resolve `state` to the wrong type.
    let private blogToJs (blog: BlogProjectionState option) =
        match blog with
        | None -> null
        | Some state ->
            box
                {| FrameEpochId = FrameEpochId.value state.FrameEpochId
                   FrameKinds =
                    state
                    |> BlogProjection.frames
                    |> List.map (fun frame -> string frame.Kind)
                    |> List.toArray
                   FrameCount = BlogProjection.frameCount state
                   Coverage =
                    box
                        {| IngestedThroughSequence = state.Coverage.IngestedThroughSequence
                           CoverableTurnCutoffExclusive = state.Coverage.CoverableTurnCutoffExclusive
                           CoveredPrefixDigest = state.Coverage.CoveredPrefixDigest |} |}

    let private companionToJs (companion: CompanionProjection option) =
        match companion with
        | None -> null
        | Some state ->
            box
                {| BloggerSessionId =
                    match state.BloggerSessionId with
                    | None -> null
                    | Some blogger -> box (SessionId.value blogger) |}

    let private xTraceToJs (xTrace: XTraceProjectionState option) =
        match xTrace with
        | None -> null
        | Some state ->
            let opening =
                match XTraceProjection.openingEvidence state with
                | None -> null
                | Some value ->
                    box
                        {| AssignmentText = value.AssignmentText
                           AuthoritativeRequirements = value.AuthoritativeRequirements |> List.toArray |}

            let parts =
                XTraceProjection.orderedSemanticParts state
                |> List.map (fun part ->
                    box
                        {| CursorSequence = XTraceCursor.sequence part.Cursor
                           Provenance = part.Provenance
                           Role = part.Role
                           Kind = part.Kind
                           TextRef = BlobRef.value part.TextRef
                           TextDigest = BlobDigest.value part.TextDigest |})
                |> List.toArray

            let latestTerminal =
                match XTraceProjection.latestTerminalEvidence state with
                | None -> null
                | Some terminal ->
                    box
                        {| TextRef = BlobRef.value terminal.TextRef
                           TextDigest = BlobDigest.value terminal.TextDigest
                           ProviderRun = ProviderRunIdentity.value terminal.ProviderRun
                           FrontierSequence = XTraceCursor.sequence terminal.Frontier |}

            box
                {| Opening = opening
                   Parts = parts
                   LatestTerminal = latestTerminal |}

    // ── obligation-ledger-004: read-only forwarders for the slices this
    // fold oracle previously hard-coded as null. Same rule as the journal
    // snapshot forwarders: public fields and owner accessors only
    // (Relay.Fold.view/roads, SessionStartedAtProjection.startedAt); no
    // private representation is destructured outside its owner domain.

    let private handleIdText (handleId: HandleId) =
        match handleId with
        | HandleId.Agent id -> "agent:" + AgentHandleId.value id
        | HandleId.Pty id -> "pty:" + PtyHandleId.value id
        | HandleId.ManagerJob id -> "manager-job:" + ManagerJobId.value id

    let private handlesToJs (state: AgentLinkageProjection) : obj =
        box
            {| handleCount = Map.count state.Handles
               nextCreationOrder = state.NextCreationOrder
               workCount = Map.count state.Works
               legacyWorkHandleCount = Set.count state.LegacyWorkHandles
               handles =
                state.Handles
                |> Map.toList
                |> List.map (fun (handleId, record) ->
                    box
                        {| handle = handleIdText handleId
                           childSessionId = SessionId.value record.ChildSessionId
                           targetAgent = record.TargetAgent
                           byname = record.Byname
                           creationOrder = record.CreationOrder |})
                |> List.toArray |}

    let private providerFailuresToJs (state: ProviderFailureProjection) : obj =
        box
            {| logicalRun = LogicalRunId.value state.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value state.AuthorityRootUserMessageId
               consecutiveFailureCount = state.Budget.ConsecutiveFailureCount
               recentFailureKeyCount = state.RecentFailureKeys.Length
               exhausted = state.Exhausted |}

    let private authorityProfileToJs (profile: PromptAuthority.AuthorityExecutionProfile) : obj =
        box
            {| session = SessionId.value profile.SessionId
               logicalRun = LogicalRunId.value profile.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId
               authorityKind =
                match profile.AuthorityKind with
                | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
                | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot" |}

    let private promptAuthorityToJs (state: PromptAuthority.PromptAuthorityProjection) : obj =
        box
            {| activeLogicalRun = state.ActiveLogicalRun |> Option.map authorityProfileToJs |> Option.toObj
               lastAuthorityProfile = state.LastAuthorityProfile |> Option.map authorityProfileToJs |> Option.toObj
               pendingClaimCount = Map.count state.PendingClaims
               acceptedDispatchCount = Map.count state.AcceptedDispatches
               physicalLandingCount = Map.count state.PhysicalLandings
               acceptedContinuationCount = Map.count state.AcceptedContinuationIds
               claimSequenceKeys = state.ClaimSequences |> Map.toList |> List.map fst |> List.toArray |}

    let private enforcementToJs (state: EnforcementProjectionState) : obj =
        box
            {| cycleCount = Map.count state.ByProviderRun
               cycles =
                state.ByProviderRun
                |> Map.toList
                |> List.map (fun (providerRun, record) ->
                    box
                        {| providerRun = ProviderRunIdentity.value providerRun
                           mainSessionId = SessionId.value record.MainSessionId
                           bloggerSessionId = SessionId.value record.BloggerSessionId
                           tipRuleId = record.TipRuleId
                           toolCallCount = List.length record.ToolCallIds |})
                |> List.toArray
               recentTips =
                state.RecentTips
                |> List.map (fun tip ->
                    box
                        {| ruleId = tip.RuleId
                           fieldName = tip.FieldName
                           cycleId = tip.CycleId |})
                |> List.toArray |}

    let private bloggerCyclesToJs (state: BloggerCycleProjectionState) : obj =
        box
            {| receiptCount = Map.count state.ByProviderRun
               receiptRuns =
                state.ByProviderRun
                |> Map.toList
                |> List.map (fst >> ProviderRunIdentity.value)
                |> List.toArray
               openRequestCount = Map.count state.OpenByRequestId
               openByBloggerCount = Map.count state.OpenByBlogger
               providerRunByRequestIdCount = Map.count state.ProviderRunByRequestId |}

    let private relayToJs (state: RelayState) : obj =
        let roadViewToJs roadId (road: RoadView) =
            box
                {| roadId = RoadId.value roadId
                   iterationOrdinal = road.IterationOrdinal
                   authorityRevisionCount = List.length road.AuthorityRevisions
                   authorityMessageIdCount = List.length road.AuthorityMessageIds
                   activeIncumbencyPresent = road.ActiveIncumbency |> Option.isSome
                   retiredIncumbencyCount = List.length road.RetiredIncumbencies
                   certificatePresent = road.Certificate |> Option.isSome
                   latestRetirementPresent = road.LatestRetirement |> Option.isSome
                   boundDevOps = road.BoundDevOps |> Option.toObj |}

        let roadIds = Wanxiangshu.Mission.Relay.Fold.roads state

        box
            {| roadCount = List.length roadIds
               roads =
                roadIds
                |> List.map (fun roadId ->
                    Wanxiangshu.Mission.Relay.Fold.view state roadId
                    |> Option.map (roadViewToJs roadId)
                    |> Option.toObj)
                |> List.toArray |}

    let private guidelinesToJs (state: GuidelineProjectionState) : obj =
        box
            {| pairCount = List.length state.Pairs
               callIds = state.CallIds |> Set.toList |> List.toArray
               placementCount = Set.count state.Placements
               visibleFromOrdinal = state.VisibleFromOrdinal
               pairs =
                state.Pairs
                |> List.map (fun pair ->
                    box
                        {| ordinal = pair.Ordinal
                           callId = ToolCallId.value pair.CallId
                           markerText = pair.MarkerText |})
                |> List.toArray |}

    let private requirementGroundingToJs (state: RequirementGroundingProjectionState) : obj =
        box
            {| pendingCount = Map.count state.Pending
               occurrenceCount = List.length state.OccurrencesRev
               visibleMaterialCount = Set.count state.VisibleMaterials
               readObservations =
                state.ObservedReads
                |> Set.toArray
                |> Array.map (fun read ->
                    box
                        {| workspace = read.Workspace
                           path = read.Path
                           digest = read.Digest
                           coverage =
                            match read.Coverage with
                            | Wanxiangshu.Requirement.Grounding.GroundingReadCoverage.CompleteFile -> "CompleteFile"
                            | Wanxiangshu.Requirement.Grounding.GroundingReadCoverage.PartialFile -> "PartialFile" |})
               visibleFromOrdinal = state.VisibleFromOrdinal |}

    let private tipDeliveryToJs (state: TipDeliveryProjectionState) : obj =
        box
            {| deliveredOccurrences = state.DeliveredOccurrences |> Set.toList |> List.toArray
               coveredTipNames = state.CoveredTipNames |> Set.toList |> List.toArray |}

    let private sessionStartedAtToJs (state: SessionStartedAtProjectionState) : obj =
        box {| startedAt = (SessionStartedAtProjection.startedAt state).ToString("o") |}

    let private delegatedToolEstimateToJs (state: DelegatedToolEstimateProjectionState) : obj =
        box
            {| remaining = state.Remaining
               countedToolCallCount = Set.count state.CountedToolCalls |}

    let private sessionToJs (session: SessionAgentProjection) : obj =
        box
            {| Companion = companionToJs session.Companion
               XTrace = xTraceToJs session.XTrace
               Blog = blogToJs session.Blog
               PrefixEpoch =
                match session.PrefixEpoch with
                | None -> null
                | Some epoch ->
                    box
                        {| EpochId = PrefixEpochId.value epoch.EpochId
                           Snapshot = prefixSnapshotToJs epoch.Snapshot |}
               Handles = session.Handles |> Option.map handlesToJs |> Option.toObj
               ProviderFailures = session.ProviderFailures |> Option.map providerFailuresToJs |> Option.toObj
               PromptAuthority = session.PromptAuthority |> Option.map promptAuthorityToJs |> Option.toObj
               Enforcement = session.Enforcement |> Option.map enforcementToJs |> Option.toObj
               BloggerCycles = session.BloggerCycles |> Option.map bloggerCyclesToJs |> Option.toObj
               Relay = session.Relay |> Option.map relayToJs |> Option.toObj
               Guidelines = session.Guidelines |> Option.map guidelinesToJs |> Option.toObj
               RequirementGrounding =
                session.RequirementGrounding
                |> Option.map requirementGroundingToJs
                |> Option.toObj
               TipDelivery = session.TipDelivery |> Option.map tipDeliveryToJs |> Option.toObj
               SessionStartedAt = session.SessionStartedAt |> Option.map sessionStartedAtToJs |> Option.toObj
               DelegatedToolEstimate =
                session.DelegatedToolEstimate
                |> Option.map delegatedToolEstimateToJs
                |> Option.toObj |}

    let private projectionToJs (projection: ProjectionSet) : obj =
        projection.AgentProjections.Sessions
        |> Map.toList
        |> List.map (fun (sessionId, session) -> SessionId.value sessionId, sessionToJs session)
        |> createObj

    /// obligation-ledger-004: the compression checkpoint windows as plain JS,
    /// so a test can observe what the checkpoint facts actually wrote without
    /// reading the durable projection's internal representation.
    let private todoCheckpointsToJs (projection: ProjectionSet) : obj =
        projection.AgentProjections.TodoCheckpoints
        |> Map.toList
        |> List.map (fun (sessionId, window) ->
            SessionId.value sessionId,
            box
                {| checkpoints =
                    window.Checkpoints
                    |> List.map (fun checkpoint -> box {| callId = ToolCallId.value checkpoint.ToolCallId |})
                    |> Array.ofList |})
        |> createObj

    let private okState projection =
        box
            {| sessions = projectionToJs projection
               todoCheckpoints = todoCheckpointsToJs projection |}

    let private rejectionToJs (rejection: FoldRejection) : obj =
        box
            {| Fact = rejection.Fact
               Reason = rejection.Reason |}

    let private foldEnvelopes (envelopes: Envelope array) : obj =
        envelopes
        |> Array.fold
            (fun result envelope ->
                result
                |> Result.bind (fun current -> Wanxiangshu.Composition.Durable.Fold.foldEnvelope current envelope))
            (Ok Wanxiangshu.Composition.Durable.Fold.empty)
        |> function
            | Ok projection ->
                box
                    {| ok = true
                       value = okState projection |}
            | Error rejection ->
                box
                    {| ok = false
                       error = rejectionToJs rejection |}

    let fold (envelopes: obj array) : obj =
        envelopes |> Array.map envelopeOfJs |> foldEnvelopes

    /// Same fold, but each envelope crosses the canonical line codec first.
    let replay (envelopes: obj array) : obj =
        let decoded =
            envelopes
            |> Array.map (fun value ->
                let envelope = envelopeOfJs value

                match Envelope.deserialize (Envelope.serialize envelope) with
                | Ok roundTripped -> roundTripped
                | Error error -> failwith $"ContextFoldSurface: envelope round trip failed: {error}")

        foldEnvelopes decoded
