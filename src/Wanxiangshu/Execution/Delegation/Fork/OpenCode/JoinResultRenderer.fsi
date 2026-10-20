namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open Wanxiangshu.Change
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Participant.Provider

/// EXEC-004 / EXEC-017 / EXEC-030: LLM-facing join wire — natural language + delivered view only.
/// No status / count / ordinal / kind / agent / code / message DTO plane.
///
/// Directional delivered-view plane (delegation-013 / delegation-019 / provider-projection-009):
/// child → parent join MUST keep the delivered view in Instruction Plane. Do NOT
/// wrap it in `view = …` or any other TOML data field. Parent → child fork payload is the opposite contract
/// (`commissioner_record` / `attached_view` fields) — do not conflate.
module JoinResultRenderer =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val AgentReturned: string = "tool/join/agent-returned"

        [<Literal>]
        val AgentFailed: string = "tool/join/agent-failed"

        [<Literal>]
        val AgentDidNotReturn: string = "tool/join/agent-did-not-return"

        [<Literal>]
        val PtyEnded: string = "tool/join/pty-ended"

        [<Literal>]
        val PtyInterrupted: string = "tool/join/pty-interrupted"

        [<Literal>]
        val TerminalLabel: string = "tool/join/terminal-label"

        [<Literal>]
        val InterruptOperatorAbort: string = "tool/join/interrupt-operator-abort"

        [<Literal>]
        val InterruptUserMessage: string = "tool/join/interrupt-user-message"

        [<Literal>]
        val InterruptDeadline: string = "tool/join/interrupt-deadline"

        [<Literal>]
        val OrchestratorPublished: string = "tool/join/orchestrator-published"

        [<Literal>]
        val OrchestratorRejectedDirty: string = "tool/join/orchestrator-rejected-dirty"

        [<Literal>]
        val OrchestratorIntegrationFailed: string = "tool/join/orchestrator-integration-failed"

        [<Literal>]
        val OrchestratorEmpty: string = "tool/join/orchestrator-empty"

        [<Literal>]
        val ForkNothingToJoin: string = "tool/join/fork-nothing-to-join"

        [<Literal>]
        val ForkCancelled: string = "tool/join/fork-cancelled"

        [<Literal>]
        val ForkJoinInProgress: string = "tool/join/fork-join-in-progress"

        [<Literal>]
        val ForkNotFound: string = "tool/join/fork-not-found"

        [<Literal>]
        val ForkTimedOut: string = "tool/join/fork-timed-out"

        [<Literal>]
        val ForkMaterializationFailed: string = "tool/join/fork-materialization-failed"

        [<Literal>]
        val RemainingCompletions: string = "tool/join/remaining-completions"

    /// EXEC-017: interrupt consequences are natural language, not error DTO.
    val renderInterrupted: lang: ProviderLanguage -> reason: JoinInterruptReason -> string

    /// EXEC-004 / EXEC-018 / EXEC-020: JoinItem batch (production JoinTool path).
    val renderJoinItemBatch:
        lang: ProviderLanguage ->
        resolveAgentName: (string -> string) ->
        batch: NonEmptyBatch<JoinItem> ->
        resolveTerminalLabel: (string -> string) ->
            string

    /// FIFO window-bounded render. Returns rendered text and un-rendered remaining items.
    val renderJoinItemBatchWithWindow:
        lang: ProviderLanguage ->
        resolveAgentName: (string -> string) ->
        batch: NonEmptyBatch<JoinItem> ->
        resolveTerminalLabel: (string -> string) ->
            string * JoinItem list

    /// EXEC-019: orchestrator verdict batch (FIFO; caller already capped at MaxJoinBatch).
    val renderOrchestratorBatch: lang: ProviderLanguage -> verdicts: NonEmptyBatch<OrchestratorVerdict> -> string

    /// True ForkError path — natural language only (not user interrupt).
    val renderForkError: lang: ProviderLanguage -> error: ForkError -> resolveAgentName: (string -> string) -> string
