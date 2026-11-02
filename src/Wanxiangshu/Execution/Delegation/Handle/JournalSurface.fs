namespace Wanxiangshu.Execution.Delegation

open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Execution.Delegation.Handle

module JournalSurface =

    let private failure error : obj = box {| ok = false; error = error |}

    let private abandonReason value =
        match value with
        | "ParentCancelled" -> Ok HandleAbandonReason.ParentCancelled
        | "DeadlineExceeded" -> Ok HandleAbandonReason.DeadlineExceeded
        | "HostSessionGone" -> Ok HandleAbandonReason.HostSessionGone
        | "Voided" -> Ok HandleAbandonReason.Voided
        | other -> Error $"unknown abandon reason '{other}'"

    let private workId agent child root : HandleWorkId =
        { Handle = HandleId.Agent(AgentHandleId.create agent)
          ChildSessionId = SessionId.create child
          AuthorityRoot = AuthorityRootUserMessageId.create root }

    let recordAbandon
        (handle: JournalHandle)
        (parent: string)
        (agent: string)
        (child: string)
        (root: string)
        (reasonName: string)
        : Task<obj> =
        task {
            let parentId = SessionId.create parent
            let work = workId agent child root

            match
                abandonReason reasonName,
                HandleProjection.tryWork work (AgentJournal.handleProjection handle.Journal parentId)
            with
            | Error error, _ -> return failure error
            | _, None -> return failure "WorkNotAdmitted"
            | Ok reason, Some _ ->
                let! result =
                    HandleController.abandonWork
                        (AgentJournalPortAdapter.fromAgentJournal handle.Journal)
                        parentId
                        work
                        reason

                return
                    match result with
                    | Ok() -> box {| ok = true |}
                    | Error error -> failure error
        }

    let private completionKind name =
        match name with
        | "Terminal" -> Ok HandleCompletionKind.Terminal
        | "SendFailure" -> Ok HandleCompletionKind.SendFailure
        | "Cancelled" -> Ok HandleCompletionKind.Cancelled
        | other -> Error $"unknown completion kind '{other}'"

    let private optionalBlob create value =
        match value with
        | null -> None
        | _ ->
            let text = string value

            if System.String.IsNullOrWhiteSpace text then
                None
            else
                Some(create text)

    /// Append one exact work completion. Used by property tests to plant the
    /// durable cell that consume must retire against.
    let recordWorkCompletion
        (handle: JournalHandle)
        (parent: string)
        (agent: string)
        (child: string)
        (root: string)
        (kindName: string)
        (completionRef: string)
        (completionDigest: string)
        : Task<obj> =
        task {
            match completionKind kindName with
            | Error error -> return failure error
            | Ok kind ->
                let parentId = SessionId.create parent
                let work = workId agent child root

                let! result =
                    (AgentJournalPortAdapter.fromAgentJournal handle.Journal).AppendExecutionFact
                        parentId
                        (ExecutionFactCases.HandleWorkCompleted
                            {| ParentSessionId = parentId
                               Work = work
                               Kind = kind
                               CompletionRef = optionalBlob BlobRef.create completionRef
                               CompletionDigest = optionalBlob BlobDigest.create completionDigest |})

                return
                    match result with
                    | Ok() -> box {| ok = true |}
                    | Error error -> failure error
        }

    /// Produce a HandleRecord for consumeWork. When `stale` is provided it is a
    /// completion cell the *caller* believes is durable; the controller must
    /// ignore it and read the real work cell after refresh.
    let private consumeRecord
        (projection: AgentLinkageProjection)
        (work: HandleWorkId)
        (stale: obj)
        : Result<HandleRecord, string> =
        match HandleProjection.tryWork work projection with
        | None -> Error "WorkNotAdmitted"
        | Some durable ->
            match Map.tryFind work.Handle projection.Handles with
            | None -> Error "UnknownHandle"
            | Some binding ->
                let fromDurable =
                    { binding with
                        Work = Some durable.Work
                        Lifecycle = durable.Lifecycle
                        LastCompletion = durable.LastCompletion }

                if isNull stale then
                    Ok fromDurable
                else
                    match completionKind (string (stale?kind)) with
                    | Error error -> Error error
                    | Ok kind ->
                        let cell =
                            { Kind = kind
                              CompletionRef = optionalBlob BlobRef.create (stale?completionRef)
                              CompletionDigest = optionalBlob BlobDigest.create (stale?completionDigest) }

                        Ok
                            { fromDurable with
                                Lifecycle = CompletedAwaitingJoin cell
                                LastCompletion = Some cell }

    let private consumeRejection (reason: HandleConsumeRejection) =
        match reason with
        | AlreadyRetired -> "AlreadyRetired"
        | NotJoinable inner -> sprintf "%A" inner
        | AppendFailed message -> message

    /// Production consume of one admitted work. Optional `stale` completion is a
    /// deliberately wrong caller cell — consume must still succeed from durable.
    let consumeWork
        (handle: JournalHandle)
        (parent: string)
        (agent: string)
        (child: string)
        (root: string)
        (stale: obj)
        : Task<obj> =
        task {
            let parentId = SessionId.create parent
            let work = workId agent child root
            let journal = AgentJournalPortAdapter.fromAgentJournal handle.Journal

            match journal.RefreshProjection() with
            | Error error -> return failure error
            | Ok() ->
                match consumeRecord (journal.HandleProjection parentId) work stale with
                | Error error -> return failure error
                | Ok record ->
                    let! outcome = HandleController.consumeWork journal parentId record

                    return
                        match outcome with
                        | Ok _ -> box {| ok = true |}
                        | Error reason -> failure (consumeRejection reason)
        }

    let private lifecycle =
        function
        | Active -> "Active"
        | CompletedAwaitingJoin _ -> "CompletedAwaitingJoin"
        | Retired -> "Retired"
        | Abandoned _ -> "Abandoned"

    let drainWhere
        (handle: JournalHandle)
        (parent: string)
        (maxCount: int)
        (completedAt: string)
        (accept: obj -> bool)
        : Task<obj> =
        task {
            let acceptRecord (record: HandleRecord) =
                accept (
                    box
                        {| child = SessionId.value record.ChildSessionId
                           lifecycle = lifecycle record.Lifecycle |}
                )

            let! result =
                JoinDrain.drainFromJournalWhere
                    (AgentJournalPortAdapter.fromAgentJournal handle.Journal)
                    (SessionId.create parent)
                    maxCount
                    (System.DateTimeOffset.Parse completedAt)
                    acceptRecord

            return
                match result with
                | Error error -> failure (string error)
                | Ok completions ->
                    let items =
                        completions
                        |> List.map (fun completed ->
                            let outcome = completed.Outcome

                            let view =
                                match outcome with
                                | Wanxiangshu.Execution.Session.AgentCompleted value -> value.View
                                | _ -> null

                            box
                                {| agent = Wanxiangshu.Execution.Session.AgentCompletion.agentId outcome
                                   kind = Wanxiangshu.Execution.Session.AgentCompletion.status outcome
                                   view = view |})
                        |> List.toArray

                    box {| ok = true; items = items |}
        }

    /// The durable view of one work unit.
    ///
    /// `lifecycle` is the work's own lifecycle; `lifecycleKind` is the kind of
    /// the **last completion** the work ever accepted (Terminal, SendFailure,
    /// Cancelled) and is `null` while the work has accepted no completion at
    /// all. `completionRef` / `completionDigest` are the durable identity of
    /// that completion — `null` while the work has accepted none.
    ///
    /// These two are the non-waiting observation of a pending work: a work
    /// that has settled nothing carries no digest. They never require waiting
    /// for a completion that may never arrive.
    let private recordView (record: HandleWork) : obj =
        let reason =
            match record.Lifecycle with
            | Abandoned reason -> Some(string reason)
            | _ -> None

        let completionKind =
            record.LastCompletion
            |> Option.map (fun completion -> string completion.Kind)
            |> Option.defaultValue null

        let completionRef =
            record.LastCompletion
            |> Option.bind (fun completion -> completion.CompletionRef)
            |> Option.map BlobRef.value
            |> Option.defaultValue null

        let completionDigest =
            record.LastCompletion
            |> Option.bind (fun completion -> completion.CompletionDigest)
            |> Option.map BlobDigest.value
            |> Option.defaultValue null

        box
            {| lifecycle = lifecycle record.Lifecycle
               abandonReason = reason
               child = SessionId.value record.Work.ChildSessionId
               lifecycleKind = completionKind
               completionRef = completionRef
               completionDigest = completionDigest |}

    let snapshot (handle: JournalHandle) (parent: string) (agent: string) (child: string) (root: string) : obj =
        let parentId = SessionId.create parent
        let projection = AgentJournal.handleProjection handle.Journal parentId

        box
            {| record =
                HandleProjection.tryWork (workId agent child root) projection
                |> Option.map recordView
                |> Option.defaultValue null
               revision = JournalRevision.value (AgentJournal.revision handle.Journal) |> string
               horizonVisible = HandleProjection.horizonVisible projection |> List.length
               views =
                {| joinable =
                    HandleProjection.joinable projection
                    |> List.map (fun record -> HandleId.describe record.Handle)
                    |> List.toArray |} |}

    let commitChildDeliveryWitness
        (journalHandle: JournalHandle)
        (sessionId: string)
        (logicalRunId: string)
        (authorityRoot: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        (toolCallId: string)
        (handle: string)
        (formalText: string)
        (viewDeclaration: string)
        (view: string)
        : Task<obj> =
        task {
            let witness: Wanxiangshu.Host.TaskDeliveryWitness =
                { Fence =
                    { SessionId = SessionId.create sessionId
                      LogicalRunId = LogicalRunId.create logicalRunId
                      AuthorityRoot = AuthorityRootUserMessageId.create authorityRoot
                      PhysicalUserMessageId = PhysicalUserMessageId.create physicalUserMessageId
                      ProviderRun = ProviderRunIdentity.create providerRun
                      ToolCallId = ToolCallId.create toolCallId
                      Task = Wanxiangshu.Host.TaskDeliveryTask.ChildWork handle }
                  FormalText = formalText
                  ViewDeclaration = viewDeclaration
                  View = view
                  MaterialDigest = Wanxiangshu.Host.HostDigest.sha256Hex view
                  RetirementId = None }

            let! committed = TaskDeliveryJournal.commit journalHandle.Journal witness

            return
                match committed with
                | TaskDeliveryJournal.TaskDeliveryCommitResult.Committed -> box {| committed = true; reason = "" |}
                | TaskDeliveryJournal.TaskDeliveryCommitResult.NotCommitted reason
                | TaskDeliveryJournal.TaskDeliveryCommitResult.Unknown reason ->
                    box {| committed = false; reason = reason |}
        }

    let findDeliveryWitnessByFence
        (journalHandle: JournalHandle)
        (sessionId: string)
        (providerRun: string)
        (toolCallId: string)
        : obj =
        TaskDeliveryJournal.tryFindByFenceFields
            journalHandle.Journal
            (SessionId.create sessionId)
            (ProviderRunIdentity.create providerRun)
            (Some(ToolCallId.create toolCallId))
            None
            None
            None
            None
        |> Option.map (fun witness ->
            box
                {| formalText = witness.FormalText
                   viewDeclaration = witness.ViewDeclaration
                   view = witness.View |})
        |> Option.defaultValue null

    let publishDeliveredReturn
        (journalHandle: JournalHandle)
        (port: obj)
        (sessionId: string)
        (providerRun: string)
        (toolCallId: string)
        : unit =
        Wanxiangshu.Execution.Delegation.Fork.Host.HostForkRunLifecycle.publishDeliveredReturn
            journalHandle.Journal
            (port :?> Wanxiangshu.Foundation.TerminalSignal.IEventObservationPort)
            (SessionId.create sessionId)
            (ProviderRunIdentity.create providerRun)
            (ToolCallId.create toolCallId)
