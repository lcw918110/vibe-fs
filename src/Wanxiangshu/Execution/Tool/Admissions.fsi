namespace Wanxiangshu.Execution.Tool

open Wanxiangshu.Host.Ports

/// The declared authority of every product tool, as product policy. Host
/// adapters attach these to the tools they register; they never decide one
/// themselves.
module ToolAdmissions =
    val join: ToolAdmission
    val horizon: ToolAdmission
    val fission: ToolAdmission
    val review: ToolAdmission
    val deliver: ToolAdmission
    val bashHoneypot: ToolAdmission
    val assume: ToolAdmission
    val fetch: ToolAdmission
    val scopeProtocol: ToolAdmission
    val resetScope: ToolAdmission
    val negotiate: ToolAdmission
    val askPreviousScope: ToolAdmission

    /// fork is the manager's own delegation tool; resume also answers the plan
    /// role, and commission is the orchestrator's hand-off.
    val fork: ToolAdmission
    val resume: ToolAdmission
    val commission: ToolAdmission

    /// A role's generated programming tool (`todowrite-for-<role>`): the role's
    /// office permissions must project a non-empty programming surface.
    val programmingFor: roleName: string -> ToolAdmission
