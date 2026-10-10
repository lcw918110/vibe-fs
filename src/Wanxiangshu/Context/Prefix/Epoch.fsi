namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type ActivePrefixEpoch =
    { EpochId: PrefixEpochId
      Snapshot: PrefixSnapshot option
      ReanchoredRuns: Set<ProviderRunIdentity>
      ReanchoredTenures: Set<string> }

[<RequireQualifiedAccess>]
type PrefixFoldRejection =
    | StalePrefixEpoch of expected: PrefixEpochId * actual: PrefixEpochId
    | NonSequentialPrefixEpoch
    | CutoffRetreated of committed: int * proposed: int
    | CandidateNotNew
    | CompactionAlreadyReanchored of run: ProviderRunIdentity
    | TenureAlreadyReanchored of incumbencyId: string

module PrefixEpochProjection =
    val empty: ActivePrefixEpoch

    val applyRebase:
        previousEpoch: PrefixEpochId ->
        nextEpoch: PrefixEpochId ->
        candidate: PrefixSnapshot ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, PrefixFoldRejection>

    val applyReanchor:
        previousEpoch: PrefixEpochId ->
        nextEpoch: PrefixEpochId ->
        observedRun: ProviderRunIdentity ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, PrefixFoldRejection>

    val isReanchored: run: ProviderRunIdentity -> state: ActivePrefixEpoch -> bool

    val applyTenureReanchor:
        previousEpoch: PrefixEpochId ->
        nextEpoch: PrefixEpochId ->
        incumbencyId: string ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, PrefixFoldRejection>

    val isTenureReanchored: incumbencyId: string -> state: ActivePrefixEpoch -> bool
    val hasSnapshot: state: ActivePrefixEpoch -> bool

    /// Absorption policy for prefix observations. `None` means the observation is
    /// the expected steady state or an already-captured candidate, so the caller
    /// must write nothing; `Some reason` is the requirement text a refusal is
    /// reported with.
    val describe: rejection: PrefixFoldRejection -> string option
