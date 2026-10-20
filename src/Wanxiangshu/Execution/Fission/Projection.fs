namespace Wanxiangshu.Execution.Fission

open Wanxiangshu.Composition.Durable

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type FissionGroupTerminal =
    | Open
    | Converged of
        terminalLane: SessionId *
        providerRun: ProviderRunIdentity *
        aggregateRef: BlobRef *
        aggregateDigest: BlobDigest
    | Failed of reason: string

[<CLIMutable>]
type FissionTakeoverProjection =
    { LaneIndex: int
      LaneSessionId: SessionId
      PromptKey: PromptKey option
      PhysicalUserMessageId: PhysicalUserMessageId option
      AggregateViewRef: BlobRef
      AggregateViewDigest: BlobDigest }

[<CLIMutable>]
type FissionGroupProjection =
    { GroupId: string
      OwnerSessionId: SessionId
      ParentSessionId: SessionId option
      OriginToolCallId: ToolCallId
      LaneCount: int
      LaneSessions: Map<int, SessionId>
      LanePrompts: Map<int, string>
      OwnerViewRef: BlobRef
      OwnerViewDigest: BlobDigest
      PreFissionCompletionIds: Set<string>
      LaneWork: Map<int, BlobRef * BlobDigest>
      LaneProviderRuns: Map<int, ProviderRunIdentity>
      CapturedCompletions: Map<string, BlobRef * BlobDigest>
      CompletionDeliveries: Map<string, Set<int>>
      ExternalAffinities: Map<string, int>
      Takeover: FissionTakeoverProjection option
      Terminal: FissionGroupTerminal }

[<CLIMutable>]
type FissionProjectionState =
    { Groups: Map<string, FissionGroupProjection>
      ActiveByOwner: Map<SessionId, string>
      LatestByOwner: Map<SessionId, string>
      LaneOwner: Map<SessionId, SessionId>
      LaneMembership: Map<SessionId, string * int> }

[<RequireQualifiedAccess>]
type FissionProjectionRejection =
    | DuplicateOwnerActive of owner: SessionId
    | ConflictingGroup of groupId: string
    | UnknownGroup of groupId: string
    | InvalidLane of laneIndex: int
    | LaneSessionMismatch of laneIndex: int
    | ConflictingLaneWork of laneIndex: int
    | ConflictingTakeover of groupId: string
    | ConvergenceIncomplete of groupId: string
    | TerminalAlreadySet of groupId: string

module FissionProjection =

    type private AdmittedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           ParentSessionId: SessionId option
           OriginToolCallId: ToolCallId
           LaneCount: int
           LaneSessions: SessionId list
           LanePrompts: string list
           OwnerViewRef: BlobRef
           OwnerViewDigest: BlobDigest
           PreFissionCompletionIds: string list |}

    type private LaneMaterializedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           ProviderRun: ProviderRunIdentity
           ViewRef: BlobRef
           ViewDigest: BlobDigest |}

    type private CompletionCapturedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           CompletionId: string
           PayloadRef: BlobRef
           PayloadDigest: BlobDigest |}

    type private CompletionDeliveredPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           CompletionId: string
           LaneIndex: int |}

    type private ExternalAffinityPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           ExternalId: string
           LaneIndex: int |}

    type private TakeoverStartedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           PhysicalUserMessageId: PhysicalUserMessageId
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}

    type private TakeoverClaimedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           PromptKey: PromptKey
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}

    type private ConvergedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           TerminalLaneSessionId: SessionId
           TerminalProviderRun: ProviderRunIdentity
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}

    type private FailedPayload =
        {| GroupId: string
           OwnerSessionId: SessionId
           Reason: string |}

    let empty =
        { Groups = Map.empty
          ActiveByOwner = Map.empty
          LatestByOwner = Map.empty
          LaneOwner = Map.empty
          LaneMembership = Map.empty }

    let tryGroup groupId state = Map.tryFind groupId state.Groups

    let tryActiveForOwner owner state =
        state.ActiveByOwner
        |> Map.tryFind owner
        |> Option.bind (fun groupId -> Map.tryFind groupId state.Groups)

    let tryLatestForOwner owner state =
        state.LatestByOwner
        |> Map.tryFind owner
        |> Option.bind (fun groupId -> Map.tryFind groupId state.Groups)

    let tryOwnerOfLane lane state = Map.tryFind lane state.LaneOwner

    let tryMembershipOfLane lane state =
        Map.tryFind lane state.LaneMembership
        |> Option.bind (fun (groupId, laneIndex) ->
            Map.tryFind groupId state.Groups |> Option.map (fun group -> group, laneIndex))

    let private allBroadcastsDelivered group =
        group.PreFissionCompletionIds
        |> Set.forall (fun completionId ->
            let delivered =
                group.CompletionDeliveries
                |> Map.tryFind completionId
                |> Option.defaultValue Set.empty

            delivered = Set.ofList [ 0 .. group.LaneCount - 1 ])

    let private convergenceReady group =
        group.LaneWork.Count = group.LaneCount
        && (group.LaneWork
            |> Map.forall (fun laneIndex _ -> laneIndex >= 0 && laneIndex < group.LaneCount))
        && allBroadcastsDelivered group

    let private replaceGroup (state: FissionProjectionState) groupId (group: FissionGroupProjection) =
        { state with
            Groups = Map.add groupId group state.Groups }

    let private admissionMatches (existing: FissionGroupProjection) (payload: AdmittedPayload) =
        existing.OwnerSessionId = payload.OwnerSessionId
        && existing.ParentSessionId = payload.ParentSessionId
        && existing.OriginToolCallId = payload.OriginToolCallId
        && existing.LaneCount = payload.LaneCount
        && (existing.LaneSessions |> Map.toList |> List.map snd) = payload.LaneSessions
        && (existing.LanePrompts |> Map.toList |> List.map snd) = payload.LanePrompts
        && existing.OwnerViewRef = payload.OwnerViewRef
        && existing.OwnerViewDigest = payload.OwnerViewDigest
        && existing.PreFissionCompletionIds = Set.ofList payload.PreFissionCompletionIds

    let private admissionPayloadValid (payload: AdmittedPayload) =
        payload.LaneCount >= 2
        && List.length payload.LaneSessions = payload.LaneCount
        && List.length payload.LanePrompts = payload.LaneCount

    let private admitFresh (state: FissionProjectionState) (payload: AdmittedPayload) =
        let laneSessions =
            payload.LaneSessions |> List.mapi (fun index lane -> index, lane) |> Map.ofList

        let group =
            { GroupId = payload.GroupId
              OwnerSessionId = payload.OwnerSessionId
              ParentSessionId = payload.ParentSessionId
              OriginToolCallId = payload.OriginToolCallId
              LaneCount = payload.LaneCount
              LaneSessions = laneSessions
              LanePrompts =
                payload.LanePrompts
                |> List.mapi (fun index prompt -> index, prompt)
                |> Map.ofList
              OwnerViewRef = payload.OwnerViewRef
              OwnerViewDigest = payload.OwnerViewDigest
              PreFissionCompletionIds = Set.ofList payload.PreFissionCompletionIds
              LaneWork = Map.empty
              LaneProviderRuns = Map.empty
              CapturedCompletions = Map.empty
              CompletionDeliveries = Map.empty
              ExternalAffinities = Map.empty
              Takeover = None
              Terminal = FissionGroupTerminal.Open }

        { state with
            Groups = Map.add payload.GroupId group state.Groups
            ActiveByOwner = Map.add payload.OwnerSessionId payload.GroupId state.ActiveByOwner
            LatestByOwner = Map.add payload.OwnerSessionId payload.GroupId state.LatestByOwner
            LaneOwner =
                laneSessions
                |> Map.fold (fun acc _ lane -> Map.add lane payload.OwnerSessionId acc) state.LaneOwner
            LaneMembership =
                laneSessions
                |> Map.fold
                    (fun acc laneIndex lane -> Map.add lane (payload.GroupId, laneIndex) acc)
                    state.LaneMembership }

    let private foldAdmitted (state: FissionProjectionState) (payload: AdmittedPayload) =
        match Map.tryFind payload.GroupId state.Groups, Map.tryFind payload.OwnerSessionId state.ActiveByOwner with
        | Some existing, _ when admissionMatches existing payload -> Ok state
        | Some _, _ -> Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | None, _ when not (admissionPayloadValid payload) ->
            Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | None, Some _ -> Error(FissionProjectionRejection.DuplicateOwnerActive payload.OwnerSessionId)
        | None, None -> Ok(admitFresh state payload)

    let private decideLaneMaterialization
        (state: FissionProjectionState)
        (payload: LaneMaterializedPayload)
        (group: FissionGroupProjection)
        =
        match
            Map.tryFind payload.LaneIndex group.LaneSessions,
            Map.tryFind payload.LaneIndex group.LaneWork,
            Map.tryFind payload.LaneIndex group.LaneProviderRuns
        with
        | Some expected, _, _ when expected <> payload.LaneSessionId ->
            Error(FissionProjectionRejection.LaneSessionMismatch payload.LaneIndex)
        | None, _, _ -> Error(FissionProjectionRejection.LaneSessionMismatch payload.LaneIndex)
        | Some _, Some existing, providerRun when
            existing = (payload.ViewRef, payload.ViewDigest)
            && providerRun = Some payload.ProviderRun
            ->
            Ok state
        | Some _, Some _, _ -> Error(FissionProjectionRejection.ConflictingLaneWork payload.LaneIndex)
        | Some _, None, _ ->
            let next =
                { group with
                    LaneWork = Map.add payload.LaneIndex (payload.ViewRef, payload.ViewDigest) group.LaneWork
                    LaneProviderRuns = Map.add payload.LaneIndex payload.ProviderRun group.LaneProviderRuns }

            Ok(replaceGroup state payload.GroupId next)

    let private foldLaneMaterialized (state: FissionProjectionState) (payload: LaneMaterializedPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group when payload.LaneIndex < 0 || payload.LaneIndex >= group.LaneCount ->
            Error(FissionProjectionRejection.InvalidLane payload.LaneIndex)
        | Some group -> decideLaneMaterialization state payload group

    let private decideCompletionCapture
        (state: FissionProjectionState)
        (payload: CompletionCapturedPayload)
        (group: FissionGroupProjection)
        =
        match Map.tryFind payload.CompletionId group.CapturedCompletions with
        | Some existing when existing = (payload.PayloadRef, payload.PayloadDigest) -> Ok state
        | Some _ -> Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | None ->
            let next =
                { group with
                    CapturedCompletions =
                        Map.add
                            payload.CompletionId
                            (payload.PayloadRef, payload.PayloadDigest)
                            group.CapturedCompletions }

            Ok(replaceGroup state payload.GroupId next)

    let private foldCompletionCaptured (state: FissionProjectionState) (payload: CompletionCapturedPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group when not (Set.contains payload.CompletionId group.PreFissionCompletionIds) ->
            Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | Some group -> decideCompletionCapture state payload group

    let private foldCompletionDelivered (state: FissionProjectionState) (payload: CompletionDeliveredPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group when payload.LaneIndex < 0 || payload.LaneIndex >= group.LaneCount ->
            Error(FissionProjectionRejection.InvalidLane payload.LaneIndex)
        | Some group when not (Map.containsKey payload.CompletionId group.CapturedCompletions) ->
            Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | Some group ->
            let current =
                group.CompletionDeliveries
                |> Map.tryFind payload.CompletionId
                |> Option.defaultValue Set.empty

            let next =
                { group with
                    CompletionDeliveries =
                        Map.add payload.CompletionId (Set.add payload.LaneIndex current) group.CompletionDeliveries }

            Ok(replaceGroup state payload.GroupId next)

    let private decideExternalAffinity
        (state: FissionProjectionState)
        (payload: ExternalAffinityPayload)
        (group: FissionGroupProjection)
        =
        match Map.tryFind payload.ExternalId group.ExternalAffinities with
        | Some existing when existing = payload.LaneIndex -> Ok state
        | Some _ -> Error(FissionProjectionRejection.ConflictingGroup payload.GroupId)
        | None ->
            let next =
                { group with
                    ExternalAffinities = Map.add payload.ExternalId payload.LaneIndex group.ExternalAffinities }

            Ok(replaceGroup state payload.GroupId next)

    let private foldExternalAffinityBound (state: FissionProjectionState) (payload: ExternalAffinityPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group when payload.LaneIndex < 0 || payload.LaneIndex >= group.LaneCount ->
            Error(FissionProjectionRejection.InvalidLane payload.LaneIndex)
        | Some group -> decideExternalAffinity state payload group

    let private takeoverMatches
        (existing: FissionTakeoverProjection)
        laneIndex
        laneSessionId
        promptKey
        physicalUserMessageId
        aggregateViewRef
        aggregateViewDigest
        =
        existing.LaneIndex = laneIndex
        && existing.LaneSessionId = laneSessionId
        && existing.PromptKey = promptKey
        && existing.PhysicalUserMessageId = physicalUserMessageId
        && existing.AggregateViewRef = aggregateViewRef
        && existing.AggregateViewDigest = aggregateViewDigest

    let private foldTakeover
        (state: FissionProjectionState)
        groupId
        laneIndex
        laneSessionId
        promptKey
        physicalUserMessageId
        aggregateViewRef
        aggregateViewDigest
        =
        let takeover: FissionTakeoverProjection =
            { LaneIndex = laneIndex
              LaneSessionId = laneSessionId
              PromptKey = promptKey
              PhysicalUserMessageId = physicalUserMessageId
              AggregateViewRef = aggregateViewRef
              AggregateViewDigest = aggregateViewDigest }

        match Map.tryFind groupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup groupId)
        | Some group when laneIndex < 0 || laneIndex >= group.LaneCount ->
            Error(FissionProjectionRejection.InvalidLane laneIndex)
        | Some group when Map.tryFind laneIndex group.LaneSessions <> Some laneSessionId ->
            Error(FissionProjectionRejection.LaneSessionMismatch laneIndex)
        | Some group when not (convergenceReady group) ->
            Error(FissionProjectionRejection.ConvergenceIncomplete groupId)
        | Some { Takeover = Some existing } when
            takeoverMatches
                existing
                laneIndex
                laneSessionId
                promptKey
                physicalUserMessageId
                aggregateViewRef
                aggregateViewDigest
            ->
            Ok state
        | Some { Takeover = Some _ } -> Error(FissionProjectionRejection.ConflictingTakeover groupId)
        | Some group -> Ok(replaceGroup state groupId { group with Takeover = Some takeover })

    let private foldTakeoverClaimed (state: FissionProjectionState) (payload: TakeoverClaimedPayload) =
        foldTakeover
            state
            payload.GroupId
            payload.LaneIndex
            payload.LaneSessionId
            (Some payload.PromptKey)
            None
            payload.AggregateViewRef
            payload.AggregateViewDigest

    let private foldTakeoverStarted (state: FissionProjectionState) (payload: TakeoverStartedPayload) =
        foldTakeover
            state
            payload.GroupId
            payload.LaneIndex
            payload.LaneSessionId
            None
            (Some payload.PhysicalUserMessageId)
            payload.AggregateViewRef
            payload.AggregateViewDigest

    let private decideConverged
        (state: FissionProjectionState)
        (payload: ConvergedPayload)
        (group: FissionGroupProjection)
        =
        match group.Terminal, group.Takeover with
        | FissionGroupTerminal.Converged(existingLane, existingRun, existingRef, existingDigest), _ when
            existingLane = payload.TerminalLaneSessionId
            && existingRun = payload.TerminalProviderRun
            && existingRef = payload.AggregateViewRef
            && existingDigest = payload.AggregateViewDigest
            ->
            Ok state
        | FissionGroupTerminal.Converged _, _
        | FissionGroupTerminal.Failed _, _ -> Error(FissionProjectionRejection.TerminalAlreadySet payload.GroupId)
        | FissionGroupTerminal.Open, _ when not (convergenceReady group) ->
            Error(FissionProjectionRejection.ConvergenceIncomplete payload.GroupId)
        | FissionGroupTerminal.Open, Some takeover when takeover.LaneSessionId <> payload.TerminalLaneSessionId ->
            Error(FissionProjectionRejection.ConflictingTakeover payload.GroupId)
        | FissionGroupTerminal.Open, _ ->
            let next =
                { group with
                    Terminal =
                        FissionGroupTerminal.Converged(
                            payload.TerminalLaneSessionId,
                            payload.TerminalProviderRun,
                            payload.AggregateViewRef,
                            payload.AggregateViewDigest
                        ) }

            Ok
                { state with
                    Groups = Map.add payload.GroupId next state.Groups
                    ActiveByOwner = Map.remove group.OwnerSessionId state.ActiveByOwner }

    let private foldConverged (state: FissionProjectionState) (payload: ConvergedPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group -> decideConverged state payload group

    let private decideFailed (state: FissionProjectionState) (payload: FailedPayload) (group: FissionGroupProjection) =
        match group.Terminal with
        | FissionGroupTerminal.Failed existing when existing = payload.Reason -> Ok state
        | FissionGroupTerminal.Open ->
            let next =
                { group with
                    Terminal = FissionGroupTerminal.Failed payload.Reason }

            Ok
                { state with
                    Groups = Map.add payload.GroupId next state.Groups
                    ActiveByOwner = Map.remove group.OwnerSessionId state.ActiveByOwner }
        | _ -> Error(FissionProjectionRejection.TerminalAlreadySet payload.GroupId)

    let private foldFailed (state: FissionProjectionState) (payload: FailedPayload) =
        match Map.tryFind payload.GroupId state.Groups with
        | None -> Error(FissionProjectionRejection.UnknownGroup payload.GroupId)
        | Some group -> decideFailed state payload group

    let fold (state: FissionProjectionState) (fact: FissionFactCases) =
        match fact with
        | FissionFactCases.FissionAdmitted payload -> foldAdmitted state payload
        | FissionFactCases.FissionLaneMaterialized payload -> foldLaneMaterialized state payload
        | FissionFactCases.FissionCompletionCaptured payload -> foldCompletionCaptured state payload
        | FissionFactCases.FissionCompletionDelivered payload -> foldCompletionDelivered state payload
        | FissionFactCases.FissionExternalAffinityBound payload -> foldExternalAffinityBound state payload
        | FissionFactCases.FissionTakeoverClaimed payload -> foldTakeoverClaimed state payload
        | FissionFactCases.FissionTakeoverStarted payload -> foldTakeoverStarted state payload
        | FissionFactCases.FissionConverged payload -> foldConverged state payload
        | FissionFactCases.FissionFailed payload -> foldFailed state payload
