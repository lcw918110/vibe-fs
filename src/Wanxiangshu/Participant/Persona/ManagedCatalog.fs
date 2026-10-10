namespace Wanxiangshu.Participant.Persona

open Wanxiangshu.Foundation

/// Sole identity directory for managed agents (AGENT-001…004).
///
/// Name, role, role groupings, and legacy rejection all derive here.
[<RequireQualifiedAccess>]
module ManagedAgentCatalog =

    let nameOf (role: Role) : string = Roles.roleLabel role

    /// Lowercase Host schema label for the `calling` selector.
    let personaCallingName (persona: Persona) : string =
        match persona with
        | Persona.Director -> "director"
        | Persona.Lead -> "lead"
        | Persona.Engineer -> "engineer"
        | Persona.Coder -> "coder"
        | Persona.Investigator -> "investigator"
        | Persona.Operator -> "operator"
        | Persona.Researcher -> "researcher"
        | Persona.Analyst -> "analyst"
        | Persona.Auditor -> "auditor"
        | Persona.Chronicler -> "chronicler"
        | Persona.Distiller -> "distiller"
        | Persona.Curator -> "curator"
        | Persona.Predictor -> "predictor"
        | Persona.Planner -> "planner"

    /// Parse one exact lowercase Host `calling` label into typed identity.
    let tryParsePersonaCallingName (value: string) : Persona option =
        match value.ToLowerInvariant() with
        | "director"
        | "orchestrator" -> Some Persona.Director
        | "lead"
        | "manager"
        | "coordinator" -> Some Persona.Lead
        | "engineer" -> Some Persona.Engineer
        | "coder" -> Some Persona.Coder
        | "investigator"
        | "inspector"
        | "scout" -> Some Persona.Investigator
        | "operator"
        | "devops"
        | "technician" -> Some Persona.Operator
        | "researcher"
        | "browser"
        | "navigator" -> Some Persona.Researcher
        | "analyst"
        | "inquiry"
        | "inquirer" -> Some Persona.Analyst
        | "auditor"
        | "examiner" -> Some Persona.Auditor
        | "chronicler"
        | "blogger"
        | "scribe" -> Some Persona.Chronicler
        | "distiller"
        | "condenser" -> Some Persona.Distiller
        | "curator"
        | "bookkeeper"
        | "clerk" -> Some Persona.Curator
        | "predictor" -> Some Persona.Predictor
        | "planner"
        | "plan" -> Some Persona.Planner
        | _ -> None

    let allRoles: Role list = Roles.all

    let allPublicRoles: Role list = allRoles |> List.filter (Roles.isInternal >> not)

    let allInternalRoles: Role list = allRoles |> List.filter Roles.isInternal

    /// Manager fork-agent enum (AGENT-009 / GLORY-031 / proposal §7.1):
    /// Only Engineer is forkable by Manager.
    let managerForkableRoles: Role list = [ Role.Engineer ]

    /// InternalLeaf Bookkeeper — not a public Role.
    let bookkeeperNames: string list = [ "bookkeeper" ]

    let bookkeeperName: string = "bookkeeper"

    let isBookkeeperName (name: string) : bool = name.ToLowerInvariant() = "bookkeeper"

    /// Canonical managed agent names using single role versions.
    let requiredNames: string list =
        (allRoles |> List.map Roles.roleLabel) @ [ "bookkeeper"; "predictor" ]

    let managerForkableNames: string list =
        managerForkableRoles |> List.map Roles.roleLabel

    let orchestratorForkableNames: string list = [ Roles.roleLabel Role.Manager ]

    let engineerToolNames: string list = [ Roles.roleLabel Role.Engineer ]

    let legacyAgentNames: Set<string> =
        set
            [ "coder"
              "inspector"
              "browser"
              "inquiry"
              "distiller"
              "build"
              "student"
              "teacher"
              "meditator"
              "executor" ]

    let isLegacyAgentName (lower: string) : bool =
        legacyAgentNames.Contains lower || lower.Contains("_")

    let formatLegacyNameNotSupported (name: string) : string =
        sprintf "Legacy agent name '%s' is not supported." name

    let formatLegacyNameInConfig (name: string) : string =
        sprintf "Legacy agent name '%s' is present in opencode.json." name
