namespace Wanxiangshu.Strength.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Strength
open Wanxiangshu.Strength.Persistence

/// DELEGATE-7: Host boundary wiring for explicit read-only delegation.
/// Phase one captures the authorization from the real completed owner batch;
/// phase two starts and consumes it. All policy math stays in Domain.
[<RequireQualifiedAccess>]
module StrengthDelegate =

    type CaptureOutcome =
        | Captured of DelegationRequest
        | Skipped of reason: string

    /// 机器可判的稳定捕获结果标识（非散文文案，严禁使用 Fable 内部反射）
    val captureOutcomeCode: outcome: CaptureOutcome -> string

    /// Phase one: freeze one authorization from the owner's completed source
    /// batch and persist DelegationRequested. The predictor configuration is a
    /// caller-provided existence input, independent of capacity and health.
    val tryCapture:
        snapshotPort: ISessionSnapshotPort option ->
        journal: AgentJournal option ->
        strengthDurability: StrengthDurabilityPort option ->
        strengthScope: PluginStrengthScope ->
        tryAttemptPlan: (SessionId -> ProviderRunIdentity -> AttemptPlan option) ->
        syncDelegateRuntime: SyncDelegateRuntime option ->
        predictorConfigured: bool ->
        projectionSessionIdOpt: string option ->
        timerPort: Wanxiangshu.Foundation.ITimerPort option ->
        output: obj ->
            Task<CaptureOutcome>

    /// Phase two: read the pending request from canonical Current, freeze the
    /// legal ordinary continuation's target and mirror, run the replica through
    /// its prepared stages with DelegationBound persisted in between, publish
    /// Prepared, and render the candidate after publication succeeds.
    /// Capture and start in ONE call at the end of the transform: freezes the
    /// authorization and starts the replica with the final outgoing request as
    /// mirror, eliminating the cross-request pending hand-off.
    val tryCaptureAndStart:
        snapshotPort: ISessionSnapshotPort option ->
        journal: AgentJournal option ->
        strengthDurability: StrengthDurabilityPort option ->
        strengthScope: PluginStrengthScope ->
        tryAttemptPlan: (SessionId -> ProviderRunIdentity -> AttemptPlan option) ->
        syncDelegateRuntime: SyncDelegateRuntime option ->
        predictorConfigured: bool ->
        projectionSessionIdOpt: string option ->
        timerPort: Wanxiangshu.Foundation.ITimerPort option ->
        output: obj ->
            Task<unit>
