namespace Wanxiangshu.Execution.Tool

open Wanxiangshu.Foundation
open Wanxiangshu.Host.Ports
open Wanxiangshu.Repository.Programming.Js

/// The declared authority of every product tool, as product policy. Host
/// adapters attach these to the tools they register; they never decide one
/// themselves.
///
/// The role that reaches a predicate is the neutral `AgentRole` the product
/// projected from the established authority profile. The predicate resolves it
/// into the product's own role vocabulary and asks the office capability
/// table; a label the product does not know resolves to no role and therefore
/// to no permission, so it admits nothing.
module ToolAdmissions =

    /// Resolve the neutral role into the product role vocabulary. Only the
    /// product knows which labels exist; an unknown label has no role.
    let private roleOf (role: AgentRole) : Role option =
        match role with
        | AgentRole label -> Roles.tryParseRole label

    let private officeRole (predicate: Role -> bool) : ToolAdmission =
        ToolAdmission.OfficeRole(fun role ->
            match roleOf role with
            | Some productRole -> predicate productRole
            | None -> false)

    let join: ToolAdmission =
        officeRole (fun r -> OfficeCapability.isAllowed r ToolPermission.Join)

    let horizon: ToolAdmission =
        officeRole (fun r -> OfficeCapability.isAllowed r ToolPermission.Horizon)

    let fission: ToolAdmission =
        officeRole (fun r -> OfficeCapability.isAllowed r ToolPermission.Fission)

    let review: ToolAdmission =
        officeRole (fun role ->
            OfficeCapability.isAllowed role ToolPermission.ReviewAssessment
            || OfficeCapability.isAllowed role ToolPermission.Review)

    let deliver: ToolAdmission =
        officeRole (fun role ->
            OfficeCapability.isAllowed role ToolPermission.Finality
            || OfficeCapability.isAllowed role ToolPermission.Deliver)

    let bashHoneypot: ToolAdmission =
        officeRole (fun r -> OfficeCapability.isAllowed r ToolPermission.BashHoneypot)

    let assume: ToolAdmission =
        officeRole (fun role -> OfficeCapability.admitsUtility role "assume")

    /// resetScope is the context-protocol tool: every role that holds the
    /// ResetScope office capability may archive its own scope.
    let resetScope: ToolAdmission =
        officeRole (fun r -> r <> Role.Advisor && OfficeCapability.isAllowed r ToolPermission.ResetScope)

    /// Non-authorizing protocol: only bound delegates can submit questions.
    let negotiate: ToolAdmission = officeRole (fun role -> role <> Role.Advisor)

    let askPreviousScope: ToolAdmission = resetScope

    let fetch: ToolAdmission =
        officeRole (fun r -> OfficeCapability.isAllowed r ToolPermission.Fetch)

    /// fork is the manager's own delegation tool; resume also answers the plan
    /// role, and commission is the orchestrator's hand-off.
    let fork: ToolAdmission = officeRole (fun r -> r = Role.Manager)

    let resume: ToolAdmission = officeRole (fun r -> r = Role.Manager || r = Role.Plan)

    let commission: ToolAdmission = officeRole (fun r -> r = Role.Orchestrator)

    let programmingFor (roleName: string) : ToolAdmission =
        // Same predicate as StaticTools.jsPermission and JsToolGenerator: the
        // role's office permissions must project a non-empty programming surface
        // (JsPlan expands inside ofToolCapabilities). No second permission list.
        officeRole (fun r ->
            (string r).ToLowerInvariant() = roleName.ToLowerInvariant()
            && JsCapability.admitsProgrammingSurface (OfficeCapability.permissions r))
