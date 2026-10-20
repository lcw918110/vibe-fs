namespace Wanxiangshu.Context.Companion

open System
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Trace
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.OpenCode.Host.PairProgramming
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

    let private optionalText (value: obj) =
        if isNull value then None else Some(text value)

    let private optionalBlobRef (value: obj) =
        if isNull value then None else Some(blobRef value)

    let private optionalBlobDigest (value: obj) =
        if isNull value then None else Some(blobDigest value)

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
        | "ViewCheckpointCommitted" ->
            AgentFact.Context(
                ContextFactCases.ViewCheckpointCommitted
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

    let private companionToJs (companion: CompanionProjection option) =
        match companion with
        | None -> null
        | Some _ -> box {| sessionPresent = true |}

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
    // (SessionStartedAtProjection.startedAt); no private representation is
    // destructured outside its owner domain.

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
               acceptedContinuationCount = Map.count state.AcceptedContinuationIds |}

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
               PrefixEpoch =
                match session.PrefixEpoch with
                | None -> null
                | Some epoch -> box {| EpochId = PrefixEpochId.value epoch.EpochId |}
               Handles = session.Handles |> Option.map handlesToJs |> Option.toObj
               ProviderFailures = session.ProviderFailures |> Option.map providerFailuresToJs |> Option.toObj
               PromptAuthority = session.PromptAuthority |> Option.map promptAuthorityToJs |> Option.toObj
               Guidelines = session.Guidelines |> Option.map guidelinesToJs |> Option.toObj
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
    let private viewCheckpointsToJs (projection: ProjectionSet) : obj =
        projection.AgentProjections.ViewCheckpoints
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
               viewCheckpoints = viewCheckpointsToJs projection |}

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
