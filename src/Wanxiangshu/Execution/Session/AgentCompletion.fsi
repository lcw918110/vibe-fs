namespace Wanxiangshu.Execution.Session

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type AgentFailurePayload =
    { AgentId: string
      ChildSessionId: SessionId option
      RunId: string
      Role: Role option
      Code: string
      Message: string }

type AgentCompletionPayload =
    { AgentId: string
      ChildSessionId: SessionId option
      RunId: string
      Role: Role
      AuthorityRoot: AuthorityRootUserMessageId option
      ProviderRun: ProviderRunIdentity option
      View: string
      Directory: string option }

type AgentCompletionOutcome =
    | AgentCompleted of AgentCompletionPayload
    | AgentFailed of AgentFailurePayload
    | AgentAbandoned of agentId: string * reason: string

type PtyExit =
    { PtyId: string
      Outcome: string
      Closed: bool }

type PtyFailure =
    { PtyId: string
      Outcome: string
      Closed: bool
      Code: string
      Message: string }

type PtyAbort =
    { PtyId: string
      Outcome: string
      Closed: bool
      Code: string
      Message: string }

type AgentJoinItem =
    | AgentCompletedItem of AgentCompletionPayload
    | AgentFailedItem of AgentFailurePayload
    | AgentAbandonedItem of agentId: string * reason: string

type PtyJoinItem =
    | PtyExited of PtyExit
    | PtyFailed of PtyFailure
    | PtyAborted of PtyAbort

type JoinItem =
    | AgentItem of AgentJoinItem
    | PtyItem of PtyJoinItem

module AgentCompletion =
    val text: outcome: AgentCompletionOutcome -> string
    val agentId: outcome: AgentCompletionOutcome -> string
    val status: outcome: AgentCompletionOutcome -> string
    val isCompleted: outcome: AgentCompletionOutcome -> bool

    val completed:
        agentId: string ->
        childSessionId: SessionId ->
        runId: string ->
        role: Role ->
        authorityRoot: AuthorityRootUserMessageId ->
        providerRun: ProviderRunIdentity ->
        view: string ->
        directory: string option ->
            AgentCompletionOutcome

    val failed:
        agentId: string ->
        runId: string ->
        role: Role option ->
        childSessionId: SessionId option ->
        code: string ->
        message: string ->
            AgentCompletionOutcome

    val ofSimpleText: agentId: string -> runId: string -> role: Role -> text: string -> AgentCompletionOutcome
    val ofSimpleError: agentId: string -> runId: string -> role: Role -> message: string -> AgentCompletionOutcome
    val abandoned: agentId: string -> reason: string -> AgentCompletionOutcome

    val withRunIdentity:
        agentId: string -> runId: string -> role: Role -> outcome: AgentCompletionOutcome -> AgentCompletionOutcome

type RunCompletion =
    { RunId: string
      AgentName: string
      Role: Role
      Outcome: AgentCompletionOutcome
      CompletedAt: DateTimeOffset }

module PtyJoinItem =
    val ptyId: item: PtyJoinItem -> string

module JoinItem =
    val ofAgentRunCompletion: completion: RunCompletion -> JoinItem
    val ofPtyJoinItem: item: PtyJoinItem -> JoinItem
