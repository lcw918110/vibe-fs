namespace Wanxiangshu.Context.Trace

open System.Threading.Tasks
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module XTraceMaterialization =
    val empty: ProviderProjection.ProviderSemanticProjection

    val tryHostToolResult:
        journal: AgentJournal ->
        messageId: TranscriptMessageAddress ->
        partId: HostToolPartId option ->
        callId: ToolCallId ->
        xTrace: XTraceProjectionState ->
            Task<Result<string option, string>>

    val currentProjection:
        journal: AgentJournal ->
        xTrace: XTraceProjectionState ->
            Task<Result<ProviderProjection.ProviderSemanticProjection, string>>

    val currentProjectionBetween:
        journal: AgentJournal ->
        range: XTraceRange ->
        xTrace: XTraceProjectionState ->
            Task<Result<ProviderProjection.ProviderSemanticProjection, string>>

    val materializeRange:
        journal: AgentJournal ->
        range: XTraceRange ->
        xTrace: XTraceProjectionState ->
            Task<Result<XTraceItem list, string>>

    val renderRange:
        journal: AgentJournal -> range: XTraceRange -> xTrace: XTraceProjectionState -> Task<Result<string, string>>
