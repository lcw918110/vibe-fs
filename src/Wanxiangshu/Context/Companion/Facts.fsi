namespace Wanxiangshu.Context.Companion

open Wanxiangshu.Foundation.Identity

type CompanionFactCases =
    | CompanionBloggerLinked of
        {| SessionId: SessionId
           BloggerSessionId: SessionId
           BloggerAgent: string |}
    | CompanionBloggerClosed of {| SessionId: SessionId |}
    | OpeningPromptCaptured of
        {| SessionId: SessionId
           AssignmentText: string
           AuthoritativeRequirements: string list
           ProviderRun: ProviderRunIdentity option |}
    | XTracePartAppended of
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
           HostToolPartId: HostToolPartId option |}
    | TerminalOutputCaptured of
        {| SessionId: SessionId
           TextRef: BlobRef
           TextDigest: BlobDigest
           ProviderRun: ProviderRunIdentity |}

type ContextFactCases =
    | BlogObservationCommitted of
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
           ObservedPrefixEpochId: PrefixEpochId |}
    | BlogObservationsSquashed of
        {| SessionId: SessionId
           BloggerSessionId: SessionId
           RequestId: BloggerRequestId
           PreviousFrameEpochId: FrameEpochId
           NextFrameEpochId: FrameEpochId
           CoveredFrameCount: int
           TextRef: BlobRef
           TextDigest: BlobDigest
           ProviderRun: ProviderRunIdentity |}
    | BloggerRequestMaterialized of
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
           PromptKey: PromptKey option |}
    | BloggerRequestAbandoned of
        {| RequestId: BloggerRequestId
           MainSessionId: SessionId
           BloggerSessionId: SessionId
           Reason: string |}
    | TodoCheckpointCommitted of
        {| SessionId: SessionId
           ToolCallId: ToolCallId |}
    | PrefixRebaseCommitted of
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
           SolvingProviderRun: ProviderRunIdentity |}
    | ContextReanchored of
        {| SessionId: SessionId
           PreviousEpochId: PrefixEpochId
           NextEpochId: PrefixEpochId
           ObservedCompactionRun: ProviderRunIdentity |}
    | TenureReanchored of
        {| SessionId: SessionId
           PreviousEpochId: PrefixEpochId
           NextEpochId: PrefixEpochId
           WorkId: string
           IncumbencyId: string |}
