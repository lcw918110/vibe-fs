namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Context.Trace

type PlanWorkId = private PlanWorkId of string

module PlanWorkId =
    let create value = PlanWorkId value
    let value (PlanWorkId value) = value

type PlanIncumbencyId = private PlanIncumbencyId of string

module PlanIncumbencyId =
    let create value = PlanIncumbencyId value
    let value (PlanIncumbencyId value) = value

[<RequireQualifiedAccess>]
type PlanStage =
    | S1
    | S2
    | S3

module PlanStage =
    let ordinal (stage: PlanStage) : int =
        match stage with
        | PlanStage.S1 -> 1
        | PlanStage.S2 -> 2
        | PlanStage.S3 -> 3

    let next (stage: PlanStage) : PlanStage option =
        match stage with
        | PlanStage.S1 -> Some PlanStage.S2
        | PlanStage.S2 -> Some PlanStage.S3
        | PlanStage.S3 -> None

    let ofString (value: string) : PlanStage option =
        match value with
        | "S1"
        | "s1"
        | "1" -> Some PlanStage.S1
        | "S2"
        | "s2"
        | "2" -> Some PlanStage.S2
        | "S3"
        | "s3"
        | "3" -> Some PlanStage.S3
        | _ -> None

    let render (stage: PlanStage) : string =
        match stage with
        | PlanStage.S1 -> "S1"
        | PlanStage.S2 -> "S2"
        | PlanStage.S3 -> "S3"

[<RequireQualifiedAccess>]
type PlanRetirementOutcome =
    | Continue
    | Delivered

module PlanRetirementOutcome =
    let ofString (value: string) : PlanRetirementOutcome option =
        match value with
        | "Continue"
        | "continue" -> Some PlanRetirementOutcome.Continue
        | "Delivered"
        | "delivered" -> Some PlanRetirementOutcome.Delivered
        | _ -> None

    let render (outcome: PlanRetirementOutcome) : string =
        match outcome with
        | PlanRetirementOutcome.Continue -> "Continue"
        | PlanRetirementOutcome.Delivered -> "Delivered"

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
    let workOpened (workId: string) (root: string) : PlanEvent =
        PlanEvent.PlanWorkOpened(PlanWorkId.create workId, root)

    let devOpsBound (workId: string) (devopsId: string) (target: string option) : PlanEvent =
        PlanEvent.PlanDevOpsBound(PlanWorkId.create workId, devopsId, target)

    let incumbencyOpened (workId: string) (incumbencyId: string) (stage: PlanStage) (openingCursor: int64) : PlanEvent =
        PlanEvent.PlanIncumbencyOpened(
            PlanWorkId.create workId,
            PlanIncumbencyId.create incumbencyId,
            stage,
            XTraceCursor.create openingCursor
        )

    let incumbencyRetired
        (incumbencyId: string)
        (outcome: PlanRetirementOutcome)
        (retirementCursor: int64)
        : PlanEvent =
        PlanEvent.PlanIncumbencyRetired(
            PlanIncumbencyId.create incumbencyId,
            outcome,
            XTraceCursor.create retirementCursor
        )

    let delivered (incumbencyId: string) (workId: string) (digest: string) (path: string) : PlanEvent =
        PlanEvent.PlanDelivered(PlanIncumbencyId.create incumbencyId, PlanWorkId.create workId, digest, path)

    let askPending (workId: string) (incumbencyId: string) (question: string) (cursor: int64) : PlanEvent =
        PlanEvent.PlanAskPending(
            PlanWorkId.create workId,
            PlanIncumbencyId.create incumbencyId,
            question,
            XTraceCursor.create cursor
        )

    let askResolved (incumbencyId: string) (cursor: int64) : PlanEvent =
        PlanEvent.PlanAskResolved(PlanIncumbencyId.create incumbencyId, XTraceCursor.create cursor)
