namespace Wanxiangshu.Execution.Fission

open System
open Fable.Core
open Fable.Core.JsInterop

/// Translate the product lane request to the library parser.
module internal FissionGroupLaw =
    [<Import("FissionGroupLaw", "wanxiangli/fission-group")>]
    let private lawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (_cls: obj) : obj = jsNative

    let law: obj = construct lawClass

open Wanxiangshu.Foundation.Identity

[<CLIMutable>]
type FissionLaneBinding =
    { GroupId: string
      OwnerSessionId: SessionId
      LaneIndex: int
      LaneCount: int }

[<RequireQualifiedAccess>]
type FissionRejectReason =
    | AlreadyFissioned
    | TooFewLanes
    | EmptyLanePrompt of int
    | CapacityExceeded
    | InvalidOrigin
    | RuntimeUnavailable of string

[<CLIMutable>]
type FissionLanePrompt = { Index: int; Prompt: string }

[<CLIMutable>]
type ParsedFissionPrompts =
    { Count: int
      Lanes: FissionLanePrompt list }

module FissionPrompt =

    let parse (prompts: string list) : Result<ParsedFissionPrompts, FissionRejectReason> =
        let verdict: obj = FissionGroupLaw.law?parsePrompts (List.toArray prompts)

        match unbox<string option> verdict?rejection with
        | Some "TooFewLanes" -> Error FissionRejectReason.TooFewLanes
        | Some "EmptyLanePrompt" -> Error(FissionRejectReason.EmptyLanePrompt(unbox<int> verdict?index))
        | Some other -> invalidOp ("wanxiangli/fission-group returned an unknown lane request rejection: " + other)
        | None ->
            Ok
                { Count = unbox<int> verdict?lanes
                  Lanes = prompts |> List.mapi (fun index prompt -> { Index = index; Prompt = prompt }) }

module FissionExternalId =
    let agent handleId = "agent:" + handleId
    let pty ptyId = "pty:" + ptyId

[<RequireQualifiedAccess>]
module FissionRequestProjection =

    /// intra-participant-parallelism-013: physical origin may only narrow the
    /// office entitlement. `true` means the provider request must carry
    /// `fission=false`.
    let apply (hasPhysicalParent: bool) : bool = not hasPhysicalParent
