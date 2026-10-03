namespace Wanxiangshu.Execution.Delegation.Handle

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery

/// EXEC-009 + EXEC-018 + clean-break: pure durable join drain.
///
/// Order: HandleRecord → blob body → DurableCompletionDecode → branch:
///   Current → fromDecoded → CAS HandleRetired → AgentJoinItem
///   LegacyFalseAbort + not retired → HandleFalseCompletionRejected → no result
///   LegacyFalseAbort + retired → fail-closed refuse (no replacement) → Error
///   Invalid → keep waiting → no consume
/// Renderer never sees legacy blob.
module JoinDrain =

    /// EXEC-018: no durable completion sequence → CreationOrder (HandleLinked
    /// fold order) then TargetAgent. Forbidden: AgentHandleId dictionary order,
    /// Promise race, wall clock, Map hash order.
    let stableJoinKey (record: HandleRecord) : int * string =
        record.CreationOrder, record.TargetAgent

    /// Ordered candidates for one drain pass (Abandoned + CompletedAwaitingJoin).
    let orderedCandidates (projection: AgentLinkageProjection) : HandleRecord list =
        (HandleProjection.reportableAbandoned projection
         @ HandleProjection.joinable projection)
        |> List.sortBy stableJoinKey

    let private abandonReasonText (reason: HandleAbandonReason) =
        match reason with
        | HandleAbandonReason.ParentCancelled -> "ParentCancelled"
        | HandleAbandonReason.DeadlineExceeded -> "DeadlineExceeded"
        | HandleAbandonReason.HostSessionGone -> "HostSessionGone"

    let private afterConsumeCas
        (runIdPrefix: string)
        (agentId: string)
        (record: HandleRecord)
        (reasonText: string)
        (completedAt: DateTimeOffset)
        (outcome: Result<HandleRecord, HandleConsumeRejection>)
        : Result<RunCompletion, ForkError> option =
        match outcome with
        | Ok _ ->
            Some(
                Ok
                    { RunId = runIdPrefix + agentId
                      AgentName = record.TargetAgent
                      Role = record.CanonicalRole
                      Outcome = AgentCompletion.abandoned agentId reasonText
                      CompletedAt = completedAt }
            )
        | Error AlreadyRetired
        | Error(NotJoinable _) -> None
        | Error(AppendFailed err) -> Some(Error(ForkError.NotFound err))

    /// A `Cancelled` completion carries no body: the run ended without producing a
    /// result (process restart, cancelled turn). The parent is still owed a report,
    /// and a child with an unreported completion counts as having unfinished
    /// delivery — reuse is refused while join used to skip it silently, which left
    /// the child unusable. Report it once and retire, exactly like an abandoned
    /// handle.
    let private tryConsumeCancelledCompletion
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        task {
            match record.Lifecycle, HandleId.tryAgent record.Handle with
            | HandleLifecycle.CompletedAwaitingJoin { Kind = HandleCompletionKind.Cancelled }, Some agentHandleId ->
                let agentId = AgentHandleId.value agentHandleId
                let! outcome = HandleController.consumeWork durable parentId record
                return afterConsumeCas "cancelled-" agentId record "Cancelled" completedAt outcome
            | _ -> return None
        }

    /// Materialise Abandoned as a batch item and CAS-retire (single report).
    /// `completedAt` is caller-minted (IClockPort at composition).
    let tryConsumeOneAbandoned
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        task {
            match record.Lifecycle, HandleId.tryAgent record.Handle with
            | HandleLifecycle.Abandoned reason, Some agentHandleId ->
                let agentId = AgentHandleId.value agentHandleId
                let! outcome = HandleController.consumeWork durable parentId record
                return afterConsumeCas "abandoned-" agentId record (abandonReasonText reason) completedAt outcome
            | _ -> return None
        }

    let private appendFact (durable: AgentJournalPort) (parentId: SessionId) (fact: ExecutionFactCases) =
        durable.AppendExecutionFact parentId fact

    let private afterRejectAppendFailure
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (err: string)
        : Result<RunCompletion, ForkError> option =
        let after = durable.HandleProjection parentId

        match HandleProjection.tryFind record.Handle after with
        | Some { Lifecycle = HandleLifecycle.Active } -> None
        | _ -> Some(Error(ForkError.NotFound err))

    let private rejectAppendOutcome
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (appendResult: Result<unit, string>)
        : Result<RunCompletion, ForkError> option =
        match appendResult with
        | Ok() -> None
        | Error err -> afterRejectAppendFailure durable parentId record err

    /// Unretired legacy false abort: append rejection, fold reverts to Active, no join item.
    /// Idempotent when already Active (fold rejects AlreadyCompleted / NotCompleted → treat as done).
    let private rejectUnretiredFalseAbort
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        : Task<Result<RunCompletion, ForkError> option> =
        task {
            match record.Lifecycle with
            | HandleLifecycle.CompletedAwaitingJoin _ ->
                let! appendResult =
                    appendFact
                        durable
                        parentId
                        (ExecutionFactCases.HandleFalseCompletionRejected
                            {| ParentSessionId = parentId
                               Handle = record.Handle
                               ExpectedCompletionRef = blobRef
                               ExpectedCompletionDigest = blobDigest
                               Reason = FalseCompletionReason.LegacyAbortWasObservation |})

                return rejectAppendOutcome durable parentId record appendResult
            | _ -> return None
        }

    let private missingBodyOutcome (agentId: string) (lifecycle: HandleLifecycle) =
        match lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin { Kind = HandleCompletionKind.Terminal }
        | HandleLifecycle.CompletedAwaitingJoin { Kind = HandleCompletionKind.SendFailure } ->
            Some(Error(ForkError.TerminalMaterializationFailed agentId))
        | HandleLifecycle.CompletedAwaitingJoin { Kind = HandleCompletionKind.Cancelled } -> None
        | _ -> None

    let private afterFinalityConsume
        (completion: RunCompletion)
        (outcome: Result<HandleRecord, HandleConsumeRejection>)
        : Result<RunCompletion, ForkError> option =
        match outcome with
        | Ok _ -> Some(Ok completion)
        | Error AlreadyRetired
        | Error(NotJoinable _) -> None
        | Error(AppendFailed err) -> Some(Error(ForkError.NotFound err))

    let private joinCurrentDecoded
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (agentId: string)
        (decoded: DurableAgentCompletionV2)
        (body: string)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        task {
            let proof =
                JoinableCompletion.fromDecoded agentId record.Handle record.ChildSessionId decoded body

            let completion =
                HandleCompletionCodec.tryMaterialiseRunCompletion record agentId decoded completedAt

            match JoinableCompletion.finality proof with
            | ChildFinality.Succeeded _
            | ChildFinality.Failed _ ->
                let! outcome = HandleController.consumeWork durable parentId record
                return afterFinalityConsume completion outcome
            | ChildFinality.Abandoned _ -> return None
        }

    let private afterDecodeBody
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (agentId: string)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        (body: string)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        match HandleCompletionCodec.decodeBody body with
        | Current decoded when
            record.Work
            |> Option.forall (fun work -> HandleCompletionCodec.belongsToWork work decoded)
            ->
            joinCurrentDecoded durable parentId record agentId decoded body completedAt
        | Current _ -> Task.FromResult(Some(Error(ForkError.NotFound "completion work identity mismatch")))
        | LegacyFalseAbort _ -> rejectUnretiredFalseAbort durable parentId record blobRef blobDigest
        | Invalid _ -> Task.FromResult None

    let private consumeMissingBody
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (agentId: string)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin { Kind = HandleCompletionKind.Cancelled } ->
            tryConsumeCancelledCompletion durable parentId record completedAt
        | _ -> Task.FromResult(missingBodyOutcome agentId record.Lifecycle)

    let private afterReadBody
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (agentId: string)
        (readResult: Result<string option * BlobRef option * BlobDigest option, string>)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        match readResult with
        | Error err -> Task.FromResult(Some(Error(ForkError.NotFound err)))
        | Ok(None, _, _) -> consumeMissingBody durable parentId record agentId completedAt
        | Ok(Some body, Some blobRef, Some blobDigest) ->
            afterDecodeBody durable parentId record agentId blobRef blobDigest body completedAt
        | Ok(Some _, _, _) ->
            Task.FromResult(Some(Error(ForkError.NotFound "completion blob ref/digest pair is incomplete")))

    /// One durable completed handle: decode first, then prove, then CAS.
    /// `completedAt` is caller-minted (IClockPort at composition).
    let tryConsumeOneDurable
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion, ForkError> option> =
        task {
            match HandleId.tryAgent record.Handle with
            | None -> return None
            | Some agentHandleId ->
                let agentId = AgentHandleId.value agentHandleId
                let! readResult = HandleCompletionCodec.tryReadBody durable record
                return! afterReadBody durable parentId record agentId readResult completedAt
        }

    /// Dispatch one record through the correct consume path.
    let tryConsumeOne
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (completedAt: DateTimeOffset)
        (record: HandleRecord)
        : Task<Result<RunCompletion, ForkError> option> =
        match record.Lifecycle with
        | HandleLifecycle.Abandoned _ -> tryConsumeOneAbandoned durable parentId record completedAt
        | HandleLifecycle.CompletedAwaitingJoin _ -> tryConsumeOneDurable durable parentId record completedAt
        | _ -> Task.FromResult None

    let private refreshCandidates
        (acc: RunCompletion list)
        (refresh: unit -> AgentLinkageProjection)
        (records: HandleRecord list)
        =
        // Exact consumed work tombstones, not agent ids, remove delivered entries.
        orderedCandidates (refresh ())
        |> fun refreshed ->
            if List.isEmpty refreshed then Choice1Of3()
            elif refreshed = records then Choice2Of3()
            else Choice3Of3 refreshed

    /// Core drain loop: ordered candidates, CAS consume, refresh on race/skip.
    /// Returns Ok list (may be empty) or Error when the first item fails hard.
    let drainJoinableBatch
        (maxCount: int)
        (projection: AgentLinkageProjection)
        (consumeOne: HandleRecord -> Task<Result<RunCompletion, ForkError> option>)
        (refresh: unit -> AgentLinkageProjection)
        : Task<Result<RunCompletion list, ForkError>> =
        let rec consumeSafe
            (acc: RunCompletion list)
            (remaining: int)
            (records: HandleRecord list)
            : Task<Result<RunCompletion list, ForkError>> =
            task {
                match remaining, records with
                | 0, _
                | _, [] -> return Ok(List.rev acc)
                | n, record :: rest -> return! afterConsumeOne acc n record rest
            }

        and afterConsumeOne
            (acc: RunCompletion list)
            (n: int)
            (record: HandleRecord)
            (rest: HandleRecord list)
            : Task<Result<RunCompletion list, ForkError>> =
            task {
                let! outcome = consumeOne record
                return! decideConsumeOutcome acc n record rest outcome
            }

        and continueAfterSkip
            (acc: RunCompletion list)
            (n: int)
            (record: HandleRecord)
            (rest: HandleRecord list)
            : Task<Result<RunCompletion list, ForkError>> =
            match refreshCandidates acc refresh (record :: rest) with
            | Choice1Of3() -> Task.FromResult(Ok(List.rev acc))
            | Choice2Of3() -> consumeSafe acc n rest
            | Choice3Of3 refreshed -> consumeSafe acc n refreshed

        and decideConsumeOutcome
            (acc: RunCompletion list)
            (n: int)
            (record: HandleRecord)
            (rest: HandleRecord list)
            (outcome: Result<RunCompletion, ForkError> option)
            : Task<Result<RunCompletion list, ForkError>> =
            match outcome with
            | Some(Ok completion) -> consumeSafe (completion :: acc) (n - 1) rest
            | Some(Error e) when List.isEmpty acc -> Task.FromResult(Error e)
            | Some(Error _) -> Task.FromResult(Ok(List.rev acc))
            | None -> continueAfterSkip acc n record rest

        let cap = min maxCount JoinBatch.Max

        if cap <= 0 then
            Task.FromResult(Ok [])
        else
            consumeSafe [] cap (orderedCandidates projection)

    let private completionBlobPair (cell: HandleCompletion) =
        match cell.CompletionRef, cell.CompletionDigest with
        | Some blobRef, Some blobDigest -> Some(blobRef, blobDigest)
        | _ -> None

    /// Unretired awaiting-join → reject path; retired → refuse path (fail-closed).
    let private tryFalseAbortCell (record: HandleRecord) =
        match record.Lifecycle, HandleId.tryAgent record.Handle, record.LastCompletion with
        | HandleLifecycle.CompletedAwaitingJoin cell, Some _, _ ->
            completionBlobPair cell |> Option.map (fun pair -> pair, false)
        | HandleLifecycle.Retired, Some _, Some cell -> completionBlobPair cell |> Option.map (fun pair -> pair, true)
        | _ -> None

    let private applyDecodedFalseAbort
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        (body: string)
        (retired: bool)
        : Task<Result<unit, ForkError>> =
        match HandleCompletionCodec.decodeBody body, retired with
        | LegacyFalseAbort _, false ->
            task {
                do!
                    rejectUnretiredFalseAbort durable parentId record blobRef blobDigest
                    |> TaskValue.map ignore

                return Ok()
            }
        | LegacyFalseAbort _, true ->
            // effect-accounting-007: retired handle with legacy false-abort tombstone.
            // Fail-closed refuse — do not mint a replacement. The bad-data set is
            // observably empty (48-journal census: zero fired); the writer is dead
            // (codec-encode-finality-aborted gate). Action: archive or remove the
            // affected journal.
            Task.FromResult(
                Error(
                    ForkError.NotFound
                        "legacy false-abort tombstone on retired handle; archive or remove the affected journal"
                )
            )
        | _ -> Task.FromResult(Ok())

    let private maybeApplyLegacyFalseAbort
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        (retired: bool)
        : Task<Result<unit, ForkError>> =
        task {
            let! readResult = durable.ReadBlob blobRef

            match readResult with
            | Ok body when durable.Sha256 body = BlobDigest.value blobDigest ->
                return! applyDecodedFalseAbort durable parentId record blobRef blobDigest body retired
            | _ -> return Ok()
        }

    let private reconcileOne
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        : Task<Result<unit, ForkError>> =
        task {
            match tryFalseAbortCell record with
            | None -> return Ok()
            | Some((blobRef, blobDigest), retired) ->
                return! maybeApplyLegacyFalseAbort durable parentId record blobRef blobDigest retired
        }

    /// Scan projection: reject unretired false aborts; refuse retired false aborts.
    /// O(handles) keyed lookups + blob reads for cells that already carry ref/digest.
    /// Returns Error on the first retired false-abort tombstone (fail-closed refuse).
    let reconcileFalseAborts (durable: AgentJournalPort) (parentId: SessionId) : Task<Result<unit, ForkError>> =
        let projection = durable.HandleProjection parentId

        let rec loop (remaining: HandleRecord list) : Task<Result<unit, ForkError>> =
            taskResult {
                match remaining with
                | [] -> return ()
                | record :: rest ->
                    do! reconcileOne durable parentId record
                    do! loop rest
            }

        loop (HandleProjection.linkedChildren projection)

    /// Fission lane join: consume only completion cells whose logical active-run
    /// affinity belongs to this present. Candidates not accepted by the predicate
    /// stay untouched and remain joinable for their owning lane.
    let drainFromJournalWhere
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (maxCount: int)
        (completedAt: DateTimeOffset)
        (accept: HandleRecord -> bool)
        : Task<Result<RunCompletion list, ForkError>> =
        let filtered () =
            let projection = durable.HandleProjection parentId

            { projection with
                Handles = projection.Handles |> Map.filter (fun _ record -> accept record) }

        task {
            let! reconcileResult = reconcileFalseAborts durable parentId

            match reconcileResult with
            | Error e -> return Error e
            | Ok() ->
                let projection = filtered ()

                return! drainJoinableBatch maxCount projection (tryConsumeOne durable parentId completedAt) filtered
        }
