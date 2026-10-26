namespace Wanxiangshu.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Participant.ManagedAgents

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Collections.Generic
open Fable.Core
open System.Threading.Tasks
open Wanxiangshu.Change.Host
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.Journal

/// Owns every per-session tool runtime.
///
/// AGENT-007: Role comes from the Authority Root's CanonicalRole and nothing
/// else. The previous version consulted three sources in order — authority, a
/// per-session role cache, then the Host context's `agent` field — so a session
/// whose authority said Coder could still be gated as DevOps because a cache
/// entry or a message field said so. Two of the three are gone.
type ToolRuntimeScope =
    new:
        sessions: ISessionHostPort *
        waitObserver: IWaitObserver *
        rootWorkspace: IRootWorkspaceReader *
        journal: AgentJournal option *
        workspaceDirectory: string option *
        sessionParents: Dictionary<string, string> *
        currentPhysicalUserMessage: (string -> string option) *
        sessionDirectories: Dictionary<string, string> *
        onRunStarted: (SessionId -> Role -> string option -> unit) option *
        viewForSession: (string -> Task<string option>) option *
        snapshot: ISessionSnapshotPort option *
        cancelSignals: (SessionId seq -> unit) option *
        ?childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) *
        ?continueManagerLoop: (SessionId -> string -> Task<Result<unit, string>>) *
        ?eventPort: IEventObservationPort ->
            ToolRuntimeScope

    interface ISessionRuntimeOwner
    interface IDisposable

    member Sessions: ISessionHostPort
    member WaitObserver: IWaitObserver
    member RootWorkspace: IRootWorkspaceReader
    member Journal: AgentJournal option
    member Snapshot: ISessionSnapshotPort option
    member EventPort: IEventObservationPort option
    member WorkspaceDirectory: string option
    member ActiveProfileFor: sessionId: SessionId -> PromptAuthority.AuthorityExecutionProfile option
    member AttachPreviousScopeTransport: ask: (string -> obj -> string -> Task<Result<string, string>>) -> unit
    member AskPreviousScope: ctx: HostToolContext * questions: string -> Task<string>

    member AttachNegotiationTransport:
        ask: (string -> string -> string -> Task<Result<string, string>>) *
        steer: (string -> string -> string -> Task<Result<unit, string>>) ->
            unit

    member Negotiate: recipient: string * questions: string -> string
    member SendNegotiationSteer: recipient: string * answer: string * receipt: string -> Task<Result<unit, string>>
    member SendAdvisorSteer: recipient: string * answer: string * processId: string * isCurrent: (unit -> bool) -> Task<Result<unit, string>>
    member AttachQuestionNotifier: notify: (string -> Task<Result<unit, string>>) -> unit
    member IsWaitingForUser: sessionId: string -> bool
    /// LC-017: the pending question's exact fence for a waiting session.
    member WaitingQuestionFenceFor: sessionId: string -> (ProviderRunIdentity * ToolCallId) option
    member AskUser: ctx: HostToolContext * question: string -> Task<Result<unit, string>>

    /// Records the question with PromptAuthority, then parks it for the user.
    member ParkReviewQuestion: ctx: HostToolContext * question: string -> Task<Result<unit, string>>

    /// Host effect ports for one review. Ask, prompt, manager session, draft, and stop.
    member ReviewPorts: ctx: HostToolContext -> obj
    member TryAskForInitialEmptyManagerReview: ctx: HostToolContext * findingsEmpty: bool -> Task<Result<bool, string>>

    member InterruptForUserQuestion:
        sessionId: SessionId * providerRun: ProviderRunIdentity * callId: ToolCallId -> Task<Result<unit, string>>

    /// The delivery of this attempt is established (commit face). The physical
    /// interrupt is not issued here: the transform hook adjudicates it. The
    /// physical user message is the one bound at delivery time — passing it
    /// explicitly keeps a later new input from becoming this delivery's aim.
    member RegisterEstablishedDelivery:
        sessionId: SessionId *
        providerRun: ProviderRunIdentity *
        callId: ToolCallId *
        ?physicalUserMessageId: PhysicalUserMessageId ->
            unit

    /// The Host accepted this delivery's tool result (presentation face).
    member RecordDeliveryPresented: sessionId: SessionId * providerRun: ProviderRunIdentity * callId: ToolCallId -> unit

    member HasEstablishedDelivery: sessionId: string -> bool
    static member EstablishedDeliveryCallId: sessionId: string * physicalUserMessageId: string -> string option

    /// Run-started callback wired by plugin bootstrap for Host child reconciliation.
    member RunStarted: (SessionId -> Role -> string option -> unit)
    member ContinueManagerLoop: sessionId: SessionId * workspace: string -> Task<Result<unit, string>>

    member SessionParents: Dictionary<string, string>
    member CurrentPhysicalUserMessage: sessionId: string -> string option
    member ViewForSession: sessionId: string -> Task<string option>
    member DirectoryFor: sessionId: string -> string option
    member LogicalOwnerFor: sessionId: SessionId -> SessionId

    member RegisterPhysicalParent: sessionId: SessionId * parentId: SessionId option -> unit


    member RegisterDirectory: sessionId: string * path: string -> unit

    member RoleFor: ctx: HostToolContext -> Role option
    member EnsureRoleFor: ctx: HostToolContext -> Task<Role option>

    member ManagerCapabilityFactsFor: sessionId: string -> Task<ManagerCapabilityFacts>

    member LifecycleWorkFor:
        sessionId: string -> Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option>

    member TryFreezeRetirement: sessionId: string * incumbentId: string -> bool
    member UnfreezeRetirement: sessionId: string -> unit
    member IsRetirementFrozen: sessionId: string -> Task<bool>
    member RetirementBlockersFor: sessionId: string -> string list

    member IsRole: ctx: HostToolContext * expected: Role -> bool

    /// Wire PluginRuntimeScope.RequireCurrentProcessJoin (or test double).
    member AttachCurrentProcessJoin: fn: (SessionId -> Task<FamilyRecovery>) -> unit

    /// Wire current-process join recovery mode directly by string label ("ready", "waiting", or "blocked").
    member AttachCurrentProcessJoinMode: mode: string -> unit

    /// EXEC-017: share PluginRuntimeScope.JoinAttempts with JoinTool.
    member AttachJoinAttempts: registry: IJoinAttemptRegistry -> unit

    member JoinAttempts: IJoinAttemptRegistry

    /// Process-local join admission: join / JoinPublishedAvailable require FamilyReady. Missing attach → FamilyBlocked.
    member RequireCurrentProcessJoin: root: SessionId -> Task<FamilyRecovery>

    member RuntimeFor: ctx: HostToolContext -> Result<HostForkRuntime, string>

    member EnsureWorkDevOpsBound: parentSessionId: SessionId -> Task<unit>

    /// work-039: create a parentless execution container. The caller must
    /// durably bind the returned SessionId before sending its first prompt.
    member CreateExecutionSession: operationId: string * agent: string -> Task<Result<string, string>>

    member PromptExecutionSession:
        execSessionId: string * agent: string * prompt: string ->
            Task<Wanxiangshu.Mission.Planning.DeliverySpawnOutcome>

    member DiscardExecutionSession: execSessionId: string -> Task<Result<unit, string>>

    /// crash-reconciliation-018: process-local adoption for explicit /continue. The durable
    /// handle stays byte-for-byte as it was at the crash boundary; a later LLM
    /// fork reuse is the first action allowed to reopen it durably.
    member AdoptExistingChild: parentSessionId: SessionId * record: HandleRecord -> Result<unit, string>

    member OrchestratorHostFor: sessionId: string -> OrchestratorHost

    member RunOwnedWork: start: (unit -> Task) -> bool

    member DisposeExecutorRuntime: sessionId: string -> Task

    /// EXEC-016: live PTY on the parent fork runtime (not Executor runtime).
    member HasLivePty: sessionId: string -> bool

    member CancelSessionChildren: sessionId: string -> Task

    /// managed-session-lifecycle-017: an internal stop is a synchronous
    /// logical termination CE. Failed delivery is what completes the durable
    /// fork handle and wakes the parent; no future TurnAborted callback carries
    /// workflow continuation state.
    member TerminateSession: sessionId: string * reason: string -> Task<Result<unit, string>>

    member DisposeSession: sessionId: string -> Task

    member DisposeAsync: unit -> Task

    member Dispose: unit -> unit
