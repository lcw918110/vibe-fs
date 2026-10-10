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
    | Chronicle
    | Fetch
    | Finality
    | BashHoneypot
    | JsPlan
    | Ask
    | Handoff
    | Deliver

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
    val managerReviewReadOnlyPermissions: ToolPermission Set
    /// capability-enforcement-025: read/cleanup/close-out surface for the final incumbent.
    val managerFinishPermissions: ToolPermission Set
    val permissions: role: Role -> ToolPermission Set
    val isAllowed: role: Role -> permission: ToolPermission -> bool
    val permissionsForManagerFacts: facts: ManagerCapabilityFacts -> ToolPermission Set
    val isAllowedForManagerFacts: facts: ManagerCapabilityFacts -> permission: ToolPermission -> bool
    /// Stable JS-native label for one permission. Single definition point for every
    /// surface that carries permissions across the JS edge.
    val permissionLabel: permission: ToolPermission -> string
    val permissionOfLabel: label: string -> ToolPermission option
