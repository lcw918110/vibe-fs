namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type ActionGateRejection =
    | AlreadyDelivered
    | DeliverForbiddenInS1
    | HandoffForbiddenInS3
    | BlockedByLiveResources of blocker: string option
    | ActionDenied of action: string * reason: string

module ActionGateRejection =
    val render: ActionGateRejection -> string

module ActionGate =
    val allowedPermissions: stage: PlanStage -> hasDelivered: bool -> hasBlocker: bool -> ToolPermission Set

    val decideAction:
        stage: PlanStage ->
        action: string ->
        hasDelivered: bool ->
        hasBlocker: bool ->
            Result<unit, ActionGateRejection>

    val isActionAllowed: request: obj -> bool
