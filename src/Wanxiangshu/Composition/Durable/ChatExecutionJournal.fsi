namespace Wanxiangshu.Composition.Durable

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity

open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
type ManagedChatSupersessionError =
    | MissingAccepted of ChatExecutionKey
    | PreProviderSettlementFailed of PreProviderSettlementError
    | ProviderSettlementFailed of ManagedChatProviderLifecycleError

module ManagedChatAcceptance =
    val accept:
        journal: AgentJournal ->
        key: ChatExecutionKey ->
        evidence: AcceptedChatExecutionEvidence ->
            Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>>

[<RequireQualifiedAccess>]
module ManagedChatProviderLifecycle =
    val internal forJournal: journal: AgentJournal -> ManagedChatProviderLifecyclePersistence

    val providerStarted:
        journal: AgentJournal ->
        key: ChatExecutionKey ->
        acceptedEvidence: AcceptedChatExecutionEvidence ->
        providerRun: ProviderRunIdentity ->
        requestKind: ProviderRequestKind ->
            Task<Result<ManagedChatProviderStartedWitness, ManagedChatProviderLifecycleError>>

    val terminal:
        journal: AgentJournal ->
        key: ChatExecutionKey ->
        startedEvidence: ProviderStartedEvidence ->
        disposition: ChatExecutionTerminalDisposition ->
            Task<Result<ManagedChatTerminalWitness, ManagedChatProviderLifecycleError>>

[<RequireQualifiedAccess>]
module PreProviderSettlement =
    val internal forJournal: journal: AgentJournal -> PreProviderSettlementPersistence

    val settle:
        journal: AgentJournal ->
        key: ChatExecutionKey ->
        evidence: AcceptedChatExecutionEvidence ->
        disposition: ChatExecutionTerminalDisposition ->
            Task<Result<PreProviderTerminalWitness, PreProviderSettlementError>>

[<RequireQualifiedAccess>]
module ManagedChatSupersession =
    val settle: journal: AgentJournal -> key: ChatExecutionKey -> Task<Result<unit, ManagedChatSupersessionError>>
