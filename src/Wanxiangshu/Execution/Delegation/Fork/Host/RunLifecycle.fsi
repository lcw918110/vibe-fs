namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

module HostForkRunLifecycle =
    [<RequireQualifiedAccess>]
    type AgentOwnerDispatchOutcome =
        | Accepted of physicalUserMessageId: PhysicalUserMessageId * authorityRoot: AuthorityRootUserMessageId
        | AcceptanceUncertain of string
        | Rejected of string

    val issueCurrentOwnerIdentitySeed:
        journal: AgentJournal option ->
        ownerSessionId: SessionId ->
        childAgent: string ->
            Result<PromptAuthority.IdentitySeed, string>

    val viewForOutcome:
        childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) ->
        run: PendingHostRun ->
        outcome: TerminalOutcome ->
            Task<string option>

    val sendAgentOwnerRootObserved:
        sessions: ISessionHostPort ->
        journal: AgentJournal option ->
        childId: SessionId ->
        identitySeed: PromptAuthority.IdentitySeed ->
        directory: string option ->
        prompt: string ->
        onAccepted: ContinuationAcceptanceObserver ->
            Task<AgentOwnerDispatchOutcome>

    val sendChildPrompt:
        sessions: ISessionHostPort ->
        _parentId: SessionId ->
        journal: AgentJournal option ->
        childId: SessionId ->
        identitySeed: PromptAuthority.IdentitySeed ->
        directory: string option ->
        prompt: string ->
        onAccepted: ContinuationAcceptanceObserver ->
            Task<AgentOwnerDispatchOutcome>

    val childPromptSender:
        sessions: ISessionHostPort ->
        parentId: SessionId ->
        journal: AgentJournal option ->
        directoryOf: (string -> string option) ->
        agentId: string ->
        childId: SessionId ->
        _role: Role ->
        identitySeed: PromptAuthority.IdentitySeed ->
        prompt: string ->
        onAccepted: ContinuationAcceptanceObserver ->
            Task<AgentOwnerDispatchOutcome>

    val complete:
        gate: obj ->
        pendingRuns: Dictionary<string, PendingHostRun> ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        run: PendingHostRun ->
        outcome: TerminalOutcome ->
        view: string option ->
            Task

    val installRun:
        gate: obj ->
        pendingRuns: Dictionary<string, PendingHostRun> ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) ->
        trackOwnedWork: ((unit -> Task) -> unit) ->
        agentId: string ->
        childId: SessionId ->
        role: Role ->
        authorityRoot: AuthorityRootUserMessageId ->
            PendingHostRun

    val settleParentCancelled:
        gate: obj -> pendingRuns: Dictionary<string, PendingHostRun> -> run: PendingHostRun -> unit

    val failRun:
        gate: obj ->
        pendingRuns: Dictionary<string, PendingHostRun> ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        run: PendingHostRun ->
        error: string ->
            Task

    val markReady:
        _gate: obj ->
        _pendingRuns: Dictionary<string, PendingHostRun> ->
        _journal: AgentJournal option ->
        _parentId: SessionId ->
        _sessions: ISessionHostPort ->
        _run: PendingHostRun ->
        _view: string option ->
            unit
