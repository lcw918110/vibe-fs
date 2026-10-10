namespace Wanxiangshu.Mission.Planning

open System

[<RequireQualifiedAccess>]
type PlanRecoveryPosition =
    | Delivered of receipt: PlanDeliveryReceipt * path: string * digest: string
    | Active of incumbencyId: string * stage: string * planExists: bool
    | Nothing
    | Conflict of reason: string

[<RequireQualifiedAccess>]
type PlanRecoveryEffect =
    | DeliveredIdempotent of workId: string * path: string
    | ActiveRebound of workId: string * incumbencyId: string * stage: string * planExists: bool
    | Conflict of workId: string * reason: string

module PlanRecovery =

    let private resolveDeliveredPosition (view: PlanWorkView) =
        match view.DeliveryDigest, view.DeliveryPath with
        | Some digest, Some path ->
            let receipt: PlanDeliveryReceipt =
                { IncumbencyId = PlanIncumbencyId.create (defaultArg view.ActiveIncumbencyId "")
                  WorkId = PlanWorkId.create (defaultArg view.WorkId "")
                  Digest = digest
                  Path = path }

            PlanRecoveryPosition.Delivered(receipt, path, digest)
        | _ -> PlanRecoveryPosition.Conflict "Delivered view missing delivery digest or path"

    let private defaultPathForWorkId (workIdOpt: string option) =
        match workIdOpt with
        | Some wid -> "plan/" + wid + "/plan.md"
        | None -> ""

    let private resolveActivePath (view: PlanWorkView) =
        match view.DeliveryPath with
        | Some p -> p
        | None -> defaultPathForWorkId view.WorkId

    let private resolveActivePosition
        (view: PlanWorkView)
        (readPlanFile: string -> string option)
        (incumbencyId: string)
        (stage: string)
        =
        let targetPath = resolveActivePath view

        let planContentOpt =
            if String.IsNullOrWhiteSpace targetPath then
                None
            else
                readPlanFile targetPath

        let planExists = planContentOpt.IsSome
        PlanRecoveryPosition.Active(incumbencyId, stage, planExists)

    let private resolveActiveOrConflict (view: PlanWorkView) (readPlanFile: string -> string option) =
        match view.ActiveIncumbencyId, view.ActiveStage with
        | Some incumbencyId, Some stage -> resolveActivePosition view readPlanFile incumbencyId stage
        | _ -> PlanRecoveryPosition.Conflict "Active incumbency missing stage in projection"

    let planRecoveryPosition (view: PlanWorkView) (readPlanFile: string -> string option) : PlanRecoveryPosition =
        if view.Delivered && view.ActiveIncumbencyId.IsSome then
            PlanRecoveryPosition.Conflict "Conflict: Plan work view is both Delivered and has an Active incumbency"
        elif view.Delivered then
            resolveDeliveredPosition view
        elif view.ActiveIncumbencyId.IsSome then
            resolveActiveOrConflict view readPlanFile
        else
            PlanRecoveryPosition.Nothing

    let private effectFromPosition (wid: string) (pos: PlanRecoveryPosition) : PlanRecoveryEffect option =
        match pos with
        | PlanRecoveryPosition.Delivered(_receipt, path, _digest) ->
            Some(PlanRecoveryEffect.DeliveredIdempotent(wid, path))
        | PlanRecoveryPosition.Active(incumbencyId, stage, planExists) ->
            Some(PlanRecoveryEffect.ActiveRebound(wid, incumbencyId, stage, planExists))
        | PlanRecoveryPosition.Conflict reason -> Some(PlanRecoveryEffect.Conflict(wid, reason))
        | PlanRecoveryPosition.Nothing -> None

    let private evaluateSingleEffect
        (readPlanFile: string -> string option)
        (view: PlanWorkView)
        : PlanRecoveryEffect option =
        match view.WorkId with
        | None -> Some(PlanRecoveryEffect.Conflict("", "视图缺少 WorkId"))
        | Some wid -> effectFromPosition wid (planRecoveryPosition view readPlanFile)

    let evaluateRecoveryEffects
        (views: PlanWorkView list)
        (readPlanFile: string -> string option)
        : PlanRecoveryEffect list =
        views |> List.choose (evaluateSingleEffect readPlanFile)
