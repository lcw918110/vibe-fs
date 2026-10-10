namespace Wanxiangshu.Mission.Relay.OpenCode

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Mission.Relay
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Attention
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

module SuicideTool =
    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/suicide/description"

        [<Literal>]
        let Blocked = "tool/suicide/blocked"

        [<Literal>]
        let AssessmentRequired = "tool/suicide/assessment-required"

        [<Literal>]
        let Retired = "tool/suicide/finished"

        [<Literal>]
        let Confirmation = "tool/suicide/confirmation"

        [<Literal>]
        let NonManagerRole = "tool/suicide/non-manager-role"

        [<Literal>]
        let NoAuthority = "tool/suicide/no-authority"

        [<Literal>]
        let NoRetirementProjection = "tool/suicide/finish-not-recorded"

        [<Literal>]
        let BindingUnavailable = "tool/suicide/binding-unavailable"

        [<Literal>]
        let FinishFailed = "tool/suicide/finish-failed"

    let private gitCapability: WorkspaceSnapshotGitCapability =
        { TryRevParseHeadTree = GitSubject.tryRevParseHeadTree
          DiffHeadBinary = GitSubject.diffHeadBinary
          LsFilesUntrackedZ = GitSubject.lsFilesUntrackedZ
          HashObjectNoFilters = GitSubject.hashObjectNoFilters
          StatusPorcelainV2Z = GitSubject.statusPorcelainV2Z
          LsFilesStageZ = GitSubject.lsFilesStageZ }

    let private text (path: string) =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) path Map.empty

    let private pendingDeferred (journal: AgentJournal) (sessionId: SessionId) =
        AgentProjection.pendingAttentionWorkPairs sessionId (AgentJournal.snapshot journal).AgentProjections

    let private currentState (journal: AgentJournal) (sessionId: SessionId) =
        AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind (fun session -> session.Relay)

    let private currentView state roadId =
        state |> Option.bind (fun relay -> Fold.view relay roadId)

    let private deterministicId prefix payload create =
        HostDigest.sha256Hex (prefix + "\n" + payload)
        |> fun digest -> create (prefix + ":" + digest)

    let private qualityCandidate (view: RoadView) incumbent =
        view.Certificate
        |> Option.filter (fun certificate -> certificate.Valid && certificate.IncumbencyId = incumbent)

    let private retirementTransaction roadId incumbent providerRun toolCallId snapshot authority (view: RoadView) =
        let candidate = qualityCandidate view incumbent

        let outcome =
            match candidate with
            | Some certificate -> RetirementOutcome.Accepted certificate.Id
            | None -> RetirementOutcome.Continue

        let outcomeTag, certificatePart =
            match outcome with
            | RetirementOutcome.Continue -> "continue", ""
            | RetirementOutcome.Accepted certificateId -> "accepted", QualityCertificateId.value certificateId

        let payload =
            String.concat
                "\n"
                [ RoadId.value roadId
                  IncumbencyId.value incumbent
                  WorkspaceSnapshotId.value snapshot
                  AuthorityRevision.value authority
                  ProviderRunIdentity.value providerRun
                  ToolCallId.value toolCallId
                  outcomeTag
                  certificatePart ]

        let retirementId = deterministicId "retirement-v1" payload RetirementId.create

        let cut =
            { ProviderRunId = ProviderRunIdentity.value providerRun
              ToolCallId = ToolCallId.value toolCallId }

        let summary: RetirementSummary =
            { Id = retirementId
              IncumbencyId = incumbent
              SnapshotId = snapshot
              AuthorityRevision = authority
              ProjectionCut = cut
              Outcome = outcome }

        let invalidation =
            view.Certificate
            |> Option.filter (fun certificate -> certificate.Valid && candidate.IsNone)
            |> Option.map (fun certificate ->
                RelayEvent.QualityCertificateInvalidated(certificate.Id, "RetirementBindingChanged"))
            |> Option.toList

        RelayTransaction.create (invalidation @ [ RelayEvent.RetirementCommitted summary ])

    let private blockedTransaction incumbent blockerDigest =
        RelayTransaction.create [ RelayEvent.RetirementCleanupBlocked(incumbent, blockerDigest) ]

    let private append journal sessionId providerRun roadId transaction =
        let fact =
            AgentFact.Relay(
                RelayFactCases.TransactionCommitted
                    {| RoadId = roadId
                       Transaction = transaction |}
            )

        AgentJournal.appendAgent (StreamId.Session sessionId) (Some providerRun) fact journal

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

    let private retiredResult () =
        ToolHostCodec.tomlObjectWithInstructions [ text Path.Retired ] [ "finished", ToolHostCodec.TBool true ]

    let private requireSome error =
        function
        | Some value -> Ok value
        | None -> Error error

    type private BoundRetirement =
        { ToolCallId: ToolCallId
          ProviderRun: ProviderRunIdentity
          Directory: string
          Journal: AgentJournal }

    type private PreparedRetirement =
        { Bound: BoundRetirement
          SessionId: SessionId
          RoadId: RoadId
          Snapshot: WorkspaceSnapshotId
          View: RoadView
          Authority: AuthorityRevision
          Incumbent: IncumbencyId }

    type private FreezeTarget =
        { Bound: BoundRetirement
          SessionId: SessionId
          RoadId: RoadId
          Incumbent: IncumbencyId }

    let private requireView roadId state =
        currentView state roadId |> requireSome (text Path.BindingUnavailable)

    let private requireActiveIncumbency (road: RoadView) =
        road.ActiveIncumbency |> requireSome (text Path.BindingUnavailable)

    let private requireSameIncumbency (target: FreezeTarget) (road: RoadView) =
        road.ActiveIncumbency
        |> Option.filter ((=) target.Incumbent)
        |> requireSome (text Path.BindingUnavailable)

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

    let private prepareTarget (context: HostToolContext) (bound: BoundRetirement) =
        let sessionId = SessionId.create context.SessionId
        let roadId = RoadId.create context.SessionId
        let state = currentState bound.Journal sessionId

        result {
            let! road = requireView roadId state
            let! incumbent = requireActiveIncumbency road

            return
                { Bound = bound
                  SessionId = sessionId
                  RoadId = roadId
                  Incumbent = incumbent }
        }

    let private appendPrepared (prepared: PreparedRetirement) transaction =
        task {
            let! outcome =
                append prepared.Bound.Journal prepared.SessionId prepared.Bound.ProviderRun prepared.RoadId transaction

            return outcome |> Result.mapError (fun _ -> text Path.FinishFailed)
        }

    let private retirementFromProjection (prepared: PreparedRetirement) projection =
        AgentProjection.tryFind prepared.SessionId projection.AgentProjections
        |> Option.bind (fun session -> session.Relay)
        |> Option.bind (fun relay -> Fold.view relay prepared.RoadId)
        |> Option.bind (fun road -> road.LatestRetirement)
        |> requireSome (text Path.NoRetirementProjection)

    let private confirmationResult (prepared: PreparedRetirement) pending =
        let commitments =
            prepared.View.AcceptedAssessmentFindings
            |> Option.map AssessmentFindings.values
            |> Option.defaultValue []
            |> List.mapi (fun index finding ->
                string index, ToolHostCodec.TString(finding.AcceptanceCriteria + " => " + finding.WorkPlan))

        let deferred =
            pending
            |> List.mapi (fun index (_, itemText) -> string index, ToolHostCodec.TString itemText)

        let optionalFields =
            [ if not (List.isEmpty commitments) then
                  yield "commitments", ToolHostCodec.TTable commitments

              if not (List.isEmpty deferred) then
                  yield "deferred", ToolHostCodec.TTable deferred ]

        ToolHostCodec.tomlObjectWithInstructions
            [ text Path.Confirmation ]
            ([ "finished", ToolHostCodec.TBool false
               "confirmation_required", ToolHostCodec.TBool true ]
             @ optionalFields)

    let private runBlocked (prepared: PreparedRetirement) blockers =
        let blockerDigest = HostDigest.sha256Hex (String.concat "\n" blockers)

        taskResult {
            let! transaction =
                blockedTransaction prepared.Incumbent blockerDigest
                |> Result.mapError (fun _ -> text Path.FinishFailed)

            let! _ = appendPrepared prepared transaction
            return blockedResult blockers
        }

    /// ATTENTION-005: a completed retirement consumes this life's remaining
    /// deferred work and leaves a durable consumption receipt, so a replayed
    /// `DeferredWorkRecorded` cannot resurrect it after restart.
    let private consumePendingDeferred (prepared: PreparedRetirement) =
        task {
            let port = AttentionConcernJournalAdapter.forAttention prepared.Bound.Journal
            let pending = AttentionProjection.pending prepared.SessionId (port.Read())

            match pending with
            | [] -> return ()
            | items ->
                let fact =
                    AttentionFactCases.DeferredWorkConsumed
                        {| SessionId = prepared.SessionId
                           OccurrenceIds = (items |> List.map (fun item -> item.OccurrenceId)) |}

                let! _ = port.Append prepared.SessionId (Some prepared.Bound.ProviderRun) fact
                return ()
        }

    let private runRetirement (prepared: PreparedRetirement) =
        taskResult {
            let! transaction =
                retirementTransaction
                    prepared.RoadId
                    prepared.Incumbent
                    prepared.Bound.ProviderRun
                    prepared.Bound.ToolCallId
                    prepared.Snapshot
                    prepared.Authority
                    prepared.View
                |> Result.mapError (fun _ -> text Path.FinishFailed)

            let! projection = appendPrepared prepared transaction
            let! _ = retirementFromProjection prepared projection

            return retiredResult ()
        }

    // ATTENTION-005: the completed retirement consumes this life's
    // remaining deferred work. An append failure leaves the outcome
    // unchanged; `RetirementCommitted` stays durable either way.
    let private completeRetirement (prepared: PreparedRetirement) =
        task {
            let! outcome = runRetirement prepared

            match outcome with
            | Ok value ->
                do! consumePendingDeferred prepared
                return Ok value
            | Error error -> return Error error
        }

    let private runRetirementOrBlocked
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        =
        let blockers = scope.RetirementBlockersFor context.SessionId

        if List.isEmpty blockers then
            completeRetirement prepared
        else
            runBlocked prepared blockers

    let private runConfirmed
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (prepared: PreparedRetirement)
        pending
        toolCallId
        providerRunId
        confirmFirst
        =
        match prepared.View.RetirementConfirmation with
        | None -> confirmFirst ()
        | Some(confirmedRun, confirmedCall) when confirmedCall = toolCallId && confirmedRun = providerRunId ->
            scope.UnfreezeRetirement context.SessionId
            Task.FromResult(Ok(confirmationResult prepared pending))
        | Some(_, confirmedCall) when confirmedCall = toolCallId ->
            Task.FromResult(Error "RetirementConfirmationReplayConflict")
        | Some _ -> runRetirementOrBlocked scope context prepared

    let private runFrozen (scope: ToolRuntimeScope) (context: HostToolContext) (prepared: PreparedRetirement) =
        let hasAssessment = prepared.View.AcceptedAssessmentTransport |> Option.isSome
        let toolCallId = ToolCallId.value prepared.Bound.ToolCallId
        let providerRunId = ProviderRunIdentity.value prepared.Bound.ProviderRun

        let pending = pendingDeferred prepared.Bound.Journal prepared.SessionId

        let confirmFirst () =
            taskResult {
                let! transaction =
                    RelayTransaction.create
                        [ RelayEvent.RetirementConfirmationCommitted(prepared.Incumbent, providerRunId, toolCallId) ]
                    |> Result.mapError (fun _ -> text Path.FinishFailed)

                let! _ = appendPrepared prepared transaction
                scope.UnfreezeRetirement context.SessionId
                return confirmationResult prepared pending
            }

        if not hasAssessment then
            scope.UnfreezeRetirement context.SessionId
            Task.FromResult(Ok(assessmentRequiredResult ()))
        else
            runConfirmed scope context prepared pending toolCallId providerRunId confirmFirst

    let private unfreezeUnlessRetired (scope: ToolRuntimeScope) (context: HostToolContext) =
        let facts = scope.ManagerCapabilityFactsFor context.SessionId

        if facts.HasActiveIncumbency then
            scope.UnfreezeRetirement context.SessionId

    let private finishOutcome (scope: ToolRuntimeScope) (context: HostToolContext) outcome =
        match outcome with
        | Ok value -> value
        | Error error ->
            scope.UnfreezeRetirement context.SessionId
            raise (InvalidOperationException error)

    // No session-scoped physical cut here. AbortSession names a session, not the
    // retired run; the Host applies it after an unobservable delay, so on a reused
    // session it lands inside the next loop iteration and kills it mid-tool. Retired
    // output is contained by the durable cut (ProjectionCut.ProviderRunId) plus Retired
    // tool denial; the retired run's further provider requests are refused in
    // the transform hook by exact manager-loop gate identity, so the run ends there.
    let private refreshAfterFreeze (context: HostToolContext) (target: FreezeTarget) =
        try
            result {
                let snapshot = WorkspaceSnapshot.capture gitCapability target.Bound.Directory
                let state = currentState target.Bound.Journal target.SessionId
                let! road = requireView target.RoadId state
                let! incumbent = requireSameIncumbency target road

                return
                    { Bound = target.Bound
                      SessionId = target.SessionId
                      RoadId = target.RoadId
                      Snapshot = snapshot
                      View = road
                      Authority = road.AuthorityRevision
                      Incumbent = incumbent }
            }
        with _ ->
            Error(text Path.FinishFailed)

    let private refreshedOrRaise (scope: ToolRuntimeScope) (context: HostToolContext) refreshResult =
        match refreshResult with
        | Ok refreshed -> refreshed
        | Error error ->
            scope.UnfreezeRetirement context.SessionId
            raise (InvalidOperationException error)

    let private executePrepared (scope: ToolRuntimeScope) (context: HostToolContext) (target: FreezeTarget) =
        task {
            try
                scope.TryFreezeRetirement(context.SessionId, target.Incumbent) |> ignore
                let refreshed = refreshAfterFreeze context target |> refreshedOrRaise scope context
                let! outcome = runFrozen scope context refreshed
                return finishOutcome scope context outcome
            with ex ->
                unfreezeUnlessRetired scope context
                return raise ex
        }

    let private execute (scope: ToolRuntimeScope) (_: HostToolArguments) (context: HostToolContext) =
        match bindInvocation scope context |> Result.bind (prepareTarget context) with
        | Ok target -> executePrepared scope context target
        | Error error -> task { return raise (InvalidOperationException error) }

    let admission =
        ToolAdmission.OfficeRole(fun _ role -> OfficeCapability.isAllowed role ToolPermission.Finality)

    let spec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        { Name = "suicide"
          Description = text Path.Description
          Arguments = []
          Admission = admission
          Execute = execute scope }
