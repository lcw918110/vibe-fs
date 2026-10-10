namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Context.Trace

[<RequireQualifiedAccess>]
type PlanEndingIntent =
    | Handoff
    | Deliver

module PlanEndingIntent =
    val ofString: string -> PlanEndingIntent option
    val render: PlanEndingIntent -> string

[<RequireQualifiedAccess>]
type PlanPhase =
    | WorkOwned
    | EndingDeclared of intent: PlanEndingIntent
    | CleanupBlocked of blocker: string option

module PlanPhase =
    val render: PlanPhase -> string

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
    val empty: PlanWorkState

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
    val initialWorkState: PlanWorkState
    val initialState: PlanState
    val viewOf: PlanWorkState -> PlanWorkView
    val applyWorkEvent: PlanEvent -> PlanWorkState -> Result<PlanWorkState, string>
    val applyEvent: PlanEvent -> PlanState -> Result<PlanState, string>
    val applyEvents: PlanEvent list -> PlanState -> Result<PlanState, string>
    val applyWorkEventJs: PlanEvent -> PlanWorkState -> obj
    val applyEventJs: PlanEvent -> PlanState -> obj
    val applyEventsJs: PlanEvent list -> PlanState -> obj
    val workState: string -> PlanState -> PlanWorkState option
    val view: string -> PlanState -> PlanWorkView option
