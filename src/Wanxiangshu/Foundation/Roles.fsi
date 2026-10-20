namespace Wanxiangshu.Foundation

[<RequireQualifiedAccess>]
type Role =
    | Manager
    | Orchestrator
    | Engineer
    | Coder
    | Inspector
    | Browser
    | Inquiry
    | DevOps
    | Distiller
    | Plan
    /// On-demand second opinion: a dedicated reviewer participant that reads a
    /// view of the caller's context and answers in prose, holding no tools.
    | Advisor

module Roles =
    val all: Role list
    val roleLabel: Role -> string
    val tryParseRole: string -> Role option
    val tryParseHistoricalRole: string -> Role option
    val isInternal: Role -> bool
