namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Result of admitting a pending attempt plan under exact physical identity.
[<RequireQualifiedAccess>]
type PendingAttemptPlanAdmission =
    | Admitted of PendingAttemptPlan
    | ReplayedExisting of PendingAttemptPlan
    | IdentityMismatch of
        expectedSession: SessionId *
        expectedPhysical: PhysicalUserMessageId *
        attempted: PendingAttemptPlan
    | PlanConflict of existing: PendingAttemptPlan * attempted: PendingAttemptPlan

module PendingAttemptPlanAdmission =
    /// Exact equality for immutable admitted request evidence.
    val areSemanticallyEqual: existing: PendingAttemptPlan -> attempted: PendingAttemptPlan -> bool

    /// Same exact physical request identity, independent of its already-frozen projection choice.
    val sameRequestIdentity: existing: PendingAttemptPlan -> attempted: PendingAttemptPlan -> bool

    /// Same accepted physical authority before a more specific request owner freezes its request kind.
    val samePhysicalAuthority: existing: PendingAttemptPlan -> attempted: PendingAttemptPlan -> bool

    val requestIdentitySummary: plan: PendingAttemptPlan -> string

/// Binding error when a pending attempt plan cannot be found at bind time.
[<RequireQualifiedAccess>]
type TransformAttemptPlanBindingError =
    | PendingAttemptPlanMissing of SessionId * PhysicalUserMessageId * ProviderRunIdentity

/// Process-local join admission, immutable attempt evidence, and manual
/// recovery diagnostics for one plugin instance.
type PluginRecoveryScope =
    new: journal: AgentJournal option -> PluginRecoveryScope

    /// Admits this process's join attempt only. Mints an empty-closure
    /// current-process permit; never recovers durable closure state.
    member RequireCurrentProcessJoin: root: SessionId -> Task<FamilyRecovery>

    /// Freezes a pre-inference attempt plan under the exact physical user message.
    member FreezePendingAttemptPlan:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        plan: PendingAttemptPlan ->
            PendingAttemptPlanAdmission

    /// Reads the immutable plan already frozen for this exact physical request.
    member TryPendingAttemptPlan:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> PendingAttemptPlan option

    /// Freezes a pre-inference attempt plan, throwing on conflict.
    member RecordPendingAttemptPlan:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> plan: PendingAttemptPlan -> unit

    /// Binds the frozen pre-inference decision to the exact assistant run.
    member TryBindAttemptPlan:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
            AttemptPlan option

    /// Records an already-bound attempt plan under the provider run.
    member RecordAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> plan: AttemptPlan -> unit

    /// Consumes the frozen plan exactly once on terminal reconciliation.
    member ConsumeAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    member TryPeekAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    /// Read-only peek alias.
    member TryAttemptPlan: sessionId: SessionId -> providerRun: ProviderRunIdentity -> AttemptPlan option

    member PublishManualChatIntervention: request: ManualInterventionRequest -> unit

    /// Returns the exact manual intervention observations held process-locally.
    member ManualChatInterventions: unit -> ManualInterventionRequest[]

    /// Revokes the exact manual intervention entry for a terminally settled key.
    member RevokeManualIntervention: key: ChatExecutionKey -> unit

    /// Session deletion drops arming and attempt plans for this session.
    member ClearSession: sessionId: string -> unit

    /// Drops attempt plans whose key prefix matches the given key.
    member ClearAttemptPlansFor: key: string -> unit
