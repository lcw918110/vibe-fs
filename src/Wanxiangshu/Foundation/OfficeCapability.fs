namespace Wanxiangshu.Foundation

/// DSL-class: Vocabulary — the fixed tool-permission catalog keyed by Role.
[<RequireQualifiedAccess>]
type ToolPermission =
    | Fork
    | Resume
    | Join
    | Horizon
    /// Manager-only living-obligation checkpoint surface.
    /// Same-participant multi-present execution consequence (eligible offices only).
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
    /// CASE-009: conditional Casebook read surface for Engineer.
    | Fetch
    /// GLORY-036: the Manager's own end-of-life tool (`deliver`).
    | Finality
    /// Engineer honeypot: visible as `bash-honeypot`, never a real shell.
    | BashHoneypot
    | JsPlan
    | Ask
    | Deliver
    /// work-021: Plan's review hard gate. Shares the `review` tool name
    /// with Manager's ReviewAssessment; the label stays distinct.
    | Review
    | DevOpsConsultation
    /// advisor: the caller session's second-opinion seat (`js.advisor`).
    /// A consult, not an actor: the caller hands over a view plus an intent
    /// and reads prose back, so the office holding it gains no acting surface.
    | Advisor

[<RequireQualifiedAccess>]
type ManagerCapabilityFacts =
    {
        HasActiveIncumbency: bool
        HasAssessment: bool
        HasConfirmation: bool
        /// True when the active incumbency's accepted assessment has empty
        /// findings: the final incumbent who settles and retires with no successor.
        IsFinalIncumbent: bool
        CleanupBlockerDigest: string option
    }

[<RequireQualifiedAccess>]
module OfficeCapability =

    let managerReviewReadOnlyPermissions: ToolPermission Set =
        set [ ToolPermission.Read; ToolPermission.Glob; ToolPermission.Grep ]

    /// capability-enforcement-025: the final incumbent's
    /// read/cleanup/close-out surface. It keeps reading, horizon, join and the
    /// deliver finality, and drops every new-work capability (fork, resume,
    /// review).
    let managerFinishPermissions: ToolPermission Set =
        set
            [ ToolPermission.Read
              ToolPermission.Glob
              ToolPermission.Grep
              ToolPermission.Horizon
              ToolPermission.Join
              ToolPermission.Finality ]

    let permissions (role: Role) : ToolPermission Set =
        match role with
        // The advisor is a reviewer, not an actor: it reads the supplied view and
        // answers in prose, so its office carries no tool at all.
        | Role.Advisor -> Set.empty
        | Role.Manager ->
            set
                [ ToolPermission.Fork
                  ToolPermission.Resume
                  ToolPermission.Join
                  ToolPermission.Horizon
                  ToolPermission.Advisor
                  ToolPermission.ReviewAssessment
                  ToolPermission.Finality
                  ToolPermission.Read
                  ToolPermission.Glob
                  ToolPermission.Grep ]
        | Role.Orchestrator ->
            set
                [ ToolPermission.Fork
                  ToolPermission.Join
                  ToolPermission.Horizon
                  ToolPermission.Advisor ]
        | Role.Engineer ->
            set
                [ ToolPermission.Read
                  ToolPermission.Write
                  ToolPermission.Edit
                  ToolPermission.Glob
                  ToolPermission.Grep
                  ToolPermission.Move
                  ToolPermission.Remove
                  ToolPermission.BashHoneypot
                  ToolPermission.Fetch
                  ToolPermission.Fission
                  ToolPermission.Advisor ]
        | Role.Coder -> Set.empty
        | Role.Inspector -> Set.empty
        | Role.Browser -> Set.empty
        | Role.Inquiry -> Set.empty
        | Role.DevOps ->
            set
                [ ToolPermission.Read
                  ToolPermission.Write
                  ToolPermission.Edit
                  ToolPermission.Glob
                  ToolPermission.Grep
                  ToolPermission.Move
                  ToolPermission.Remove
                  ToolPermission.Exec
                  ToolPermission.Join
                  ToolPermission.Horizon
                  ToolPermission.Advisor ]
        | Role.Distiller -> Set.empty
        | Role.Plan ->
            // devops 委派调查，ask 只提问用户；两者不是同一项能力。
            set
                [ ToolPermission.JsPlan
                  ToolPermission.Ask
                  ToolPermission.DevOpsConsultation
                  ToolPermission.Review
                  ToolPermission.Deliver ]

    let isAllowed (role: Role) (permission: ToolPermission) : bool =
        if Roles.all |> List.contains role then
            permissions role |> Set.contains permission
        else
            false

    /// Manager gate over exact lifecycle facts. No phase enum crosses this
    /// boundary: retired/no-active grants nothing, a cleanup blocker confines
    /// to the Join+Finality finish window, the final incumbent keeps the
    /// read/cleanup/close-out surface but loses every new-work capability, and
    /// all other active facts keep the full Manager set.
    let permissionsForManagerFacts (facts: ManagerCapabilityFacts) : ToolPermission Set =
        if not facts.HasActiveIncumbency then
            Set.empty
        // A confirmation without a review is an illegal fact: the two-stage
        // deliver only confirms after the review is durable. Fail closed.
        elif facts.HasConfirmation && not facts.HasAssessment then
            Set.empty
        elif facts.CleanupBlockerDigest.IsSome then
            set [ ToolPermission.Join; ToolPermission.Finality ]
        elif facts.IsFinalIncumbent then
            managerFinishPermissions
        elif facts.HasAssessment then
            Set.difference (permissions Role.Manager) managerReviewReadOnlyPermissions
        else
            permissions Role.Manager

    let isAllowedForManagerFacts (facts: ManagerCapabilityFacts) (permission: ToolPermission) : bool =
        permissionsForManagerFacts facts |> Set.contains permission

    /// Stable JS-native label for one permission.
    let permissionLabel (permission: ToolPermission) : string =
        match permission with
        | ToolPermission.Fork -> "Fork"
        | ToolPermission.Resume -> "Resume"
        | ToolPermission.Join -> "Join"
        | ToolPermission.Horizon -> "Horizon"
        | ToolPermission.Fission -> "Fission"
        | ToolPermission.Read -> "Read"
        | ToolPermission.Write -> "Write"
        | ToolPermission.Edit -> "Edit"
        | ToolPermission.Glob -> "Glob"
        | ToolPermission.Grep -> "Grep"
        | ToolPermission.Move -> "Move"
        | ToolPermission.Remove -> "Remove"
        | ToolPermission.Exec -> "Exec"
        | ToolPermission.Pty -> "Pty"
        | ToolPermission.ReviewAssessment -> "ReviewAssessment"
        | ToolPermission.Fetch -> "Fetch"
        | ToolPermission.Finality -> "Finality"
        | ToolPermission.BashHoneypot -> "BashHoneypot"
        | ToolPermission.JsPlan -> "JsPlan"
        | ToolPermission.Ask -> "Ask"
        | ToolPermission.Deliver -> "Deliver"
        | ToolPermission.Review -> "Review"
        | ToolPermission.DevOpsConsultation -> "DevOpsConsultation"
        | ToolPermission.Advisor -> "Advisor"

    /// Unknown labels are not a permission.
    let permissionOfLabel (label: string) : ToolPermission option =
        match label with
        | "Fork" -> Some ToolPermission.Fork
        | "Resume" -> Some ToolPermission.Resume
        | "Join" -> Some ToolPermission.Join
        | "Horizon" -> Some ToolPermission.Horizon
        | "Fission" -> Some ToolPermission.Fission
        | "Read" -> Some ToolPermission.Read
        | "Write" -> Some ToolPermission.Write
        | "Edit" -> Some ToolPermission.Edit
        | "Glob" -> Some ToolPermission.Glob
        | "Grep" -> Some ToolPermission.Grep
        | "Move" -> Some ToolPermission.Move
        | "Remove" -> Some ToolPermission.Remove
        | "Exec" -> Some ToolPermission.Exec
        | "Pty" -> Some ToolPermission.Pty
        | "ReviewAssessment" -> Some ToolPermission.ReviewAssessment
        | "Fetch" -> Some ToolPermission.Fetch
        | "Finality" -> Some ToolPermission.Finality
        | "BashHoneypot" -> Some ToolPermission.BashHoneypot
        | "JsPlan" -> Some ToolPermission.JsPlan
        | "Ask" -> Some ToolPermission.Ask
        | "Deliver" -> Some ToolPermission.Deliver
        | "Review" -> Some ToolPermission.Review
        | "DevOpsConsultation" -> Some ToolPermission.DevOpsConsultation
        | "Advisor" -> Some ToolPermission.Advisor
        | _ -> None
