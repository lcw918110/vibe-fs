namespace Wanxiangshu.Foundation

open System

/// Typed identities. The cheapest border guard: if two concepts share a
/// primitive type, the compiler cannot tell them apart and every function
/// signature becomes a promise nobody checks.
///
/// PROMPT-001 is the reason this module exists at all: `PhysicalUserMessage ≠
/// AuthorityTurn`. As long as both are `string`, or share one generic message-id
/// type, that clause is a comment. Here it is a type error.
module Identity =

    // ── runtime and transport ───────────────────────────────────────────────

    /// One plugin process incarnation. Journal envelopes are ordered per runtime.
    type RuntimeId = private RuntimeId of string

    /// A Host session. Never a child, never a PTY.
    type SessionId = private SessionId of string

    /// A Host session created as a direct child through a fork runtime.
    type ChildId = private ChildId of string

    /// An OS process the plugin owns.
    type ProcessId = private ProcessId of string

    type EventId = private EventId of string

    /// Monotonic per-runtime append counter. Ordering within a runtime is by
    /// this; ordering across runtimes is by ObservedAt (PERSIST-001).
    type LocalSeq = private LocalSeq of int64

    /// Process-local journal subscription cursor. Advances only on a successful
    /// fold after append. Not a program counter: it names a durable position
    /// observers can await (Join wake), not "where control goes next".
    type JournalRevision = private JournalRevision of int64

    type LocalEpoch = int64
    type ObservedAt = DateTimeOffset

    // ── prompt authority (docs/what/prompt.md) ──────────────────────────────────────────

    /// A complete conversation sequence caused by one Authority Root
    /// (PROMPT-002). Continuations extend it; they never create a new one.
    type LogicalRunId = private LogicalRunId of string

    /// The physical user message that became an Authority Root. Only this
    /// identity may change the execution profile (PROMPT-002).
    type AuthorityRootUserMessageId = private AuthorityRootUserMessageId of string

    /// A `role=user` message on the wire. Transport format, not authority
    /// (PROMPT-001). Zero-width characters, whitespace, templates, timing and
    /// text length are not identity evidence.
    type PhysicalUserMessageId = private PhysicalUserMessageId of string

    /// Stable idempotency key for one dispatched prompt (PROMPT-011). Written
    /// into Host prompt metadata so an unresolved claim can be reconciled after
    /// a crash. Derived, never invented at the call site.
    type PromptKey = private PromptKey of string

    /// The identity of an interaction-repair request. Its payload digest is
    /// derived from the request, the terminal provider run and the repair kind.
    type RepairRequestId = private RepairRequestId of string

    /// What the Host prompt-send endpoint hands back: an `accepted-*` admission
    /// receipt.
    ///
    /// PROMPT-005 forbids treating this as a physical message id. Keeping it in
    /// its own type is how that clause is enforced rather than remembered —
    /// there is no function from a receipt to any message identity.
    type TransportReceipt = private TransportReceipt of string

    // ── provider runs (docs/what/host.md) ─────────────────────────────────────────────

    /// One provider request and the assistant message it produces.
    ///
    /// HOST-010 and HOST-011: the Host creates and persists the assistant
    /// message before triggering `messages.transform`, and hands the same id to
    /// every tool call in that run as `ToolContext.messageID`. So one assistant
    /// message id is exactly one provider run, and this is the only per-run
    /// identity the SDK exposes — `modelID` and `providerID` are configuration
    /// labels, identical across every run of a session.
    ///
    /// One Host assistant message is one provider request is one attempt, so
    /// there is exactly one type for it. SSOT used to name the concept twice
    /// (`ProviderAttemptIdentity` in PROMPT-008 and provider-attempt-recovery-007); the wording is
    /// now unified, because two types would make "are these two identities of the
    /// same attempt equal" an askable but meaningless question.
    type ProviderRunIdentity = private ProviderRunIdentity of string



    /// One tool invocation inside a provider run. `ToolContext.callID`.
    ///
    /// Distinct from ProviderRunIdentity because causal idempotency requires both: two
    /// distinct tool invocations must differ in run AND in call. Sharing a type would
    /// make that check expressible but meaningless.
    type ToolCallId = private ToolCallId of string

    /// One persisted Host ToolPart. Distinct from the provider-facing call id:
    /// the former identifies the Host object, the latter identifies one invocation.
    type HostToolPartId = private HostToolPartId of string

    /// One physical part inside a Host transcript message. OpenCode gives text,
    /// reasoning and tool parts stable ids even while the surrounding assistant
    /// message is still growing. XTrace stable capture uses this address rather
    /// than the part's mutable array position.
    type HostMessagePartId = private HostMessagePartId of string

    /// A Host transcript message address (HOST-013).
    ///
    /// The raw message's `info.id` / `id` — the same address Session snapshot
    /// lookups use. Distinct from `PhysicalUserMessageId` (user-only PROMPT-001
    /// identity), `AuthorityRootUserMessageId`, `ProviderRunIdentity` and
    /// `ToolCallId`: those are semantic identities, this names a transcript
    /// position any message (user, assistant, tool) can occupy.
    type TranscriptMessageAddress = private TranscriptMessageAddress of string

    /// A transcript gap where a synthetic half anchors (HOST-013).
    ///
    /// `Start` = before the first real message; `Before id` = between the
    /// messages preceding and including `id`; `After id` = between the messages
    /// including and following `id`. A pair's two halves carry independent gaps:
    /// the bracket spans a real tool batch (`real calls → synthetic call →
    /// real results → synthetic result`).
    [<RequireQualifiedAccess>]
    type TranscriptGap =
        | Start
        | Before of TranscriptMessageAddress
        | After of TranscriptMessageAddress

    /// Which system prompt a provider request carried (PROMPT-008).
    ///
    /// An id, not the text. The prompt body lives under `resources/provider/` and is
    /// loaded at the Host boundary; carrying it in the profile would put a
    /// multi-kilobyte string into every diagnostic and every journal line that
    /// mentions an attempt.
    type SystemPromptId = private SystemPromptId of string

    // ── blob storage (PERSIST-007) ──────────────────────────────────────────

    /// Where a large body lives outside the NDJSON line. PERSIST-007 keeps the
    /// journal line small: the blob is written first, the event references it.
    type BlobRef = private BlobRef of string

    /// Content digest of a blob body. Separate from `BlobRef` because the two
    /// answer different questions — "where is it" versus "is it the bytes I
    /// meant" — and a single string type would let a caller pass either.
    type BlobDigest = private BlobDigest of string

    /// Retired. The Companion frame sequence this id numbered is gone and
    /// nothing produces it any more; the type survives only as a decode helper.
    type FrameEpochId = private FrameEpochId of int64

    /// Which generation of the provider-visible prefix is in force
    /// (COMPANION-009). Advances when a reanchor retires the snapshot.
    ///
    /// Its own type, not `FrameEpochId`'s: the prefix generation is the live one
    /// and the frame generation is retired, so one type would let a fold accept a
    /// retired number as the prefix generation.
    type PrefixEpochId = private PrefixEpochId of int64

    // ── execution handles (docs/what/execution.md) ─────────────────────────────────────────

    /// A forked agent child, persisted across restart (EXEC-009).
    type AgentHandleId = private AgentHandleId of string

    /// A PTY session. DevOps only (AGENT-013).
    type PtyHandleId = private PtyHandleId of string

    /// One orchestrated Manager job: one worktree, one Manager (ORCH-003).
    type ManagerJobId = private ManagerJobId of string

    /// EXEC-009 requires typed handles per resource kind. The union exists so a
    /// join mailbox can hold all three without erasing which kind it holds; the
    /// cases are not interchangeable.
    [<RequireQualifiedAccess>]
    type HandleId =
        | Agent of AgentHandleId
        | Pty of PtyHandleId
        | ManagerJob of ManagerJobId

    // ── git and worktrees (docs/what/orchestrator.md) ─────────────────────────────────────────

    /// Stable identity of a worktree, independent of where it currently lives.
    /// ORCH-006 records both: recovery locates by identity, diagnostics show the
    /// path. A path is mutable state; an identity is not.
    type WorktreeIdentity = private WorktreeIdentity of string

    /// Filesystem location of a worktree. Diagnostic only.
    type WorktreePath = private WorktreePath of string

    /// A branch reference frozen at fork time via `git symbolic-ref`
    /// (ORCH-008). Never resolved to HEAD on failure.
    type TargetRef = private TargetRef of string

    /// A commit. Separate from TargetRef because ORCH-007's recovery compares a
    /// ref's current head against an expected commit; one type for both would
    /// make that comparison a tautology.
    type CommitHash = private CommitHash of string

    // ── constructors and accessors ──────────────────────────────────────────
    //
    // `create` is total: validation belongs to the boundary that has the Host
    // context to fail closed with a reason (HOST-001), not to a string wrapper.

    module RuntimeId =
        let create (value: string) = RuntimeId value
        let value (RuntimeId v) = v

    module SessionId =
        let create (value: string) = SessionId value
        let value (SessionId v) = v

    module ChildId =
        let create (value: string) = ChildId value
        let value (ChildId v) = v

    module ProcessId =
        let create (value: string) = ProcessId value
        let value (ProcessId v) = v

    module EventId =
        let create (value: string) = EventId value
        let value (EventId v) = v

    module LocalSeq =
        let create (v: int64) = LocalSeq v
        let value (LocalSeq v) = v

    module JournalRevision =
        let create (v: int64) = JournalRevision v
        let value (JournalRevision v) = v
        let initial = JournalRevision 0L
        let next (JournalRevision v) = JournalRevision(v + 1L)
        let isAfter (JournalRevision a) (JournalRevision b) = a > b

    module LogicalRunId =
        let create (value: string) = LogicalRunId value
        let value (LogicalRunId v) = v

    module AuthorityRootUserMessageId =
        let create (value: string) = AuthorityRootUserMessageId value
        let value (AuthorityRootUserMessageId v) = v

    module PhysicalUserMessageId =
        let create (value: string) = PhysicalUserMessageId value
        let value (PhysicalUserMessageId v) = v
        let isNonBlank (PhysicalUserMessageId v) = not (String.IsNullOrWhiteSpace v)

        /// Promote a physical message to Authority Root.
        ///
        /// Deliberately explicit and one-way. PROMPT-005 allows this only once
        /// `PhysicalAccepted` is proven, so every call site is a place where
        /// that proof must exist. There is no inverse: an Authority Root is a
        /// semantic fact, not a wire address.
        let promoteToAuthorityRoot (PhysicalUserMessageId v) = AuthorityRootUserMessageId v

    module PromptKey =
        let create (value: string) = PromptKey value
        let value (PromptKey v) = v

    module RepairRequestId =
        let create (value: string) = RepairRequestId value
        let value (RepairRequestId v) = v

    module TransportReceipt =
        let create (value: string) = TransportReceipt value
        let value (TransportReceipt v) = v

        /// Host admission ids are `accepted-*`. A receipt that does not look
        /// like one means the Host returned something else and the caller must
        /// decide, so this is a predicate rather than a validating constructor.
        let isAdmissionShaped (TransportReceipt v) =
            v.StartsWith("accepted-", StringComparison.Ordinal)

    module ProviderRunIdentity =
        let create (value: string) = ProviderRunIdentity value
        let value (ProviderRunIdentity v) = v



    module ToolCallId =
        let create (value: string) = ToolCallId value
        let value (ToolCallId v) = v

    module HostToolPartId =
        let create (value: string) = HostToolPartId value
        let value (HostToolPartId v) = v

    module HostMessagePartId =
        let create (value: string) = HostMessagePartId value
        let value (HostMessagePartId v) = v

    module TranscriptMessageAddress =
        let create (value: string) = TranscriptMessageAddress value
        let value (TranscriptMessageAddress v) = v

    module SystemPromptId =
        let create (value: string) = SystemPromptId value
        let value (SystemPromptId v) = v

    module BlobRef =
        let create (value: string) = BlobRef value
        let value (BlobRef v) = v

    module BlobDigest =
        let create (value: string) = BlobDigest value
        let value (BlobDigest v) = v

    module FrameEpochId =
        let create (value: int64) = FrameEpochId value
        let value (FrameEpochId v) = v
        let initial = FrameEpochId 0L
        let next (FrameEpochId v) = FrameEpochId(v + 1L)

    module PrefixEpochId =
        let create (value: int64) = PrefixEpochId value
        let value (PrefixEpochId v) = v
        let initial = PrefixEpochId 0L
        let next (PrefixEpochId v) = PrefixEpochId(v + 1L)

    module AgentHandleId =
        let create (value: string) = AgentHandleId value
        let value (AgentHandleId v) = v

    module PtyHandleId =
        let create (value: string) = PtyHandleId value
        let value (PtyHandleId v) = v

    module ManagerJobId =
        let create (value: string) = ManagerJobId value
        let value (ManagerJobId v) = v

    module HandleId =
        /// Diagnostic rendering only (HOST-007). Never parsed back: EXEC-009
        /// forbids turning a retired handle string into a fresh fork target.
        let describe (handle: HandleId) =
            match handle with
            | HandleId.Agent id -> "agent:" + AgentHandleId.value id
            | HandleId.Pty id -> "pty:" + PtyHandleId.value id
            | HandleId.ManagerJob id -> "manager-job:" + ManagerJobId.value id

        /// The agent handle inside an agent handle, or None for the other kinds.
        ///
        /// Not the inverse of `describe`: this reads the typed case out of the
        /// union, it does not parse a rendered string. EXEC-009's prohibition is on
        /// reviving a retired id as a fork target, so callers must still ask
        /// `HandleProjection.isRetired` before acting on the result.
        let tryAgent (handle: HandleId) =
            match handle with
            | HandleId.Agent id -> Some id
            | HandleId.Pty _
            | HandleId.ManagerJob _ -> None

    module WorktreeIdentity =
        let create (value: string) = WorktreeIdentity value
        let value (WorktreeIdentity v) = v

    module WorktreePath =
        let create (value: string) = WorktreePath value
        let value (WorktreePath v) = v

    module TargetRef =
        let create (value: string) = TargetRef value
        let value (TargetRef v) = v

    module CommitHash =
        let create (value: string) = CommitHash value
        let value (CommitHash v) = v
