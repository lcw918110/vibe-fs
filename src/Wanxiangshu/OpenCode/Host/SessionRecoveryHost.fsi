namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Host.Ports


/// Optional Host capability bound to exact already-accepted physical material.
/// Absence is not permission to create a replacement PromptClaim or resend text.
type ExactAcceptedMessageRecoveryPort =
    { ResumeAccepted: PreProviderResumeRequest -> Task<bool> }

type SessionRecoveryHost =
    new:
        journal: AgentJournal *
        transcript: ITranscriptPort *
        scope: PluginRecoveryScope *
        acceptedMessageRecovery: ExactAcceptedMessageRecoveryPort option *
        notifyTerminal: (SessionId -> TerminalOutcome -> unit) option ->
            SessionRecoveryHost

    member Signal: event: ChatExecutionRecoveryLifecycleEvent -> Task

    member SignalSession:
        sessionId: SessionId * eventOf: (ChatExecutionKey -> ChatExecutionRecoveryLifecycleEvent) -> Task

    member Drain: sessionId: SessionId -> Task

    member ResumePreProvider: request: PreProviderResumeRequest -> Task

    member Finalize: request: TerminalFinalizationRequest -> Task

    /// managed-chat-execution-015：exact provider 启动边界拒绝的报告入口。
    /// 返回值是该信号的可观察结局：`terminalized` | `already-terminal` |
    /// `no-execution` | `ignored`。
    member SettleProviderStartBoundaryRejected: key: ChatExecutionKey * reason: string -> Task<string>

    /// crash-reconciliation-023: start or join THIS session's load-phase reload
    /// sweep: the session's own leftover executions (resources, unknowns,
    /// settlements) decide before its next admission proceeds, while every
    /// other session's sweep runs in the background and never blocks it. A
    /// faulting sweep propagates to its awaiter and is cleared, so a later
    /// caller starts a fresh sweep instead of re-observing a stale failure.
    member ReloadSession: sessionId: SessionId -> Task

    /// The session ids that still owe a load-phase reload decision — the same
    /// set the process-wide sweep would visit, one id per session.
    member SessionsPendingReload: unit -> SessionId list
