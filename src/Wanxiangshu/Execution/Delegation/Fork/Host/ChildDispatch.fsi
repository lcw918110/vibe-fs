namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

module HostForkChildDispatch =
    val sendToExistingChild:
        gate: obj ->
        pendingRuns: Dictionary<string, PendingHostRun> ->
        journal: AgentJournal option ->
        parentId: SessionId ->
        sessions: ISessionHostPort ->
        childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) ->
        trackOwnedWork: ((unit -> Task) -> unit) ->
        runtime: ForkRuntime ->
        sendChildPrompt:
            (string
                -> SessionId
                -> Role
                -> PromptAuthority.IdentitySeed
                -> string
                -> (PhysicalUserMessageId -> unit)
                -> Task<HostForkRunLifecycle.AgentOwnerDispatchOutcome>) ->
        sendBusyNudge:
            (string -> SessionId -> PromptAuthority.AuthorityExecutionProfile -> string -> Task<Result<unit, string>>) ->
        onRunStarted: (SessionId -> Role -> unit) ->
        agentId: string ->
        childId: SessionId ->
        role: Role ->
        prompt: string ->
        agent: string ->
        enrichedPrompt: string option ->
            Task<Result<ForkResult, string>>

    val teardownChildren: sessions: ISessionHostPort -> childIds: SessionId list -> Task<Result<unit, string>>

    val cancelParent:
        cancelSignals: (SessionId seq -> unit) ->
        drainCancelledCallbacks: (string list -> string list -> Task<unit>) ->
        runtime: ForkRuntime ->
        ptyPort: PtyPort ->
        parentKey: string ->
        parentAbortToken: int ->
        gate: obj ->
        pendingRuns: Dictionary<string, PendingHostRun> ->
        children: Dictionary<string, SessionId> ->
        sessions: ISessionHostPort ->
        journal: AgentJournal option ->
        durableHandles: AgentLinkageProjection option ->
        parentId: SessionId ->
        settleAbandoned: (PendingHostRun -> unit) ->
        abandonedAt: DateTimeOffset ->
            Task<unit>
