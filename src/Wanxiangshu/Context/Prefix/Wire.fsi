namespace Wanxiangshu.Context.Prefix

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Attempt

type XWireReconciliationDecision = { Cleared: bool; KeptPlan: bool }

type AttemptPlanCapability =
    { TryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option
      TryBindAttemptPlan: SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> AttemptPlan option
      ConsumeAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option
      FreezePendingAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan -> PendingAttemptPlanAdmission
      TryPendingAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan option }

module XWire =

    val reconciliationDecision: hasPlan: bool -> outcome: AttemptOutcome option -> XWireReconciliationDecision

    val suppressHostMessagesByIds: rawMessages: obj list -> hostMessageIds: Set<string> -> obj list

    val applyTransform:
        snapshot: ISessionSnapshotPort option ->

        port: WireJournalPort option ->
        attempts: AttemptPlanCapability ->
        output: obj ->
            Task

    val reconcileAttempt:
        port: WireJournalPort option -> attempts: AttemptPlanCapability -> turn: ReconciledTurn -> Task
