namespace Wanxiangshu.Context.Companion

open Wanxiangshu.Foundation.Identity

/// Durable Companion lifecycle and XTrace facts.
type CompanionFactCases =
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

/// Durable Companion-context facts.
type ContextFactCases =
    | ViewCheckpointCommitted of
        {| SessionId: SessionId
           ToolCallId: ToolCallId |}
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
