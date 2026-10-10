namespace Wanxiangshu.Host

open Wanxiangshu.Foundation.Identity

/// HOST-006: one Host compaction setting the plugin must force off.
///
/// The path is the config key as the Host spells it, so a mismatch is visible here
/// rather than discovered as a silently ignored write.
type CompactionSetting =
    {
        Path: string list
        /// What the setting must be. Every key is boolean today; the field exists so a
        /// future non-boolean key does not need a second mechanism.
        Required: bool
        Clause: string
        /// Why this one matters. Rendered into the startup failure, because an operator
        /// reading "compaction.auto could not be disabled" needs to know what breaks.
        Reason: string
    }

/// HOST-006 startup probe: one Host message reduced to the two facts the
/// first-turn window needs.
///
/// A record rather than a tuple, so the two flags cannot silently swap at a
/// call site.
type CompactionWindowMessage =
    { CompletedAssistant: bool
      Compaction: bool }

/// HOST-006 startup probe verdict.
[<RequireQualifiedAccess>]
type CompactionGateVerdict =
    /// Every required setting is off and the first turn produced no pseudo-run.
    | Satisfied
    /// A setting could not be written or read back. The config key moved, or the Host
    /// version does not have it.
    | SettingUnavailable of CompactionSetting
    /// The settings are off yet a compaction pseudo-run appeared on the first turn of
    /// the first managed session. Something compacts outside the configuration the
    /// plugin can reach.
    | CompactedDespiteSettings of session: SessionId * runs: int

/// HOST-006: the prevention layer's required settings, and the containment layer's
/// decision.
///
/// Pure. The config object and the Host snapshot are read by the adapter; what is
/// here is which keys are required and what an observation means, so both are
/// testable without a Host (verification-system-001 Pure laws).
[<RequireQualifiedAccess>]
module HostCompactionPolicy =

    /// HOST-006's prevention layer as CONFIG KEYS.
    ///
    /// The clause lists four behaviours; there are three keys, because `compaction.auto`
    /// closes two of them. Both the threshold path (`overflow.ts:28`) and the
    /// provider-error path (`processor.ts:608`) short-circuit on that one key, so
    /// "overflow compaction" has no switch of its own. Listing a fourth entry with no
    /// key would produce a setting the probe can never verify.
    ///
    /// `prune` is here and was not in the frozen clause. It bypasses the transform
    /// boundary and deletes persisted message rows outright (`compaction.ts:248`),
    /// which contradicts COMPANION-009's "the original Host transcript is never
    /// physically deleted" — and unlike the other behaviours, the containment layer
    /// cannot repair it: a deleted row is not a voided index, it is absent.
    let requiredSettings =
        [ { Path = [ "compaction"; "auto" ]
            Required = false
            Clause = "HOST-006"
            Reason =
              "automatic and predictive overflow compaction both short-circuit on this key "
              + "(overflow.ts:28, processor.ts:608); with it on, the Host rewrites context "
              + "whenever it judges the window full" }
          { Path = [ "compaction"; "prune" ]
            Required = false
            Clause = "COMPANION-009"
            Reason =
              "prune reads persisted messages directly and deletes them (compaction.ts:248), "
              + "bypassing the transform boundary; a deleted row cannot be reanchored" }
          { Path = [ "compaction"; "autocontinue" ]
            Required = false
            Clause = "HOST-006"
            Reason =
              "a synthetic continue turn after compaction issues a provider request the "
              + "plugin never claimed (PROMPT-005)" } ]

    /// The `experimental.compaction.autocontinue` answer.
    ///
    /// Always `false`. The hook is the only vetoable synthetic-turn injection point,
    /// and leaving it unanswered would mean relying on an upstream default staying
    /// `true`-but-harmless. `auto = false` already makes the replay branch
    /// unreachable, so this is belt and braces on purpose.
    let autoContinueEnabled = false

    /// HOST-006 containment: is this message a Host compaction pseudo-run.
    ///
    /// The three raw fields (`agent = "compaction"`, `mode = "compaction"`,
    /// `summary = true`) are folded into one predicate at the snapshot boundary
    /// (`SessionMessage.IsCompaction`), so this takes the folded answer. A caller that
    /// re-derived it from raw fields would be a second definition of the observation
    /// the whole containment layer keys on.
    ///
    /// Deliberately no source discrimination. A user's `/compact` and an unexpected
    /// Host compaction get identical handling (CTX-005), so there is no parameter for
    /// "which kind" and no branch to write.
    let isContainableCompaction (isCompaction: bool) = isCompaction

    /// HOST-006 containment: which observed compaction still needs a reanchor.
    ///
    /// `observed` is every compaction pseudo-run in the session's snapshot, oldest
    /// first. `isReanchored` answers whether the journal has already handled one.
    ///
    /// A predicate rather than a `Set`: the caller holds a keyed projection, so
    /// "has this run been reanchored" is an O(1) lookup it can already answer
    /// (PERSIST-008). Taking a `Set` would make the caller materialise every
    /// reanchored run in the session's history just to ask about the two or three in
    /// the current snapshot.
    ///
    /// Returns at most ONE run — the newest unhandled one — rather than all of them.
    /// A reanchor retires the prefix and zeroes coverage; doing that twice in a row
    /// changes nothing the second time, so emitting several would produce facts whose
    /// only effect is to advance the epoch counter. The newest is chosen because it is
    /// the one whose numbering the current transcript actually reflects.
    let nextReanchor
        (observed: ProviderRunIdentity list)
        (isReanchored: ProviderRunIdentity -> bool)
        : ProviderRunIdentity option =
        observed |> List.filter (isReanchored >> not) |> List.tryLast

    /// HOST-006 startup probe: how many compaction pseudo-runs sit inside the
    /// first-turn window.
    ///
    /// The window runs from the start of the session to the first completed assistant
    /// message, inclusive. That boundary is the earliest observable end of the first
    /// turn; a first turn is necessarily far below any threshold, so a compaction
    /// inside the window cannot come from the setting the plugin already disabled.
    ///
    /// A pseudo-run after the window is containment work: a user's `/compact` or a
    /// later-round compaction must reanchor, not refuse startup. `None` means the
    /// first turn is still open and the probe stays armed.
    let firstTurnCompactionRuns (messages: CompactionWindowMessage list) : int option =
        match messages |> List.tryFindIndex (fun message -> message.CompletedAssistant) with
        | None -> None
        | Some boundary ->
            messages
            |> List.take (boundary + 1)
            |> List.filter (fun message -> message.Compaction)
            |> List.length
            |> Some

    /// HOST-006: the startup probe judgement.
    ///
    /// The probe deliberately does NOT assert "no pseudo-run ever". That would judge a
    /// user's legitimate `/compact` as a Host contract violation. It asserts the far
    /// narrower claim that no compaction happened on the FIRST turn of the first
    /// managed session — a turn that is necessarily far below any threshold, so an
    /// automatic compaction there cannot be legitimate.
    ///
    /// Residual misjudgement: a user manually compacting an empty session between
    /// plugin start and the first turn completing. The cost is one startup refusal
    /// with a stated reason. The inverse — missing a second compaction implementation
    /// whose config the plugin cannot reach — would run two compression systems in
    /// parallel with no symptom.
    let judgeFirstTurn
        (unavailable: CompactionSetting option)
        (session: SessionId)
        (pseudoRunsOnFirstTurn: int)
        : CompactionGateVerdict =
        match unavailable with
        | Some setting -> CompactionGateVerdict.SettingUnavailable setting
        | None when pseudoRunsOnFirstTurn > 0 ->
            CompactionGateVerdict.CompactedDespiteSettings(session, pseudoRunsOnFirstTurn)
        | None -> CompactionGateVerdict.Satisfied

    /// The `HostContractUnsupported` message for a failed gate.
    let describeVerdict (verdict: CompactionGateVerdict) =
        match verdict with
        | CompactionGateVerdict.Satisfied -> "Host compaction contract satisfied"
        | CompactionGateVerdict.SettingUnavailable setting ->
            sprintf
                "HostContractUnsupported: %s could not be set to %b (%s). %s"
                (String.concat "." setting.Path)
                setting.Required
                setting.Clause
                setting.Reason
        | CompactionGateVerdict.CompactedDespiteSettings(session, runs) ->
            sprintf
                "HostContractUnsupported: session %s produced %d compaction run(s) on its first turn "
                (SessionId.value session)
                runs
            + "despite every compaction setting being disabled (HOST-006). A first turn is far below "
            + "any threshold, so this indicates a compaction implementation the plugin's configuration "
            + "does not reach; running two compression systems would grind the frame sequence with no "
            + "visible symptom."
