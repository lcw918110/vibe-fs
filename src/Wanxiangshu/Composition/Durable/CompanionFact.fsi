namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Foundation.Identity

module CompanionFact =
    val inline CompanionBloggerLinked:
        payload:
            {| SessionId: SessionId
               BloggerSessionId: SessionId
               BloggerAgent: string |} ->
            AgentFact

    val inline CompanionBloggerClosed: payload: {| SessionId: SessionId |} -> AgentFact

    val inline OpeningPromptCaptured:
        payload:
            {| SessionId: SessionId
               AssignmentText: string
               AuthoritativeRequirements: string list
               ProviderRun: ProviderRunIdentity option |} ->
            AgentFact

    val inline XTracePartAppended:
        payload:
            {| SessionId: SessionId
               CursorSequence: int64
               Role: string
               Turn: int
               PartIndex: int
               Kind: string
               ToolName: string option
               TextRef: BlobRef
               TextDigest: BlobDigest
               Provenance: string
               ProviderRun: ProviderRunIdentity option
               ToolCallId: ToolCallId option
               HostToolPartId: HostToolPartId option |} ->
            AgentFact

    val inline TerminalOutputCaptured:
        payload:
            {| SessionId: SessionId
               TextRef: BlobRef
               TextDigest: BlobDigest
               ProviderRun: ProviderRunIdentity |} ->
            AgentFact

module ContextFact =
    val inline BlogObservationCommitted:
        payload:
            {| SessionId: SessionId
               BloggerSessionId: SessionId
               RequestId: BloggerRequestId
               FrameEpochId: FrameEpochId
               PreviousIngestedThroughSequence: int64
               NextIngestedThroughSequence: int64
               PreviousCoverableTurnCutoffExclusive: int
               NextCoverableTurnCutoffExclusive: int
               NextCoveredPrefixDigest: string
               TextRef: BlobRef
               TextDigest: BlobDigest
               ProviderRun: ProviderRunIdentity
               ToolCallIds: ToolCallId list
               TipRuleId: string
               FieldNameAtCommit: string option
               EvidenceRef: BlobRef option
               ObservedPrefixEpochId: PrefixEpochId |} ->
            AgentFact

    val inline BlogObservationsSquashed:
        payload:
            {| SessionId: SessionId
               BloggerSessionId: SessionId
               RequestId: BloggerRequestId
               PreviousFrameEpochId: FrameEpochId
               NextFrameEpochId: FrameEpochId
               CoveredFrameCount: int
               TextRef: BlobRef
               TextDigest: BlobDigest
               ProviderRun: ProviderRunIdentity |} ->
            AgentFact

    val inline BloggerRequestMaterialized:
        payload:
            {| RequestId: BloggerRequestId
               MainSessionId: SessionId
               BloggerSessionId: SessionId
               RequestKind: string
               ContextRef: BlobRef
               ContextDigest: BlobDigest
               ObservedPrefixEpochId: PrefixEpochId
               PreviousIngestedThroughSequence: int64
               NextIngestedThroughSequence: int64
               FrameEpochId: FrameEpochId
               SelectedFrameDigests: BlobDigest list
               PromptKey: PromptKey option |} ->
            AgentFact

    val inline BloggerRequestAbandoned:
        payload:
            {| RequestId: BloggerRequestId
               MainSessionId: SessionId
               BloggerSessionId: SessionId
               Reason: string |} ->
            AgentFact

    val inline TodoCheckpointCommitted:
        payload:
            {| SessionId: SessionId
               ToolCallId: ToolCallId |} ->
            AgentFact

    val inline PrefixRebaseCommitted:
        payload:
            {| SessionId: SessionId
               PreviousEpochId: PrefixEpochId
               NextEpochId: PrefixEpochId
               FrozenRecordPrefixRef: BlobRef
               FrozenRecordPrefixDigest: BlobDigest
               CutoffExclusive: int
               CoveredPrefixDigest: string
               SealRoot: string
               SyntheticMessageId: string
               ProbeId: string
               SolvingProviderRun: ProviderRunIdentity |} ->
            AgentFact

    val inline ContextReanchored:
        payload:
            {| SessionId: SessionId
               PreviousEpochId: PrefixEpochId
               NextEpochId: PrefixEpochId
               ObservedCompactionRun: ProviderRunIdentity |} ->
            AgentFact

    val inline TenureReanchored:
        payload:
            {| SessionId: SessionId
               PreviousEpochId: PrefixEpochId
               NextEpochId: PrefixEpochId
               WorkId: string
               IncumbencyId: string |} ->
            AgentFact
