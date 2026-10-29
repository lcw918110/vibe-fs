namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host

/// HNI-09: the product decisions of one generated js-* tool. The OpenCode
/// binding (`JsToolSpec`) keeps the host-wire plumbing and asks this module
/// for the decisions.
[<RequireQualifiedAccess>]
module JsToolPolicy =

    /// The directory one session's js-* surface executes in: the path its
    /// owner registered for that session, else the given fallback.
    val rootForSession:
        sessionDirectories: System.Collections.Generic.Dictionary<string, string> ->
        fallback: string ->
        sessionId: string ->
            string

    /// Whether the office role may call the generated tool of that role name.
    val admitsRole: roleName: string -> role: Role -> bool

    /// Manager cannot read by default.
    val defaultReadCheck:
        roleName: string -> readCheck: ('context -> Task<bool>) option -> ('context -> Task<bool>) option

    /// Required name of this program invocation.
    val argNameProse: unit -> string

    val argProgramProse: unit -> string

    val argIntentProse: unit -> string
    /// Retired resource path; returns retired notice. Not a product schema field.
    val argEstimateProse: unit -> string

    /// The localized prose of the refusal of a call that carries no state.
    val missingProgramProse: unit -> string

    /// The refusal tool result when the required Host program argument is absent.
    val missingProgramResult: unit -> string

    /// The rendered result of a settled call, with the view declaration
    /// committed when the program settled.
    val renderCommitted:
        sessionId: string ->
        callIdentity: (string * string) option ->
        declaration: obj ->
        outcome: JsToolWorkflow.JsToolOutcome ->
            string
