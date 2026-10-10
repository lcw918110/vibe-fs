namespace Wanxiangshu.Participant.Persona

open Wanxiangshu.Foundation

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
    val render: Persona -> string
    val tryParse: string -> Persona option

[<RequireQualifiedAccess>]
module PersonaCatalog =
    val persona: Role -> Persona
    val bookkeeperPersona: unit -> Persona
    val personaV1: Role -> string
    val bookkeeperPersonaV1: unit -> string
    val inheritFrom: string -> string
