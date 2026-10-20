namespace Wanxiangshu.Foundation

open System
open Wanxiangshu.Foundation.Identity

/// Completion payload for a successful agent run.
///
/// No transport parts are kept: business programs read the typed turn
/// (HOST-004), never a raw payload.
type AgentRunResult =
    {
        SessionId: SessionId
        AuthorityRootUserMessageId: AuthorityRootUserMessageId
        /// The provider run that reached terminal. HOST-011: this is the assistant
        /// message id, which is also what tool calls in that run observed.
        ProviderRun: ProviderRunIdentity
        Role: Role
        /// The worktree this run executed in, when it has one.
        ///
        /// `option`, not `""`. An empty path is not a directory, and every consumer
        /// had to re-test for blankness to find that out — terminal completion
        /// did exactly that before passing it on, which means the sentinel was
        /// converted back into an option one layer later anyway.
        Directory: string option
        /// Terminal output for this run (HOST-005 / COMPANION-003): formal text
        /// plus host-visible reasoning. Excludes tool raw streams. Becomes the
        /// XTrace's terminal segment for that run; not also a parallel
        /// session-wide text.
        TerminalText: string
        /// This turn's formal assistant text only, without reasoning. Used for
        /// interaction-repair terminal-validity checks (COMPANION-005).
        TurnFormalText: string
    }

    /// EXEC-006: a completed run must carry terminal output. Empty means the
    /// turn was not actually reconciled, so consumers must not treat it as done.
    member this.IsValid = not (String.IsNullOrWhiteSpace this.TerminalText)

type AgentRunFailure =
    { SessionId: SessionId; Reason: string }

module Outcome =

    /// The result of asking the Host to accept a prompt (PROMPT-005 `Submitted`).
    ///
    /// The two admitted cases are separate because the Host may return either an
    /// `accepted-*` admission receipt or a real message id, and PROMPT-005 gives
    /// them different authority: only a real physical message id may become an
    /// Authority Root. One case carrying an untyped id would erase that
    /// distinction at exactly the point it matters.
    type SendOutcome =
        /// Host accepted and returned only a transport receipt. Write
        /// `Submitted`; `PhysicalAccepted` still requires a real message.
        | AdmittedWithReceipt of TransportReceipt
        /// Host accepted and returned a real physical message identity.
        | AdmittedWithPhysicalMessage of PhysicalUserMessageId
        /// Transport failed in a way that proves the prompt was not accepted.
        | Retryable of reason: string
        /// PROMPT-011: acceptance cannot be proven either way. Stay Pending and
        /// never auto-resend — the contract is at-most-one logical effect.
        | AcceptanceUnknown of reason: string
        | Fatal of reason: string

    type SessionOutcome =
        | CompletedSession of message: string
        | CancelledSession
        | TerminatedSession of reason: string

    type SessionError =
        | NoProgress of reason: string
        | SessionCancelled
        /// provider-attempt-recovery-005: the bounded automatic provider-recovery budget is spent.
        | AutoRecoveryExhausted
        | ReviewExhausted
        /// A dispatched prompt whose physical acceptance could not be proven.
        | PromptUncertain
        | ProjectionBroken of reason: string
        | InboxFull
        | Protocol of reason: string

/// Why a journal line was refused during a fold.
///
/// PERSIST-004 requires a corrupt journal to stop startup rather than be
/// absorbed. A benign duplicate is not corruption, so the two are separated
/// here: `FoldRejection` means the line is impossible, and the caller must fail
/// closed.
type FoldRejection = { Fact: string; Reason: string }

module FoldRejection =

    let reject factName reason =
        Error { Fact = factName; Reason = reason }
