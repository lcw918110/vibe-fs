namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Execution.Session
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
    let areSemanticallyEqual (existing: PendingAttemptPlan) (attempted: PendingAttemptPlan) : bool =
        existing = attempted

    let samePhysicalAuthority (existing: PendingAttemptPlan) (attempted: PendingAttemptPlan) : bool =
        existing.Authority.SessionId = attempted.Authority.SessionId
        && existing.Authority.LogicalRunId = attempted.Authority.LogicalRunId
        && existing.Authority.AuthorityRootUserMessageId = attempted.Authority.AuthorityRootUserMessageId
        && existing.Authority.AuthorityKind = attempted.Authority.AuthorityKind
        && existing.Authority.SelectedAgent = attempted.Authority.SelectedAgent
        && existing.Authority.CanonicalRole = attempted.Authority.CanonicalRole
        && existing.PhysicalUserMessageId = attempted.PhysicalUserMessageId
        && existing.Origin = attempted.Origin

    let sameRequestIdentity (existing: PendingAttemptPlan) (attempted: PendingAttemptPlan) : bool =
        samePhysicalAuthority existing attempted
        && existing.RequestKind = attempted.RequestKind

    let requestIdentitySummary (plan: PendingAttemptPlan) : string =
        sprintf
            "session=%s physical=%s logical=%s root=%s authority=%A participant=%s role=%s origin=%A kind=%A"
            (SessionId.value plan.Authority.SessionId)
            (PhysicalUserMessageId.value plan.PhysicalUserMessageId)
            (LogicalRunId.value plan.Authority.LogicalRunId)
            (AuthorityRootUserMessageId.value plan.Authority.AuthorityRootUserMessageId)
            plan.Authority.AuthorityKind
            plan.Authority.SelectedAgent
            (Roles.roleLabel plan.Authority.CanonicalRole)
            plan.Origin
            plan.RequestKind

[<RequireQualifiedAccess>]
type TransformAttemptPlanBindingError =
    | PendingAttemptPlanMissing of SessionId * PhysicalUserMessageId * ProviderRunIdentity

/// Family recovery coordination (PROMPT-011 + C5 + RECOVERY-FAMILY) and
/// attempt planning state for one plugin instance: recovery ports attachment,
/// and per-provider-run attempt plans.
///
/// Owning recovery CE holds the permits internally; Host callbacks are only
/// rendezvous/observation adapters that deliver typed observations. Physical
/// identity (SessionId / ProviderRunIdentity) is the typed capability key;
/// no stringly-typed TryGet/Clear drives business branching (structured-workflow-017, structured-workflow-009, provider-attempt-recovery-011).
type PluginRecoveryScope(journal: AgentJournal option) =

    // Owning CE internal single-flight channels — process-local, crash-zero.
    /// Pre-inference attempt plans, keyed by exact physical user identity. They
    /// become ordinary provider-run keyed AttemptPlans only after Host exposes
    /// the assistant run.
    /// DSL-cross-callback-proof: physical single-flight — transform freezes one
    /// immutable plan under exact (SessionId, PhysicalUserMessageId); the first
    /// later Host observation carrying that same physical parent plus the exact
    /// ProviderRunIdentity consumes it and rekeys it to the provider-run channel.
    /// Mismatched physical material cannot probe/clear it; session deletion is
    /// the only non-bind cleanup. No durable workflow PC is reconstructed here.
    /// DSL-cross-callback-proof: physical
    // DSL-MUTABLE: single-flight — pre-inference attempt plan awaiting exact provider run binding
    let pendingAttemptPlans = Dictionary<string, PendingAttemptPlan>()

    /// DSL-cross-callback-proof: physical single-flight — opaque frozen attempt plan channel.
    /// Owning recovery CE (XWire.reconcileAttempt) consumes via ConsumeAttemptPlan on terminal;
    /// transform adapter records via RecordAttemptPlan.
    /// No stringly-typed TryGet/Clear drives business branching (structured-workflow-017②, structured-workflow-009, provider-attempt-recovery-011).
    // DSL-MUTABLE: single-flight — per-provider-run attempt plan channel (frozen decision, typed handle)
    let attemptPlans = Dictionary<string, AttemptPlan>()

    /// DSL-cross-callback-proof: physical resource — crash-zero typed recovery-request ownership projection.
    let manualChatInterventions =
        Dictionary<ChatExecutionKey, ManualInterventionRequest>()

    let physicalPlanKey (sessionId: SessionId) (physicalUserMessageId: PhysicalUserMessageId) =
        SessionId.value sessionId
        + "\u001f"
        + PhysicalUserMessageId.value physicalUserMessageId

    let providerPlanKey (sessionId: SessionId) (providerRun: ProviderRunIdentity) =
        SessionId.value sessionId + "\u001f" + ProviderRunIdentity.value providerRun

    let admitOrdinaryUnderKey (key: string) (ordinaryPlan: PendingAttemptPlan) : PendingAttemptPlanAdmission =
        match pendingAttemptPlans.TryGetValue key with
        | true, existing when PendingAttemptPlanAdmission.areSemanticallyEqual existing ordinaryPlan ->
            PendingAttemptPlanAdmission.ReplayedExisting existing
        | true, existing -> PendingAttemptPlanAdmission.PlanConflict(existing, ordinaryPlan)
        | false, _ ->
            pendingAttemptPlans.[key] <- ordinaryPlan
            PendingAttemptPlanAdmission.Admitted ordinaryPlan

    let bindProviderRunUnderKey
        (key: string)
        (providerRun: ProviderRunIdentity)
        (physicalUserMessageId: PhysicalUserMessageId)
        (plan: AttemptPlan)
        : AttemptPlan =
        match attemptPlans.TryGetValue key with
        | true, established when established.Profile.PhysicalUserMessageId = physicalUserMessageId -> established
        | true, _ ->
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: cannot re-bind provider run %A to different physical user message %A"
                    providerRun
                    physicalUserMessageId
            )
        | false, _ ->
            attemptPlans.[key] <- plan
            plan

    let installBoundPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) (plan: AttemptPlan) : unit =
        let key = providerPlanKey sessionId providerRun

        match attemptPlans.TryGetValue key with
        | true, established when established = plan -> ()
        | true, established ->
            invalidOp (
                sprintf "HOST-BOUNDARY-008: cannot re-bind provider run %A from %A to %A" providerRun established plan
            )
        | false, _ -> attemptPlans.[key] <- plan

    let installOrdinaryPendingPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (ordinaryPlan: PendingAttemptPlan)
        : PendingAttemptPlanAdmission =
        if
            ordinaryPlan.Authority.SessionId <> sessionId
            || ordinaryPlan.PhysicalUserMessageId <> physicalUserMessageId
        then
            PendingAttemptPlanAdmission.IdentityMismatch(sessionId, physicalUserMessageId, ordinaryPlan)
        else
            admitOrdinaryUnderKey (physicalPlanKey sessionId physicalUserMessageId) ordinaryPlan

    /// Ordinary business entry never performs cross-process recovery. The permit
    /// only admits this process's join attempt and intentionally carries no old
    /// durable closure members. It never recovers durable closure state; explicit
    /// session /continue owns future user-driven work.
    member _.RequireCurrentProcessJoin(root: SessionId) : Task<FamilyRecovery> =
        let sequence =
            journal
            |> Option.map (AgentJournal.revision >> JournalRevision.value)
            |> Option.defaultValue 0L

        Task.FromResult(FamilyRecovery.FamilyReady(FamilyRecoveryPermit.currentProcess root sequence))

    member _.FreezePendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (plan: PendingAttemptPlan)
        =
        installOrdinaryPendingPlan sessionId physicalUserMessageId plan

    member this.RecordPendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (plan: PendingAttemptPlan)
        =
        match this.FreezePendingAttemptPlan sessionId physicalUserMessageId plan with
        | PendingAttemptPlanAdmission.Admitted _ -> ()
        | PendingAttemptPlanAdmission.ReplayedExisting _ -> ()
        | PendingAttemptPlanAdmission.IdentityMismatch(expectedSession, expectedPhysical, attempted) ->
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: pending attempt plan identity mismatch: expected=(%A, %A) attempted=%A"
                    expectedSession
                    expectedPhysical
                    attempted
            )
        | PendingAttemptPlanAdmission.PlanConflict(existing, attempted) ->
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: conflicting pending attempt plan: existing=%A attempted=%A"
                    existing
                    attempted
            )

    member _.TryPendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        : PendingAttemptPlan option =
        let pendingKey = physicalPlanKey sessionId physicalUserMessageId

        match pendingAttemptPlans.TryGetValue pendingKey with
        | true, pending -> Some pending
        | false, _ -> None

    member private this.BindPendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        : AttemptPlan option =
        match this.TryPendingAttemptPlan sessionId physicalUserMessageId with
        | None -> None
        | Some pending ->
            AttemptPlanner.bindProviderRun providerRun pending
            |> bindProviderRunUnderKey (providerPlanKey sessionId providerRun) providerRun physicalUserMessageId
            |> Some

    /// Bind the frozen pre-inference decision to the exact assistant run exposed
    /// by a later Host observation. Repeated observations are idempotent by the
    /// provider-run keyed bound-plan registry.
    member this.TryBindAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        : AttemptPlan option =
        match this.TryAttemptPlan sessionId providerRun with
        | Some established when established.Profile.PhysicalUserMessageId = physicalUserMessageId -> Some established
        | Some _ -> None
        | None -> this.BindPendingAttemptPlan sessionId physicalUserMessageId providerRun

    member this.RecordAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) (plan: AttemptPlan) =
        if plan.Profile.SessionId <> sessionId then
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: attempt plan session mismatch: expected=%A attempted=%A"
                    sessionId
                    plan.Profile.SessionId
            )

        if plan.Profile.ProviderRun <> providerRun then
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: attempt plan run mismatch: expected=%A attempted=%A"
                    providerRun
                    plan.Profile.ProviderRun
            )

        let expectedBound =
            this.TryPendingAttemptPlan sessionId plan.Profile.PhysicalUserMessageId
            |> Option.map (AttemptPlanner.bindProviderRun providerRun)

        match expectedBound with
        | Some expected when expected = plan -> installBoundPlan sessionId providerRun plan
        | _ ->
            invalidOp (
                sprintf
                    "HOST-BOUNDARY-008: no exact admitted pending plan for session %A physical %A run %A"
                    sessionId
                    plan.Profile.PhysicalUserMessageId
                    providerRun
            )

    /// Owning CE consumes the frozen plan exactly once on terminal reconciliation.
    /// Provisional/unknown turns must use TryAttemptPlan (peek) to keep the plan alive;
    /// only terminal outcomes (TurnCompleted/TurnFailed/TurnAborted) call ConsumeAttemptPlan.
    /// Returns None if already consumed or never recorded.
    member this.ConsumeAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) : AttemptPlan option =
        let key = providerPlanKey sessionId providerRun

        match attemptPlans.TryGetValue key with
        | true, plan ->
            attemptPlans.Remove key |> ignore

            pendingAttemptPlans.Remove(physicalPlanKey sessionId plan.Profile.PhysicalUserMessageId)
            |> ignore

            Some plan
        | false, _ -> None

    member this.TryPeekAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) =
        let key = providerPlanKey sessionId providerRun

        match attemptPlans.TryGetValue key with
        | true, plan -> Some plan
        | false, _ -> None

    member this.TryAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) =
        this.TryPeekAttemptPlan sessionId providerRun

    member _.PublishManualChatIntervention(request: ManualInterventionRequest) =
        manualChatInterventions.[request.ExecutionState.key] <- request

    member _.ManualChatInterventions() : ManualInterventionRequest[] =
        manualChatInterventions.Values |> Seq.toArray

    member _.RevokeManualIntervention(key: ChatExecutionKey) : unit =
        manualChatInterventions.Remove key |> ignore

    /// Session deletion drops arming and attempt plans for this session.
    member this.ClearSession(sessionId: string) =
        this.ClearAttemptPlansFor sessionId

        manualChatInterventions.Keys
        |> Seq.filter (fun key -> SessionId.value key.SessionId = sessionId)
        |> Seq.toArray
        |> Array.iter (fun key -> manualChatInterventions.Remove key |> ignore)

    /// Drops attempt plans whose key prefix matches (used for a session and
    /// its linked keys during session deletion). Prefer ConsumeAttemptPlan.
    member this.ClearAttemptPlansFor(key: string) =
        attemptPlans.Keys
        |> Seq.filter (fun planKey -> planKey.StartsWith(key + "\u001f", StringComparison.Ordinal))
        |> Seq.toList
        |> List.iter (fun planKey -> attemptPlans.Remove planKey |> ignore)

        pendingAttemptPlans.Keys
        |> Seq.filter (fun planKey -> planKey.StartsWith(key + "\u001f", StringComparison.Ordinal))
        |> Seq.toList
        |> List.iter (fun planKey -> pendingAttemptPlans.Remove planKey |> ignore)
