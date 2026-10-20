namespace Wanxiangshu.Execution.Session

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// DSL-state-combination: domain — optional child-session identity and role
/// qualify one immutable failure fact; neither is a continuation token.
type AgentFailurePayload =
    {
        AgentId: string
        /// Typed, matching the completed payload. It was `string option` beside a
        /// `SessionId option`, so the same concept had two types across two cases of
        /// one union.
        ChildSessionId: SessionId option
        RunId: string
        Role: Role option
        Code: string
        Message: string
    }

/// One finished child run, as `join` reports it (EXEC-004).
///
/// The four identities are `option` because a PTY run genuinely has none of them:
/// it owns no Host child session, no Authority Root, no provider run, no worktree.
/// They used to be `string` filled with `""`, which made "absent" and "the empty
/// string" the same value — the sentinel PROMPT-001 exists to remove — and forced
/// every reader to re-test for blankness to find out which it had.
///
/// Typed, not `string option`: HOST-010 makes the terminal provider run the
/// assistant message id, and PROMPT-002 makes the root a promoted physical
/// message. `AssistantMessageId` was a second name for the former.
///
/// `View` holds the final delivered TOML view from js.deliver,
/// materialised at terminal.
/// DSL-state-combination: domain — optional child/session/provider identities
/// are evidence facets of one terminal completion payload, not independent
/// workflow stages.
type AgentCompletionPayload =
    {
        AgentId: string
        ChildSessionId: SessionId option
        RunId: string
        Role: Role
        AuthorityRoot: AuthorityRootUserMessageId option
        ProviderRun: ProviderRunIdentity option
        /// The delivered TOML view, materialised at terminal.
        View: string
        /// The worktree this child ran in, when it has one.
        Directory: string option
    }

/// Agent join finality only (EXEC-020): Completed | Failed | Abandoned.
/// Host abort is observation, not agent terminal.
type AgentCompletionOutcome =
    | AgentCompleted of AgentCompletionPayload
    | AgentFailed of AgentFailurePayload
    /// EXEC-009: durable HandleAbandoned reported once in a join [[result]] batch.
    /// Flat wire: status="abandoned", agent, reason — not nested [error].
    | AgentAbandoned of agentId: string * reason: string

/// PTY exit payload (kind=pty, status=completed).
type PtyExit =
    { PtyId: string
      Outcome: string
      Closed: bool }

/// PTY failure payload (kind=pty, status=failed).
type PtyFailure =
    { PtyId: string
      Outcome: string
      Closed: bool
      Code: string
      Message: string }

/// PTY abort payload (kind=pty, status=aborted) — PTY-only; agent path forbids aborted.
type PtyAbort =
    { PtyId: string
      Outcome: string
      Closed: bool
      Code: string
      Message: string }

/// Agent item inside a join [[result]] batch. No aborted branch.
type AgentJoinItem =
    | AgentCompletedItem of AgentCompletionPayload
    | AgentFailedItem of AgentFailurePayload
    | AgentAbandonedItem of agentId: string * reason: string

/// PTY item inside a join [[result]] batch. Aborted is legal for PTY only.
type PtyJoinItem =
    | PtyExited of PtyExit
    | PtyFailed of PtyFailure
    | PtyAborted of PtyAbort

/// One join batch item: agent or PTY.
type JoinItem =
    | AgentItem of AgentJoinItem
    | PtyItem of PtyJoinItem

module AgentCompletion =
    let text (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted payload -> payload.View
        | AgentFailed payload -> payload.Message
        | AgentAbandoned(_, reason) -> reason

    /// The agentId owning this completion — the canonical Map key, carried by
    /// the Outcome payload (Completed/Failed) or the Abandoned tuple head.
    let agentId (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted payload -> payload.AgentId
        | AgentFailed payload -> payload.AgentId
        | AgentAbandoned(agentId, _) -> agentId

    let status (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted payload when String.IsNullOrWhiteSpace payload.View -> "undelivered"
        | AgentCompleted _ -> "completed"
        | AgentFailed _ -> "failed"
        | AgentAbandoned _ -> "abandoned"

    let isCompleted (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted payload -> not (String.IsNullOrWhiteSpace payload.View)
        | _ -> false

    let completed
        (agentId: string)
        (childSessionId: SessionId)
        (runId: string)
        (role: Role)
        (authorityRoot: AuthorityRootUserMessageId)
        (providerRun: ProviderRunIdentity)
        (view: string)
        (directory: string option)
        =
        AgentCompleted
            { AgentId = agentId
              ChildSessionId = Some childSessionId
              RunId = runId
              Role = role
              AuthorityRoot = Some authorityRoot
              ProviderRun = Some providerRun
              View = view
              Directory = directory }

    let failed (agentId: string) (runId: string) (role: Role option) (childSessionId: SessionId option) code message =
        AgentFailed
            { AgentId = agentId
              ChildSessionId = childSessionId
              RunId = runId
              Role = role
              Code = code
              Message = message }

    /// A PTY or local run that has no Host child session and no Authority Root.
    ///
    /// The four identity arguments are absent rather than blank. `completed` used to
    /// be called here with `"" "" ""` for ChildSessionId, root and directory, which
    /// made "this run has no child session" and "its session id is the empty string"
    /// the same value — the sentinel pattern PROMPT-001 exists to remove.
    ///
    /// The view is the run's plain text: a PTY has no delivered view, and its completion
    /// schema stays deliberately minimal (EXEC-004).
    let ofSimpleText (agentId: string) (runId: string) (role: Role) (text: string) =
        AgentCompleted
            { AgentId = agentId
              ChildSessionId = None
              RunId = runId
              Role = role
              AuthorityRoot = None
              ProviderRun = None
              View = text
              Directory = None }

    let ofSimpleError (agentId: string) (runId: string) (role: Role) (message: string) =
        failed agentId runId (Some role) None "ERROR" message

    let abandoned (agentId: string) (reason: string) = AgentAbandoned(agentId, reason)

    let withRunIdentity (agentId: string) (runId: string) (role: Role) (outcome: AgentCompletionOutcome) =
        match outcome with
        | AgentCompleted payload ->
            AgentCompleted
                { payload with
                    RunId = runId
                    AgentId = agentId
                    Role = role }
        | AgentFailed payload ->
            AgentFailed
                { payload with
                    RunId = runId
                    AgentId = agentId
                    Role = Some role }
        | AgentAbandoned(_, reason) -> AgentAbandoned(agentId, reason)

/// One agent or PTY run completion cell for join wire / JoinDrain materialisation.
/// GREEN-5: agent facts live in Journal; mailbox agent channel is wake-only.
/// PTY facts live in mailbox as PtyJoinItem and project here for Join API.
type RunCompletion =
    {
        /// Unique identity for this run attempt.
        RunId: string

        /// The managed agent name (e.g. "coder", "inspector").
        AgentName: string

        /// Canonical role of the agent.
        Role: Role

        /// Agent finality only: completed | failed | abandoned (no aborted).
        Outcome: AgentCompletionOutcome

        /// When the run reached terminal state.
        CompletedAt: DateTimeOffset
    }

/// PTY mailbox helpers (GREEN-5): physical item ↔ join wire projection.
module PtyJoinItem =
    let ptyId (item: PtyJoinItem) =
        match item with
        | PtyExited e -> e.PtyId
        | PtyFailed f -> f.PtyId
        | PtyAborted a -> a.PtyId

/// Canonical RunCompletion → typed JoinItem projection (agent vs PTY).
module JoinItem =
    /// Durable agent completion → AgentItem. PTY facts stay PtyJoinItem through
    /// `ofPtyJoinItem`; this projection has one canonical agent input.
    let ofAgentRunCompletion (completion: RunCompletion) : JoinItem =
        match completion.Outcome with
        | AgentCompleted payload -> AgentItem(AgentCompletedItem payload)
        | AgentFailed payload -> AgentItem(AgentFailedItem payload)
        | AgentAbandoned(agentId, reason) -> AgentItem(AgentAbandonedItem(agentId, reason))

    /// PTY mailbox fact stays PtyJoinItem through join wire (EXEC-020).
    let ofPtyJoinItem (item: PtyJoinItem) : JoinItem = PtyItem item
