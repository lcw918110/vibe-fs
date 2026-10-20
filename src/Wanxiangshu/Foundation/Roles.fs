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
    | Plan
    /// On-demand second opinion: a dedicated reviewer participant that reads a
    /// view of the caller's context and answers in prose, holding no tools.
    | Advisor

module Roles =

    let all: Role list =
        [ Role.Orchestrator
          Role.Manager
          Role.Engineer
          Role.DevOps
          Role.Plan
          Role.Advisor ]

    /// Canonical wire label for a role (lowercase, AGENT-001 vocabulary).
    let roleLabel (role: Role) : string =
        match role with
        | Role.Manager -> "manager"
        | Role.Orchestrator -> "orchestrator"
        | Role.Engineer -> "engineer"
        | Role.DevOps -> "devops"
        | Role.Plan -> "plan"
        | Role.Advisor -> "advisor"
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
        | "plan" -> Some Role.Plan
        | "advisor" -> Some Role.Advisor
        | "coder" -> Some Role.Coder
        | "inspector" -> Some Role.Inspector
        | "browser" -> Some Role.Browser
        | "inquiry" -> Some Role.Inquiry
        | "distiller" -> Some Role.Distiller
        | _ -> None

    let tryParseHistoricalRole (value: string) : Role option = tryParseRole value

    /// AGENT-008 / capability-enforcement-006: Distiller and Advisor are private runtimes, not
    /// public fork / horizon vocabulary.
    let isInternal (role: Role) : bool =
        match role with
        | Role.Advisor
        | Role.Distiller -> true
        | _ -> false
