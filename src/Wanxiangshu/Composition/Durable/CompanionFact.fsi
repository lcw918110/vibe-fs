namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Foundation.Identity

module CompanionFact =
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
    val inline ViewCheckpointCommitted:
        payload:
            {| SessionId: SessionId
               ToolCallId: ToolCallId |} ->
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
