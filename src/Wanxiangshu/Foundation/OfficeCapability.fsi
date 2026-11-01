namespace Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type ToolPermission =
    | Fork
    | Resume
    | Join
    | Horizon
    | Fission
    | Read
    | Write
    | Edit
    | Glob
    | Grep
    | Move
    | Remove
    | Exec
    | Pty
    | ReviewAssessment
    | Fetch
    | Finality
    | BashHoneypot
    | JsPlan
    | Ask
    | Deliver
    /// work-021: Plan's review hard gate. Shares the `review` tool name
    /// with Manager's ReviewAssessment; the label stays distinct.
    | Review
    | DevOpsConsultation
    | ResetScope

[<RequireQualifiedAccess>]
type ManagerCapabilityFacts =
    {
        HasActiveIncumbency: bool
        HasAssessment: bool
        /// True when the active incumbency's accepted assessment has empty
        /// findings: the final incumbent who settles and retires with no successor.
        IsFinalIncumbent: bool
        CleanupBlockerDigest: string option
    }

[<RequireQualifiedAccess>]
module OfficeCapability =
    val permissions: role: Role -> ToolPermission Set
    val isAllowed: role: Role -> permission: ToolPermission -> bool
    val isAllowedForManagerFacts: facts: ManagerCapabilityFacts -> permission: ToolPermission -> bool
    /// Stable JS-native label for one permission. Single definition point for every
    /// surface that carries permissions across the JS edge.
    val permissionLabel: permission: ToolPermission -> string
    val permissionOfLabel: label: string -> ToolPermission option
    /// capability-enforcement-012: permission → host provider tool names (may be empty:
    /// the duty lives on the programming surface only). Answered by wanxiangli/capability.
    val toolNames: permission: ToolPermission -> string list
    /// The sole reverse lookup tool name → permission for one role; a verb shared by two
    /// permissions resolves to the one this role owns.
    val permissionOfToolName: role: Role -> name: string -> ToolPermission option
    /// Visible equals callable: the role owns the permission this exact name maps to.
    val admitsToolForRole: role: Role -> name: string -> bool
    /// Host utility tool names (`skill`, `assume`): not office permissions. Answered by
    /// wanxiangli/capability.
    val utilityToolNames: string list
    /// Utility admission for one role: schema projection, request tool map and execute
    /// admission all read this. Roles outside the office matrix fail closed.
    val admitsUtility: role: Role -> name: string -> bool
