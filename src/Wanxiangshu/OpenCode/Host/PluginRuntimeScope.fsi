namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Explicit lifetime root for one plugin instance. Collections here are either
/// physical resources, display caches, or bounded per-call deduplication.
type PluginRuntimeScope =
    new:
        journal: AgentJournal option *
        isModelLeaseExternallyOwned: (SessionId -> bool) *
        jsLifecyclePersistence: Wanxiangshu.Repository.Programming.Js.IJsLifecyclePersistence option ->
            PluginRuntimeScope

    interface IDisposable

    member Journal: AgentJournal option

    member AttachDurabilityActivation: activate: (unit -> unit) -> unit

    member ActivateDurability: unit -> unit

    /// crash-reconciliation-020: load-phase recovery work whose completion the
    /// next handoff must observe; admission awaits these before dispatching.
    member TrackDurabilityActivation: work: Task -> unit

    member AwaitDurabilityActivation: unit -> Task

    /// crash-reconciliation-018: load-phase normalization owes one restart
    /// status guidance to the next real user instruction in this process.
    member MarkRestartGuidancePending: unit -> unit

    member RestartGuidancePending: bool

    member MarkRestartGuidanceDelivered: unit -> unit


    /// Composition-of-owners: per-instance session registries live in their own scope.
    member Sessions: PluginSessionScope

    /// Composition-of-owners: family recovery + attempt planning live in their own scope.
    member Recovery: PluginRecoveryScope

    member AttachSyncDelegateRuntime: runtime: SyncDelegateRuntime -> unit

    member AttachChatRecoveryRuntime: runtime: SessionRecoveryHost -> unit

    member SignalChatRecovery: event: ChatExecutionRecoveryLifecycleEvent -> Task

    member SignalChatRecoverySession:
        sessionId: SessionId -> eventOf: (ChatExecutionKey -> ChatExecutionRecoveryLifecycleEvent) -> Task

    member DrainChatRecovery: sessionId: SessionId -> Task

    member SyncDelegateRuntime: SyncDelegateRuntime option

    member AttachMessageVisibility: hub: MessageVisibilityHub -> unit

    /// None until the signal stack wires the hub; the catch-up re-read then
    /// falls back to its bounded immediate form.
    member MessageVisibility: MessageVisibilityHub option

    /// Current-process join admission only; no cross-process tool recovery.
    member RequireCurrentProcessJoin: root: SessionId -> Task<FamilyRecovery>

    member PublishManualChatIntervention: request: ManualInterventionRequest -> unit

    /// Exact manual intervention observations held process-locally.
    member ManualChatInterventions: unit -> ManualInterventionRequest[]

    /// Revokes the exact manual intervention entry for a terminally settled key.
    member RevokeManualIntervention: key: ChatExecutionKey -> unit

    member TryBindAttemptPlan:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
            AttemptPlan option

    member BindAttemptPlan:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
        ordinary: PendingAttemptPlan ->
            AttemptPlan

    member TryAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    member ConsumeAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    member AttachToolRuntime: owner: ISessionRuntimeOwner -> unit

    member TrackSubscription: value: IDisposable option -> unit

    member TrackReconcileShutdown: stopAndDrain: (unit -> Task) -> unit

    member RunBackground: start: (unit -> Task) -> unit

    /// RunBackground variant returning the observed task for await tracking.
    member RunBackgroundTracked: start: (unit -> Task) -> Task

    member RunOwnedWork: start: (unit -> Task) -> Task

    member AttachSharedTerminal: key: string option * port: ISessionTerminalSource option -> unit

    member DisposeExecutorRuntime: sessionId: string -> Task

    /// EXEC-016: live PTY probe for DevOps join guard.
    member HasLivePty: sessionId: string -> bool
    member RetirementBlockersFor: sessionId: string -> string list

    member CancelSessionChildren: sessionId: string -> Task

    member DisposeSession: sessionId: string -> Task

    member DropSessionIdentity: sessionId: string -> unit

    member DisposeAsync: unit -> Task

    member Dispose: unit -> unit
