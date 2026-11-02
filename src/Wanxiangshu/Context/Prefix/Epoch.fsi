namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type ArchivedScope =
    { SourceEpochId: PrefixEpochId
      TargetEpochId: PrefixEpochId
      ResetCallId: string
      ArchiveRef: BlobRef
      ArchiveDigest: BlobDigest }

type ActivePrefixEpoch

[<RequireQualifiedAccess>]
type PrefixFoldRejection =
    | StalePrefixEpoch of expected: PrefixEpochId * actual: PrefixEpochId
    | NonSequentialPrefixEpoch
    | CompactionAlreadyReanchored of run: ProviderRunIdentity
    | TenureAlreadyReanchored of incumbencyId: string
    | ScopeAlreadyReanchored of callId: string

type PrefixReanchorCandidate =
    { PreviousEpochId: PrefixEpochId
      NextEpochId: PrefixEpochId
      CompactionRun: ProviderRunIdentity }

module PrefixEpochProjection =
    val empty: ActivePrefixEpoch
    val ofGeneration: generation: obj -> ActivePrefixEpoch
    val epochId: state: ActivePrefixEpoch -> PrefixEpochId
    val scopeCount: state: ActivePrefixEpoch -> int
    val reanchoredScopes: state: ActivePrefixEpoch -> string array
    val reanchoredTenures: state: ActivePrefixEpoch -> string array
    val reanchoredRuns: state: ActivePrefixEpoch -> ProviderRunIdentity array
    val lastScopeArchive: state: ActivePrefixEpoch -> ArchivedScope option

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

    val applyScopeReanchor:
        previousEpoch: PrefixEpochId ->
        nextEpoch: PrefixEpochId ->
        callId: string ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, PrefixFoldRejection>

    val applyArchivedScopeReanchor:
        previousEpoch: PrefixEpochId ->
        nextEpoch: PrefixEpochId ->
        callId: string ->
        blob: BlobRef option ->
        digest: BlobDigest option ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, PrefixFoldRejection>

    val scopeQuestionReceipt: callId: string -> state: ActivePrefixEpoch -> string option

    val recordScopeQuestion:
        targetEpoch: PrefixEpochId ->
        callId: string ->
        digest: string ->
        state: ActivePrefixEpoch ->
            Result<ActivePrefixEpoch, string>

    val isScopeReanchored: callId: string -> state: ActivePrefixEpoch -> bool

    /// HOST-006: the newest observed compaction not yet reanchored, as a proposed
    /// line. Proposing never changes the projection (wanxiangli/prefix).
    val proposeCompaction:
        observed: ProviderRunIdentity list -> state: ActivePrefixEpoch -> PrefixReanchorCandidate option

    /// Absorption policy for prefix observations. `None` means the observation is
    /// the expected steady state or an already-captured candidate, so the caller
    /// must write nothing; `Some reason` is the requirement text a refusal is
    /// reported with.
    val describe: rejection: PrefixFoldRejection -> string option
