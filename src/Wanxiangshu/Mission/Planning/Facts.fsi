namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Context.Trace

type PlanWorkId = private PlanWorkId of string

module PlanWorkId =
    val create: string -> PlanWorkId
    val value: PlanWorkId -> string

type PlanIncumbencyId = private PlanIncumbencyId of string

module PlanIncumbencyId =
    val create: string -> PlanIncumbencyId
    val value: PlanIncumbencyId -> string

[<RequireQualifiedAccess>]
type PlanStage =
    | S1
    | S2
    | S3

module PlanStage =
    val ordinal: PlanStage -> int
    val next: PlanStage -> PlanStage option
    val ofString: string -> PlanStage option
    val render: PlanStage -> string

[<RequireQualifiedAccess>]
type PlanRetirementOutcome =
    | Continue
    | Delivered

module PlanRetirementOutcome =
    val ofString: string -> PlanRetirementOutcome option
    val render: PlanRetirementOutcome -> string

type PlanDeliveryReceipt =
    { IncumbencyId: PlanIncumbencyId
      WorkId: PlanWorkId
      Digest: string
      Path: string }

[<RequireQualifiedAccess>]
type PlanEvent =
    | PlanWorkOpened of workId: PlanWorkId * root: string
    | PlanDevOpsBound of workId: PlanWorkId * devopsId: string * target: string option
    | PlanIncumbencyOpened of
        workId: PlanWorkId *
        incumbencyId: PlanIncumbencyId *
        stage: PlanStage *
        openingCursor: XTraceCursor
    | PlanIncumbencyRetired of
        incumbencyId: PlanIncumbencyId *
        outcome: PlanRetirementOutcome *
        retirementCursor: XTraceCursor
    | PlanDelivered of incumbencyId: PlanIncumbencyId * workId: PlanWorkId * digest: string * path: string
    | PlanAskPending of workId: PlanWorkId * incumbencyId: PlanIncumbencyId * question: string * cursor: XTraceCursor
    | PlanAskResolved of incumbencyId: PlanIncumbencyId * cursor: XTraceCursor

module PlanEvents =
    val workOpened: string -> string -> PlanEvent
    val devOpsBound: string -> string -> string option -> PlanEvent
    val incumbencyOpened: string -> string -> PlanStage -> int64 -> PlanEvent
    val incumbencyRetired: string -> PlanRetirementOutcome -> int64 -> PlanEvent
    val delivered: string -> string -> string -> string -> PlanEvent
    val askPending: string -> string -> string -> int64 -> PlanEvent
    val askResolved: string -> int64 -> PlanEvent
