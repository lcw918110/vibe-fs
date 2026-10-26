namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Persistence.Journal

module PromptJournalAdapter =
    val create: journal: AgentJournal -> IPromptJournal

module UserQuestionJournal =
    val tryFind:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
        callId: Wanxiangshu.Foundation.Identity.ToolCallId ->
            Wanxiangshu.Interaction.Authority.PromptAuthority.UserQuestion option

    val pending:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
            Wanxiangshu.Interaction.Authority.UserQuestionAskedPayload option

    val request:
        journal: AgentJournal ->
        question: Wanxiangshu.Interaction.Authority.UserQuestionAskedPayload ->
            System.Threading.Tasks.Task<Result<unit, string>>

    val suppressesTurn:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
        authorityRoot: Wanxiangshu.Foundation.Identity.AuthorityRootUserMessageId ->
        providerRun: Wanxiangshu.Foundation.Identity.ProviderRunIdentity ->
            bool

    val observeHumanInput:
        journal: AgentJournal ->
        sessionId: Wanxiangshu.Foundation.Identity.SessionId ->
        physicalId: Wanxiangshu.Foundation.Identity.PhysicalUserMessageId ->
            System.Threading.Tasks.Task<Result<unit, string>>
