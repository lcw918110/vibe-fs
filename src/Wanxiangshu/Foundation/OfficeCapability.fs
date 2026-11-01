namespace Wanxiangshu.Foundation

open Fable.Core

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

/// Typed F# projection of `wanxiangli/capability` (CUT-08). The role ×
/// permission matrix and the Manager lifecycle gate are decided by the library;
/// this module only maps permission labels onto `ToolPermission`.
[<RequireQualifiedAccess>]
module OfficeCapability =

    /// The slice of the stateless `wanxiangli/capability` object this module reads.
    type private CapabilityLibrary =
        abstract permissions: role: string -> string array
        abstract managerFinishPermissions: unit -> string array
        abstract permissionsForManagerFacts: facts: obj -> string array
        abstract toolNames: permission: string -> string array
        abstract permissionOfToolName: role: string * name: string -> string option
        abstract admitsToolForRole: role: string * name: string -> bool
        abstract utilityToolNames: unit -> string array
        abstract admitsUtility: role: string * name: string -> bool

    [<Import("Capability", "wanxiangli/capability")>]
    let private capabilityClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : CapabilityLibrary = jsNative

    // Stateless library object; one shared instance.
    let private library: CapabilityLibrary = construct capabilityClass

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
        | ToolPermission.ResetScope -> "ResetScope"

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
        | "ResetScope" -> Some ToolPermission.ResetScope
        | _ -> None

    let private permissionSet (labels: string array) : ToolPermission Set =
        labels
        |> Array.map (fun label ->
            match permissionOfLabel label with
            | Some permission -> permission
            | None -> failwithf "wanxiangli/capability returned an unknown permission %s" label)
        |> Set.ofArray

    /// capability-enforcement-025: the final incumbent's read/cleanup/close-out surface.
    let managerFinishPermissions: ToolPermission Set =
        library.managerFinishPermissions () |> permissionSet

    let permissions (role: Role) : ToolPermission Set =
        library.permissions (Roles.roleLabel role) |> permissionSet

    let isAllowed (role: Role) (permission: ToolPermission) : bool =
        permissions role |> Set.contains permission

    /// Manager gate over exact lifecycle facts; the decision is the library's.
    let permissionsForManagerFacts (facts: ManagerCapabilityFacts) : ToolPermission Set =
        box
            {| hasActiveIncumbency = facts.HasActiveIncumbency
               hasAssessment = facts.HasAssessment
               isFinalIncumbent = facts.IsFinalIncumbent
               hasCleanupBlocker = facts.CleanupBlockerDigest.IsSome |}
        |> library.permissionsForManagerFacts
        |> permissionSet

    let isAllowedForManagerFacts (facts: ManagerCapabilityFacts) (permission: ToolPermission) : bool =
        permissionsForManagerFacts facts |> Set.contains permission

    let toolNames (permission: ToolPermission) : string list =
        library.toolNames (permissionLabel permission) |> Array.toList

    let permissionOfToolName (role: Role) (name: string) : ToolPermission option =
        library.permissionOfToolName (Roles.roleLabel role, name)
        |> Option.map (fun label ->
            match permissionOfLabel label with
            | Some permission -> permission
            | None -> failwithf "wanxiangli/capability returned an unknown permission %s" label)

    let admitsToolForRole (role: Role) (name: string) : bool =
        library.admitsToolForRole (Roles.roleLabel role, name)

    let utilityToolNames: string list = library.utilityToolNames () |> Array.toList

    let admitsUtility (role: Role) (name: string) : bool =
        library.admitsUtility (Roles.roleLabel role, name)
