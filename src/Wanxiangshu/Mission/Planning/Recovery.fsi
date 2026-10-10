namespace Wanxiangshu.Mission.Planning

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
    val planRecoveryPosition: view: PlanWorkView -> readPlanFile: (string -> string option) -> PlanRecoveryPosition

    val evaluateRecoveryEffects:
        views: PlanWorkView list -> readPlanFile: (string -> string option) -> PlanRecoveryEffect list
