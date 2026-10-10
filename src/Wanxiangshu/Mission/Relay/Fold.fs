namespace Wanxiangshu.Mission.Relay

open FsToolkit.ErrorHandling

type private AssessmentRecord =
    { Id: AssessmentId
      Binding: AssessmentBinding
      SnapshotId: WorkspaceSnapshotId
      AuthorityRevision: AuthorityRevision
      Findings: AssessmentFindings }

type private RetirementConfirmation =
    { ProviderRunId: string
      ToolCallId: string }

type private ActiveIncumbency =
    { Id: IncumbencyId
      SnapshotId: WorkspaceSnapshotId
      AuthorityRevision: AuthorityRevision
      Phase: IncumbencyPhase
      Assessment: AssessmentRecord option
      CleanupBlockerDigest: string option
      Confirmation: RetirementConfirmation option }

type private RoadState =
    { AuthorityRevision: AuthorityRevision
      AuthorityRevisions: AuthorityRevision list
      AuthorityMessageIds: PhysicalUserMessageId list
      Active: ActiveIncumbency option
      Retired: IncumbencyId list
      RetiredProviderRunIds: Set<string>
      SeenAssessmentIds: Set<string>
      SeenAssessmentToolCallIds: Set<string>
      Certificate: QualityCertificate option
      LatestRetirement: RetirementSummary option
      BoundDevOps: string option
      BoundDevOpsModelTarget: string option }

type RelayState = private RelayState of Map<string, RoadState>

type RoadView =
    {
        AuthorityRevision: AuthorityRevision
        AuthorityRevisions: AuthorityRevision list
        AuthorityMessageIds: PhysicalUserMessageId list
        /// How many iterations this road has opened, counting the active one.
        /// Derived purely from durable openings; never an execution cursor.
        IterationOrdinal: int
        ActiveIncumbency: IncumbencyId option
        ActivePhase: IncumbencyPhase option
        ActiveSnapshotId: WorkspaceSnapshotId option
        ActiveAuthorityRevision: AuthorityRevision option
        ActiveCleanupBlockerDigest: string option
        AcceptedAssessmentTransport: (string * string) option
        /// The first end-of-work confirmation recorded for the active
        /// incumbency: (providerRunId, toolCallId). None before the first
        /// suicide call. A second call with a different toolCallId retires.
        RetirementConfirmation: (string * string) option
        /// The active incumbency's accepted findings, exposed so the first
        /// suicide confirmation can return the review commitments verbatim.
        AcceptedAssessmentFindings: AssessmentFindings option
        RetiredIncumbencies: IncumbencyId list
        RetiredProviderRunIds: Set<string>
        Certificate: QualityCertificate option
        LatestRetirement: RetirementSummary option
        BoundDevOps: string option
        BoundDevOpsModelTarget: string option
    }

module private Internal =
    let private key roadId = RoadId.value roadId
    let road roadId (RelayState roads) = Map.tryFind (key roadId) roads

    let update roadId roadState (RelayState roads) =
        RelayState(Map.add (key roadId) roadState roads)

    let certificateId assessmentId =
        QualityCertificateId.create ("certificate:" + AssessmentId.value assessmentId)

    let require error =
        function
        | Some value -> Ok value
        | None -> Error error

    let private newCertificate perfect assessmentId (active: ActiveIncumbency) snapshotId authorityRevision binding =
        if perfect then
            Some
                { Id = certificateId assessmentId
                  AssessmentId = assessmentId
                  IncumbencyId = active.Id
                  SnapshotId = snapshotId
                  AuthorityRevision = authorityRevision
                  Binding = binding
                  Valid = true
                  InvalidationReason = None }
        else
            None

    let private phaseAfterAssessment perfect =
        if perfect then
            IncumbencyPhase.PerfectAwaitingRetirement
        else
            IncumbencyPhase.WorkOwned

    let private acceptAssessment
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        let perfect = AssessmentFindings.isEmpty findings

        let certificate =
            newCertificate perfect assessmentId active snapshotId authorityRevision binding

        let updatedActive =
            { active with
                Assessment =
                    Some
                        { Id = assessmentId
                          Binding = binding
                          // Always record latest snapshot from the assessment
                          SnapshotId = snapshotId
                          AuthorityRevision = authorityRevision
                          Findings = findings }
                Phase = phaseAfterAssessment perfect }

        { current with
            Active = Some updatedActive
            SeenAssessmentIds = Set.add (AssessmentId.value assessmentId) current.SeenAssessmentIds
            SeenAssessmentToolCallIds = Set.add binding.ToolCallId current.SeenAssessmentToolCallIds
            Certificate = certificate }
        |> fun updated -> update roadId updated state
        |> Ok

    let private requireMatchingIncumbency (active: ActiveIncumbency) incumbencyId =
        if active.Id <> incumbencyId then
            Error "IncumbencyNotActive"
        else
            Ok()

    let private isExactAssessment
        (accepted: AssessmentRecord)
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        accepted.Id = assessmentId
        && accepted.Binding = binding
        && accepted.SnapshotId = snapshotId
        && accepted.AuthorityRevision = authorityRevision
        && accepted.Findings = findings

    let private isConflictingAssessment
        (accepted: AssessmentRecord)
        (assessmentId: AssessmentId)
        (binding: AssessmentBinding)
        =
        accepted.Id = assessmentId || accepted.Binding.ToolCallId = binding.ToolCallId

    let tryReplayAssessment state roadId incumbencyId binding snapshotId authorityRevision findings =
        result {
            let! current = road roadId state |> require "RoadNotOpen"
            let! active = current.Active |> require "NoActiveIncumbency"
            do! requireMatchingIncumbency active incumbencyId
            let! accepted = active.Assessment |> require "AssessmentRequired"

            if
                current.AuthorityRevision = authorityRevision
                && active.AuthorityRevision = authorityRevision
                && isExactAssessment accepted accepted.Id binding snapshotId authorityRevision findings
            then
                return accepted.Findings
            else
                return! Error "AssessmentReplayConflict"
        }

    let private decideStoredAssessment
        (accepted: AssessmentRecord)
        state
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        if isExactAssessment accepted assessmentId binding snapshotId authorityRevision findings then
            Ok state
        elif isConflictingAssessment accepted assessmentId binding then
            Error "AssessmentReplayConflict"
        else
            Error "AssessmentAlreadySubmitted"

    let private validateFreshAssessment
        (active: ActiveIncumbency)
        (current: RoadState)
        snapshotId
        authorityRevision
        assessmentId
        (binding: AssessmentBinding)
        =
        if active.Phase <> IncumbencyPhase.AuditPending then
            Error "AssessmentNotAllowedInCurrentPhase"
        elif active.AuthorityRevision <> authorityRevision then
            Error "AuthorityRevisionStale"
        elif Set.contains (AssessmentId.value assessmentId) current.SeenAssessmentIds then
            Error "AssessmentReplayConflict"
        elif Set.contains binding.ToolCallId current.SeenAssessmentToolCallIds then
            // relay-assessment-002: one tool call owns at most one assessment on a
            // road; a later incumbency cannot replay a retired call as fresh work.
            Error "AssessmentReplayToolCall"
        else
            Ok()

    let private commitFreshAssessment
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        result {
            do! validateFreshAssessment active current snapshotId authorityRevision assessmentId binding

            return!
                acceptAssessment roadId state current active assessmentId binding snapshotId authorityRevision findings
        }

    let private decideAssessment
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        match active.Assessment with
        | Some accepted ->
            decideStoredAssessment accepted state assessmentId binding snapshotId authorityRevision findings
        | None ->
            commitFreshAssessment roadId state current active assessmentId binding snapshotId authorityRevision findings

    let private assess
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        incumbencyId
        assessmentId
        binding
        snapshotId
        authorityRevision
        findings
        =
        result {
            do! requireMatchingIncumbency active incumbencyId

            return!
                decideAssessment roadId state current active assessmentId binding snapshotId authorityRevision findings
        }

    let private authorityReplay exactReplay state =
        if exactReplay then
            Ok state
        else
            Error "AuthorityRevisionReplayConflict"

    let private invalidateForAuthorityRevision next (certificate: QualityCertificate) =
        if certificate.Valid then
            { certificate with
                Valid = false
                InvalidationReason = Some("AuthorityRevisionAdvanced:" + AuthorityRevision.value next) }
        else
            certificate

    let private isExactAuthorityReplay
        (current: RoadState)
        (active: ActiveIncumbency)
        next
        snapshotId
        authorityMessageId
        =
        active.AuthorityRevision = next
        && active.SnapshotId = snapshotId
        && current.AuthorityRevisions |> List.tryLast = Some next
        && current.AuthorityMessageIds |> List.tryLast = Some authorityMessageId

    let private findAuthorityAdvanceViolation
        (current: RoadState)
        (active: ActiveIncumbency)
        expected
        next
        authorityMessageId
        =
        if List.contains next current.AuthorityRevisions then
            Some "AuthorityRevisionAlreadySuperseded"
        elif current.AuthorityRevision <> expected || active.AuthorityRevision <> expected then
            Some "AuthorityRevisionStale"
        elif next = expected then
            Some "AuthorityRevisionUnchanged"
        elif List.contains authorityMessageId current.AuthorityMessageIds then
            Some "AuthorityMessageAlreadyUsed"
        else
            None

    let private commitAuthorityAdvance
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        next
        snapshotId
        authorityMessageId
        =
        let updatedActive =
            { active with
                AuthorityRevision = next
                SnapshotId = snapshotId }

        { current with
            AuthorityRevision = next
            AuthorityRevisions = current.AuthorityRevisions @ [ next ]
            AuthorityMessageIds = current.AuthorityMessageIds @ [ authorityMessageId ]
            Active = Some updatedActive
            Certificate = current.Certificate |> Option.map (invalidateForAuthorityRevision next) }
        |> fun updated -> update roadId updated state
        |> Ok

    let private decideFreshAuthorityAdvance
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        expected
        next
        authorityMessageId
        snapshotId
        =
        match findAuthorityAdvanceViolation current active expected next authorityMessageId with
        | Some error -> Error error
        | None -> commitAuthorityAdvance roadId state current active next snapshotId authorityMessageId

    let private decideAuthorityAdvance
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        expected
        next
        authorityMessageId
        snapshotId
        =
        if current.AuthorityRevision = next then
            authorityReplay (isExactAuthorityReplay current active next snapshotId authorityMessageId) state
        else
            decideFreshAuthorityAdvance roadId state current active expected next authorityMessageId snapshotId

    let private advanceAuthority
        roadId
        state
        (current: RoadState)
        (active: ActiveIncumbency)
        incumbentId
        expected
        next
        authorityMessageId
        snapshotId
        =
        result {
            do! requireMatchingIncumbency active incumbentId
            return! decideAuthorityAdvance roadId state current active expected next authorityMessageId snapshotId
        }

    let private requireRetirementAssessment (active: ActiveIncumbency) =
        match active.Assessment with
        | None -> Error "AssessmentRequired"
        | Some assessment -> Ok assessment

    let private checkRetirementAuthority
        (current: RoadState)
        (active: ActiveIncumbency)
        (retirement: RetirementSummary)
        =
        if
            retirement.AuthorityRevision <> current.AuthorityRevision
            || retirement.AuthorityRevision <> active.AuthorityRevision
        then
            Error "AuthorityRevisionMismatch"
        else
            Ok()

    let private checkAcceptedPrerequisites (active: ActiveIncumbency) =
        if
            active.Phase <> IncumbencyPhase.PerfectAwaitingRetirement
            && active.Phase <> IncumbencyPhase.RetirementCleanupBlocked
        then
            Error "RetirementRequiresPerfectAssessment"
        else
            Ok()

    let private checkAcceptedCertificateBindings
        (certificate: QualityCertificate)
        (certificateId: QualityCertificateId)
        (active: ActiveIncumbency)
        =
        if certificate.Id <> certificateId then
            Error "QualityCertificateMismatch"
        elif not certificate.Valid then
            Error "QualityCertificateInvalid"
        elif certificate.IncumbencyId <> active.Id then
            Error "QualityCertificateIncumbencyMismatch"
        else
            Ok()

    let private decideAcceptedCertificate
        (current: RoadState)
        (certificateId: QualityCertificateId)
        (active: ActiveIncumbency)
        =
        match current.Certificate with
        | None -> Error "QualityCertificateNotFound"
        | Some certificate -> checkAcceptedCertificateBindings certificate certificateId active

    let private admitAcceptedOutcome
        (current: RoadState)
        (active: ActiveIncumbency)
        (certificateId: QualityCertificateId)
        =
        result {
            do! checkAcceptedPrerequisites active
            return! decideAcceptedCertificate current certificateId active
        }

    let private admitContinueOutcome () = Ok()

    let private admitOutcomeForAssessment
        (current: RoadState)
        (active: ActiveIncumbency)
        (retirement: RetirementSummary)
        =
        match retirement.Outcome with
        | RetirementOutcome.Accepted certificateId -> admitAcceptedOutcome current active certificateId
        | RetirementOutcome.Continue -> admitContinueOutcome ()

    let private admitOutcome (current: RoadState) (active: ActiveIncumbency) (retirement: RetirementSummary) =
        result {
            let! _ = requireRetirementAssessment active
            do! checkRetirementAuthority current active retirement
            return! admitOutcomeForAssessment current active retirement
        }

    let private replayedRetirement (current: RoadState) state (retirement: RetirementSummary) =
        match current.Active, current.LatestRetirement with
        | None, Some accepted when accepted = retirement -> Some(Ok state)
        | None, Some accepted when accepted.Id = retirement.Id -> Some(Error "RetirementReplayConflict")
        | _ -> None

    let private requireRetirementTarget (current: RoadState) (retirement: RetirementSummary) =
        match current.Active with
        | None -> Error "NoActiveIncumbency"
        | Some active when active.Id <> retirement.IncumbencyId -> Error "IncumbencyNotActive"
        | Some _ when List.contains retirement.IncumbencyId current.Retired -> Error "IncumbencyAlreadyRetired"
        | Some active -> Ok active

    let private commitRetirement roadId state (current: RoadState) (retirement: RetirementSummary) =
        { current with
            Active = None
            Retired = current.Retired @ [ retirement.IncumbencyId ]
            RetiredProviderRunIds = Set.add retirement.ProjectionCut.ProviderRunId current.RetiredProviderRunIds
            LatestRetirement = Some retirement }
        |> fun updated -> update roadId updated state
        |> Ok

    let private commitNewRetirement roadId state (current: RoadState) (retirement: RetirementSummary) =
        result {
            let! active = requireRetirementTarget current retirement
            do! admitOutcome current active retirement
            return! commitRetirement roadId state current retirement
        }

    let private retire roadId state (current: RoadState) (retirement: RetirementSummary) =
        replayedRetirement current state retirement
        |> Option.defaultWith (fun () -> commitNewRetirement roadId state current retirement)

    let private openRoad roadId state eventRoadId authorityRevision authorityMessageId =
        match eventRoadId = roadId, road roadId state with
        | false, _ -> Error "RoadIdentityMismatch"
        | true, Some current when current.AuthorityRevisions.IsEmpty ->
            { current with
                AuthorityRevision = authorityRevision
                AuthorityRevisions = [ authorityRevision ]
                AuthorityMessageIds = [ authorityMessageId ] }
            |> fun opened -> update roadId opened state
            |> Ok
        | true, Some _ -> Error "RoadAlreadyOpen"
        | true, None ->
            { AuthorityRevision = authorityRevision
              AuthorityRevisions = [ authorityRevision ]
              AuthorityMessageIds = [ authorityMessageId ]
              Active = None
              Retired = []
              RetiredProviderRunIds = Set.empty
              SeenAssessmentIds = Set.empty
              SeenAssessmentToolCallIds = Set.empty
              Certificate = None
              LatestRetirement = None
              BoundDevOps = Some("devops:" + RoadId.value roadId)
              BoundDevOpsModelTarget = None }
            |> fun opened -> update roadId opened state
            |> Ok

    let private isForeignDevOps roadId (existing: string) (devopsId: string) =
        existing <> devopsId && existing <> ("devops:" + RoadId.value roadId)

    let private applyDevOpsBinding roadId state (current: RoadState) devopsId modelTarget =
        match current.BoundDevOps, current.BoundDevOpsModelTarget with
        | Some existing, Some target when existing = devopsId && Some target = modelTarget -> Ok state
        | Some existing, None when existing = devopsId ->
            update
                roadId
                { current with
                    BoundDevOps = Some devopsId
                    BoundDevOpsModelTarget = modelTarget }
                state
            |> Ok
        | Some existing, _ when isForeignDevOps roadId existing devopsId -> Error "RoadDevOpsAlreadyBound"
        | _ ->
            update
                roadId
                { current with
                    BoundDevOps = Some devopsId
                    BoundDevOpsModelTarget = modelTarget }
                state
            |> Ok

    let private openRoadForDevOps roadId state devopsId modelTarget =
        let opened =
            { AuthorityRevision = AuthorityRevision.create ""
              AuthorityRevisions = []
              AuthorityMessageIds = []
              Active = None
              Retired = []
              RetiredProviderRunIds = Set.empty
              SeenAssessmentIds = Set.empty
              SeenAssessmentToolCallIds = Set.empty
              Certificate = None
              LatestRetirement = None
              BoundDevOps = Some devopsId
              BoundDevOpsModelTarget = modelTarget }

        update roadId opened state |> Ok

    let private applyDevOpsToRoad roadId state (existing: RoadState option) devopsId modelTarget =
        match existing with
        | None -> openRoadForDevOps roadId state devopsId modelTarget
        | Some current -> applyDevOpsBinding roadId state current devopsId modelTarget

    let private bindDevOps roadId state eventRoadId devopsId modelTarget =
        if eventRoadId <> roadId then
            Error "RoadIdentityMismatch"
        else
            applyDevOpsToRoad roadId state (road roadId state) devopsId modelTarget

    let private makePendingIncumbency (current: RoadState) incumbentId snapshotId =
        { Id = incumbentId
          SnapshotId = snapshotId
          AuthorityRevision = current.AuthorityRevision
          Phase = IncumbencyPhase.AuditPending
          Assessment = None
          CleanupBlockerDigest = None
          Confirmation = None }

    let private commitPendingIncumbency roadId state (current: RoadState) incumbentId snapshotId =
        update
            roadId
            { current with
                Active = Some(makePendingIncumbency current incumbentId snapshotId) }
            state
        |> Ok

    let private decideAcceptedReopen roadId state (current: RoadState) incumbentId snapshotId =
        commitPendingIncumbency roadId state current incumbentId snapshotId

    let private decideRetirementReopen roadId state (current: RoadState) incumbentId snapshotId retirement =
        match retirement.Outcome with
        | RetirementOutcome.Continue -> commitPendingIncumbency roadId state current incumbentId snapshotId
        | RetirementOutcome.Accepted _ -> decideAcceptedReopen roadId state current incumbentId snapshotId

    let private decideInactiveReopen roadId state (current: RoadState) incumbentId snapshotId =
        match current.LatestRetirement with
        | None -> commitPendingIncumbency roadId state current incumbentId snapshotId
        | Some retirement -> decideRetirementReopen roadId state current incumbentId snapshotId retirement

    let private decideIncumbencyOpen roadId state (current: RoadState) incumbentId snapshotId =
        match current.Active with
        | Some active when active.Id = incumbentId && active.SnapshotId = snapshotId -> Ok state
        | Some _ -> Error "ActiveIncumbencyAlreadyExists"
        | None when List.contains incumbentId current.Retired -> Error "RetiredIncumbencyCannotReactivate"
        | None -> decideInactiveReopen roadId state current incumbentId snapshotId

    let private openIncumbency roadId state incumbentId snapshotId =
        result {
            let! current = road roadId state |> require "RoadNotOpen"
            return! decideIncumbencyOpen roadId state current incumbentId snapshotId
        }

    let private invalidateCertificate roadId state certificateId reason =
        result {
            let! current = road roadId state |> require "RoadNotOpen"

            return!
                match current.Certificate with
                | Some certificate when certificate.Id = certificateId && certificate.Valid ->
                    let invalidated =
                        { certificate with
                            Valid = false
                            InvalidationReason = Some reason }

                    update
                        roadId
                        { current with
                            Certificate = Some invalidated }
                        state
                    |> Ok
                | Some certificate when certificate.Id = certificateId -> Ok state
                | _ -> Error "QualityCertificateNotFound"
        }

    let private requireBlockTarget (current: RoadState) incumbencyId =
        match current.Active with
        | Some active when active.Id = incumbencyId -> Ok active
        | Some _ -> Error "IncumbencyNotActive"
        | None -> Error "NoActiveIncumbency"

    let private requireBlockAssessment (active: ActiveIncumbency) =
        match active.Assessment with
        | None -> Error "AssessmentRequired"
        | Some _ -> Ok()

    let private commitBlockedCleanup roadId state (current: RoadState) (active: ActiveIncumbency) blockerDigest =
        update
            roadId
            { current with
                Active =
                    Some
                        { active with
                            Phase = IncumbencyPhase.RetirementCleanupBlocked
                            CleanupBlockerDigest = Some blockerDigest } }
            state
        |> Ok

    let private blockRetirementCleanup roadId state incumbencyId blockerDigest =
        result {
            let! current = road roadId state |> require "RoadNotOpen"
            let! active = requireBlockTarget current incumbencyId
            do! requireBlockAssessment active
            return! commitBlockedCleanup roadId state current active blockerDigest
        }

    let private confirmRetirement roadId state incumbencyId providerRunId toolCallId =
        result {
            let! current = road roadId state |> require "RoadNotOpen"
            let! active = requireBlockTarget current incumbencyId

            match active.Confirmation with
            | Some existing when existing.ProviderRunId = providerRunId && existing.ToolCallId = toolCallId ->
                return state
            | Some _ -> return! Error "RetirementConfirmationReplayConflict"
            | None ->
                return
                    update
                        roadId
                        { current with
                            Active =
                                Some
                                    { active with
                                        Confirmation =
                                            Some
                                                { ProviderRunId = providerRunId
                                                  ToolCallId = toolCallId } } }
                        state
        }

    let applyEvent roadId state event =
        match event with
        | RelayEvent.RoadOpened(eventRoadId, authorityRevision, authorityMessageId) ->
            openRoad roadId state eventRoadId authorityRevision authorityMessageId
        | RelayEvent.RoadDevOpsBound(eventRoadId, devopsId, modelTarget) ->
            bindDevOps roadId state eventRoadId devopsId modelTarget
        | RelayEvent.IncumbencyOpened(incumbentId, snapshotId) -> openIncumbency roadId state incumbentId snapshotId
        | RelayEvent.AssessmentCommitted(assessmentId, incumbencyId, binding, snapshotId, authorityRevision, findings) ->
            result {
                let! current = road roadId state |> require "RoadNotOpen"
                let! active = current.Active |> require "NoActiveIncumbency"

                return!
                    assess
                        roadId
                        state
                        current
                        active
                        incumbencyId
                        assessmentId
                        binding
                        snapshotId
                        authorityRevision
                        findings
            }
        | RelayEvent.AuthorityRevisionAdvanced(incumbentId, expected, next, authorityMessageId, snapshotId) ->
            result {
                let! current = road roadId state |> require "RoadNotOpen"
                let! active = current.Active |> require "NoActiveIncumbency"

                return!
                    advanceAuthority roadId state current active incumbentId expected next authorityMessageId snapshotId
            }
        | RelayEvent.QualityCertificateInvalidated(certificateId, reason) ->
            invalidateCertificate roadId state certificateId reason
        | RelayEvent.RetirementCleanupBlocked(incumbencyId, blockerDigest) ->
            blockRetirementCleanup roadId state incumbencyId blockerDigest
        | RelayEvent.RetirementConfirmationCommitted(incumbencyId, providerRunId, toolCallId) ->
            confirmRetirement roadId state incumbencyId providerRunId toolCallId
        | RelayEvent.RetirementCommitted retirement ->
            result {
                let! current = road roadId state |> require "RoadNotOpen"
                return! retire roadId state current retirement
            }

module Fold =
    let empty = RelayState Map.empty

    let tryReplayAssessment state roadId incumbencyId binding snapshotId authorityRevision findings =
        Internal.tryReplayAssessment state roadId incumbencyId binding snapshotId authorityRevision findings

    let apply state roadId transaction =
        RelayTransaction.events transaction
        |> List.fold
            (fun accumulated event ->
                match accumulated with
                | Error error -> Error error
                | Ok current -> Internal.applyEvent roadId current event)
            (Ok state)

    /// obligation-ledger-004: enumerate every durable road id so a read-only
    /// projection forwarder can observe each road through `view` without
    /// touching the private state representation.
    let roads (state: RelayState) : RoadId list =
        let (RelayState roadMap) = state

        roadMap |> Map.toList |> List.map (fst >> RoadId.create)

    let view state roadId =
        Internal.road roadId state
        |> Option.map (fun (road: RoadState) ->
            { AuthorityRevision = road.AuthorityRevision
              AuthorityRevisions = road.AuthorityRevisions
              AuthorityMessageIds = road.AuthorityMessageIds
              // An opening is a durable IncumbencyOpened event: the road's own
              // history plus the active one, so the first iteration is 1.
              IterationOrdinal = List.length road.Retired + (if road.Active.IsSome then 1 else 0)
              ActiveIncumbency = road.Active |> Option.map (fun active -> active.Id)
              ActivePhase = road.Active |> Option.map (fun active -> active.Phase)
              ActiveSnapshotId = road.Active |> Option.map (fun active -> active.SnapshotId)
              ActiveAuthorityRevision = road.Active |> Option.map (fun active -> active.AuthorityRevision)
              ActiveCleanupBlockerDigest = road.Active |> Option.bind (fun active -> active.CleanupBlockerDigest)
              AcceptedAssessmentTransport =
                road.Active
                |> Option.bind (fun active ->
                    active.Assessment
                    |> Option.map (fun assessment -> assessment.Binding.ToolCallId, assessment.Binding.PayloadDigest))
              RetirementConfirmation =
                road.Active
                |> Option.bind (fun active ->
                    active.Confirmation
                    |> Option.map (fun confirmation -> confirmation.ProviderRunId, confirmation.ToolCallId))
              AcceptedAssessmentFindings =
                road.Active
                |> Option.bind (fun active -> active.Assessment |> Option.map (fun assessment -> assessment.Findings))
              RetiredIncumbencies = road.Retired
              RetiredProviderRunIds = road.RetiredProviderRunIds
              Certificate = road.Certificate
              LatestRetirement = road.LatestRetirement
              BoundDevOps = road.BoundDevOps
              BoundDevOpsModelTarget = road.BoundDevOpsModelTarget })

module Decision =
    let private commit state roadId events =
        match RelayTransaction.create events with
        | Error error -> Error error
        | Ok transaction -> Fold.apply state roadId transaction

    let openIncumbency state roadId incumbentId snapshotId authorityRevision =
        match Fold.view state roadId with
        | None ->
            let authorityMessageId =
                PhysicalUserMessageId.create (AuthorityRevision.value authorityRevision)

            commit
                state
                roadId
                [ RelayEvent.RoadOpened(roadId, authorityRevision, authorityMessageId)
                  RelayEvent.IncumbencyOpened(incumbentId, snapshotId) ]
        | Some view when view.AuthorityRevisions.IsEmpty ->
            let authorityMessageId =
                PhysicalUserMessageId.create (AuthorityRevision.value authorityRevision)

            commit
                state
                roadId
                [ RelayEvent.RoadOpened(roadId, authorityRevision, authorityMessageId)
                  RelayEvent.IncumbencyOpened(incumbentId, snapshotId) ]
        | Some view when view.AuthorityRevision <> authorityRevision -> Error "AuthorityRevisionMismatch"
        | Some _ -> commit state roadId [ RelayEvent.IncumbencyOpened(incumbentId, snapshotId) ]

    let private isForeignDevOps roadId (existing: string) (devopsId: string) =
        existing <> devopsId && existing <> ("devops:" + RoadId.value roadId)

    let private applyRoadDevOpsBinding state roadId (view: RoadView) devopsId modelTarget =
        match view.BoundDevOps, view.BoundDevOpsModelTarget with
        | Some existing, Some _ when existing = devopsId -> Ok state
        | Some existing, None when existing = devopsId ->
            commit state roadId [ RelayEvent.RoadDevOpsBound(roadId, devopsId, modelTarget) ]
        | Some existing, _ when isForeignDevOps roadId existing devopsId -> Error "RoadDevOpsAlreadyBound"
        | _ -> commit state roadId [ RelayEvent.RoadDevOpsBound(roadId, devopsId, modelTarget) ]

    let bindRoadDevOps state roadId devopsId (modelTarget: string option) =
        match Fold.view state roadId with
        | None -> commit state roadId [ RelayEvent.RoadDevOpsBound(roadId, devopsId, modelTarget) ]
        | Some view -> applyRoadDevOpsBinding state roadId view devopsId modelTarget

    let advanceAuthority state roadId incumbentId expected next authorityMessageId snapshotId =
        commit
            state
            roadId
            [ RelayEvent.AuthorityRevisionAdvanced(incumbentId, expected, next, authorityMessageId, snapshotId) ]

    let assess state roadId incumbentId assessmentId binding snapshotId authorityRevision findings =
        match Fold.view state roadId with
        | None -> Error "RoadNotOpen"
        | Some view when view.ActiveIncumbency = Some incumbentId ->
            commit
                state
                roadId
                [ RelayEvent.AssessmentCommitted(
                      assessmentId,
                      incumbentId,
                      binding,
                      snapshotId,
                      authorityRevision,
                      findings
                  ) ]
        | Some _ -> Error "IncumbencyNotActive"

    let invalidateCertificate state roadId reason =
        result {
            let! view = Fold.view state roadId |> Internal.require "RoadNotOpen"
            let! certificate = view.Certificate |> Internal.require "QualityCertificateNotFound"
            return! commit state roadId [ RelayEvent.QualityCertificateInvalidated(certificate.Id, reason) ]
        }

    let blockCleanup state roadId incumbentId blockerDigest =
        commit state roadId [ RelayEvent.RetirementCleanupBlocked(incumbentId, blockerDigest) ]

    let confirmRetirement state roadId incumbentId providerRunId toolCallId =
        match Fold.view state roadId with
        | None -> Error "RoadNotOpen"
        | Some view when view.ActiveIncumbency = Some incumbentId ->
            commit state roadId [ RelayEvent.RetirementConfirmationCommitted(incumbentId, providerRunId, toolCallId) ]
        | Some _ -> Error "IncumbencyNotActive"

    let retire state roadId incumbentId retirement =
        match Fold.view state roadId with
        | None -> Error "RoadNotOpen"
        | Some view when view.ActiveIncumbency = Some incumbentId ->
            commit state roadId [ RelayEvent.RetirementCommitted retirement ]
        | Some _ -> Error "IncumbencyNotActive"
