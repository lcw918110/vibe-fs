namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Dispatch.OpenCode
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

    /// crash-reconciliation-018: load-phase normalization owes one restart
    /// status guidance to the next real user instruction in this process.
    member MarkRestartGuidancePending: unit -> unit

    member RestartGuidancePending: bool

    member MarkRestartGuidanceDelivered: unit -> unit

    member AttachSessionCleanup: cleanup: (string -> unit) -> unit

    member AttachScopeDispose: dispose: (unit -> unit) -> unit


    /// Composition-of-owners: per-instance session registries live in their own scope.
    member Sessions: PluginSessionScope

    /// Composition-of-owners: family recovery + attempt planning live in their own scope.
    member Recovery: PluginRecoveryScope

    /// The single lifecycle owner's registry, shared by every AttachmentKind adapter.
    member AttachAttachmentRegistry: registry: AttachmentLeaseRegistry -> unit

    member AttachmentRegistry: AttachmentLeaseRegistry

    member AttachCompanionLeases: runtime: CompanionLeaseRuntime -> unit

    member CompanionLeases: CompanionLeaseRuntime

    member AttachSyncDelegateRuntime: runtime: SyncDelegateRuntime -> unit

    member AttachChatRecoveryRuntime: runtime: SessionRecoveryHost -> unit

    member SignalChatRecovery: event: ChatExecutionRecoveryLifecycleEvent -> Task

    member SignalChatRecoverySession:
        sessionId: SessionId -> eventOf: (ChatExecutionKey -> ChatExecutionRecoveryLifecycleEvent) -> Task

    member DrainChatRecovery: sessionId: SessionId -> Task

    member SyncDelegateRuntime: SyncDelegateRuntime option

    member AttachLoopSensor: sensor: ILoopSensor -> unit

    member AttachMessageVisibility: hub: MessageVisibilityHub -> unit

    /// None until the signal stack wires the hub; the catch-up re-read then
    /// falls back to its bounded immediate form.
    member MessageVisibility: MessageVisibilityHub option

    member LoopSensor: ILoopSensor

    /// Current-process join admission only; no cross-process tool recovery.
    member RequireCurrentProcessJoin: root: SessionId -> Task<FamilyRecovery>

    member PublishManualChatIntervention: request: ManualInterventionRequest -> unit

    /// Exact manual intervention observations held process-locally.
    member ManualChatInterventions: unit -> ManualInterventionRequest[]

    /// Revokes the exact manual intervention entry for a terminally settled key.
    member RevokeManualIntervention: key: ChatExecutionKey -> unit

    member RecordPendingAttemptPlan:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> plan: PendingAttemptPlan -> unit

    member TryBindAttemptPlan:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
            AttemptPlan option

    member RecordAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> plan: AttemptPlan -> unit

    member TryAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    member ConsumeAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    /// HOST-006 prevention layer: the config hook's finding.
    ///
    /// Written once at config time, read once by the startup probe. Not a collection
    /// because there is one verdict per plugin instance — the settings are
    /// instance-global (`config/config.ts:607`), not per session.
    member RecordCompactionSettingGap: gap: CompactionSetting option -> unit

    member CompactionSettingGap: CompactionSetting option

    /// HOST-006 startup probe: has it already run.
    ///
    /// One probe per plugin instance, not per session. The claim it tests is about the
    /// Host build, and the first managed session's first turn is the cheapest place to
    /// observe it — running it again on every later session would keep asking a
    /// question already answered while risking a false refusal from a legitimate
    /// `/compact`.
    ///
    /// `TryClaimStartupProbe` returns true exactly once, so the caller cannot
    /// accidentally judge twice from concurrent reconcile passes.
    member TryClaimStartupProbe: unit -> bool

    /// Cheap read for the common case: after the probe has run, every later reconcile
    /// pass skips the judgement entirely rather than building a verdict and discarding
    /// it.
    member IsStartupProbeOpen: bool

    member AttachToolRuntime: owner: ISessionRuntimeOwner -> unit

    member TrackSubscription: value: IDisposable option -> unit

    member TrackReconcileShutdown: stopAndDrain: (unit -> Task) -> unit

    member RunBackground: start: (unit -> Task) -> unit

    member RunOwnedWork: start: (unit -> Task) -> Task

    member AttachSharedTerminal: key: string option * port: Events.HostEventPort option -> unit

    member DisposeExecutorRuntime: sessionId: string -> Task

    /// EXEC-016: live PTY probe for DevOps join guard.
    member HasLivePty: sessionId: string -> bool
    member RetirementBlockersFor: sessionId: string -> string list

    member CancelSessionChildren: sessionId: string -> Task

    member DisposeSession: sessionId: string -> Task

    member DropSessionIdentity: sessionId: string -> unit

    member DisposeAsync: unit -> Task

    member Dispose: unit -> unit
