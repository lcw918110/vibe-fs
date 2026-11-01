namespace Wanxiangshu.Participant.Persona

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation

/// DSL-class: Vocabulary — the fixed persona catalog, canonical 11 cases.
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
    | Distiller
    | Curator
    | Planner

module internal IdentityLibrary =

    [<Import("Identity", "wanxiangli/identity")>]
    let private identityClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    /// Stateless `wanxiangli/identity` object (CUT-07); one shared instance.
    let instance: obj = construct identityClass

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
        | Persona.Distiller -> "Distiller"
        | Persona.Curator -> "Curator"
        | Persona.Planner -> "Planner"

    let private cases: Persona list =
        [ Persona.Director
          Persona.Lead
          Persona.Engineer
          Persona.Coder
          Persona.Investigator
          Persona.Operator
          Persona.Researcher
          Persona.Analyst
          Persona.Distiller
          Persona.Curator
          Persona.Planner ]

    let internal ofCanonical (label: obj) : Persona option =
        if isNull label then
            None
        else
            let text = unbox<string> label
            cases |> List.tryFind (fun persona -> render persona = text)

    /// Persona label decoding (including historical aliases) is owned by
    /// `wanxiangli/identity`.
    let tryParse (label: string) : Persona option =
        ofCanonical (IdentityLibrary.instance?tryParsePersona label)

/// AGENT-028: Role → the persona embedded in ParticipantIdentity. The V1
/// catalog is owned by `wanxiangli/identity` (CUT-07); IdentitySeed resolves
/// it once. Bookkeeper is InternalLeaf — not a public Role; use `bookkeeperPersona`.
[<RequireQualifiedAccess>]
module PersonaCatalog =

    let personaV1 (role: Role) : string =
        unbox<string> (IdentityLibrary.instance?personaOfRole (Roles.roleLabel role))

    let persona (role: Role) : Persona =
        match Persona.ofCanonical (box (personaV1 role)) with
        | Some value -> value
        | None -> failwithf "wanxiangli/identity has no V1 persona for role %s" (Roles.roleLabel role)

    let bookkeeperPersonaV1 () : string =
        unbox<string> (IdentityLibrary.instance?bookkeeperPersona ())

    let bookkeeperPersona () : Persona =
        match Persona.ofCanonical (box (bookkeeperPersonaV1 ())) with
        | Some value -> value
        | None -> failwith "wanxiangli/identity has no V1 bookkeeper persona"

    /// HOST-026 analogue: child / attached / InternalLeaf ParticipantIdentity inherits the owner persona.
    let inheritFrom (ownerPersona: string) : string = ownerPersona
