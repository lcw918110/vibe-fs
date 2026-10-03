namespace Wanxiangshu.Execution.Delegation.Handle

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type HandleConsumeRejection =
    | AlreadyRetired
    | NotJoinable of HandleTransitionRejection
    | AppendFailed of string

module HandleController =
    val agentHandle: agentId: string -> HandleId

    val linkNamed:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        agentId: string ->
        childSessionId: SessionId ->
        targetAgent: string ->
        byname: string ->
        role: Role ->
        ownership: HandleOwnership ->
            Task<Result<unit, string>>

    val link:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        agentId: string ->
        childSessionId: SessionId ->
        targetAgent: string ->
        role: Role ->
        ownership: HandleOwnership ->
            Task<Result<unit, string>>

    val settleExemptedWork:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        admitted: AdmittedWork ->
        reason: HandleAbandonReason ->
            Task<Result<unit, string>>

    val recordWorkCompletion:
        AgentJournalPort option -> SessionId -> AdmittedWork -> JoinableCompletion -> Task<Result<unit, string>>

    val consumeWork: AgentJournalPort -> SessionId -> HandleRecord -> Task<Result<HandleRecord, HandleConsumeRejection>>

    val recordCompletion:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        completion: JoinableCompletion ->
            Task<Result<unit, string>>

    val recordAbandon:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        agentId: string ->
        reason: HandleAbandonReason ->
        abandonedAt: DateTimeOffset ->
            Task<Result<unit, string>>

    val retire: journal: AgentJournalPort option -> parentId: SessionId -> agentId: string -> Task<Result<unit, string>>

    val consume:
        journal: AgentJournalPort ->
        parentId: SessionId ->
        handle: HandleId ->
            Task<Result<HandleRecord, HandleConsumeRejection>>

    val cancelChildren:
        journal: AgentJournalPort option ->
        parentId: SessionId ->
        agentIds: string list ->
        abandonedAt: DateTimeOffset ->
            Task<Result<unit, string>>
