namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
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
type TransformAttemptIntentindingError =
    | PendingAttemptPlanMissing of SessionId * PhysicalUserMessageId * ProviderRunIdentity

module private RecoveryStoreInterop =
    [<Import("RecoveryAttemptPlanStore", "wanxiangli/failure")>]
    let recoveryAttemptPlanStoreClass: obj = jsNative

    [<Emit("new $0()")>]
    let createRecoveryAttemptPlanStore (cls: obj) : obj = jsNative

    [<Emit("$0 === null || $0 === undefined")>]
    let isAbsent (x: obj) : bool = jsNative

/// Family recovery coordination (PROMPT-011 + C5 + RECOVERY-FAMILY) and
/// attempt planning state for one plugin instance: recovery ports attachment,
/// and per-provider-run attempt plans.
///
/// All attempt planning and intervention state is 100% owned by wanxiangli/failure (RecoveryAttemptPlanStore).
/// PluginRecoveryScope holds an autonomous store instance process-locally.
type PluginRecoveryScope(journal: AgentJournal option) =

    let store: obj =
        RecoveryStoreInterop.createRecoveryAttemptPlanStore RecoveryStoreInterop.recoveryAttemptPlanStoreClass

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
        : PendingAttemptPlanAdmission =
        let s = SessionId.value sessionId
        let p = PhysicalUserMessageId.value physicalUserMessageId
        let ps = SessionId.value plan.Authority.SessionId
        let pp = PhysicalUserMessageId.value plan.PhysicalUserMessageId
        let isEqual (a: obj) (b: obj) =
            PendingAttemptPlanAdmission.areSemanticallyEqual (unbox<PendingAttemptPlan> a) (unbox<PendingAttemptPlan> b)

        let res: obj = store?FreezePending(s, p, ps, pp, box plan, isEqual)
        match string res?kind with
        | "Admitted" -> PendingAttemptPlanAdmission.Admitted (unbox<PendingAttemptPlan> res?plan)
        | "ReplayedExisting" -> PendingAttemptPlanAdmission.ReplayedExisting (unbox<PendingAttemptPlan> res?existing)
        | "IdentityMismatch" ->
            PendingAttemptPlanAdmission.IdentityMismatch(
                SessionId.create (string res?expectedSession),
                PhysicalUserMessageId.create (string res?expectedPhysical),
                unbox<PendingAttemptPlan> res?attempted
            )
        | "PlanConflict" ->
            PendingAttemptPlanAdmission.PlanConflict(
                unbox<PendingAttemptPlan> res?existing,
                unbox<PendingAttemptPlan> res?attempted
            )
        | other -> failwithf "wanxiangli/failure: unexpected admission kind %s" other

    member _.TryPendingAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        : PendingAttemptPlan option =
        let res: obj =
            store?TryPending(SessionId.value sessionId, PhysicalUserMessageId.value physicalUserMessageId)

        if RecoveryStoreInterop.isAbsent res then None
        else Some (unbox<PendingAttemptPlan> res)

    /// Bind the frozen pre-inference decision to the exact assistant run exposed
    /// by a later Host observation. Repeated observations are idempotent by the
    /// provider-run keyed bound-plan registry.
    member this.TryBindAttemptPlan
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        : AttemptPlan option =
        let s = SessionId.value sessionId
        let p = PhysicalUserMessageId.value physicalUserMessageId
        let r = ProviderRunIdentity.value providerRun
        let bindFn (pendingObj: obj) : obj =
            box (AttemptPlanner.bindProviderRun providerRun (unbox<PendingAttemptPlan> pendingObj))

        let res: obj = store?TryBindAttemptPlan(s, p, r, bindFn)
        if RecoveryStoreInterop.isAbsent res then None
        else Some (unbox<AttemptPlan> res)

    /// Owning CE consumes the frozen plan exactly once on terminal reconciliation.
    /// Provisional/unknown turns must use TryAttemptPlan (peek) to keep the plan alive;
    /// only terminal outcomes (TurnCompleted/TurnFailed/TurnAborted) call ConsumeAttemptPlan.
    /// Returns None if already consumed or never recorded.
    member this.ConsumeAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) : AttemptPlan option =
        let res: obj =
            store?ConsumeAttemptPlan(SessionId.value sessionId, ProviderRunIdentity.value providerRun)

        if RecoveryStoreInterop.isAbsent res then None
        else Some (unbox<AttemptPlan> res)

    member this.TryAttemptPlan (sessionId: SessionId) (providerRun: ProviderRunIdentity) : AttemptPlan option =
        let res: obj =
            store?TryAttemptPlan(SessionId.value sessionId, ProviderRunIdentity.value providerRun)

        if RecoveryStoreInterop.isAbsent res then None
        else Some (unbox<AttemptPlan> res)

    member _.PublishManualChatIntervention(request: ManualInterventionRequest) : unit =
        let sid = SessionId.value request.ExecutionState.key.SessionId
        let phys = PhysicalUserMessageId.value request.ExecutionState.key.PhysicalUserMessageId
        store?PublishManualIntervention(sid, phys, box request) |> ignore

    member _.ManualChatInterventions() : ManualInterventionRequest[] =
        let arr: obj array = store?ManualInterventions()
        if RecoveryStoreInterop.isAbsent arr then [||]
        else arr |> Array.map unbox<ManualInterventionRequest>

    member _.RevokeManualIntervention(key: ChatExecutionKey) : unit =
        let sid = SessionId.value key.SessionId
        let phys = PhysicalUserMessageId.value key.PhysicalUserMessageId
        store?RevokeManualIntervention(sid, phys) |> ignore

    /// Session deletion drops arming and attempt plans for this session.
    member this.ClearSession(sessionId: string) : unit =
        store?ClearSession(sessionId) |> ignore

    /// Drops attempt plans whose key prefix matches (used for a session and
    /// its linked keys during session deletion). Prefer ConsumeAttemptPlan.
    member this.ClearAttemptPlansFor(key: string) : unit =
        store?ClearAttemptPlansFor(key) |> ignore
