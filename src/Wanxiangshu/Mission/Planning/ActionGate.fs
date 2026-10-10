namespace Wanxiangshu.Mission.Planning

open Fable.Core.JsInterop
open Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type ActionGateRejection =
    | AlreadyDelivered
    | DeliverForbiddenInS1
    | HandoffForbiddenInS3
    | BlockedByLiveResources of blocker: string option
    | ActionDenied of action: string * reason: string

module ActionGateRejection =
    let render (rejection: ActionGateRejection) : string =
        match rejection with
        | ActionGateRejection.AlreadyDelivered ->
            "The planning artifact has already been delivered. Only js-plan.read is permitted."
        | ActionGateRejection.DeliverForbiddenInS1 ->
            "Direct delivery is forbidden in stage S1. The initial runner must hand off to stage S2."
        | ActionGateRejection.HandoffForbiddenInS3 ->
            "Stage S3 is the final algorithm stage with no successor. Handoff is forbidden; deliver only."
        | ActionGateRejection.BlockedByLiveResources(Some b) -> sprintf "Action blocked by live resources: %s" b
        | ActionGateRejection.BlockedByLiveResources None -> "Action blocked by live resources."
        | ActionGateRejection.ActionDenied(act, reason) -> sprintf "Action '%s' denied: %s" act reason

module ActionGate =

    let allowedPermissions (stage: PlanStage) (hasDelivered: bool) (hasBlocker: bool) : ToolPermission Set =
        match hasDelivered, hasBlocker, stage with
        | true, _, _ -> set [ ToolPermission.JsPlan ]
        | false, true, _ -> set [ ToolPermission.JsPlan; ToolPermission.Ask; ToolPermission.Resume ]
        | false, false, PlanStage.S1 ->
            set
                [ ToolPermission.JsPlan
                  ToolPermission.Ask
                  ToolPermission.Resume
                  ToolPermission.Handoff ]
        | false, false, PlanStage.S2 ->
            set
                [ ToolPermission.JsPlan
                  ToolPermission.Ask
                  ToolPermission.Resume
                  ToolPermission.Handoff
                  ToolPermission.Deliver ]
        | false, false, PlanStage.S3 ->
            set
                [ ToolPermission.JsPlan
                  ToolPermission.Ask
                  ToolPermission.Resume
                  ToolPermission.Deliver ]

    let decideAction
        (stage: PlanStage)
        (action: string)
        (hasDelivered: bool)
        (hasBlocker: bool)
        : Result<unit, ActionGateRejection> =
        let normAction = action.ToLowerInvariant()

        match hasDelivered, hasBlocker, stage, normAction with
        | true, _, _, ("read" | "js-plan.read" | "js-plan") -> Ok()
        | true, _, _, _ -> Error ActionGateRejection.AlreadyDelivered
        | false, true, _, ("handoff" | "deliver") -> Error(ActionGateRejection.BlockedByLiveResources None)
        | false, _, PlanStage.S3, "handoff" -> Error ActionGateRejection.HandoffForbiddenInS3
        | false, _, PlanStage.S1, "deliver" -> Error ActionGateRejection.DeliverForbiddenInS1
        | false, false, (PlanStage.S1 | PlanStage.S2), "handoff" -> Ok()
        | false, false, (PlanStage.S2 | PlanStage.S3), "deliver" -> Ok()
        | false,
          _,
          _,
          ("ask" | "resume" | "read" | "edit" | "rewrite" | "js-plan" | "js-plan.read" | "js-plan.edit" | "js-plan.rewrite") ->
            Ok()
        | false, _, _, _ -> Error(ActionGateRejection.ActionDenied(action, "Unrecognized planning action"))

    let isActionAllowed (request: obj) : bool =
        if isNull request then
            false
        else
            let stageOpt = PlanStage.ofString request?stage

            stageOpt
            |> Option.exists (fun stage ->
                decideAction stage request?action request?hasDelivered request?hasBlocker
                |> Result.isOk)
