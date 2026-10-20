namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Mission.Work
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Context.Trace
open Wanxiangshu.Persistence.Journal

module DeliverTool =
    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/deliver/description"

        [<Literal>]
        let Blocked = "tool/deliver/blocked"

        [<Literal>]
        let AssessmentRequired = "tool/deliver/assessment-required"

        [<Literal>]
        let Retired = "tool/deliver/finished"

        [<Literal>]
        let Confirmation = "tool/deliver/confirmation"

        [<Literal>]
        let NonManagerRole = "tool/deliver/non-manager-role"

        [<Literal>]
        let NoAuthority = "tool/deliver/no-authority"

        [<Literal>]
        let NoRetirementProjection = "tool/deliver/finish-not-recorded"

        [<Literal>]
        let BindingUnavailable = "tool/deliver/binding-unavailable"

        [<Literal>]
        let FinishFailed = "tool/deliver/finish-failed"

    let private text (path: string) =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) path Map.empty

    /// The one lifecycle work this session owns. Lifecycle storage is the
    /// wanxiangli journal, independent of the workspace EventStore.
    let private lifecycleWorkOf
        (directory: string)
        (sessionId: string)
        : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
        task {
            let! commonDir = RuntimePath.stateRoot directory

            if String.IsNullOrWhiteSpace commonDir then
                return None
            else
                return Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir sessionId
        }

    /// Algorithm E1.1: the fence (ProviderRunId, ToolCallId) is looked up on the
    /// retirements the fold already committed, before the entry reads an active
    /// tenure or a review position. A retried call must return the state that
    /// call recorded: a delivered work has no active tenure left, and a relayed
    /// one leaves a successor that is not yet reviewed. The recorded outcome
    /// name is already the closed result vocabulary, so the expected advance the
    /// entry read is the one field left to compare; a different one is the same
    /// refusal the second call gets. This path writes nothing.
    let recordedFenceReplay
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (providerRunId: string)
        (toolCallId: string)
        : Result<string, string> option =
        let sameFence (retirement: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostRetirement) =
            retirement.ProjectionCut
            && retirement.Fence.ProviderRunId = providerRunId
            && retirement.Fence.ToolCallId = toolCallId

        let recordedState (retirement: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.HostRetirement) =
            if retirement.Expected = work.Authority then
                Ok(text Path.Retired)
            else
                Error "RetirementReplayConflict"

        work.Retirements |> List.tryFind sameFence |> Option.map recordedState

    let private blockedResult blockers =
        ToolHostCodec.tomlObjectWithInstructions
            [ text Path.Blocked ]
            [ "finished", ToolHostCodec.TBool false
              "blocker_count", ToolHostCodec.TInt(List.length blockers)
              "blockers",
              ToolHostCodec.TTable(
                  blockers
                  |> List.mapi (fun index blocker -> string index, ToolHostCodec.TString blocker)
              ) ]

    let private assessmentRequiredResult () =
        ToolHostCodec.tomlObjectWithInstructions
            [ text Path.AssessmentRequired ]
            [ "finished", ToolHostCodec.TBool false
              "assessment_required", ToolHostCodec.TBool true ]

    let private requireSome error =
        function
        | Some value -> Ok value
        | None -> Error error

    /// The second deliver writes the retirement cursor from the XTrace head
    /// observed on the Host snapshot. A head already on the journal is used
    /// as-is; otherwise the snapshot messages are captured through the real
    /// XTrace path and the receipt's current head is read. The capture is
    /// append-only and idempotent, so a message already observed adds nothing.
    /// A missing snapshot is not a guessed cursor: None leaves the kernel
    /// refusal unchanged.
    let private captureObservedCursor
        (journal: AgentJournal)
        (sessionId: SessionId)
        (snapshotPort: ISessionSnapshotPort)
        : Task<int64 option> =
        task {
            let! messagesResult = snapshotPort.GetMessages sessionId

            match messagesResult with
            | Error _ -> return None
            | Ok messages ->
                let! captured = XTraceCapture.captureSessionMessagesWithReceipt (Some journal) sessionId messages

                return
                    captured
                    |> Result.toOption
                    |> Option.map (fun receipt -> XTraceCursor.sequence receipt.CurrentHead)
        }

    let private observedRetirementCursor (scope: ToolRuntimeScope) (context: HostToolContext) (journal: AgentJournal) =
        let headOf projections =
            AgentProjection.tryFind (SessionId.create context.SessionId) projections
            |> Option.bind (fun session -> session.XTrace)
            |> Option.map (XTraceProjection.headCursor >> XTraceCursor.sequence)

        let current = headOf (AgentJournal.snapshot journal).AgentProjections

        match current, scope.Snapshot with
        | Some cursor, _ -> Task.FromResult(Some cursor)
        | None, None -> Task.FromResult None
        | None, Some snapshotPort -> captureObservedCursor journal (SessionId.create context.SessionId) snapshotPort

    type private BoundRetirement =
        { ToolCallId: ToolCallId
          ProviderRun: ProviderRunIdentity
          Directory: string
          Journal: AgentJournal }

    type private PreparedRetirement =
        { Bound: BoundRetirement
          SessionId: SessionId
          Incumbent: string
          Reviewed: bool }

    /// Algorithm E1.1 and E2: the entry either replays a fence the fold already
    /// settled, or binds the active tenure. The replay decision belongs to the
    /// fold, so no earlier layer answers a retried call with a refusal for the
    /// tenure the retirement removed.
    type private DeliverEntry =
        | Replayed of string
        | Prepared of PreparedRetirement

    let private bindInvocation (scope: ToolRuntimeScope) (context: HostToolContext) =
        match
            scope.RoleFor context,
            context.ToolCallId,
            context.ProviderRunId,
            scope.CurrentPhysicalUserMessage context.SessionId,
            scope.WorkspaceDirectory,
            scope.Journal
        with
        | Some Role.Manager, Some toolCallId, Some providerRun, Some _, Some directory, Some journal when
            not (String.IsNullOrWhiteSpace context.SessionId)
            ->
            Ok
                { ToolCallId = toolCallId
                  ProviderRun = providerRun
                  Directory = directory
                  Journal = journal }
        | Some Role.Manager, _, _, _, _, _ -> Error(text Path.BindingUnavailable)
        | Some _, _, _, _, _, _ -> Error(text Path.NonManagerRole)
        | None, _, _, _, _, _ -> Error(text Path.NoAuthority)

    /// Algorithm E1: the caller enters the two-stage sequence only for the
    let private enterDeliver (context: HostToolContext) (bound: BoundRetirement) : Task<Result<DeliverEntry, string>> =
        task {
            let fenceReplay (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
                recordedFenceReplay
                    work
                    (ProviderRunIdentity.value bound.ProviderRun)
                    (ToolCallId.value bound.ToolCallId)
                |> Option.map (Result.map Replayed)

            let activeEntry (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
                work.ActiveId
                |> Option.map (fun active ->
                    Ok(
                        Prepared
                            { Bound = bound
                              SessionId = SessionId.create context.SessionId
                              Incumbent = active
                              Reviewed = work.Reviewed }
                    ))
                |> Option.defaultValue (Error(text Path.BindingUnavailable))

            let openedEntry (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
                fenceReplay work |> Option.defaultWith (fun () -> activeEntry work)

            match! lifecycleWorkOf bound.Directory context.SessionId with
            | Some work when work.Sequence = [ "S4" ] -> return openedEntry work
            | _ -> return Error(text Path.BindingUnavailable)
        }

    let private confirmationResult (prepared: PreparedRetirement) pending =
        let deferred =
            pending
            |> List.mapi (fun index (_, itemText) -> string index, ToolHostCodec.TString itemText)

        let optionalFields =
            [ if not (List.isEmpty deferred) then
                  yield "deferred", ToolHostCodec.TTable deferred ]

        ToolHostCodec.tomlObjectWithInstructions
            [ text Path.Confirmation ]
            ([ "finished", ToolHostCodec.TBool false
               "confirmation_required", ToolHostCodec.TBool true ]
             @ optionalFields)

    /// The confirmed path of the two-stage deliver. The committed confirmation
    /// fence names the run and call that confirmed; this call either replays
    /// that exact confirmation to its recorded state, or is the different call
    /// that closes out.
    let private runConfirmed
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        pending
        toolCallId
        providerRunId
        confirmFirst
        =
        task {
            let! confirmedReplay =
                task {
                    match scope.WorkspaceDirectory with
                    | None -> return false
                    | Some directory ->
                        let! workOpt = lifecycleWorkOf directory context.SessionId

                        return
                            workOpt
                            |> Option.bind (fun work -> work.Confirmation)
                            |> Option.exists (fun fence ->
                                fence.ProviderRunId = providerRunId && fence.ToolCallId = toolCallId)
                }

            if confirmedReplay then
                scope.UnfreezeRetirement context.SessionId
                return Ok(confirmationResult prepared pending)
            else
                return! confirmFirst ()
        }

    let private enqueueRelayedSuccessor (scope: ToolRuntimeScope) (prepared: PreparedRetirement) (kind: string) =
        task {
            if kind = "Relayed" then
                let! continued = scope.ContinueManagerLoop(prepared.SessionId, prepared.Bound.Directory)

                continued
                |> Result.defaultWith (fun error -> raise (InvalidOperationException error))
        }

    let private deliverFrozenCursor
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        (pending: (string * string) list)
        (providerRunId: string)
        (toolCallId: string)
        (journalDir: string)
        (cursor: int64)
        : Task<Result<string, string>> =
        let blockers = scope.RetirementBlockersFor context.SessionId
        let requestId = providerRunId + ":" + toolCallId

        let command =
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.command
                "Deliver"
                [ "incumbencyId", box prepared.Incumbent
                  "providerRunId", box providerRunId
                  "toolCallId", box toolCallId
                  "cursor", box (float cursor)
                  "blockers", box (List.toArray blockers)
                  "nextOwnerId", box context.SessionId ]

        let receipt =
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.execute journalDir context.SessionId requestId command

        let durableOutcome =
            receipt.Status = "Committed"
            || receipt.Status = "Replayed"
            || receipt.Status = "Observed"

        if
            receipt.Status = "Unknown"
            || receipt.Status = "NotWritten"
            || receipt.Status = "Conflict"
        then
            Task.FromResult(
                Error(
                    if receipt.Reason = "" then
                        receipt.Status
                    else
                        receipt.Reason
                )
            )
        elif receipt.Kind = "ReviewRequired" then
            Task.FromResult(Error "assessment required")
        elif durableOutcome then
            task {
                do! enqueueRelayedSuccessor scope prepared receipt.Kind
                scope.UnfreezeRetirement context.SessionId

                if receipt.Kind = "Delivered" then
                    let retirementId =
                        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.resultText receipt "retirementId"

                    do! JournalSurface.appendAuthorityLogicalRunClosed journalDir context.SessionId retirementId

                    let! interrupted = scope.Sessions.InterruptAttempt prepared.SessionId

                    interrupted
                    |> Result.defaultWith (fun reason ->
                        invalidOp ("Manager delivery was recorded, but its attempt interrupt failed: " + reason))

                return
                    Ok(
                        if receipt.Kind = "Confirmed" then
                            confirmationResult prepared pending
                        elif receipt.Kind = "Blocked" then
                            // Algorithm E8: a committed blocker keeps the tenure
                            // active. The call reports what to close and may be
                            // repeated once the resources have settled.
                            blockedResult blockers
                        else
                            text Path.Retired
                    )
            }
        else
            Task.FromResult(
                Error(
                    if receipt.Reason = "" then
                        receipt.Status
                    else
                        receipt.Reason
                )
            )

    let private observeFrozenCursor
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        (pending: (string * string) list)
        (providerRunId: string)
        (toolCallId: string)
        (journalDir: string)
        : Task<Result<string, string>> =
        task {
            let! observedHead = observedRetirementCursor scope context prepared.Bound.Journal

            match observedHead with
            | None -> return Error "retirement cursor was not observed"
            | Some cursor ->
                return! deliverFrozenCursor scope context prepared pending providerRunId toolCallId journalDir cursor
        }

    let private runFrozenDeliver
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        (pending: (string * string) list)
        (providerRunId: string)
        (toolCallId: string)
        (journalDir: string)
        : Task<Result<string, string>> =
        task {
            match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected journalDir context.SessionId with
            | None -> return Error "lifecycle work is missing"
            | Some _ -> return! observeFrozenCursor scope context prepared pending providerRunId toolCallId journalDir
        }

    let private runFrozen (scope: ToolRuntimeScope) (context: HostToolContext) (prepared: PreparedRetirement) =
        let hasAssessment = prepared.Reviewed
        let toolCallId = ToolCallId.value prepared.Bound.ToolCallId
        let providerRunId = ProviderRunIdentity.value prepared.Bound.ProviderRun

        let pending: (string * string) list = []

        let confirmFirst () =
            task {
                let directory = scope.WorkspaceDirectory

                let! commonDir =
                    match directory with
                    | None -> Task.FromResult None
                    | Some d ->
                        task {
                            let! cr = RuntimePath.stateRoot d
                            return Some cr
                        }

                match commonDir with
                | None -> return Error "lifecycle journal directory is unavailable"
                | Some journalDir when String.IsNullOrWhiteSpace journalDir ->
                    return Error "lifecycle journal directory is unavailable"
                | Some journalDir ->
                    return! runFrozenDeliver scope context prepared pending providerRunId toolCallId journalDir
            }

        if not hasAssessment then
            scope.UnfreezeRetirement context.SessionId
            Task.FromResult(Ok(assessmentRequiredResult ()))
        else
            runConfirmed scope context prepared pending toolCallId providerRunId confirmFirst

    let private unfreezeUnlessRetired (scope: ToolRuntimeScope) (context: HostToolContext) =
        task {
            let! facts = scope.ManagerCapabilityFactsFor context.SessionId

            if facts.HasActiveIncumbency then
                scope.UnfreezeRetirement context.SessionId
        }

    let private finishOutcome (scope: ToolRuntimeScope) (context: HostToolContext) outcome =
        match outcome with
        | Ok value -> value
        | Error error ->
            scope.UnfreezeRetirement context.SessionId
            raise (InvalidOperationException error)

    let private refreshedTarget
        (target: PreparedRetirement)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        =
        match work.ActiveId, work.Reviewed with
        | Some id, reviewed when id = target.Incumbent ->
            Ok
                { Bound = target.Bound
                  SessionId = target.SessionId
                  Incumbent = target.Incumbent
                  Reviewed = reviewed }
        | _ -> Error(text Path.BindingUnavailable)

    let private refreshAfterFreeze
        (context: HostToolContext)
        (target: PreparedRetirement)
        : Task<Result<PreparedRetirement, string>> =
        task {
            try
                let! workOpt = lifecycleWorkOf target.Bound.Directory (SessionId.value target.SessionId)

                return
                    workOpt
                    |> Option.map (refreshedTarget target)
                    |> Option.defaultValue (Error(text Path.BindingUnavailable))
            with _ ->
                return Error(text Path.FinishFailed)
        }

    let private refreshedOrRaise
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (refreshResult: Result<PreparedRetirement, string>)
        =
        match refreshResult with
        | Ok refreshed -> refreshed
        | Error error ->
            scope.UnfreezeRetirement context.SessionId
            raise (InvalidOperationException error)

    let private executePrepared (scope: ToolRuntimeScope) (context: HostToolContext) (target: PreparedRetirement) =
        task {
            try
                scope.TryFreezeRetirement(context.SessionId, target.Incumbent) |> ignore

                let! refreshed =
                    task {
                        let! r = refreshAfterFreeze context target
                        return refreshedOrRaise scope context r
                    }

                let! outcome = runFrozen scope context refreshed
                return finishOutcome scope context outcome
            with ex ->
                do! unfreezeUnlessRetired scope context
                return raise ex
        }

    let private execute (scope: ToolRuntimeScope) (_: HostToolArguments) (context: HostToolContext) =
        task {
            let boundRes = bindInvocation scope context

            let! entered =
                match boundRes with
                | Error err -> Task.FromResult(Error err)
                | Ok bound -> enterDeliver context bound

            match entered with
            | Ok(Replayed result) -> return result
            | Ok(Prepared target) -> return! executePrepared scope context target
            | Error error -> return raise (InvalidOperationException error)
        }

    let description () = text Path.Description

    let admission =
        ToolAdmission.OfficeRole(fun _ role -> OfficeCapability.isAllowed role ToolPermission.Finality)

    let spec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        { Name = "deliver"
          Description = text Path.Description
          Arguments = []
          Admission = admission
          Execute = execute scope }
