namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module SessionRecoveryHostSurface =
    type RecoveryHostHandle =
        { Journal: Wanxiangshu.Persistence.Journal.JournalHandle
          Scope: PluginRecoveryScope
          Sessions: PluginSessionScope
          Host: SessionRecoveryHost
          PortOutcome: string
          TerminalGate: TaskCompletionSource<unit>
          TerminalArrival: TaskCompletionSource<unit>
          ResumeCalls: System.Collections.Generic.List<bool> }

    val bootRecoveryHost: directory: string -> portOutcome: string -> Task<RecoveryHostHandle>

    val resumeAccepted: handle: RecoveryHostHandle -> sessionId: string -> physicalUserMessageId: string -> Task<obj>

    val seedAccepted: handle: RecoveryHostHandle -> sessionId: string -> physicalUserMessageId: string -> Task<unit>

    val seedProviderStarted:
        handle: RecoveryHostHandle ->
        sessionId: string ->
        physicalUserMessageId: string ->
        providerRun: string ->
            Task<unit>

    val finalizeCompleted:
        handle: RecoveryHostHandle ->
        sessionId: string ->
        physicalUserMessageId: string ->
        providerRun: string ->
            Task<obj>

    val signalCancelled: handle: RecoveryHostHandle -> sessionId: string -> physicalUserMessageId: string -> Task<obj>

    val executionStatus: handle: RecoveryHostHandle -> sessionId: string -> physicalUserMessageId: string -> obj

    val journalExecutionStatus:
        handle: Wanxiangshu.Persistence.Journal.JournalHandle ->
        sessionId: string ->
        physicalUserMessageId: string ->
            obj

    /// provider-attempt-recovery-023：宿主 session idle。扫描只考虑该 session 中 `Accepted` 且无
    /// `ProviderStarted` 的执行；返回值是完整可观察效果（port 调用 + manual）。
    val signalSessionQuiesced: handle: RecoveryHostHandle -> sessionId: string -> Task<obj>

    /// managed-chat-execution-015：transform 在 provider 启动边界拒绝 exact 执行。
    /// 返回该拒绝的可观察结局：`terminalized`（pre-provider Failed + 精确释放）、
    /// `already-terminal`（幂等；容量仍 held 时精确释放）、`no-execution`（无 Accepted
    /// 投影，不伪造）、`ignored`（已 ProviderStarted，不由 pre-provider 路径终结）。
    val signalProviderStartBoundaryRejected:
        handle: RecoveryHostHandle -> sessionId: string -> physicalUserMessageId: string -> reason: string -> Task<obj>

    /// provider-attempt-recovery-024：加载期废弃 stale Blogger open request 后，
    /// 定夺同源 `Accepted ∧ ¬ProviderStarted` 的执行。返回 `{ settled,
    /// alreadyTerminal, manuals }`。
    val settleStaleBloggerAcceptedExecutions: handle: RecoveryHostHandle -> sessionId: string -> Task<obj>

    val disposeRecoveryHost: handle: RecoveryHostHandle -> unit

    /// managed-chat-execution-006：受控 terminal barrier boot。terminalMode 为
    /// "committed"（纯透传）、"held"（Terminal append 停在门上直到放行）或
    /// "commitUnknown"（Terminal append 回答 typed unknown 且不写盘）。
    val bootControlledRecoveryHost:
        directory: string -> portOutcome: string -> terminalMode: string -> Task<RecoveryHostHandle>

    val awaitTerminalBarrier: handle: RecoveryHostHandle -> Task<unit>

    val releaseTerminalBarrier: handle: RecoveryHostHandle -> unit

    /// managed-chat-execution-006：经生产 recovery signal 路径投递 exact assistant
    /// terminal 事件（Signal → 决策 → Finalize → terminal append → exact release），
    /// 返回结算后的 lifecycle 视图；持久化失败时该 task reject。
    val signalExactTerminal:
        handle: RecoveryHostHandle ->
        sessionId: string ->
        physicalUserMessageId: string ->
        providerRun: string ->
        disposition: string ->
            Task<obj>

    /// managed-session-lifecycle-019：驱动 session 删除的 drain owner——
    /// runtime 的 DisposeSession 所 await 的同一 PluginSessionScope.ClearSession，
    /// 返回的 task 即该删除排空的公开生命周期完成 Promise。
    val clearSession: handle: RecoveryHostHandle -> sessionId: string -> Task

    /// managed-session-lifecycle-019：驱动 logical cancel 的 drain owner——
    /// runtime 的 SignalChatRecoverySession 所转发的同一
    /// SessionRecoveryHost.SignalSession，逐 key 结算该会话的全部执行。
    val signalSessionCancelled: handle: RecoveryHostHandle -> sessionId: string -> Task
