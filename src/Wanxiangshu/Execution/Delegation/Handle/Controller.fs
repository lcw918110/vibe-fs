namespace Wanxiangshu.Execution.Delegation.Handle

open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Delegation

/// Why a controlled consume refused to retire (EXEC-009).
type HandleConsumeRejection =
    /// Handle is already Retired — a concurrent join won, or a restart replay.
    | AlreadyRetired
    /// Handle is still Active — no completion cell yet.
    | NotJoinable of HandleTransitionRejection
    /// Journal append failed (includes CommitUnknown). Must not deliver.
    | AppendFailed of string

/// Single writer of stable HandleLinked and exact scoped work settlements.
/// Unscoped historical terminals remain readable but have no new writer.
///
/// One lifecycle, one writer. Progressions are
/// `Active → CompletedAwaitingJoin → Retired` or `Active|CompletedAwaitingJoin →
/// Abandoned`. `HandleProjection` rejects any out-of-order transition. Spreading
/// the appends across the fork path, the completion path and the cancel path
/// meant three modules each knew part of that order, and none of them could see
/// whether the other two agreed.
///
/// `recordWorkCompletion` requires canonical AdmittedWork and JoinableCompletion.
/// Raw Aborted / bare kind+body cannot claim the completion cell.
module HandleController =

    /// An agent child's handle IS its runtime agent id.
    ///
    /// EXEC-009 requires the same handle id after a restart, and the agent id is
    /// what every runtime map is already keyed by. Minting a separate id would
    /// create a second identity for one resource and a mapping to keep in step.
    let agentHandle (agentId: string) =
        HandleId.Agent(AgentHandleId.create agentId)

    let private append (journal: AgentJournalPort) (parentId: SessionId) (fact: ExecutionFactCases) =
        journal.AppendExecutionFact parentId fact

    /// EXEC-009: a fork bound a handle to a Host child session.
    ///
    /// `childSessionId` is recorded because only the Host can issue it: a recovered
    /// handle with no session points at nothing, and deriving one from the handle id
    /// would fabricate an identity every later operation silently no-ops against.
    let linkNamed
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (agentId: string)
        (childSessionId: SessionId)
        (targetAgent: string)
        (byname: string)
        (role: Role)
        (ownership: HandleOwnership)
        : Task<Result<unit, string>> =
        match journal with
        | None -> Task.FromResult(Ok())
        | Some durable ->
            match
                HandleProjection.linkNamed
                    (agentHandle agentId)
                    childSessionId
                    targetAgent
                    byname
                    role
                    ownership
                    (durable.HandleProjection parentId)
            with
            | Error reason -> Task.FromResult(Error(sprintf "binding rejected: %A" reason))
            | Ok _ ->
                append
                    durable
                    parentId
                    (ExecutionFactCases.HandleLinked
                        {| ParentSessionId = parentId
                           ChildSessionId = childSessionId
                           Handle = agentHandle agentId
                           TargetAgent = targetAgent
                           Byname = byname
                           CanonicalRole = role
                           Ownership = ownership |})

    /// Internal compatibility: when no distinct provider presentation identity
    /// exists, use the Host target name as the byname too.
    let link
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (agentId: string)
        (childSessionId: SessionId)
        (targetAgent: string)
        (role: Role)
        (ownership: HandleOwnership)
        : Task<Result<unit, string>> =
        linkNamed journal parentId agentId childSessionId targetAgent targetAgent role ownership

    /// EXEC-004 / P0-RECOVERY-JOIN-001: claim the single-assignment completion cell
    /// only with a proven `JoinableCompletion` (Succeeded | Failed finality).
    ///
    /// Blob write precedes the fact (PERSIST-007). Kind + body come from the proof;
    /// callers cannot pass raw Aborted or bare `HandleCompletionKind` + `"ABORTED"`.
    /// The fold refuses a second claim, so a duplicate is a no-op rather than overwrite.
    let private writeCompletionBlob
        (durable: AgentJournalPort)
        (content: string)
        : Task<Result<BlobRef option * BlobDigest option, string>> =
        task {
            match! durable.WriteBlob content with
            | Error err -> return Error err
            | Ok(blobRef, blobDigest) -> return Ok(Some blobRef, Some blobDigest)
        }

    let private completionBlobRefs
        (durable: AgentJournalPort)
        (body: string option)
        : Task<Result<BlobRef option * BlobDigest option, string>> =
        task {
            match body with
            | None -> return Ok(None, None)
            | Some content -> return! writeCompletionBlob durable content
        }

    let private bodyBelongsToWork (work: HandleWorkId) (body: string) =
        match HandleCompletionCodec.decodeBody body with
        | Current decoded -> HandleCompletionCodec.belongsToWork work decoded
        | _ -> false

    let private appendActiveWorkCompletion
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (work: HandleWorkId)
        (completion: JoinableCompletion)
        : Task<Result<unit, string>> =
        task {
            let! refs = completionBlobRefs durable (JoinableCompletion.body completion)

            match refs with
            | Error err -> return Error err
            | Ok(completionRef, completionDigest) ->
                return!
                    append
                        durable
                        parentId
                        (ExecutionFactCases.HandleWorkCompleted
                            {| ParentSessionId = parentId
                               Work = work
                               Kind = JoinableCompletion.kind completion
                               CompletionRef = completionRef
                               CompletionDigest = completionDigest |})
        }

    let private recordAdmittedWorkCompletion
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (admitted: AdmittedWork)
        (completion: JoinableCompletion)
        : Task<Result<unit, string>> =
        let work = AdmittedWork.id admitted

        match HandleProjection.tryWork work (durable.HandleProjection parentId) with
        | None -> Task.FromResult(Error "completion work was never canonically admitted")
        | Some prior when prior.LogicalRunId <> AdmittedWork.logicalRunId admitted ->
            Task.FromResult(Error "completion logical run mismatch")
        | Some { Lifecycle = Retired
                 LastCompletion = None } -> Task.FromResult(Error "voided work cannot produce a completion")
        | Some { Lifecycle = Active } when JoinableCompletion.body completion |> Option.exists (bodyBelongsToWork work) ->
            appendActiveWorkCompletion durable parentId work completion
        | Some { Lifecycle = Active } -> Task.FromResult(Error "completion body is not an exact work terminal")
        | Some _ -> Task.FromResult(Ok())

    let recordWorkCompletion
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (admitted: AdmittedWork)
        (completion: JoinableCompletion)
        : Task<Result<unit, string>> =
        task {
            let work = AdmittedWork.id admitted

            match journal with
            | None -> return Error "scoped work completion requires its canonical journal"
            | Some durable when
                JoinableCompletion.handle completion <> work.Handle
                || JoinableCompletion.childSession completion <> work.ChildSessionId
                ->
                return Error "completion belongs to another child work"
            | Some durable -> return! recordAdmittedWorkCompletion durable parentId admitted completion
        }

    let private recordScopedCompletion
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (completion: JoinableCompletion)
        : Result<unit, string> =
        let projection = durable.HandleProjection parentId

        match HandleProjection.tryBinding (JoinableCompletion.handle completion) projection with
        | Some { Lifecycle = CompletedAwaitingJoin _ }
        | Some { Lifecycle = Retired } -> Ok()
        | _ -> Error "legacy unscoped completion is read-only; exact admitted work is required"

    let recordCompletion
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (completion: JoinableCompletion)
        : Task<Result<unit, string>> =
        task {
            match journal with
            | None -> return Ok()
            | Some durable -> return recordScopedCompletion durable parentId completion
        }

    /// EXEC-009: durable abandon. Single-assignment via fold CAS.
    ///
    /// Does not write a completion cell and does not retire. Join must see
    /// Abandoned as non-joinable and return an explicit abandon outcome.
    ///
    /// msl-009/018: authorization is the confirmed parent/session termination
    /// or permanent child loss — the durable binding carries it, with or
    /// without an admitted work. An exact active work settles as
    /// HandleWorkAbandoned; an unscoped binding settles as HandleAbandoned.
    /// A replayed abandon is absorbed idempotently by the fold (first winner
    /// keeps the reason), so the journal accepts the line.
    ///
    /// Call only for irreversible loss (parent cancel, deadline, host session
    /// gone). Never from degeneration-guard interrupt, provider-retry wake, or any path
    /// that may continue the same handle through an independently owned control path.
    let private abandonUnscopedBinding
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (handle: HandleId)
        (reason: HandleAbandonReason)
        (abandonedAt: System.DateTimeOffset)
        : Task<Result<unit, string>> =
        match HandleProjection.tryBinding handle (durable.HandleProjection parentId) with
        | Some { Lifecycle = Retired } -> Task.FromResult(Error "retired handle cannot be abandoned")
        | Some _ ->
            append
                durable
                parentId
                (ExecutionFactCases.HandleAbandoned
                    {| ParentSessionId = parentId
                       Handle = handle
                       Reason = reason
                       AbandonedAt = abandonedAt |})
        | None -> Task.FromResult(Error "unknown handle cannot be abandoned")

    let private abandonActiveOrBinding
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (agentId: string)
        (reason: HandleAbandonReason)
        (abandonedAt: System.DateTimeOffset)
        : Task<Result<unit, string>> =
        let projection = durable.HandleProjection parentId
        let handle = agentHandle agentId

        let activeWork =
            HandleProjection.workRecords projection
            |> List.tryFind (fun record -> record.Handle = handle && record.Lifecycle = Active)
            |> Option.bind _.Work

        match activeWork with
        | Some work ->
            append
                durable
                parentId
                (ExecutionFactCases.HandleWorkAbandoned
                    {| ParentSessionId = parentId
                       Work = work
                       Reason = reason |})
        | None -> abandonUnscopedBinding durable parentId handle reason abandonedAt

    let recordAbandon
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (agentId: string)
        (reason: HandleAbandonReason)
        (abandonedAt: System.DateTimeOffset)
        : Task<Result<unit, string>> =
        match journal with
        | None -> Task.FromResult(Ok())
        | Some durable -> abandonActiveOrBinding durable parentId agentId reason abandonedAt

    /// Historical unscoped entry points never emit new retirement facts.
    let retire (journal: AgentJournalPort option) (parentId: SessionId) (agentId: string) : Task<Result<unit, string>> =
        Task.FromResult(Error "unscoped retirement is read-only; consume an exact admitted work")

    let consume
        (journal: AgentJournalPort)
        (parentId: SessionId)
        (handle: HandleId)
        : Task<Result<HandleRecord, HandleConsumeRejection>> =
        let result =
            match HandleProjection.tryBinding handle (journal.HandleProjection parentId) with
            | None -> Error(NotJoinable UnknownHandle)
            | Some { Lifecycle = Retired } -> Error AlreadyRetired
            | Some { Lifecycle = Active } -> Error(NotJoinable NotCompleted)
            | Some _ -> Error(AppendFailed "unscoped completion is audit-only; exact settlement evidence is required")

        Task.FromResult result

    let private expectedCompletionCell (lifecycle: HandleLifecycle) =
        match lifecycle with
        | CompletedAwaitingJoin cell -> Some cell
        | Abandoned _ ->
            Some
                { Kind = HandleCompletionKind.Cancelled
                  CompletionRef = None
                  CompletionDigest = None }
        | _ -> None

    let private confirmConsumedWork
        (journal: AgentJournalPort)
        (parentId: SessionId)
        (work: HandleWorkId)
        (record: HandleRecord)
        (consumptionId: string)
        : Result<HandleRecord, HandleConsumeRejection> =
        match HandleProjection.tryWork work (journal.HandleProjection parentId) with
        | Some consumed when consumed.ConsumptionId = Some consumptionId -> Ok record
        | Some { Lifecycle = Retired } -> Error AlreadyRetired
        | _ -> Error(AppendFailed "exact consumption receipt was not confirmed")

    let private appendWorkConsumption
        (journal: AgentJournalPort)
        (parentId: SessionId)
        (work: HandleWorkId)
        (record: HandleRecord)
        (cell: HandleCompletion)
        : Task<Result<HandleRecord, HandleConsumeRejection>> =
        task {
            // Identifies this consumption attempt, not a work generation or ordering key.
            let consumptionId = System.Guid.NewGuid().ToString("N")

            match!
                journal.AppendExecutionFact
                    parentId
                    (ExecutionFactCases.HandleWorkConsumed
                        {| ParentSessionId = parentId
                           Work = work
                           ConsumptionId = consumptionId
                           Kind = cell.Kind
                           CompletionRef = cell.CompletionRef
                           CompletionDigest = cell.CompletionDigest |})
            with
            | Error err -> return Error(AppendFailed err)
            | Ok() -> return confirmConsumedWork journal parentId work record consumptionId
        }

    let private consumeAdmittedWork
        (journal: AgentJournalPort)
        (parentId: SessionId)
        (work: HandleWorkId)
        (record: HandleRecord)
        : Task<Result<HandleRecord, HandleConsumeRejection>> =
        let expected = expectedCompletionCell record.Lifecycle

        match HandleProjection.tryWork work (journal.HandleProjection parentId), expected with
        | Some { Lifecycle = Retired }, _ -> Task.FromResult(Error AlreadyRetired)
        | None, _ -> Task.FromResult(Error(NotJoinable WorkNotAdmitted))
        | _, None -> Task.FromResult(Error(NotJoinable NotCompleted))
        | Some _, Some cell -> appendWorkConsumption journal parentId work record cell

    let consumeWork
        (journal: AgentJournalPort)
        (parentId: SessionId)
        (record: HandleRecord)
        : Task<Result<HandleRecord, HandleConsumeRejection>> =
        task {
            match record.Work with
            | None ->
                return
                    Error(
                        AppendFailed
                            "unscoped historical completion is audit-only; explicit settlement evidence is required"
                    )
            | Some work when Set.contains work.Handle (journal.HandleProjection parentId).LegacyWorkHandles ->
                return Error(AppendFailed "ambiguous unscoped work history is quarantined")
            | Some work -> return! consumeAdmittedWork journal parentId work record
        }

    /// managed-session-lifecycle-024 / delegation-027: parent cancellation exempts
    /// the fixed DevOps *handle* from abandon, but its in-flight work unit still
    /// needs a durable work-level terminal. Without one the next resume's
    /// admitWork is refused with WorkStillActive and a physically accepted
    /// dispatch degrades to DispatchUncertain. Work-scoped only: the binding
    /// itself stays Active and reusable for the next charge. The fold absorbs
    /// a replay idempotently (AlreadyCompleted/AlreadyAbandoned/HandleIsRetired
    /// all settle to no-op), so a work that already reached a terminal between
    /// the cancel decision and this append is not an error.
    let settleExemptedWork
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (admitted: AdmittedWork)
        (reason: HandleAbandonReason)
        : Task<Result<unit, string>> =
        match journal with
        | None -> Task.FromResult(Ok())
        | Some durable ->
            append
                durable
                parentId
                (ExecutionFactCases.HandleWorkAbandoned
                    {| ParentSessionId = parentId
                       Work = AdmittedWork.id admitted
                       Reason = reason |})

    /// Parent cancel: durable `HandleAbandoned` (ParentCancelled) per owned agent.
    ///
    /// Replaces the previous `Cancelled` completion + retire pair so abandon is an
    /// explicit durable terminal that is not joinable. Each child is abandoned
    /// individually — EXEC-009 requires parent cancel to cancel every owned resource
    /// one by one, so there is deliberately no bulk "abandon all children" fact.
    let private abandonChild journal parentId agentId abandonedAt =
        recordAbandon journal parentId agentId HandleAbandonReason.ParentCancelled abandonedAt

    /// msl-009: parent cancel abandons every owned active child — scoped and
    /// unscoped bindings alike — through the same recordAbandon settlement.
    let cancelChildren
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (agentIds: string list)
        (abandonedAt: System.DateTimeOffset)
        : Task<Result<unit, string>> =
        let rec loop ids =
            task {
                match ids with
                | [] -> return Ok()
                | agentId :: rest ->
                    let! result = abandonChild journal parentId agentId abandonedAt
                    return! continueAbandon rest result
            }

        and continueAbandon rest result =
            task {
                match result with
                | Error err -> return Error err
                | Ok() -> return! loop rest
            }

        loop agentIds
