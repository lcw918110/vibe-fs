namespace Wanxiangshu.Execution.Fission

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
    val empty: FissionProjectionState
    val tryGroup: groupId: string -> state: FissionProjectionState -> FissionGroupProjection option
    val tryActiveForOwner: owner: SessionId -> state: FissionProjectionState -> FissionGroupProjection option
    val tryLatestForOwner: owner: SessionId -> state: FissionProjectionState -> FissionGroupProjection option
    val tryOwnerOfLane: lane: SessionId -> state: FissionProjectionState -> SessionId option
    val tryMembershipOfLane: lane: SessionId -> state: FissionProjectionState -> (FissionGroupProjection * int) option

    val fold:
        state: FissionProjectionState ->
        fact: FissionFactCases ->
            Result<FissionProjectionState, FissionProjectionRejection>
