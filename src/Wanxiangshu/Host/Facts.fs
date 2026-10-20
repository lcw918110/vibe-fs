namespace Wanxiangshu.Host

open Wanxiangshu.Foundation.Identity

type ConcernPlacementBatch =
    { AnnouncedGenerations: string list
      DeliveredMessages: string list }

[<RequireQualifiedAccess>]
type TipPresentation =
    | Full
    | IdentityOnly

type HostFactCases =
    | PairProgrammingGuidelineAnchored of
        {| SessionId: SessionId
           Ordinal: int64
           CallId: ToolCallId
           MarkerText: string
           CallGap: TranscriptGap
           ResultGap: TranscriptGap
           ConcernPlacement: ConcernPlacementBatch option |}
    | SessionStartedAtBound of
        {| SessionId: SessionId
           StartedAt: System.DateTimeOffset |}
