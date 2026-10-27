namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Persistence.Journal

module HostForkRunLifecycle =
    /// The delivered view of one terminal, resolved on demand.
    type ViewResolution = unit -> Task<string option>

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

    val publishDeliveredReturn:
        journal: AgentJournal ->
        port: IEventObservationPort ->
        sessionId: SessionId ->
        providerRun: ProviderRunIdentity ->
        toolCallId: ToolCallId ->
            unit

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

    val internal tryGetRun: store: obj -> agentId: string -> PendingHostRun option
    val internal containsRun: store: obj -> agentId: string -> bool
    val internal pendingRunCount: store: obj -> int
    val internal allRuns: store: obj -> PendingHostRun list
    val internal allRunsAsDictionary: store: obj -> Dictionary<string, PendingHostRun>
    val internal removeRunByAgentId: store: obj -> agentId: string -> unit
    val internal drainAllRuns: store: obj -> {| Run: PendingHostRun; Subscription: IDisposable option |} list
    val internal drainCancelledRuns: store: obj -> agentIds: string list -> IDisposable list

    val complete:
        gate: obj ->
        pendingRuns: obj ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        run: PendingHostRun ->
        outcome: TerminalOutcome ->
        view: ViewResolution option ->
            Task

    val installRun:
        gate: obj ->
        pendingRuns: obj ->
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
        gate: obj -> pendingRuns: obj -> run: PendingHostRun -> unit

    val failRun:
        gate: obj ->
        pendingRuns: obj ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        run: PendingHostRun ->
        error: string ->
            Task

    val markReady:
        _gate: obj ->
        _pendingRuns: obj ->
        _journal: AgentJournal option ->
        _parentId: SessionId ->
        _sessions: ISessionHostPort ->
        _run: PendingHostRun ->
        _view: string option ->
            unit
