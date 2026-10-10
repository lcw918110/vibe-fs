namespace Wanxiangshu.Context.Companion.Blogger

open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Enforcer
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type ContextProjectionChange =
    | BloggerCyclesSet of sessionId: SessionId * cycles: BloggerCycleProjectionState
    | EnforcementSet of sessionId: SessionId * enforcement: EnforcementProjectionState
    | BlogSet of sessionId: SessionId * blog: BlogProjectionState
    | PrefixEpochSet of sessionId: SessionId * epoch: ActivePrefixEpoch
    | BlogReanchored of sessionId: SessionId
    | AuxiliaryVisibilityRetired of sessionId: SessionId

[<RequireQualifiedAccess>]
type ContextFoldRejection =
    | BloggerRequestMaterializedRejected of reason: string
    | BlogObservationCommittedRejected of reason: string
    | BlogObservationsSquashedRejected of reason: string
    | BlogObservationCommittedFrameRejected of rejection: BlogFoldRejection
    | BlogObservationsSquashedFrameRejected of rejection: BlogFoldRejection
    | PrefixRebaseCommittedRejected of reason: string
    | ContextReanchoredRejected of reason: string
    | TenureReanchoredRejected of reason: string

[<RequireQualifiedAccess>]
module ContextFoldRejection =
    let fact (rejection: ContextFoldRejection) : string =
        match rejection with
        | ContextFoldRejection.BloggerRequestMaterializedRejected _ -> "BloggerRequestMaterialized"
        | ContextFoldRejection.BlogObservationCommittedRejected _
        | ContextFoldRejection.BlogObservationCommittedFrameRejected _ -> "BlogObservationCommitted"
        | ContextFoldRejection.BlogObservationsSquashedRejected _
        | ContextFoldRejection.BlogObservationsSquashedFrameRejected _ -> "BlogObservationsSquashed"
        | ContextFoldRejection.PrefixRebaseCommittedRejected _ -> "PrefixRebaseCommitted"
        | ContextFoldRejection.ContextReanchoredRejected _ -> "ContextReanchored"
        | ContextFoldRejection.TenureReanchoredRejected _ -> "TenureReanchored"

    /// PERSIST-010: every Companion frame refusal describes a line a correct
    /// writer could not have produced, so none of them is absorbed.
    ///
    /// A stale frame epoch is the one that looks benign and is not. It means the
    /// line was written against a frame sequence that a squash has already
    /// replaced, so applying it would append an entry describing frames that no
    /// longer exist — and skipping it would lose an entry whose delta was already
    /// consumed. Neither is recoverable, so the fold refuses the journal.
    let private blogFrameMessage (rejection: BlogFoldRejection) : string =
        match rejection with
        | BlogFoldRejection.StaleFrameEpoch(expected, actual) ->
            sprintf
                "frame epoch %d is in force but the line was written against %d (PERSIST-010)"
                (FrameEpochId.value expected)
                (FrameEpochId.value actual)
        | BlogFoldRejection.NonSequentialFrameEpoch ->
            "squash frame epoch is not the successor of the previous one (PERSIST-010)"
        | BlogFoldRejection.IngestCursorNotAdvanced ->
            "committed entry consumed nothing, so the same delta could be blogged forever (CTX-011)"
        | BlogFoldRejection.IngestCursorMismatch ->
            "entry's previous ingest cursor disagrees with the projection (PERSIST-010)"
        | BlogFoldRejection.CoverageRetreated -> "coverage moved backwards within one numbering (CTX-011)"
        | BlogFoldRejection.CoveredFrameCountOutOfRange(claimed, available) ->
            sprintf "squash claimed %d of %d available frames (CTX-012)" claimed available

    let message (rejection: ContextFoldRejection) : string =
        match rejection with
        | ContextFoldRejection.BloggerRequestMaterializedRejected reason -> reason
        | ContextFoldRejection.BlogObservationCommittedRejected reason -> reason
        | ContextFoldRejection.BlogObservationsSquashedRejected reason -> reason
        | ContextFoldRejection.PrefixRebaseCommittedRejected reason -> reason
        | ContextFoldRejection.ContextReanchoredRejected reason -> reason
        | ContextFoldRejection.TenureReanchoredRejected reason -> reason
        | ContextFoldRejection.BlogObservationCommittedFrameRejected rejection -> blogFrameMessage rejection
        | ContextFoldRejection.BlogObservationsSquashedFrameRejected rejection -> blogFrameMessage rejection

module ContextFactFold =

    let private rebaseRejected
        (rejection: PrefixFoldRejection)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.describe rejection with
        | None -> Ok []
        | Some reason -> Error(ContextFoldRejection.PrefixRebaseCommittedRejected reason)

    let private reanchorRejected
        (rejection: PrefixFoldRejection)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.describe rejection with
        | None -> Ok []
        | Some reason -> Error(ContextFoldRejection.ContextReanchoredRejected reason)

    let private tenureReanchorRejected
        (rejection: PrefixFoldRejection)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.describe rejection with
        | None -> Ok []
        | Some reason -> Error(ContextFoldRejection.TenureReanchoredRejected reason)

    // CTX-019: a successful Y prefix rebase is itself a provider-horizon
    // cold boundary. PrefixEpoch and auxiliary visibility must therefore
    // move atomically: retaining an old guideline/tip/grounding visibility
    // floor while the raw X prefix is replaced lets request-local prose
    // tunnel through any covered Host rows that intentionally survive the
    // replacement (notably retained todowrite rounds).
    let private foldPrefixRebaseCommitted
        sessionId
        previousEpoch
        nextEpoch
        candidate
        (current: ActivePrefixEpoch)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.applyRebase previousEpoch nextEpoch candidate current with
        | Ok rebased ->
            Ok
                [ ContextProjectionChange.PrefixEpochSet(sessionId, rebased)
                  ContextProjectionChange.AuxiliaryVisibilityRetired sessionId ]
        | Error rejection -> rebaseRejected rejection

    // HOST-006: one physical event, two projections. The prefix retires and
    // PrefixCoverage returns to the origin; both must land or neither — a
    // retired prefix beside a cutoff claim in the voided numbering is the
    // state the single fact exists to prevent.
    //
    // Hence one session-level update rather than two chained ones: the
    // atomicity is structural, not something a reader has to verify by
    // tracing whether the second step was reached.
    //
    // Frames and RecordCoverage (IngestedThrough) survive. Host prefix
    // mapping is zeroed (BlogProjection.applyReanchor / COMPANION-008).
    // TipDelivery Full set also clears so post-compaction resolveTipGuidance
    // re-emits Full main.md instead of stranding Main on IdentityOnly.
    let private foldContextReanchored
        sessionId
        previousEpoch
        nextEpoch
        observedRun
        (current: ActivePrefixEpoch)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.applyReanchor previousEpoch nextEpoch observedRun current with
        | Ok retired ->
            Ok
                [ ContextProjectionChange.PrefixEpochSet(sessionId, retired)
                  ContextProjectionChange.BlogReanchored sessionId
                  ContextProjectionChange.AuxiliaryVisibilityRetired sessionId ]
        | Error rejection -> reanchorRejected rejection

    let private foldTenureReanchored
        sessionId
        previousEpoch
        nextEpoch
        incumbencyId
        (current: ActivePrefixEpoch)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        match PrefixEpochProjection.applyTenureReanchor previousEpoch nextEpoch incumbencyId current with
        | Ok retired ->
            Ok
                [ ContextProjectionChange.PrefixEpochSet(sessionId, retired)
                  ContextProjectionChange.BlogReanchored sessionId
                  ContextProjectionChange.AuxiliaryVisibilityRetired sessionId ]
        | Error rejection -> tenureReanchorRejected rejection

    let fold
        (bloggerCyclesOf: SessionId -> BloggerCycleProjectionState option)
        (enforcementOf: SessionId -> EnforcementProjectionState option)
        (blogOf: SessionId -> BlogProjectionState option)
        (prefixEpochOf: SessionId -> ActivePrefixEpoch option)
        (fact: ContextFactCases)
        : Result<ContextProjectionChange list, ContextFoldRejection> =
        // ── failure-driven context recovery (docs/what/context.md) ───────────────────────
        match fact with
        | ContextFactCases.BloggerRequestMaterialized payload ->
            let cycles =
                bloggerCyclesOf payload.MainSessionId
                |> Option.defaultValue BloggerCycleProjection.empty

            BloggerCycleProjection.materialize
                { RequestId = payload.RequestId
                  MainSessionId = payload.MainSessionId
                  BloggerSessionId = payload.BloggerSessionId
                  RequestKind = payload.RequestKind
                  ContextRef = payload.ContextRef
                  ContextDigest = payload.ContextDigest
                  ObservedPrefixEpochId = payload.ObservedPrefixEpochId
                  PreviousIngestedThroughSequence = payload.PreviousIngestedThroughSequence
                  NextIngestedThroughSequence = payload.NextIngestedThroughSequence
                  FrameEpochId = payload.FrameEpochId
                  SelectedFrameDigests = payload.SelectedFrameDigests
                  PromptKey = payload.PromptKey }
                cycles
            |> Result.map (fun updated -> [ ContextProjectionChange.BloggerCyclesSet(payload.MainSessionId, updated) ])
            |> Result.mapError ContextFoldRejection.BloggerRequestMaterializedRejected

        | ContextFactCases.BloggerRequestAbandoned payload ->
            let cycles =
                bloggerCyclesOf payload.MainSessionId
                |> Option.defaultValue BloggerCycleProjection.empty

            Ok
                [ ContextProjectionChange.BloggerCyclesSet(
                      payload.MainSessionId,
                      BloggerCycleProjection.abandon payload.RequestId payload.BloggerSessionId cycles
                  ) ]

        | ContextFactCases.TodoCheckpointCommitted _ ->
            // The aggregate context bridge owns the cross-cutting checkpoint
            // window because it also has the canonical XTrace needed to prune
            // it after a committed prefix rebase.
            Ok []

        | ContextFactCases.BlogObservationCommitted payload ->
            // ENFORCER-045 + C5: Blog + Enforcement + unified cycle receipt.
            let sid = payload.SessionId

            let enforcement =
                enforcementOf sid |> Option.defaultValue EnforcementProjection.empty

            let cycles = bloggerCyclesOf sid |> Option.defaultValue BloggerCycleProjection.empty

            let record: EnforcementCycleRecord =
                { MainSessionId = payload.SessionId
                  BloggerSessionId = payload.BloggerSessionId
                  ProviderRun = payload.ProviderRun
                  ToolCallIds = payload.ToolCallIds
                  CycleTextRef = payload.TextRef
                  CycleTextDigest = payload.TextDigest
                  TipRuleId = payload.TipRuleId
                  FieldNameAtCommit = payload.FieldNameAtCommit
                  CycleEvidenceRef = payload.EvidenceRef
                  ObservedPrefixEpochId = payload.ObservedPrefixEpochId }

            EnforcementProjection.applyFromEntry enforcement record
            |> Result.mapError ContextFoldRejection.BlogObservationCommittedRejected
            |> Result.bind (fun enfUpdated ->
                BloggerCycleProjection.recordReceipt
                    { ProviderRun = payload.ProviderRun
                      Kind = BlogFrameKind.Entry
                      RequestId = payload.RequestId }
                    cycles
                |> Result.mapError ContextFoldRejection.BlogObservationCommittedRejected
                |> Result.bind (fun cycleUpdated ->
                    blogOf sid
                    |> Option.defaultValue BlogProjection.empty
                    |> BlogProjection.applyEntry
                        payload.FrameEpochId
                        payload.PreviousIngestedThroughSequence
                        payload.NextIngestedThroughSequence
                        payload.PreviousCoverableTurnCutoffExclusive
                        payload.NextCoverableTurnCutoffExclusive
                        payload.NextCoveredPrefixDigest
                        { Kind = BlogFrameKind.Entry
                          Digest = payload.TextDigest
                          TextRef = payload.TextRef
                          CoveredFromSequence = payload.PreviousIngestedThroughSequence
                          CoveredThroughSequence = payload.NextIngestedThroughSequence
                          CutoffExclusive = payload.NextCoverableTurnCutoffExclusive }
                    |> Result.mapError ContextFoldRejection.BlogObservationCommittedFrameRejected
                    |> Result.map (fun blogUpdated ->
                        [ ContextProjectionChange.EnforcementSet(sid, enfUpdated)
                          ContextProjectionChange.BloggerCyclesSet(sid, cycleUpdated)
                          ContextProjectionChange.BlogSet(sid, blogUpdated) ])))

        | ContextFactCases.BlogObservationsSquashed payload ->
            // Blog frames squash + Enforcement tip co-truncate on the same main session
            // (payload.SessionId — same owner BlogObservationCommitted uses for Enforcement).
            let sid = payload.SessionId

            let enforcement =
                enforcementOf sid
                |> Option.defaultValue EnforcementProjection.empty
                |> EnforcementProjection.applySquash payload.CoveredFrameCount

            let cycles = bloggerCyclesOf sid |> Option.defaultValue BloggerCycleProjection.empty

            BloggerCycleProjection.recordReceipt
                { ProviderRun = payload.ProviderRun
                  Kind = BlogFrameKind.Squash
                  RequestId = payload.RequestId }
                cycles
            |> Result.mapError ContextFoldRejection.BlogObservationsSquashedRejected
            |> Result.bind (fun cycleUpdated ->
                blogOf sid
                |> Option.defaultValue BlogProjection.empty
                |> BlogProjection.applySquash
                    payload.PreviousFrameEpochId
                    payload.NextFrameEpochId
                    payload.CoveredFrameCount
                    { Kind = BlogFrameKind.Squash
                      Digest = payload.TextDigest
                      TextRef = payload.TextRef
                      CoveredFromSequence = 0L
                      CoveredThroughSequence = 0L
                      // The collapse derives its own claim from the frames it replaces.
                      CutoffExclusive = 0 }
                |> Result.mapError ContextFoldRejection.BlogObservationsSquashedFrameRejected
                |> Result.map (fun blogUpdated ->
                    [ ContextProjectionChange.EnforcementSet(sid, enforcement)
                      ContextProjectionChange.BloggerCyclesSet(sid, cycleUpdated)
                      ContextProjectionChange.BlogSet(sid, blogUpdated) ]))

        | ContextFactCases.PrefixRebaseCommitted payload ->
            let current =
                prefixEpochOf payload.SessionId
                |> Option.defaultValue PrefixEpochProjection.empty

            let candidate =
                { FrozenRecordPrefixRef = payload.FrozenRecordPrefixRef
                  FrozenRecordPrefixDigest = payload.FrozenRecordPrefixDigest
                  CutoffExclusive = payload.CutoffExclusive
                  CoveredPrefixDigest = payload.CoveredPrefixDigest
                  SealRoot = payload.SealRoot
                  SyntheticMessageId = payload.SyntheticMessageId }

            foldPrefixRebaseCommitted payload.SessionId payload.PreviousEpochId payload.NextEpochId candidate current

        | ContextFactCases.ContextReanchored payload ->
            let current =
                prefixEpochOf payload.SessionId
                |> Option.defaultValue PrefixEpochProjection.empty

            foldContextReanchored
                payload.SessionId
                payload.PreviousEpochId
                payload.NextEpochId
                payload.ObservedCompactionRun
                current

        | ContextFactCases.TenureReanchored payload ->
            let current =
                prefixEpochOf payload.SessionId
                |> Option.defaultValue PrefixEpochProjection.empty

            foldTenureReanchored
                payload.SessionId
                payload.PreviousEpochId
                payload.NextEpochId
                payload.IncumbencyId
                current
