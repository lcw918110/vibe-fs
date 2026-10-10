namespace Wanxiangshu.Mission.Planning

open System
open System.Threading.Tasks
open Wanxiangshu.Context.Trace
open Wanxiangshu.Host
open Wanxiangshu.Persistence.EventStore

type HandoffResult =
    { Ok: bool
      RetiredIncumbencyId: string
      NextIncumbencyId: string
      NextStage: string
      Message: string }

type DeliverResult =
    { Ok: bool
      Digest: string
      Path: string
      Message: string }

type AskResult =
    { Kind: string
      Question: string
      Status: string }

type ResumeResult =
    { Status: string
      Target: string
      Charge: string }

module PlanTools =

    let private requireOpt (err: string) =
        function
        | Some v -> Ok v
        | None -> Error err

    let private validateActiveIncumbency
        (activeOpt: ActivePlanIncumbency option)
        (expectedId: string)
        : Result<ActivePlanIncumbency, string> =
        match activeOpt with
        | None -> Error "No active incumbency found for this work to hand off"
        | Some active when PlanIncumbencyId.value active.Id <> expectedId ->
            Error(
                sprintf
                    "Active incumbency ID '%s' does not match invocation target '%s'"
                    (PlanIncumbencyId.value active.Id)
                    expectedId
            )
        | Some active -> Ok active

    let private validateDeliverIncumbency
        (activeOpt: ActivePlanIncumbency option)
        (expectedId: string)
        : Result<ActivePlanIncumbency, string> =
        match activeOpt with
        | None -> Error "No active incumbency found for this work to deliver"
        | Some active when PlanIncumbencyId.value active.Id <> expectedId ->
            Error(
                sprintf
                    "Active incumbency ID '%s' does not match invocation target '%s'"
                    (PlanIncumbencyId.value active.Id)
                    expectedId
            )
        | Some active -> Ok active

    let private appendOpenOnly
        (store: IEventStore)
        (pid: PlanWorkId)
        (openEvent: PlanEvent)
        : Task<Result<unit, string>> =
        task {
            match! PlanEventStore.append store pid openEvent with
            | Error err -> return Error(sprintf "Failed to open next incumbency during handoff: %s" err)
            | Ok _ -> return Ok()
        }

    let private appendRetireAndOpen
        (store: IEventStore)
        (pid: PlanWorkId)
        (retEvent: PlanEvent)
        (openEvent: PlanEvent)
        : Task<Result<unit, string>> =
        task {
            match! PlanEventStore.append store pid retEvent with
            | Error err -> return Error(sprintf "Failed to retire incumbency during handoff: %s" err)
            | Ok _ -> return! appendOpenOnly store pid openEvent
        }

    let private performHandoffEvents
        (store: IEventStore)
        (pid: PlanWorkId)
        (workId: string)
        (incumbencyId: string)
        (active: ActivePlanIncumbency)
        (nextStage: PlanStage)
        (retirementCursor: int64 option)
        : Task<Result<HandoffResult, string>> =
        task {
            let openCurSeq = XTraceCursor.sequence active.OpeningCursor
            let retCurSeq = defaultArg retirementCursor (openCurSeq + 10L)
            let nextOpenCurSeq = retCurSeq + 1L

            let nextIncumbencyId =
                "inc-"
                + (HostDigest.sha256Hex (
                    sprintf "handoff:%s:%s:%d" incumbencyId (PlanStage.render nextStage) nextOpenCurSeq
                )).[0..15]

            let retEvent =
                PlanEvents.incumbencyRetired incumbencyId PlanRetirementOutcome.Continue retCurSeq

            let openEvent =
                PlanEvents.incumbencyOpened workId nextIncumbencyId nextStage nextOpenCurSeq

            match! appendRetireAndOpen store pid retEvent openEvent with
            | Error err -> return Error err
            | Ok() ->
                return
                    Ok
                        { Ok = true
                          RetiredIncumbencyId = incumbencyId
                          NextIncumbencyId = nextIncumbencyId
                          NextStage = PlanStage.render nextStage
                          Message =
                            sprintf
                                "Handed off from stage %s to %s"
                                (PlanStage.render active.Stage)
                                (PlanStage.render nextStage) }
        }

    let private resolveNextStage (stage: PlanStage) : Result<PlanStage, string> =
        match PlanStage.next stage with
        | None -> Error "Stage S3 cannot hand off (no successor stage; S3 must deliver)"
        | Some nextStage -> Ok nextStage

    let private checkHandoffEligibility
        (active: ActivePlanIncumbency)
        (isDelivered: bool)
        (hasBlocker: bool)
        : Result<PlanStage, string> =
        ActionGate.decideAction active.Stage "handoff" isDelivered hasBlocker
        |> Result.mapError ActionGateRejection.render
        |> Result.bind (fun () -> resolveNextStage active.Stage)

    let executeHandoff
        (store: IEventStore)
        (workId: string)
        (incumbencyId: string)
        (hasBlocker: bool)
        (note: string option)
        (retirementCursor: int64 option)
        : Task<Result<HandoffResult, string>> =
        task {
            let pid = PlanWorkId.create workId

            let state =
                PlanEventStore.workState store pid
                |> Option.defaultValue PlanFold.initialWorkState

            let planResult =
                validateActiveIncumbency state.Active incumbencyId
                |> Result.bind (fun active ->
                    checkHandoffEligibility active state.Delivered.IsSome hasBlocker
                    |> Result.map (fun nextStage -> (active, nextStage)))

            match planResult with
            | Error err -> return Error err
            | Ok(active, nextStage) ->
                return! performHandoffEvents store pid workId incumbencyId active nextStage retirementCursor
        }

    let private resolvePathAndDigest
        (content: string)
        (root: string)
        (workId: string)
        : Result<string * string, string> =
        match PlanPath.resolvePlanPath root workId with
        | Error err -> Error(sprintf "Cannot resolve plan path: %s" err)
        | Ok planPath ->
            let digest = HostDigest.sha256Hex content
            Ok(digest, planPath)

    let private readAndValidatePlan (root: string) (workId: string) : Result<string * string, string> =
        match PlanPath.readPlan root workId with
        | Error err -> Error(sprintf "Cannot deliver: %s" err)
        | Ok content when String.IsNullOrWhiteSpace content -> Error "Cannot deliver: plan artifact is empty"
        | Ok content -> resolvePathAndDigest content root workId

    let private appendDeliverOnly
        (store: IEventStore)
        (pid: PlanWorkId)
        (delEvent: PlanEvent)
        : Task<Result<unit, string>> =
        task {
            match! PlanEventStore.append store pid delEvent with
            | Error err -> return Error(sprintf "Failed to record plan delivery: %s" err)
            | Ok _ -> return Ok()
        }

    let private appendRetireAndDeliver
        (store: IEventStore)
        (pid: PlanWorkId)
        (retEvent: PlanEvent)
        (delEvent: PlanEvent)
        : Task<Result<unit, string>> =
        task {
            match! PlanEventStore.append store pid retEvent with
            | Error err -> return Error(sprintf "Failed to retire incumbency during delivery: %s" err)
            | Ok _ -> return! appendDeliverOnly store pid delEvent
        }

    let private performDeliverEvents
        (store: IEventStore)
        (pid: PlanWorkId)
        (workId: string)
        (incumbencyId: string)
        (active: ActivePlanIncumbency)
        (digest: string)
        (planPath: string)
        (retirementCursor: int64 option)
        : Task<Result<DeliverResult, string>> =
        task {
            let openCurSeq = XTraceCursor.sequence active.OpeningCursor
            let retCurSeq = defaultArg retirementCursor (openCurSeq + 10L)

            let retEvent =
                PlanEvents.incumbencyRetired incumbencyId PlanRetirementOutcome.Delivered retCurSeq

            let delEvent = PlanEvents.delivered incumbencyId workId digest planPath

            match! appendRetireAndDeliver store pid retEvent delEvent with
            | Error err -> return Error err
            | Ok() ->
                return
                    Ok
                        { Ok = true
                          Digest = digest
                          Path = planPath
                          Message = "Plan delivered successfully" }
        }

    let private checkDeliverEligibility
        (active: ActivePlanIncumbency)
        (isDelivered: bool)
        (hasBlocker: bool)
        : Result<unit, string> =
        ActionGate.decideAction active.Stage "deliver" isDelivered hasBlocker
        |> Result.mapError ActionGateRejection.render

    let executeDeliver
        (store: IEventStore)
        (root: string)
        (workId: string)
        (incumbencyId: string)
        (hasBlocker: bool)
        (note: string option)
        (retirementCursor: int64 option)
        : Task<Result<DeliverResult, string>> =
        task {
            let pid = PlanWorkId.create workId

            let state =
                PlanEventStore.workState store pid
                |> Option.defaultValue PlanFold.initialWorkState

            let planResult =
                validateDeliverIncumbency state.Active incumbencyId
                |> Result.bind (fun active ->
                    checkDeliverEligibility active state.Delivered.IsSome hasBlocker
                    |> Result.bind (fun () -> readAndValidatePlan root workId)
                    |> Result.map (fun (digest, planPath) -> (active, digest, planPath)))

            match planResult with
            | Error err -> return Error err
            | Ok(active, digest, planPath) ->
                return! performDeliverEvents store pid workId incumbencyId active digest planPath retirementCursor
        }

    let private checkPendingAsk
        (pendingAskOpt: (PlanIncumbencyId * string * XTraceCursor) option)
        (question: string)
        : Result<unit, string> =
        match pendingAskOpt with
        | Some(_, pQ, _) when pQ = question -> Ok()
        | Some _ -> Error "An ask is already pending for this incumbency; only one question can be pending at a time"
        | None -> Ok()

    let executeAsk (store: IEventStore) (workId: string) (question: string) : Result<AskResult, string> =
        let pid = PlanWorkId.create workId

        let state =
            PlanEventStore.workState store pid
            |> Option.defaultValue PlanFold.initialWorkState

        if state.Delivered.IsSome then
            Error "The planning artifact has already been delivered. No further questions are permitted."
        elif String.IsNullOrWhiteSpace question then
            Error "Question cannot be empty"
        else
            checkPendingAsk state.PendingAsk question
            |> Result.map (fun () ->
                { Kind = "waiting_for_user"
                  Question = question
                  Status = "pending" })

    let private ensureDevOpsBound
        (store: IEventStore)
        (pid: PlanWorkId)
        (workId: string)
        (target: string)
        (isBound: bool)
        : Task<unit> =
        task {
            if not isBound then
                let boundEvent = PlanEvents.devOpsBound workId target None
                let! _ = PlanEventStore.append store pid boundEvent
                ()
        }

    let executeResume
        (store: IEventStore)
        (workId: string)
        (charge: string)
        (name: string option)
        : Task<Result<ResumeResult, string>> =
        task {
            let pid = PlanWorkId.create workId

            let state =
                PlanEventStore.workState store pid
                |> Option.defaultValue PlanFold.initialWorkState

            if state.Delivered.IsSome then
                return
                    Error
                        "The planning artifact has already been delivered. No further DevOps invocations are permitted."
            elif String.IsNullOrWhiteSpace charge then
                return Error "Charge cannot be empty"
            else
                let target = defaultArg name "devops"
                do! ensureDevOpsBound store pid workId target state.BoundDevOps.IsSome

                return
                    Ok
                        { Status = "dispatched"
                          Target = target
                          Charge = charge }
        }

    let executeJsPlan
        (root: string)
        (workId: string)
        (action: string)
        (content: string option)
        (patches: (string * string) list option)
        : Result<string, string> =
        match action.ToLowerInvariant() with
        | "read"
        | "js-plan.read" -> PlanPath.readPlan root workId
        | "rewrite"
        | "js-plan.rewrite" ->
            content
            |> requireOpt "Missing content for rewrite"
            |> Result.bind (PlanPath.rewritePlanAtomic root workId)
            |> Result.map (fun (path, digest) -> sprintf "Plan rewritten at %s (digest: %s)" path digest)
        | "edit"
        | "js-plan.edit" ->
            patches
            |> requireOpt "Missing patches for edit"
            |> Result.bind (PlanPath.editPlanAtomic root workId)
            |> Result.map (fun (path, digest) -> sprintf "Plan edited at %s (digest: %s)" path digest)
        | _ -> Error(sprintf "Unknown js-plan action: %s" action)
