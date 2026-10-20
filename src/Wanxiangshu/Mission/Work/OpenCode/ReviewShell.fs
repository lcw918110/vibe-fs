namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.Review
open Wanxiangshu.Mission.Planning
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider

/// work-038: the shared `review` shell. Owns the one ToolSpec
/// (name, description, findings schema, admission) and dispatches by caller
/// role: Plan goes to the plan backend (ReviewPlanBackend), every other
/// admitted caller goes to the Manager backend (ReviewBackendManager). The
/// shell renders the plan backend's result; the Manager backend keeps its own
/// result rendering so its byte behavior is unchanged.
module ReviewShell =

    [<RequireQualifiedAccess>]
    module private Path =
        [<Literal>]
        let Description = "tool/review/description"

        [<Literal>]
        let FindingsArgument = "tool/review/findings-argument"

        [<Literal>]
        let PlanPromoted = "tool/review/plan-promoted"

        [<Literal>]
        let PlanStayed = "tool/review/plan-stayed"

        [<Literal>]
        let PlanS2 = "planning/s2"

        [<Literal>]
        let PlanS3 = "planning/s3"

    let private providerText (path: string) substitutions =
        ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) path substitutions

    let private promotedStageInstructions (target: string) =
        match target with
        | "S2" -> [ providerText Path.PlanS2 Map.empty ]
        | "S3" -> [ providerText Path.PlanS3 Map.empty ]
        | _ -> []

    let admission =
        ToolAdmission.OfficeRole(fun _ role ->
            OfficeCapability.isAllowed role ToolPermission.ReviewAssessment
            || OfficeCapability.isAllowed role ToolPermission.Review)

    let private planRejected path =
        ToolHostCodec.tomlObjectWithInstructions
            [ providerText path Map.empty ]
            [ "recorded", ToolHostCodec.TBool false ]

    let private rejectionPath (error: PlanReviewError) =
        match error with
        | PlanReviewError.AlreadyReviewed _ -> ReviewBackendManager.Path.AlreadySubmitted
        | PlanReviewError.NoActiveIncumbency
        | PlanReviewError.IncumbencyMismatch _ -> ReviewBackendManager.Path.BindingUnavailable
        | PlanReviewError.RecordFailed _ -> ReviewBackendManager.Path.RecordFailed

    let private gapLine gap =
        let (acceptance, workPlan) = gap
        sprintf "- acceptance_criteria: %s | work_plan: %s" acceptance workPlan

    let private planAccepted (review: PlanReviewResult) =
        // work-038: the gaps return to the model as repair obligations,
        // echoed verbatim below the outcome instruction; a promotion to a new
        // stage returns that stage's method in the same tool response so the
        // current tenure can begin its stretch immediately.
        let instructions =
            if review.Outcome = PlanReviewOutcome.Promoted then
                [ providerText Path.PlanPromoted Map.empty ]
                @ promotedStageInstructions review.Target
            else
                [ providerText Path.PlanStayed Map.empty ] @ (review.Gaps |> List.map gapLine)

        ToolHostCodec.tomlObjectWithInstructions instructions [ "recorded", ToolHostCodec.TBool true ]

    let private renderPlanReview (result: Result<PlanReviewResult, PlanReviewError>) : string =
        match result with
        | Error error -> planRejected (rejectionPath error)
        | Ok review -> planAccepted review

    // Same findings schema and three-question semantics as the Manager
    // backend (work-038 shared shell).
    let private parseFindings (args: HostToolArguments) : Result<ReviewFindings, string> =
        match Model.tryParse args.Raw with
        | Error _ -> Error ReviewBackendManager.Path.InvalidFindings
        | Ok findings -> Ok findings

    let private activeIncumbencyId (activeId: string option) : Result<string, string> =
        match activeId with
        | Some id -> Ok id
        | None -> Error ReviewBackendManager.Path.BindingUnavailable

    let private resolveActiveIncumbency (gitCommonDir: string) (workId: string) : Result<string, string> =
        Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir workId
        |> Option.bind (fun work -> work.ActiveId)
        |> activeIncumbencyId

    let private gapsOf (findings: ReviewFindings) : (string * string) list =
        ReviewFindings.values findings
        |> List.map (fun finding -> (finding.AcceptanceCriteria, finding.WorkPlan))

    // One preparation chain: findings and the durable active incumbency resolve
    // to the exact review invocation. The old workspace EventStore is not a
    // lifecycle prerequisite.
    let private preparePlanReview
        (gitCommonDir: string)
        (args: HostToolArguments)
        (context: HostToolContext)
        : Result<string * (string * string) list, string> =
        if String.IsNullOrWhiteSpace gitCommonDir then
            Error "git common directory is missing"
        else
            parseFindings args
            |> Result.bind (fun findings ->
                resolveActiveIncumbency gitCommonDir context.SessionId
                |> Result.map (fun incumbencyId -> (incumbencyId, gapsOf findings)))

    let private executePlan (gitCommonDir: string) (args: HostToolArguments) (context: HostToolContext) : Task<string> =
        task {
            // work-038 binding: workId is the caller's session.
            match preparePlanReview gitCommonDir args context with
            | Error path -> return planRejected path
            | Ok(incumbencyId, gaps) ->
                let! result = ReviewPlanBackend.executeReview gitCommonDir context.SessionId incumbencyId gaps

                return renderPlanReview result
        }

    let spec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let findingSchema =
            ToolHostCodec.objectSchemaOf
                [ "acceptance_criteria", ToolHostCodec.stringSchema factory
                  "work_plan", ToolHostCodec.stringSchema factory ]
                factory

        let findingsSchema =
            ToolHostCodec.arraySchemaOf findingSchema (providerText Path.FindingsArgument Map.empty) factory

        let noteArgument = "note", ToolHostCodec.optionalStringSchema factory

        let dispatch args context : Task<string> =
            task {
                let! gitCommonDir =
                    match scope.WorkspaceDirectory with
                    | Some ws -> Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot ws
                    | None -> Task.FromResult ""

                if scope.IsRole(context, Role.Plan) then
                    return! executePlan gitCommonDir args context
                else
                    return! ReviewBackendManager.execute scope args context
            }

        { Name = "review"
          Description = providerText Path.Description Map.empty
          Arguments = [ "findings", findingsSchema; noteArgument ]
          Admission = admission
          Execute = dispatch }
