namespace Wanxiangshu.Execution.Fission

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type FissionFactCases =
    | FissionAdmitted of
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
    | FissionLaneMaterialized of
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           ProviderRun: ProviderRunIdentity
           ViewRef: BlobRef
           ViewDigest: BlobDigest |}
    | FissionCompletionCaptured of
        {| GroupId: string
           OwnerSessionId: SessionId
           CompletionId: string
           PayloadRef: BlobRef
           PayloadDigest: BlobDigest |}
    | FissionCompletionDelivered of
        {| GroupId: string
           OwnerSessionId: SessionId
           CompletionId: string
           LaneIndex: int |}
    | FissionExternalAffinityBound of
        {| GroupId: string
           OwnerSessionId: SessionId
           ExternalId: string
           LaneIndex: int |}
    | FissionTakeoverClaimed of
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           PromptKey: PromptKey
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}
    | FissionTakeoverStarted of
        {| GroupId: string
           OwnerSessionId: SessionId
           LaneIndex: int
           LaneSessionId: SessionId
           PhysicalUserMessageId: PhysicalUserMessageId
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}
    | FissionConverged of
        {| GroupId: string
           OwnerSessionId: SessionId
           TerminalLaneSessionId: SessionId
           TerminalProviderRun: ProviderRunIdentity
           AggregateViewRef: BlobRef
           AggregateViewDigest: BlobDigest |}
    | FissionFailed of
        {| GroupId: string
           OwnerSessionId: SessionId
           Reason: string |}
