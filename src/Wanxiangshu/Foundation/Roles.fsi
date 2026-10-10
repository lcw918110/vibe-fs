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
    | Blogger
    | Plan

module Roles =
    val all: Role list
    val roleLabel: Role -> string
    val tryParseRole: string -> Role option
    val tryParseHistoricalRole: string -> Role option
    val isInternal: Role -> bool
