namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Foundation
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
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

    /// HOST-007 diagnostic. All emits go through `Diagnostic.emit` (CTX-014
    /// whitelist); there is deliberately no raw `console.warn` here.
    let private logWarn = Diagnostic.emit

    /// Write one setting into the Host config object, creating intermediate nodes.
    ///
    /// The Host hands the plugin the live instance-state config (`config/config.ts:607`
    /// returns `s.config` with no clone), and `plugin.init` runs before other services
    /// (`bootstrap.ts:36`), so a write here is in force before anything reads it.
    let private writeSetting (config: obj) (setting: CompactionSetting) : unit =
        let ensureChild (node: obj) head =
            if isNull node?(head) then
                node?(head) <- createObj []

            node?(head)

        let rec descend (node: obj) (path: string list) =
            match path with
            | [] -> ()
            | [ leaf ] -> node?(leaf) <- box setting.Required
            | head :: rest -> descend (ensureChild node head) rest

        descend config setting.Path

    /// Read a setting back, or `None` when the key is absent after writing.
    ///
    /// Absence after a write means the Host does not have this key at all — the config
    /// moved between versions — which is exactly the `SettingUnavailable` case. Writing
    /// blind and assuming success is what would let a renamed key silently re-enable
    /// automatic compaction.
    let private readSetting (config: obj) (setting: CompactionSetting) : bool option =
        let readLeaf (node: obj) leaf =
            if isNull node?(leaf) then
                None
            else
                Some(unbox<bool> node?(leaf))

        let rec descendNonNull (node: obj) (path: string list) =
            match path with
            | [] -> None
            | [ leaf ] -> readLeaf node leaf
            | head :: rest -> descendChild node?(head) rest

        and descendChild (child: obj) rest =
            if isNull child then None else descendNonNull child rest

        let descend (node: obj) (path: string list) =
            if isNull node then None else descendNonNull node path

        descend config setting.Path

    /// HOST-006 prevention layer: force every required setting off.
    ///
    /// Returns the first setting that could not be established, which
    /// `HostCompactionPolicy.judgeFirstTurn` turns into the startup verdict. The first
    /// rather than all of them: they share one root cause when the config shape moved,
    /// and an operator needs one actionable name.
    let enforceSettings (config: obj) : CompactionSetting option =
        if isNull config then
            // No config object means no way to disable anything. Reported as the first
            // required setting rather than silently passing.
            List.tryHead HostCompactionPolicy.requiredSettings
        else
            HostCompactionPolicy.requiredSettings
            |> List.tryFind (fun setting ->
                writeSetting config setting
                readSetting config setting <> Some setting.Required)

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
    let onSessionCompacting (input: obj) (output: obj) : unit =
        let sessionId =
            if isNull input || isNull input?sessionID then
                "<unknown>"
            else
                unbox<string> input?sessionID

        logWarn "compaction_started" [ "session_id", sessionId; "result", "cannot-refuse" ]

    /// The `experimental.compaction.autocontinue` hook.
    ///
    /// Always `enabled = false` (HOST-006). `compaction.auto = false` already makes the
    /// replay branch unreachable, so this is belt and braces — but it is the only
    /// vetoable synthetic-turn injection point, and an unanswered hook relies on an
    /// upstream default staying harmless.
    let onCompactionAutoContinue (input: obj) (output: obj) : unit =
        output?enabled <- box HostCompactionPolicy.autoContinueEnabled

    /// HOST-007 diagnostic for a reanchor that could not be appended.
    ///
    /// A failure here is deliberately not fatal to the turn that just completed.
    /// PERSIST-003 already owns the poisoned-journal path; what the reconcile loop must
    /// not do is throw and leave its `Running` latch set, which would silence every
    /// later pass for that session.
    ///
    /// The consequence of a missed reanchor is bounded and self-correcting: the
    /// compaction message stays in the transcript, so the next pass observes it again.
    let logReanchorFailure (sessionId: SessionId) (reason: string) : unit =
        logWarn "reanchor_failed" [ "session_id", SessionId.value sessionId; "result", reason ]

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
    let reanchorObserved
        (journal: AgentJournal)
        (sessionId: SessionId)
        (observed: ProviderRunIdentity list)
        : Task<Result<ProviderRunIdentity option, string>> =
        task {
            let snapshot = AgentJournal.snapshot journal

            let epoch =
                AgentProjection.tryFind sessionId snapshot.AgentProjections
                |> Option.bind (fun session -> session.PrefixEpoch)
                |> Option.defaultValue PrefixEpochProjection.empty

            // "Already reanchored" is answered from the durable projection, not a runtime
            // set: the observation repeats on every reconcile, and a memory-only set would
            // let a restart reanchor the same compaction a second time.
            match
                HostCompactionPolicy.nextReanchor observed (fun run -> PrefixEpochProjection.isReanchored run epoch)
            with
            | None -> return Ok None
            | Some run ->
                let fact =
                    ContextFact.ContextReanchored
                        {| SessionId = sessionId
                           PreviousEpochId = epoch.EpochId
                           NextEpochId = PrefixEpochId.next epoch.EpochId
                           ObservedCompactionRun = run |}

                match! AgentJournal.appendAgent (StreamId.Session sessionId) (Some run) fact journal with
                | Ok _ -> return Ok(Some run)
                | Error failure -> return Error(JournalAppendFailure.describe failure)
        }

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
    let judgeStartup
        (settingGap: CompactionSetting option)
        (sessionId: SessionId)
        (messages: SessionMessage list)
        : CompactionGateVerdict option =
        let window =
            messages
            |> List.map (fun message ->
                { CompletedAssistant = message.Role = "assistant" && message.Completed
                  Compaction = HostCompactionPolicy.isContainableCompaction message.IsCompaction })

        match HostCompactionPolicy.firstTurnCompactionRuns window with
        | None -> None
        | Some pseudoRunsOnFirstTurn ->
            Some(HostCompactionPolicy.judgeFirstTurn settingGap sessionId pseudoRunsOnFirstTurn)
