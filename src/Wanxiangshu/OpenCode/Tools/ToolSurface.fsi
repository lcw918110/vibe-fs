namespace Wanxiangshu.OpenCode

/// JS-native static contracts for capability-owned tools. Dynamic Host schemas
/// remain exercised through the real plugin; this surface exposes only the
/// owner-defined identity and catalog facts needed by semantic unit laws.
module ToolSurface =

    val toolSpecNames: unit -> string array
    val bashHoneypotContract: unit -> obj

    /// capability-enforcement-009/025: the review-only tool catalog and the
    /// semantic permissions one tool name maps to, as stable labels.
    val reviewToolNames: unit -> string array
    val isReviewTool: toolName: string -> bool
    val reviewToolPermissions: toolName: string -> string array

    /// capability-enforcement-012: the host permission rules a canonical role
    /// projects. An unknown role projects no tool at all.
    val rolePermissionRules: roleLabel: string -> obj
