namespace Wanxiangshu.Foundation

open Fable.Core

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

/// Typed F# projection of the role vocabulary owned by `wanxiangli/identity`
/// (CUT-07). Which labels are active, historical or internal is decided by the
/// library; this module only maps the library's wire labels onto `Role`.
module Roles =

    /// The slice of the stateless `wanxiangli/identity` object this module reads.
    type private RoleCatalog =
        abstract activeRoleLabels: unit -> string array
        abstract tryParseHistoricalRole: label: string -> string
        abstract isInternalRole: label: string -> bool

    [<Import("Identity", "wanxiangli/identity")>]
    let private identityClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : RoleCatalog = jsNative

    // Stateless library object; one shared instance.
    let private identity: RoleCatalog = construct identityClass

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

    let private cases: Role list =
        [ Role.Manager
          Role.Orchestrator
          Role.Engineer
          Role.Coder
          Role.Inspector
          Role.Browser
          Role.Inquiry
          Role.DevOps
          Role.Distiller
          Role.Plan
          Role.Advisor ]

    let private ofLabel (label: string) : Role option =
        if isNull label then
            None
        else
            cases |> List.tryFind (fun role -> roleLabel role = label)

    /// Active roles in catalog order, as decided by the library.
    let all: Role list =
        identity.activeRoleLabels () |> Array.toList |> List.choose ofLabel

    /// Decodes an active or historical role label (case-insensitive). The host
    /// has always accepted historical labels here because journal decoding
    /// relies on it; active-only admission is enforced by ParticipantIdentity.
    let tryParseRole (value: string) : Role option =
        ofLabel (identity.tryParseHistoricalRole value)

    let tryParseHistoricalRole (value: string) : Role option = tryParseRole value

    /// AGENT-008 / capability-enforcement-006: Distiller and Advisor are private runtimes, not
    /// public fork / horizon vocabulary.
    let isInternal (role: Role) : bool =
        identity.isInternalRole (roleLabel role)

    /// Manager, Orchestrator and Plan own long work. A tool-free natural stop
    /// is never their end: while the work is open they are nudged on, and no
    /// late answer may stop them. Engineer, DevOps and Advisor close in prose.
    let isLongRunning (role: Role) : bool =
        match role with
        | Role.Manager
        | Role.Orchestrator
        | Role.Plan -> true
        | _ -> false
