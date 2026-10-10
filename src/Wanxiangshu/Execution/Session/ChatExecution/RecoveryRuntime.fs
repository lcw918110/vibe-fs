namespace Wanxiangshu.Execution.Session.ChatExecution

open System.Threading.Tasks
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type ChatExecutionRecoveryLifecycleEvent =
    | DurabilityActivated
    | PluginRuntimeReloaded
    | ExactAssistantStarted of ProviderStartedEvidence
    | ExactAssistantTerminal of ProviderStartedEvidence * ChatExecutionTerminalDisposition
    | SessionAborted of ChatExecutionKey
    | SessionDeleted of ChatExecutionKey
    | SessionCancelled of ChatExecutionKey
    | SessionSuperseded of ChatExecutionKey
    | PhysicalExecutionQuiesced of ChatExecutionKey
    | CapacityProjectionReplayed

    /// provider-attempt-recovery-023：宿主发布 session idle 后，本 session 的 `Accepted` 且无
    /// `ProviderStarted` 的执行是确切义务集：宿主已停止执行它们，恢复必须
    /// 逐一定夺（resume 或终态），不得留成悬空态。
    | SessionQuiesced of SessionId

    /// managed-chat-execution-015：transform 在 provider 启动边界拒绝 exact 执行时
    /// 发出的 typed 报告，携带 exact key 与诊断码；settlement 由 recovery owner
    /// 按 durable projection 分流，不携带 free-form 决策。
    | ProviderStartBoundaryRejected of ChatExecutionKey * string

    /// provider-attempt-recovery-024：加载期废弃了该 Blogger session 的 stale open
    /// request；recovery owner 定夺同源 `Accepted ∧ ¬ProviderStarted ∧ ¬Terminal`
    /// 的执行（terminal Failed + exact release），不动已启动或已终态的执行。
    | BloggerStaleRequestAbandoned of SessionId

type ChatExecutionRecoveryActionPorts =
    { ReconcilePhysical: PhysicalReconciliationRequest -> Task
      ResumePreProvider: PreProviderResumeRequest -> Task
      Finalize: TerminalFinalizationRequest -> Task
      MarkManualIntervention: ManualInterventionRequest -> Task }

[<RequireQualifiedAccess>]
module ChatExecutionRecoveryRuntime =

    let interpret (ports: ChatExecutionRecoveryActionPorts) (decision: ChatExecutionRecoveryDecision) : Task =
        match decision with
        | ChatExecutionRecoveryDecision.Ignore _ -> Task.FromResult(()) :> Task
        | ChatExecutionRecoveryDecision.ReconcilePhysical request -> ports.ReconcilePhysical request
        | ChatExecutionRecoveryDecision.ResumePreProvider request -> ports.ResumePreProvider request
        | ChatExecutionRecoveryDecision.Finalize request -> ports.Finalize request
        | ChatExecutionRecoveryDecision.MarkManualIntervention request -> ports.MarkManualIntervention request

    let recover
        (ports: ChatExecutionRecoveryActionPorts)
        (evidence: ChatExecutionRecoveryEvidence)
        : Task<ChatExecutionRecoveryDecision> =
        task {
            let decision = ChatExecutionRecovery.decide evidence
            do! interpret ports decision
            return decision
        }
