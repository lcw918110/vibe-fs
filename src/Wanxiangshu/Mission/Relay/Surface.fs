namespace Wanxiangshu.Mission.Relay

open Fable.Core.JsInterop
open FsToolkit.ErrorHandling

module Surface =
    let empty () = Fold.empty

    let openRoad road authority message incumbent snapshot =
        let roadId = RoadId.create road
        let authorityRevision = AuthorityRevision.create authority
        let authorityMessageId = PhysicalUserMessageId.create message
        let incumbentId = IncumbencyId.create incumbent
        let snapshotId = WorkspaceSnapshotId.create snapshot

        match
            RelayTransaction.create
                [ RelayEvent.RoadOpened(roadId, authorityRevision, authorityMessageId)
                  RelayEvent.IncumbencyOpened(incumbentId, snapshotId) ]
        with
        | Error error -> failwith error
        | Ok transaction ->
            match Fold.apply Fold.empty roadId transaction with
            | Ok state -> state
            | Error error -> failwith error

    let private result value =
        match value with
        | Ok state -> box {| ok = true; state = state |}
        | Error error -> box {| ok = false; error = error |}

    let openIncumbency state road incumbent snapshot authority =
        Decision.openIncumbency
            state
            (RoadId.create road)
            (IncumbencyId.create incumbent)
            (WorkspaceSnapshotId.create snapshot)
            (AuthorityRevision.create authority)
        |> result

    let private findingOf (value: obj) =
        let acceptance = emitJsExpr (value, "acceptance_criteria") "$0[$1]"
        let workPlan = emitJsExpr (value, "work_plan") "$0[$1]"

        if
            emitJsExpr acceptance "typeof $0 === 'string'"
            && emitJsExpr workPlan "typeof $0 === 'string'"
        then
            Ok
                { AcceptanceCriteria = unbox<string> acceptance
                  WorkPlan = unbox<string> workPlan }
        else
            Error "surface finding requires string acceptance_criteria and work_plan"

    let private findingsOf (value: obj) =
        if not (emitJsExpr value "Array.isArray($0)") then
            Error "surface findings must be an array"
        else
            let length: int = emitJsExpr value "$0.length"

            [ 0 .. length - 1 ]
            |> List.traverseResultM (fun index -> emitJsExpr (value, index) "$0[$1]" |> findingOf)
            |> Result.bind AssessmentFindings.tryCreate

    let assess state road incumbent assessment snapshot authority (findings: obj) =
        match findingsOf findings with
        | Error error -> box {| ok = false; error = error |}
        | Ok findings ->
            let payloadDigest =
                findings
                |> AssessmentFindings.values
                |> List.map (fun finding -> finding.AcceptanceCriteria + "\u0000" + finding.WorkPlan)
                |> String.concat "\n"

            let binding =
                { PhysicalUserMessageId = authority
                  ProviderRunId = "surface-run"
                  ToolCallId = assessment
                  NarrativeDigest = "surface-narrative"
                  PayloadDigest = payloadDigest
                  RootRequestDigest = "surface-root"
                  RequirementSetDigest = "surface-requirements"
                  EvidenceFrontierDigest = "surface-evidence" }

            Decision.assess
                state
                (RoadId.create road)
                (IncumbencyId.create incumbent)
                (AssessmentId.create assessment)
                binding
                (WorkspaceSnapshotId.create snapshot)
                (AuthorityRevision.create authority)
                findings
            |> result

    let invalidateCertificate state road reason =
        Decision.invalidateCertificate state (RoadId.create road) reason |> result

    let advanceAuthority state road incumbent expected next authorityMessageId snapshot =
        Decision.advanceAuthority
            state
            (RoadId.create road)
            (IncumbencyId.create incumbent)
            (AuthorityRevision.create expected)
            (AuthorityRevision.create next)
            (PhysicalUserMessageId.create authorityMessageId)
            (WorkspaceSnapshotId.create snapshot)
        |> result

    let blockCleanup state road incumbent blockerDigest =
        Decision.blockCleanup state (RoadId.create road) (IncumbencyId.create incumbent) blockerDigest
        |> result

    let confirmRetirement state road incumbent providerRunId toolCallId =
        Decision.confirmRetirement state (RoadId.create road) (IncumbencyId.create incumbent) providerRunId toolCallId
        |> result

    let private normalize (value: string) = if isNull value then "" else value

    let private activeRetirementContext state road incumbent =
        match Fold.view state (RoadId.create road) with
        | None -> Error "RoadNotOpen"
        | Some view ->
            match view.ActiveIncumbency, view.ActiveAuthorityRevision with
            | Some activeId, Some authorityRevision when activeId = IncumbencyId.create incumbent ->
                Ok authorityRevision
            | _ -> Error "IncumbencyNotActive"

    let retireContinue state road incumbent retirement providerRun toolCall snapshot =
        match normalize snapshot with
        | "" ->
            box
                {| ok = false
                   error = "RetirementSnapshotStale" |}
        | snapshotValue ->
            match activeRetirementContext state road incumbent with
            | Error error -> box {| ok = false; error = error |}
            | Ok authorityRevision ->
                let summary =
                    { Id = RetirementId.create retirement
                      IncumbencyId = IncumbencyId.create incumbent
                      SnapshotId = WorkspaceSnapshotId.create snapshotValue
                      AuthorityRevision = authorityRevision
                      ProjectionCut =
                        { ProviderRunId = normalize providerRun
                          ToolCallId = normalize toolCall }
                      Outcome = RetirementOutcome.Continue }

                Decision.retire state (RoadId.create road) (IncumbencyId.create incumbent) summary
                |> result

    let retireAccepted state road incumbent retirement providerRun toolCall certificateId snapshot =
        match normalize certificateId, normalize snapshot with
        | "", _ ->
            box
                {| ok = false
                   error = "MissingQualityCertificate" |}
        | _, "" ->
            box
                {| ok = false
                   error = "RetirementSnapshotStale" |}
        | certificate, snapshotValue ->
            match activeRetirementContext state road incumbent with
            | Error error -> box {| ok = false; error = error |}
            | Ok authorityRevision ->
                let summary =
                    { Id = RetirementId.create retirement
                      IncumbencyId = IncumbencyId.create incumbent
                      SnapshotId = WorkspaceSnapshotId.create snapshotValue
                      AuthorityRevision = authorityRevision
                      ProjectionCut =
                        { ProviderRunId = normalize providerRun
                          ToolCallId = normalize toolCall }
                      Outcome = RetirementOutcome.Accepted(QualityCertificateId.create certificate) }

                Decision.retire state (RoadId.create road) (IncumbencyId.create incumbent) summary
                |> result

    let private phaseName phase =
        match phase with
        | IncumbencyPhase.AuditPending -> "AuditPending"
        | IncumbencyPhase.WorkOwned -> "WorkOwned"
        | IncumbencyPhase.PerfectAwaitingRetirement -> "PerfectAwaitingRetirement"
        | IncumbencyPhase.RetirementCleanupBlocked -> "RetirementCleanupBlocked"

    let private nullableString value =
        match value with
        | None -> null
        | Some text -> box text

    let view state road =
        match Fold.view state (RoadId.create road) with
        | None -> null
        | Some roadView ->
            box
                {| activeIncumbency = roadView.ActiveIncumbency |> Option.map IncumbencyId.value |> nullableString
                   iterationOrdinal = roadView.IterationOrdinal
                   phase = roadView.ActivePhase |> Option.map phaseName |> nullableString
                   retirementConfirmed = Option.isSome roadView.RetirementConfirmation
                   retired = roadView.RetiredIncumbencies |> List.map IncumbencyId.value |> List.toArray |}

    let authority state road =
        match Fold.view state (RoadId.create road) with
        | None -> null
        | Some roadView ->
            box
                {| roadRevision = AuthorityRevision.value roadView.AuthorityRevision
                   revisionHistory = roadView.AuthorityRevisions |> List.map AuthorityRevision.value |> List.toArray
                   activeRevision =
                    roadView.ActiveAuthorityRevision
                    |> Option.map AuthorityRevision.value
                    |> nullableString
                   activeSnapshot =
                    roadView.ActiveSnapshotId
                    |> Option.map WorkspaceSnapshotId.value
                    |> nullableString
                   messageIds =
                    roadView.AuthorityMessageIds
                    |> List.map PhysicalUserMessageId.value
                    |> List.toArray |}

    let certificate state road =
        match Fold.view state (RoadId.create road) with
        | Some roadView ->
            match roadView.Certificate with
            | Some certificate ->
                box
                    {| assessmentId = AssessmentId.value certificate.AssessmentId
                       snapshotId = WorkspaceSnapshotId.value certificate.SnapshotId
                       authorityRevision = AuthorityRevision.value certificate.AuthorityRevision
                       valid = certificate.Valid |}
            | None -> null
        | None -> null

    let private outcomeName outcome =
        match outcome with
        | RetirementOutcome.Continue -> "Continue"
        | RetirementOutcome.Accepted _ -> "Accepted"

    let private outcomeCertificate outcome =
        match outcome with
        | RetirementOutcome.Continue -> null
        | RetirementOutcome.Accepted certificateId -> box (QualityCertificateId.value certificateId)

    let retirement state road =
        match Fold.view state (RoadId.create road) with
        | Some roadView ->
            match roadView.LatestRetirement with
            | Some retirement ->
                box
                    {| retirementId = RetirementId.value retirement.Id
                       incumbentId = IncumbencyId.value retirement.IncumbencyId
                       outcome = outcomeName retirement.Outcome
                       providerRunId = retirement.ProjectionCut.ProviderRunId
                       toolCallId = retirement.ProjectionCut.ToolCallId
                       certificateId = outcomeCertificate retirement.Outcome
                       snapshotId = WorkspaceSnapshotId.value retirement.SnapshotId
                       authorityRevision = AuthorityRevision.value retirement.AuthorityRevision |}
            | None -> null
        | None -> null

    let roadDevOps state road =
        match Fold.view state (RoadId.create road) with
        | None -> null
        | Some roadView ->
            let devopsId = roadView.BoundDevOps |> nullableString

            let incumbentId =
                roadView.ActiveIncumbency |> Option.map IncumbencyId.value |> nullableString

            box
                {| devopsId = devopsId
                   incumbentId = incumbentId
                   modelTarget = roadView.BoundDevOpsModelTarget |> nullableString |}

    let bindRoadDevOps state road devopsId (modelTarget: obj) =
        let target =
            if isNull modelTarget then
                None
            elif emitJsExpr modelTarget "typeof $0 === 'string' && $0.trim().length > 0" then
                Some(string modelTarget)
            else
                None

        Decision.bindRoadDevOps state (RoadId.create road) devopsId target |> result
