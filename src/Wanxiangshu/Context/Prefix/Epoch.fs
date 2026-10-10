namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

/// COMPANION-009: which prefix generation is in force.
///
/// `Snapshot = None` is the honest representation of two different histories that
/// call for identical behaviour: nothing has been promoted yet, and a Host
/// compaction retired what had been promoted (HOST-006). Both mean "send raw
/// history", so they are one state, not two.
///
/// `PrefixSnapshot` comes from `Domain.PrefixCandidate`: the attempt profile carries
/// one (PROMPT-008), the selector builds one (CTX-011), and this fold validates one.
/// A separate copy here would let the profile's snapshot and the committed snapshot
/// differ in shape, and CTX-012 requires them to be byte-identical.
type ActivePrefixEpoch =
    {
        EpochId: PrefixEpochId
        Snapshot: PrefixSnapshot option
        /// HOST-006: which compaction pseudo-runs have already been reanchored.
        ///
        /// Durable, because a compaction message stays in the Host transcript forever.
        /// Every later reconcile observes the same run again, and the epoch check alone
        /// does NOT stop that: by then the epoch has moved on, so a freshly decided
        /// reanchor for that old compaction carries a `PreviousEpochId` that MATCHES the
        /// current one and would be accepted — advancing the epoch again and zeroing
        /// coverage the session had legitimately rebuilt.
        ///
        /// The epoch check and this set therefore guard different failures. The epoch
        /// check catches a replayed LINE (a crash between append and fold). This set
        /// catches a repeated DECISION (the same observation acted on twice). Neither
        /// subsumes the other.
        ///
        /// Bounded by the number of compactions in one session, not by turns.
        ReanchoredRuns: Set<ProviderRunIdentity>
        ReanchoredTenures: Set<string>
    }

/// Why a prefix-epoch line was refused. One case per PERSIST-010 rule.
[<RequireQualifiedAccess>]
type PrefixFoldRejection =
    /// The line was written against a different epoch than the one in force.
    | StalePrefixEpoch of expected: PrefixEpochId * actual: PrefixEpochId
    /// `NextEpochId` was not the successor of `PreviousEpochId`.
    | NonSequentialPrefixEpoch
    /// CTX-011: a promoted cutoff may not be earlier than the committed one.
    | CutoffRetreated of committed: int * proposed: int
    /// CTX-011: the candidate is indistinguishable from what is already committed,
    /// so promoting it would burn an epoch and a cold boundary for no change.
    | CandidateNotNew
    /// HOST-006: this compaction pseudo-run was already reanchored.
    ///
    /// Separate from `StalePrefixEpoch` because it is reachable with a perfectly
    /// current epoch — see `ReanchoredRuns`. Collapsing the two would make the fold
    /// accept a second reanchor for one compaction whenever any other epoch change
    /// happened in between.
    | CompactionAlreadyReanchored of run: ProviderRunIdentity
    | TenureAlreadyReanchored of incumbencyId: string

module PrefixEpochProjection =

    let empty =
        { EpochId = PrefixEpochId.initial
          Snapshot = None
          ReanchoredRuns = Set.empty
          ReanchoredTenures = Set.empty }

    let private rebaseSnapshot
        (nextEpoch: PrefixEpochId)
        (candidate: PrefixSnapshot)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, PrefixFoldRejection> =
        match state.Snapshot with
        | Some committed when candidate.CutoffExclusive < committed.CutoffExclusive ->
            Error(PrefixFoldRejection.CutoffRetreated(committed.CutoffExclusive, candidate.CutoffExclusive))
        | Some committed when PrefixSnapshot.sameIdentity candidate committed ->
            Error PrefixFoldRejection.CandidateNotNew
        | _ ->
            Ok
                { state with
                    EpochId = nextEpoch
                    Snapshot = Some candidate }

    /// PERSIST-010 `PrefixRebaseCommitted`: a probe produced a valid terminal, so
    /// its candidate becomes the committed epoch.
    ///
    /// The candidate arrives whole rather than field by field. CTX-012 requires the
    /// promoted snapshot to be byte-identical to the one the successful request
    /// used — in particular its SealRoot, so the next request continues the same
    /// prefix instead of paying a second cold boundary. Rebuilding it here would be
    /// a second construction site that could disagree.
    let applyRebase
        (previousEpoch: PrefixEpochId)
        (nextEpoch: PrefixEpochId)
        (candidate: PrefixSnapshot)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, PrefixFoldRejection> =
        if previousEpoch <> state.EpochId then
            Error(PrefixFoldRejection.StalePrefixEpoch(state.EpochId, previousEpoch))
        elif nextEpoch <> PrefixEpochId.next previousEpoch then
            Error PrefixFoldRejection.NonSequentialPrefixEpoch
        else
            rebaseSnapshot nextEpoch candidate state

    /// PERSIST-010 `ContextReanchored`: HOST-006 containment.
    ///
    /// Retirement, not replacement. The projection cannot repoint the snapshot at a
    /// position after the Host summary: `CutoffExclusive` belongs to the voided
    /// current-generation XTrace semantic-turn numbering, and the Companion may have
    /// been behind the Host when compaction happened, so any new cutoff would be a
    /// claim the journal cannot support.
    ///
    /// The epoch still advances. This is a real cold boundary — the provider-visible
    /// prefix changed and the seal barrier broke — and COMPANION-009's byte-stability
    /// guarantee is scoped to one epoch, so staying on the same number would state
    /// something false.
    ///
    /// `observedRun` is recorded so the same compaction cannot be reanchored twice.
    /// That check has to be here and not only at the decision site: the compaction
    /// message stays in the transcript forever, so once the epoch has moved on for any
    /// other reason, a freshly decided reanchor for that old run would carry a matching
    /// `PreviousEpochId` and be accepted — advancing the epoch again and zeroing
    /// coverage the session had legitimately rebuilt.
    let applyReanchor
        (previousEpoch: PrefixEpochId)
        (nextEpoch: PrefixEpochId)
        (observedRun: ProviderRunIdentity)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, PrefixFoldRejection> =
        if Set.contains observedRun state.ReanchoredRuns then
            Error(PrefixFoldRejection.CompactionAlreadyReanchored observedRun)
        elif previousEpoch <> state.EpochId then
            Error(PrefixFoldRejection.StalePrefixEpoch(state.EpochId, previousEpoch))
        elif nextEpoch <> PrefixEpochId.next previousEpoch then
            Error PrefixFoldRejection.NonSequentialPrefixEpoch
        else
            Ok
                { EpochId = nextEpoch
                  Snapshot = None
                  ReanchoredRuns = Set.add observedRun state.ReanchoredRuns
                  ReanchoredTenures = state.ReanchoredTenures }

    /// HOST-006: has this compaction pseudo-run already been reanchored.
    ///
    /// The predicate `HostCompactionPolicy.nextReanchor` consumes. Exposed as a query
    /// so the adapter reads it from the projection rather than keeping a runtime set
    /// that a restart would lose.
    let isReanchored (run: ProviderRunIdentity) (state: ActivePrefixEpoch) = Set.contains run state.ReanchoredRuns

    let applyTenureReanchor
        (previousEpoch: PrefixEpochId)
        (nextEpoch: PrefixEpochId)
        (incumbencyId: string)
        (state: ActivePrefixEpoch)
        : Result<ActivePrefixEpoch, PrefixFoldRejection> =
        if Set.contains incumbencyId state.ReanchoredTenures then
            Error(PrefixFoldRejection.TenureAlreadyReanchored incumbencyId)
        elif previousEpoch <> state.EpochId then
            Error(PrefixFoldRejection.StalePrefixEpoch(state.EpochId, previousEpoch))
        elif nextEpoch <> PrefixEpochId.next previousEpoch then
            Error PrefixFoldRejection.NonSequentialPrefixEpoch
        else
            Ok
                { EpochId = nextEpoch
                  Snapshot = None
                  ReanchoredRuns = state.ReanchoredRuns
                  ReanchoredTenures = Set.add incumbencyId state.ReanchoredTenures }

    let isTenureReanchored (incumbencyId: string) (state: ActivePrefixEpoch) =
        Set.contains incumbencyId state.ReanchoredTenures

    /// COMPANION-009: is a companion-memory prefix in force for this session.
    let hasSnapshot (state: ActivePrefixEpoch) = Option.isSome state.Snapshot

    /// PERSIST-010 prefix-epoch absorption policy: `None` means the caller must
    /// write nothing, `Some reason` is the requirement text a refusal is
    /// reported with.
    ///
    /// `StalePrefixEpoch` is absorbed, unlike its frame counterpart. Every
    /// epoch-advancing line carries the epoch it expected, so a replayed rebase or
    /// reanchor — the crash-recovery path in CTX-012 deliberately re-attempts
    /// both — arrives stale and means "already applied". That is what makes
    /// recovery idempotent without a second dedupe mechanism. `CandidateNotNew`
    /// is absorbed for the same reason: CTX-011 already refuses to build such a
    /// probe, so a line carrying one is a replay.
    let describe (rejection: PrefixFoldRejection) : string option =
        match rejection with
        | PrefixFoldRejection.StalePrefixEpoch _
        | PrefixFoldRejection.CandidateNotNew
        // HOST-006: the same compaction observed twice. Absorbed rather than fatal —
        // the observation repeats on every reconcile because the compaction message
        // stays in the transcript, so this is the expected steady state, not corruption.
        | PrefixFoldRejection.CompactionAlreadyReanchored _ -> None
        | PrefixFoldRejection.TenureAlreadyReanchored _ -> None
        | PrefixFoldRejection.NonSequentialPrefixEpoch ->
            Some "prefix epoch is not the successor of the previous one (PERSIST-010)"
        | PrefixFoldRejection.CutoffRetreated(committed, proposed) ->
            Some(sprintf "promoted cutoff %d is earlier than the committed %d (CTX-011)" proposed committed)
