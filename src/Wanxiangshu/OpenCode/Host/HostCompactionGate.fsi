namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal

/// HOST-006 Host adapter: force the compaction settings off, refuse the compaction
/// hooks, and turn an observed pseudo-run into one `ContextReanchored`.
///
/// The decisions live in `Domain.HostCompactionPolicy`; this module only reads and
/// writes Host objects and the journal. `HostCompactionPolicy` is what a layer-1 test
/// exercises, which is why the judgement is not inlined here (verification-system-001 Pure laws).
module HostCompactionGate =

    /// HOST-006 prevention layer: force every required setting off.
    ///
    /// Returns the first setting that could not be established, which
    /// `HostCompactionPolicy.judgeFirstTurn` turns into the startup verdict. The first
    /// rather than all of them: they share one root cause when the config shape moved,
    /// and an operator needs one actionable name.
    val enforceSettings: config: obj -> CompactionSetting option

    /// The `experimental.session.compacting` hook.
    ///
    /// The hook cannot veto — its output is `{ context; prompt? }` with no cancel field
    /// (`plugin/index.ts:305`), and `plugin.trigger` discards the return value. So this
    /// does the only thing available: leaves the prompt untouched and records that a
    /// compaction is starting, which is a diagnostic (HOST-007), not a control action.
    ///
    /// Registering it anyway matters. Without a registration the plugin would learn of
    /// a compaction only when its pseudo-run appears in a later snapshot; with one, the
    /// containment layer has a same-turn signal it can log against, and the absence of
    /// a cancel field is documented at the boundary rather than inferred.
    val onSessionCompacting: input: obj -> output: obj -> unit

    /// The `experimental.compaction.autocontinue` hook.
    ///
    /// Always `enabled = false` (HOST-006). `compaction.auto = false` already makes the
    /// replay branch unreachable, so this is belt and braces — but it is the only
    /// vetoable synthetic-turn injection point, and an unanswered hook relies on an
    /// upstream default staying harmless.
    val onCompactionAutoContinue: input: obj -> output: obj -> unit

    /// HOST-007 diagnostic for a reanchor that could not be appended.
    ///
    /// A failure here is deliberately not fatal to the turn that just completed.
    /// PERSIST-003 already owns the poisoned-journal path; what the reconcile loop must
    /// not do is throw and leave its `Running` latch set, which would silence every
    /// later pass for that session.
    ///
    /// The consequence of a missed reanchor is bounded and self-correcting: the
    /// compaction message stays in the transcript, so the next pass observes it again.
    val logReanchorFailure: sessionId: SessionId -> reason: string -> unit

    /// HOST-006 containment: write one `ContextReanchored` for an observed pseudo-run.
    ///
    /// `observed` is every compaction run in the session's snapshot, oldest first.
    /// Which one to act on — and whether any is left — is
    /// `HostCompactionPolicy.nextReanchor`'s decision; this function supplies the
    /// "already handled" predicate from the journal and does the append.
    ///
    /// Idempotency has two independent guards, deliberately. Here, the predicate skips
    /// a run the journal already reanchored. At the fold, `PrefixEpochProjection`
    /// refuses a stale epoch. The second is what survives a crash between the decision
    /// and the append.
    val reanchorObserved:
        journal: AgentJournal ->
        sessionId: SessionId ->
        observed: ProviderRunIdentity list ->
            Task<Result<ProviderRunIdentity option, string>>

    /// HOST-006 startup probe: judge the first turn of the first managed session.
    ///
    /// `None` means "not yet applicable" — this snapshot does not represent a completed
    /// first turn, so there is nothing to judge. The caller keeps the probe armed.
    ///
    /// A first turn is recognised by the presence of a COMPLETED assistant message, and
    /// the pseudo-runs counted are the ones inside that same window — everything up to
    /// and including the first completed assistant. The alternative — judging on the
    /// first snapshot of any kind — would fire mid-stream, before the Host has had the
    /// opportunity to compact at all, and would therefore pass unconditionally: a probe
    /// that cannot fail is not a probe.
    ///
    /// Why the first turn specifically. A first turn is necessarily far below any
    /// context threshold, so an automatic compaction there cannot be legitimate. A
    /// pseudo-run inside the window means something compacted outside the configuration
    /// the plugin can reach, and that is the state HOST-006 refuses to run in:
    /// reanchoring every few rounds means probe coverage never accumulates while
    /// everything looks normal from outside. A pseudo-run after the window is a user
    /// `/compact` or a later-round compaction; it belongs to the containment layer,
    /// which must reanchor rather than refuse startup.
    ///
    /// The residual misjudgement is a user manually compacting an empty session between
    /// plugin start and the first turn finishing. That costs one startup refusal with a
    /// stated reason. The inverse — missing a second compaction implementation — has no
    /// symptom at all.
    val judgeStartup:
        settingGap: CompactionSetting option ->
        sessionId: SessionId ->
        messages: SessionMessage list ->
            CompactionGateVerdict option
