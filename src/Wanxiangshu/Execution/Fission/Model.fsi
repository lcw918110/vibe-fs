namespace Wanxiangshu.Execution.Fission

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
    val parse: prompts: string list -> Result<ParsedFissionPrompts, FissionRejectReason>

module FissionExternalId =
    val agent: handleId: string -> string
    val pty: ptyId: string -> string

[<RequireQualifiedAccess>]
module FissionRequestProjection =
    val apply: hasPhysicalParent: bool -> bool
