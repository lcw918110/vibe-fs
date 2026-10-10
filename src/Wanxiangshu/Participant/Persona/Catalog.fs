namespace Wanxiangshu.Participant.Persona

open Wanxiangshu.Foundation

/// DSL-class: Vocabulary — the fixed persona catalog, canonical 12 cases.
/// No case carries state or ordering.
[<RequireQualifiedAccess>]
type Persona =
    | Director
    | Lead
    | Engineer
    | Coder
    | Investigator
    | Operator
    | Researcher
    | Analyst
    | Auditor
    | Chronicler
    | Distiller
    | Curator
    | Predictor
    | Planner

[<RequireQualifiedAccess>]
module Persona =

    let render (persona: Persona) : string =
        match persona with
        | Persona.Director -> "Director"
        | Persona.Lead -> "Lead"
        | Persona.Engineer -> "Engineer"
        | Persona.Coder -> "Coder"
        | Persona.Investigator -> "Investigator"
        | Persona.Operator -> "Operator"
        | Persona.Researcher -> "Researcher"
        | Persona.Analyst -> "Analyst"
        | Persona.Auditor -> "Auditor"
        | Persona.Chronicler -> "Chronicler"
        | Persona.Distiller -> "Distiller"
        | Persona.Curator -> "Curator"
        | Persona.Predictor -> "Predictor"
        | Persona.Planner -> "Planner"

    let tryParse (label: string) : Persona option =
        match label with
        | "Director"
        | "DirectorV1" -> Some Persona.Director
        | "Lead" -> Some Persona.Lead
        | "Engineer" -> Some Persona.Engineer
        | "Coder" -> Some Persona.Coder
        | "Investigator" -> Some Persona.Investigator
        | "Operator" -> Some Persona.Operator
        | "Researcher" -> Some Persona.Researcher
        | "Analyst" -> Some Persona.Analyst
        | "Auditor" -> Some Persona.Auditor
        | "Chronicler" -> Some Persona.Chronicler
        | "Distiller" -> Some Persona.Distiller
        | "Curator" -> Some Persona.Curator
        | "Predictor" -> Some Persona.Predictor
        | "Planner" -> Some Persona.Planner
        | _ -> None

/// AGENT-028: Role → the persona embedded in ParticipantIdentity.
/// IdentitySeed resolves it once. Bookkeeper is InternalLeaf — not a public Role;
/// use `bookkeeperPersona`.
[<RequireQualifiedAccess>]
module PersonaCatalog =

    let persona (role: Role) : Persona =
        match role with
        | Role.Orchestrator -> Persona.Director
        | Role.Manager -> Persona.Lead
        | Role.Engineer -> Persona.Engineer
        | Role.DevOps -> Persona.Operator
        | Role.Blogger -> Persona.Chronicler
        | Role.Plan -> Persona.Planner
        | Role.Coder -> Persona.Coder
        | Role.Inspector -> Persona.Investigator
        | Role.Browser -> Persona.Researcher
        | Role.Inquiry -> Persona.Analyst
        | Role.Distiller -> Persona.Distiller

    let bookkeeperPersona () : Persona = Persona.Curator

    let personaV1 (role: Role) : string = persona role |> Persona.render

    let bookkeeperPersonaV1 () : string = bookkeeperPersona () |> Persona.render

    /// HOST-026 analogue: child / attached / InternalLeaf ParticipantIdentity inherits the owner persona.
    let inheritFrom (ownerPersona: string) : string = ownerPersona
