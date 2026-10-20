namespace Wanxiangshu.Host

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation.Identity

module HostFact =
    val inline PairProgrammingGuidelineAnchored:
        payload:
            {| SessionId: SessionId
               Ordinal: int64
               CallId: ToolCallId
               MarkerText: string
               CallGap: TranscriptGap
               ResultGap: TranscriptGap
               ConcernPlacement: ConcernPlacementBatch option |} ->
            AgentFact


    val inline SessionStartedAtBound:
        payload:
            {| SessionId: SessionId
               StartedAt: System.DateTimeOffset |} ->
            AgentFact
