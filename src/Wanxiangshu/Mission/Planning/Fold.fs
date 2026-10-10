namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Context.Trace

[<RequireQualifiedAccess>]
type PlanEndingIntent =
    | Handoff
    | Deliver

module PlanEndingIntent =
    let ofString (value: string) : PlanEndingIntent option =
        match value with
        | "Handoff"
        | "handoff" -> Some PlanEndingIntent.Handoff
        | "Deliver"
        | "deliver" -> Some PlanEndingIntent.Deliver
        | _ -> None

    let render (intent: PlanEndingIntent) : string =
        match intent with
        | PlanEndingIntent.Handoff -> "Handoff"
        | PlanEndingIntent.Deliver -> "Deliver"

[<RequireQualifiedAccess>]
type PlanPhase =
    | WorkOwned
    | EndingDeclared of intent: PlanEndingIntent
    | CleanupBlocked of blocker: string option

module PlanPhase =
    let render (phase: PlanPhase) : string =
        match phase with
        | PlanPhase.WorkOwned -> "WorkOwned"
        | PlanPhase.EndingDeclared intent -> sprintf "EndingDeclared(%s)" (PlanEndingIntent.render intent)
        | PlanPhase.CleanupBlocked None -> "CleanupBlocked"
        | PlanPhase.CleanupBlocked(Some b) -> sprintf "CleanupBlocked(%s)" b

type ActivePlanIncumbency =
    { Id: PlanIncumbencyId
      Stage: PlanStage
      Phase: PlanPhase
      OpeningCursor: XTraceCursor }

type PlanWorkState =
    { WorkId: PlanWorkId option
      Root: string option
      Active: ActivePlanIncumbency option
      Retired: PlanIncumbencyId list
      LatestRetirement: (PlanIncumbencyId * PlanRetirementOutcome * XTraceCursor * XTraceCursor) option
      BoundDevOps: (string * string option) option
      Delivered: PlanDeliveryReceipt option
      PreviousIncumbencyStage: PlanStage option
      PendingAsk: (PlanIncumbencyId * string * XTraceCursor) option
      LatestResolvedAsk: (PlanIncumbencyId * XTraceCursor) option }

module PlanWorkState =
    let empty: PlanWorkState =
        { WorkId = None
          Root = None
          Active = None
          Retired = []
          LatestRetirement = None
          BoundDevOps = None
          Delivered = None
          PreviousIncumbencyStage = None
          PendingAsk = None
          LatestResolvedAsk = None }

type PlanWorkView =
    { WorkId: string option
      ActiveIncumbencyId: string option
      ActiveStage: string option
      ActivePhase: string option
      RetiredCount: int
      LatestRetirementOutcome: string option
      Delivered: bool
      DeliveryDigest: string option
      DeliveryPath: string option
      BoundDevOpsId: string option
      PendingAskQuestion: string option
      PendingAskIncumbencyId: string option
      PendingAskCursor: int64 option }

type PlanState = private PlanState of Map<string, PlanWorkState>

module PlanFold =
    let initialWorkState: PlanWorkState = PlanWorkState.empty
    let initialState: PlanState = PlanState Map.empty

    let viewOf (state: PlanWorkState) : PlanWorkView =
        { WorkId = state.WorkId |> Option.map PlanWorkId.value
          ActiveIncumbencyId = state.Active |> Option.map (fun a -> PlanIncumbencyId.value a.Id)
          ActiveStage = state.Active |> Option.map (fun a -> PlanStage.render a.Stage)
          ActivePhase = state.Active |> Option.map (fun a -> PlanPhase.render a.Phase)
          RetiredCount = state.Retired.Length
          LatestRetirementOutcome =
            state.LatestRetirement
            |> Option.map (fun (_, o, _, _) -> PlanRetirementOutcome.render o)
          Delivered = state.Delivered.IsSome
          DeliveryDigest = state.Delivered |> Option.map (fun d -> d.Digest)
          DeliveryPath = state.Delivered |> Option.map (fun d -> d.Path)
          BoundDevOpsId = state.BoundDevOps |> Option.map fst
          PendingAskQuestion = state.PendingAsk |> Option.map (fun (_, q, _) -> q)
          PendingAskIncumbencyId = state.PendingAsk |> Option.map (fun (i, _, _) -> PlanIncumbencyId.value i)
          PendingAskCursor = state.PendingAsk |> Option.map (fun (_, _, c) -> XTraceCursor.sequence c) }

    let private applyWorkOpened
        (workId: PlanWorkId)
        (root: string)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.WorkId, state.Root with
        | Some existingId, Some existingRoot when existingId = workId && existingRoot = root -> Ok state
        | Some existingId, _ when existingId <> workId -> Error "Cannot re-open existing work with different work ID"
        | _, Some existingRoot when existingRoot <> root -> Error "Cannot re-open work with conflicting root directory"
        | _ ->
            Ok
                { state with
                    WorkId = Some workId
                    Root = Some root }

    let private applyDevOpsBound
        (workId: PlanWorkId)
        (devopsId: string)
        (target: string option)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.WorkId, state.BoundDevOps with
        | None, _ -> Error "Cannot bind DevOps before work is opened"
        | Some id, _ when id <> workId -> Error "Work ID mismatch on DevOps binding"
        | Some _, Some(existingDevops, existingTarget) when existingDevops = devopsId && existingTarget = target ->
            Ok state
        | Some _, Some _ -> Error "Cannot rebind DevOps with conflicting target or identity"
        | Some _, None ->
            Ok
                { state with
                    BoundDevOps = Some(devopsId, target) }

    let private nextStageValid
        (stage: PlanStage)
        (retired: PlanIncumbencyId list)
        (previousStage: PlanStage option)
        : Result<unit, string> =
        let expectedNext = previousStage |> Option.bind PlanStage.next

        match retired.IsEmpty, stage, previousStage, expectedNext with
        | true, PlanStage.S1, _, _ -> Ok()
        | true, _, _, _ -> Error "Initial incumbency must start at stage S1"
        | false, _, None, _ -> Error "Missing previous incumbency stage tracking"
        | false, target, Some _, Some exp when exp = target -> Ok()
        | false, target, Some prev, _ ->
            Error(
                sprintf
                    "Illegal stage transition: expected next stage after %s, got %s"
                    (PlanStage.render prev)
                    (PlanStage.render target)
            )

    let private applyIncumbencyOpened
        (workId: PlanWorkId)
        (incumbencyId: PlanIncumbencyId)
        (stage: PlanStage)
        (openingCursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        let isAlreadyRetired = List.contains incumbencyId state.Retired

        match state.WorkId, state.Active, isAlreadyRetired with
        | None, _, _ -> Error "Cannot open incumbency before work is opened"
        | Some id, _, _ when id <> workId -> Error "Work ID mismatch on opening incumbency"
        | Some _, Some active, _ when
            active.Id = incumbencyId
            && active.Stage = stage
            && XTraceCursor.sequence active.OpeningCursor = XTraceCursor.sequence openingCursor
            ->
            Ok state
        | Some _, Some _, _ -> Error "An active incumbency is already present for this work"
        | Some _, None, true -> Error "Cannot revive an already retired incumbency"
        | Some _, None, false ->
            nextStageValid stage state.Retired state.PreviousIncumbencyStage
            |> Result.map (fun () ->
                let active =
                    { Id = incumbencyId
                      Stage = stage
                      Phase = PlanPhase.WorkOwned
                      OpeningCursor = openingCursor }

                { state with
                    Active = Some active
                    PreviousIncumbencyStage = Some stage })

    let private applyIncumbencyRetired
        (incumbencyId: PlanIncumbencyId)
        (outcome: PlanRetirementOutcome)
        (retirementCursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        let isRetired = List.contains incumbencyId state.Retired

        match state.Active, isRetired, state.LatestRetirement with
        | None, true, Some(retId, retOutcome, _, retCursor) when
            retId = incumbencyId
            && retOutcome = outcome
            && XTraceCursor.sequence retCursor = XTraceCursor.sequence retirementCursor
            ->
            Ok state
        | None, true, _ -> Error "Conflicting retirement payload for already retired incumbency"
        | None, false, _ -> Error "No active incumbency found to retire"
        | Some active, _, _ when active.Id <> incumbencyId ->
            Error "Active incumbency ID does not match retirement target"
        | Some active, _, _ when XTraceCursor.isAfter active.OpeningCursor retirementCursor ->
            Error "Retirement cursor cannot precede incumbency opening cursor"
        | Some active, _, _ ->
            let retired = active.Id :: state.Retired
            let latest = Some(active.Id, outcome, active.OpeningCursor, retirementCursor)

            Ok
                { state with
                    Active = None
                    Retired = retired
                    LatestRetirement = latest }

    let private validateReceipt
        (incumbencyId: PlanIncumbencyId)
        (workId: PlanWorkId)
        (digest: string)
        (path: string)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match System.String.IsNullOrWhiteSpace digest || System.String.IsNullOrWhiteSpace path with
        | true -> Error "Plan delivery requires non-empty digest and path"
        | false ->
            let receipt =
                { IncumbencyId = incumbencyId
                  WorkId = workId
                  Digest = digest
                  Path = path }

            Ok { state with Delivered = Some receipt }

    let private applyDelivered
        (incumbencyId: PlanIncumbencyId)
        (workId: PlanWorkId)
        (digest: string)
        (path: string)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.WorkId, state.Active.IsSome, state.LatestRetirement, state.Delivered with
        | None, _, _, _ -> Error "Cannot deliver before work is opened"
        | Some id, _, _, _ when id <> workId -> Error "Work ID mismatch on plan delivery"
        | Some _, true, _, _ ->
            Error "Cannot deliver while an active incumbency exists; incumbency must retire with Delivered first"
        | Some _, false, Some(retId, PlanRetirementOutcome.Delivered, _, _), Some receipt when
            retId = incumbencyId
            && receipt.IncumbencyId = incumbencyId
            && receipt.Digest = digest
            && receipt.Path = path
            ->
            Ok state
        | Some _, false, Some(retId, PlanRetirementOutcome.Delivered, _, _), Some _ when retId = incumbencyId ->
            Error "Delivery already recorded with conflicting digest or path"
        | Some _, false, Some(retId, PlanRetirementOutcome.Delivered, _, _), None when retId = incumbencyId ->
            validateReceipt incumbencyId workId digest path state
        | _ -> Error "Delivery requires the incumbency to be retired with Delivered outcome"

    let private resolvePendingAskTransition
        (incumbencyId: PlanIncumbencyId)
        (question: string)
        (cursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.PendingAsk with
        | Some(pInc, pQ, pCur) when
            pInc = incumbencyId
            && pQ = question
            && XTraceCursor.sequence pCur = XTraceCursor.sequence cursor
            ->
            Ok state
        | Some _ -> Error "An ask is already pending for this incumbency; only one question can be pending at a time"
        | None ->
            Ok
                { state with
                    PendingAsk = Some(incumbencyId, question, cursor) }

    let private applyAskPending
        (workId: PlanWorkId)
        (incumbencyId: PlanIncumbencyId)
        (question: string)
        (cursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.WorkId, state.Delivered.IsSome, state.Active with
        | None, _, _ -> Error "Cannot ask before work is opened"
        | Some wid, _, _ when wid <> workId -> Error "Work ID mismatch on ask pending"
        | _, true, _ -> Error "The planning artifact has already been delivered. No further questions are permitted."
        | _, _, None -> Error "No active incumbency found to ask question"
        | _, _, Some active when active.Id <> incumbencyId -> Error "Active incumbency ID does not match ask target"
        | _, _, Some active -> resolvePendingAskTransition incumbencyId question cursor state

    let private resolveLatestAsk
        (incumbencyId: PlanIncumbencyId)
        (cursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.LatestResolvedAsk with
        | Some(resInc, resCur) when
            resInc = incumbencyId
            && XTraceCursor.sequence resCur = XTraceCursor.sequence cursor
            ->
            Ok state
        | Some(resInc, _) when resInc = incumbencyId -> Error "Conflicting ask resolved payload for incumbency"
        | _ -> Error(sprintf "No pending ask found to resolve for incumbency %s" (PlanIncumbencyId.value incumbencyId))

    let private applyAskResolved
        (incumbencyId: PlanIncumbencyId)
        (cursor: XTraceCursor)
        (state: PlanWorkState)
        : Result<PlanWorkState, string> =
        match state.PendingAsk with
        | Some(pInc, _, _) when pInc = incumbencyId ->
            Ok
                { state with
                    PendingAsk = None
                    LatestResolvedAsk = Some(incumbencyId, cursor) }
        | Some(pInc, _, _) ->
            Error(
                sprintf
                    "Pending ask incumbency '%s' does not match resolution target '%s'"
                    (PlanIncumbencyId.value pInc)
                    (PlanIncumbencyId.value incumbencyId)
            )
        | None -> resolveLatestAsk incumbencyId cursor state

    let applyWorkEvent (event: PlanEvent) (state: PlanWorkState) : Result<PlanWorkState, string> =
        match event with
        | PlanEvent.PlanWorkOpened(workId, root) -> applyWorkOpened workId root state
        | PlanEvent.PlanDevOpsBound(workId, devopsId, target) -> applyDevOpsBound workId devopsId target state
        | PlanEvent.PlanIncumbencyOpened(workId, incumbencyId, stage, openingCursor) ->
            applyIncumbencyOpened workId incumbencyId stage openingCursor state
        | PlanEvent.PlanIncumbencyRetired(incumbencyId, outcome, retirementCursor) ->
            applyIncumbencyRetired incumbencyId outcome retirementCursor state
        | PlanEvent.PlanDelivered(incumbencyId, workId, digest, path) ->
            applyDelivered incumbencyId workId digest path state
        | PlanEvent.PlanAskPending(workId, incumbencyId, question, cursor) ->
            applyAskPending workId incumbencyId question cursor state
        | PlanEvent.PlanAskResolved(incumbencyId, cursor) -> applyAskResolved incumbencyId cursor state

    let private workKey (event: PlanEvent) : string =
        match event with
        | PlanEvent.PlanWorkOpened(workId, _) -> PlanWorkId.value workId
        | PlanEvent.PlanDevOpsBound(workId, _, _) -> PlanWorkId.value workId
        | PlanEvent.PlanIncumbencyOpened(workId, _, _, _) -> PlanWorkId.value workId
        | PlanEvent.PlanDelivered(_, workId, _, _) -> PlanWorkId.value workId
        | PlanEvent.PlanAskPending(workId, _, _, _) -> PlanWorkId.value workId
        | PlanEvent.PlanIncumbencyRetired(incumbencyId, _, _) -> PlanIncumbencyId.value incumbencyId
        | PlanEvent.PlanAskResolved(incumbencyId, _) -> PlanIncumbencyId.value incumbencyId

    let private applyRetiredToState
        (incumbencyId: PlanIncumbencyId)
        (event: PlanEvent)
        (works: Map<string, PlanWorkState>)
        : Result<PlanState, string> =
        let found =
            works
            |> Map.tryFindKey (fun _ st ->
                (st.Active |> Option.exists (fun a -> a.Id = incumbencyId))
                || (st.Retired |> List.contains incumbencyId))

        match found with
        | Some k ->
            let st = Map.find k works

            applyWorkEvent event st
            |> Result.map (fun nextSt -> PlanState(Map.add k nextSt works))
        | None -> Error(sprintf "No work state found for retiring incumbency %s" (PlanIncumbencyId.value incumbencyId))

    let private applyKeyedEventToState
        (k: string)
        (event: PlanEvent)
        (works: Map<string, PlanWorkState>)
        : Result<PlanState, string> =
        let current = works |> Map.tryFind k |> Option.defaultValue initialWorkState

        applyWorkEvent event current
        |> Result.map (fun nextSt -> PlanState(Map.add k nextSt works))

    let applyEvent (event: PlanEvent) (PlanState works) : Result<PlanState, string> =
        match event with
        | PlanEvent.PlanIncumbencyRetired(incumbencyId, _, _) -> applyRetiredToState incumbencyId event works
        | PlanEvent.PlanAskResolved(incumbencyId, _) -> applyRetiredToState incumbencyId event works
        | _ -> applyKeyedEventToState (workKey event) event works

    let rec applyEvents (events: PlanEvent list) (state: PlanState) : Result<PlanState, string> =
        match events with
        | [] -> Ok state
        | head :: tail -> applyEvent head state |> Result.bind (applyEvents tail)

    let applyWorkEventJs (event: PlanEvent) (state: PlanWorkState) : obj =
        match applyWorkEvent event state with
        | Ok nextSt ->
            box
                {| ok = true
                   state = nextSt
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   state = state
                   error = err |}

    let applyEventJs (event: PlanEvent) (state: PlanState) : obj =
        match applyEvent event state with
        | Ok nextSt ->
            box
                {| ok = true
                   state = nextSt
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   state = state
                   error = err |}

    let applyEventsJs (events: PlanEvent list) (state: PlanState) : obj =
        match applyEvents events state with
        | Ok nextSt ->
            box
                {| ok = true
                   state = nextSt
                   error = null |}
        | Error err ->
            box
                {| ok = false
                   state = state
                   error = err |}

    let workState (workId: string) (PlanState works) : PlanWorkState option = Map.tryFind workId works

    let view (workId: string) (state: PlanState) : PlanWorkView option =
        workState workId state |> Option.map viewOf
