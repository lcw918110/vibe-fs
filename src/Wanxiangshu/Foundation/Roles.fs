namespace Wanxiangshu.Foundation

/// DSL-class: Vocabulary — the fixed set of managed agent roles (one
/// vocabulary, no control-flow reading).
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

    let all: Role list =
        [ Role.Orchestrator
          Role.Manager
          Role.Engineer
          Role.DevOps
          Role.Blogger
          Role.Plan ]

    /// Canonical wire label for a role (lowercase, AGENT-001 vocabulary).
    let roleLabel (role: Role) : string =
        match role with
        | Role.Manager -> "manager"
        | Role.Orchestrator -> "orchestrator"
        | Role.Engineer -> "engineer"
        | Role.DevOps -> "devops"
        | Role.Blogger -> "blogger"
        | Role.Plan -> "plan"
        | Role.Coder -> "coder"
        | Role.Inspector -> "inspector"
        | Role.Browser -> "browser"
        | Role.Inquiry -> "inquiry"
        | Role.Distiller -> "distiller"

    let tryParseRole (value: string) : Role option =
        match value.ToLowerInvariant() with
        | "manager" -> Some Role.Manager
        | "orchestrator" -> Some Role.Orchestrator
        | "engineer" -> Some Role.Engineer
        | "devops" -> Some Role.DevOps
        | "blogger" -> Some Role.Blogger
        | "plan" -> Some Role.Plan
        | "coder" -> Some Role.Coder
        | "inspector" -> Some Role.Inspector
        | "browser" -> Some Role.Browser
        | "inquiry" -> Some Role.Inquiry
        | "distiller" -> Some Role.Distiller
        | _ -> None

    let tryParseHistoricalRole (value: string) : Role option = tryParseRole value

    /// AGENT-008 / capability-enforcement-006: Distiller and Blogger are private runtimes, not
    /// public fork / horizon vocabulary.
    let isInternal (role: Role) : bool =
        match role with
        | Role.Blogger
        | Role.Distiller -> true
        | _ -> false
