namespace Wanxiangshu.Execution.Fission

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity

[<CLIMutable>]
type FissionStartedLane =
    { Index: int
      SessionId: SessionId
      Prompt: string }

[<CLIMutable>]
type FissionAdmission =
    { OwnerSessionId: SessionId
      ParentSessionId: SessionId option
      OwnerView: string option
      Lanes: FissionStartedLane list }

[<CLIMutable>]
type FissionAdmissionDependencies =
    { ParentOf: SessionId -> Task<Result<SessionId option, string>>
      OwnerView: SessionId -> Task<Result<string option, string>>
      CreateLane: SessionId -> SessionId option -> FissionLanePrompt -> Task<Result<SessionId, string>>
      StartLane: SessionId -> string -> Task<Result<unit, string>>
      AbortLane: SessionId -> Task
      SilentInterruptOwner: SessionId -> Task<Result<unit, string>> }

// OnLanesCreated commits the admitted group. Its owner view is the text the lanes
// actually received: the owner's delivered view, empty when the owner delivered none.
[<CLIMutable>]
type FissionAdmissionHooks =
    { OnLanesCreated: SessionId -> SessionId option -> string -> FissionStartedLane list -> Task<Result<unit, string>>
      OnFailed: SessionId -> string -> Task }

[<Sealed>]
type FissionAdmissionRuntime

module FissionStartup =
    val render: laneCount: int -> lane: FissionLanePrompt -> ownerView: string option -> string

module FissionAdmission =
    val create: deps: FissionAdmissionDependencies -> FissionAdmissionRuntime
    val createWithHooks: deps: FissionAdmissionDependencies -> hooks: FissionAdmissionHooks -> FissionAdmissionRuntime
    val isActive: _runtime: FissionAdmissionRuntime -> ownerSessionId: SessionId -> bool
    val release: _runtime: FissionAdmissionRuntime -> ownerSessionId: SessionId -> unit
    val releaseOwner: ownerSessionId: SessionId -> unit

    val admit:
        runtime: FissionAdmissionRuntime ->
        ownerSessionId: SessionId ->
        parsed: ParsedFissionPrompts ->
            Task<Result<FissionAdmission, FissionRejectReason>>
