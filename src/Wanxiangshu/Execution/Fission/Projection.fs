namespace Wanxiangshu.Execution.Fission

open Fable.Core
open Fable.Core.JsInterop
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

type FissionProjectionState = private { History: obj }

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
    [<Import("FissionHistory", "wanxiangli/fission-group")>]
    let private historyClass: obj = jsNative

    [<Import("FissionGroupLaw", "wanxiangli/fission-group")>]
    let private lawClass: obj = jsNative

    let private law = createNew lawClass ()
    let empty: FissionProjectionState = { History = createNew historyClass () }
    let private get<'T> (value: obj) (name: string) : 'T = unbox value?(name)

    let private optional convert (value: obj) =
        if isNull value then
            None
        else
            Some(convert (unbox<string> value))

    let private nullable convert value =
        value |> Option.map (convert >> box) |> Option.defaultValue null

    let private references (reference: string, digest: string) =
        BlobRef.create reference, BlobDigest.create digest

    let private referenceMap<'Key when 'Key: comparison> (value: obj) : Map<'Key, BlobRef * BlobDigest> =
        unbox<('Key * (string * string)) array> value
        |> Array.map (fun (key, pair) -> key, references pair)
        |> Map.ofArray

    let private takeoverFrom (value: obj) : FissionTakeoverProjection option =
        if isNull value then
            None
        else
            Some
                { LaneIndex = get value "laneIndex"
                  LaneSessionId = SessionId.create (get value "session")
                  PromptKey = optional PromptKey.create (get<obj> value "prompt")
                  PhysicalUserMessageId = optional PhysicalUserMessageId.create (get<obj> value "physical")
                  AggregateViewRef = BlobRef.create (get value "reference")
                  AggregateViewDigest = BlobDigest.create (get value "digest") }

    let private terminalFrom (value: obj) =
        match get<string> value "kind" with
        | "Open" -> FissionGroupTerminal.Open
        | "Failed" -> FissionGroupTerminal.Failed(get value "reason")
        | "Converged" ->
            FissionGroupTerminal.Converged(
                SessionId.create (get value "session"),
                ProviderRunIdentity.create (get value "run"),
                BlobRef.create (get value "reference"),
                BlobDigest.create (get value "digest")
            )
        | kind -> invalidOp ("wanxiangli/fission-group returned an unknown terminal: " + kind)

    let private groupFrom (value: obj) : FissionGroupProjection =
        { GroupId = get value "id"
          OwnerSessionId = SessionId.create (get value "owner")
          ParentSessionId = optional SessionId.create (get<obj> value "parent")
          OriginToolCallId = ToolCallId.create (get value "origin")
          LaneCount = get value "count"
          LaneSessions =
            get<(int * string) array> value "sessions"
            |> Array.map (fun (lane, session) -> lane, SessionId.create session)
            |> Map.ofArray
          LanePrompts = get<(int * string) array> value "prompts" |> Map.ofArray
          OwnerViewRef = BlobRef.create (get value "ownerReference")
          OwnerViewDigest = BlobDigest.create (get value "ownerDigest")
          PreFissionCompletionIds = get<string array> value "before" |> Set.ofArray
          LaneWork = referenceMap<int> (get<obj> value "work")
          LaneProviderRuns =
            get<(int * string) array> value "runs"
            |> Array.map (fun (lane, run) -> lane, ProviderRunIdentity.create run)
            |> Map.ofArray
          CapturedCompletions = referenceMap<string> (get<obj> value "captured")
          CompletionDeliveries =
            get<(string * int array) array> value "delivered"
            |> Array.map (fun (key, lanes) -> key, Set.ofArray lanes)
            |> Map.ofArray
          ExternalAffinities = get<(string * int) array> value "affinities" |> Map.ofArray
          Takeover = takeoverFrom (get<obj> value "takeover")
          Terminal = terminalFrom (get<obj> value "terminal") }

    let native (state: FissionProjectionState) = state.History
    let fromGroup (value: obj) = groupFrom value

    let private groupOption value =
        if isNull value then None else Some(groupFrom value)

    let tryGroup (groupId: string) state =
        state.History?group(groupId) |> groupOption

    let tryActiveForOwner owner state =
        state.History?activeForOwner(SessionId.value owner) |> groupOption

    let tryLatestForOwner owner state =
        state.History?latestForOwner(SessionId.value owner) |> groupOption

    let tryOwnerOfLane lane state =
        state.History?ownerOfLane(SessionId.value lane) |> optional SessionId.create

    let tryMembershipOfLane lane state =
        let value: obj = state.History?membership(SessionId.value lane)

        if isNull value then
            None
        else
            Some(groupFrom (get value "group"), get<int> value "laneIndex")

    let activeGroups state =
        unbox<obj array> (state.History?activeGroups())
        |> Array.map groupFrom
        |> Array.toList

    let convergenceReady (group: FissionGroupProjection) : bool =
        let progress =
            createObj
                [ "workedLanes" ==> (group.LaneWork |> Map.toArray |> Array.map (fst >> box))
                  "deliveries"
                  ==> (group.PreFissionCompletionIds
                       |> Set.toArray
                       |> Array.map (fun key ->
                           group.CompletionDeliveries
                           |> Map.tryFind key
                           |> Option.defaultValue Set.empty
                           |> Set.toArray
                           |> Array.map box)) ]

        law?convergenceReady (createObj [ "laneCount" ==> group.LaneCount; "progress" ==> progress ])

    let private rejection groupId owner laneIndex =
        function
        | "UnknownGroup" -> FissionProjectionRejection.UnknownGroup groupId
        | "ConflictingGroup" -> FissionProjectionRejection.ConflictingGroup groupId
        | "DuplicateOwnerActive" -> FissionProjectionRejection.DuplicateOwnerActive owner
        | "InvalidLane" -> FissionProjectionRejection.InvalidLane laneIndex
        | "LaneSessionMismatch" -> FissionProjectionRejection.LaneSessionMismatch laneIndex
        | "ConflictingLaneWork" -> FissionProjectionRejection.ConflictingLaneWork laneIndex
        | "ConflictingTakeover" -> FissionProjectionRejection.ConflictingTakeover groupId
        | "ConvergenceIncomplete" -> FissionProjectionRejection.ConvergenceIncomplete groupId
        | "TerminalAlreadySet" -> FissionProjectionRejection.TerminalAlreadySet groupId
        | name -> invalidOp ("wanxiangli/fission-group returned an unknown rejection: " + name)

    let private event kind groupId owner fields =
        createObj (
            [ "kind" ==> kind; "groupId" ==> groupId; "owner" ==> SessionId.value owner ]
            @ fields
        )

    let private takeover groupId owner lane session prompt physical reference digest =
        event
            "takeover"
            groupId
            owner
            [ "laneIndex" ==> lane
              "session" ==> SessionId.value session
              "prompt" ==> nullable PromptKey.value prompt
              "physical" ==> nullable PhysicalUserMessageId.value physical
              "reference" ==> BlobRef.value reference
              "digest" ==> BlobDigest.value digest ]

    let fold (state: FissionProjectionState) (fact: FissionFactCases) =
        let request, groupId, owner, lane =
            match fact with
            | FissionFactCases.FissionAdmitted p ->
                event
                    "admitted"
                    p.GroupId
                    p.OwnerSessionId
                    [ "parent" ==> nullable SessionId.value p.ParentSessionId
                      "origin" ==> ToolCallId.value p.OriginToolCallId
                      "count" ==> p.LaneCount
                      "sessions" ==> (p.LaneSessions |> List.map SessionId.value |> List.toArray)
                      "prompts" ==> List.toArray p.LanePrompts
                      "reference" ==> BlobRef.value p.OwnerViewRef
                      "digest" ==> BlobDigest.value p.OwnerViewDigest
                      "before" ==> List.toArray p.PreFissionCompletionIds ],
                p.GroupId,
                p.OwnerSessionId,
                0
            | FissionFactCases.FissionLaneMaterialized p ->
                event
                    "laneMaterialized"
                    p.GroupId
                    p.OwnerSessionId
                    [ "laneIndex" ==> p.LaneIndex
                      "session" ==> SessionId.value p.LaneSessionId
                      "run" ==> ProviderRunIdentity.value p.ProviderRun
                      "reference" ==> BlobRef.value p.ViewRef
                      "digest" ==> BlobDigest.value p.ViewDigest ],
                p.GroupId,
                p.OwnerSessionId,
                p.LaneIndex
            | FissionFactCases.FissionCompletionCaptured p ->
                event
                    "completionCaptured"
                    p.GroupId
                    p.OwnerSessionId
                    [ "completion" ==> p.CompletionId
                      "reference" ==> BlobRef.value p.PayloadRef
                      "digest" ==> BlobDigest.value p.PayloadDigest ],
                p.GroupId,
                p.OwnerSessionId,
                0
            | FissionFactCases.FissionCompletionDelivered p ->
                event
                    "completionDelivered"
                    p.GroupId
                    p.OwnerSessionId
                    [ "completion" ==> p.CompletionId; "laneIndex" ==> p.LaneIndex ],
                p.GroupId,
                p.OwnerSessionId,
                p.LaneIndex
            | FissionFactCases.FissionExternalAffinityBound p ->
                event
                    "externalAffinityBound"
                    p.GroupId
                    p.OwnerSessionId
                    [ "external" ==> p.ExternalId; "laneIndex" ==> p.LaneIndex ],
                p.GroupId,
                p.OwnerSessionId,
                p.LaneIndex
            | FissionFactCases.FissionTakeoverClaimed p ->
                takeover
                    p.GroupId
                    p.OwnerSessionId
                    p.LaneIndex
                    p.LaneSessionId
                    (Some p.PromptKey)
                    None
                    p.AggregateViewRef
                    p.AggregateViewDigest,
                p.GroupId,
                p.OwnerSessionId,
                p.LaneIndex
            | FissionFactCases.FissionTakeoverStarted p ->
                takeover
                    p.GroupId
                    p.OwnerSessionId
                    p.LaneIndex
                    p.LaneSessionId
                    None
                    (Some p.PhysicalUserMessageId)
                    p.AggregateViewRef
                    p.AggregateViewDigest,
                p.GroupId,
                p.OwnerSessionId,
                p.LaneIndex
            | FissionFactCases.FissionConverged p ->
                event
                    "converged"
                    p.GroupId
                    p.OwnerSessionId
                    [ "session" ==> SessionId.value p.TerminalLaneSessionId
                      "run" ==> ProviderRunIdentity.value p.TerminalProviderRun
                      "reference" ==> BlobRef.value p.AggregateViewRef
                      "digest" ==> BlobDigest.value p.AggregateViewDigest ],
                p.GroupId,
                p.OwnerSessionId,
                0
            | FissionFactCases.FissionFailed p ->
                event "failed" p.GroupId p.OwnerSessionId [ "reason" ==> p.Reason ], p.GroupId, p.OwnerSessionId, 0

        let result: obj = state.History?apply(request)

        let updatedState next =
            if obj.ReferenceEquals(next, state.History) then
                state
            else
                { History = next }

        if unbox<bool> result?ok then
            Ok(updatedState result?history)
        else
            Error(rejection groupId owner lane (unbox<string> result?rejection))
