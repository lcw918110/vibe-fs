namespace Wanxiangshu.Execution.Fission

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
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
type FissionAdmissionRuntime internal (deps: FissionAdmissionDependencies, hooks: FissionAdmissionHooks) =
    member internal _.Dependencies = deps
    member internal _.Hooks = hooks

module FissionStartup =

    /// Fresh lane startup deliberately uses the owner's delivered view rather than transcript
    /// cloning. The lane input is copied exactly after the canonical parser. The owner's
    /// delivered view is optional context: a lane starts from the lane input alone when the
    /// owner has not delivered a view yet.
    let render laneCount (lane: FissionLanePrompt) (ownerView: string option) =
        let ownerViewInstructions =
            match ownerView with
            | Some view when not (String.IsNullOrWhiteSpace view) ->
                [ "The owner's latest delivered view follows. Continue its unfinished responsibility."
                  view ]
            | _ -> []

        let instructions =
            [ "You are one coequal present of the same logical participant after Fission."
              "Your lane input follows. Carry it as your own current responsibility."
              lane.Prompt ]
            @ ownerViewInstructions
            @ [ "Continue the same participant's responsibility. Do not treat sibling lanes as delegated agents." ]

        LlmFacing.instructions instructions
        |> LlmFacing.withData [ LlmFacing.Data.intField "lane_count" laneCount ]
        |> LlmFacing.render

module FissionAdmission =

    [<Import("FissionLifecycle", "wanxiangli/fission-group")>]
    let private lifecycleClass: obj = jsNative

    let private lifecycle: obj = createNew lifecycleClass ()

    let private noHooks =
        { OnLanesCreated = fun _ _ _ _ -> Task.FromResult(Ok())
          OnFailed = fun _ _ -> Task.FromResult(()) }

    let create deps = FissionAdmissionRuntime(deps, noHooks)
    let createWithHooks deps hooks = FissionAdmissionRuntime(deps, hooks)

    let isActive (_runtime: FissionAdmissionRuntime) (ownerSessionId: SessionId) : bool =
        lifecycle?isActive (SessionId.value ownerSessionId)

    let release (_runtime: FissionAdmissionRuntime) (ownerSessionId: SessionId) : unit =
        lifecycle?release (SessionId.value ownerSessionId) |> ignore

    let releaseOwner (ownerSessionId: SessionId) : unit =
        lifecycle?release (SessionId.value ownerSessionId) |> ignore

    let private resultToNative convert (operation: Task<Result<'a, string>>) =
        task {
            let! result = operation

            return
                match result with
                | Ok value -> createObj [ "ok" ==> true; "value" ==> convert value ]
                | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
        }

    let private optionalText (value: obj) =
        if isNullOrUndefined value then
            None
        else
            Some(unbox<string> value)

    let private optionalSession value =
        optionalText value |> Option.map SessionId.create

    let private lanePrompt (value: obj) : FissionLanePrompt =
        { Index = unbox value?index
          Prompt = unbox value?prompt }

    let private startedLane (value: obj) : FissionStartedLane =
        { Index = unbox value?index
          Prompt = unbox value?prompt
          SessionId = SessionId.create (unbox value?session) }

    let private startedLanes (values: obj array) =
        values |> Array.map startedLane |> Array.toList

    let private effects (runtime: FissionAdmissionRuntime) =
        let deps = runtime.Dependencies
        let hooks = runtime.Hooks

        createObj
            [ "parentOf"
              ==> (fun owner ->
                  deps.ParentOf(SessionId.create owner)
                  |> resultToNative (Option.map SessionId.value >> box))
              "ownerView"
              ==> (fun owner -> deps.OwnerView(SessionId.create owner) |> resultToNative box)
              "createLane"
              ==> (fun owner parent lane ->
                  deps.CreateLane (SessionId.create owner) (optionalSession parent) (lanePrompt lane)
                  |> resultToNative (SessionId.value >> box))
              "renderStartup"
              ==> (fun count lane view -> FissionStartup.render count (lanePrompt lane) (optionalText view))
              "startLane"
              ==> (fun session prompt -> deps.StartLane (SessionId.create session) prompt |> resultToNative box)
              "abortLane" ==> (fun session -> deps.AbortLane(SessionId.create session))
              "interruptOwner"
              ==> (fun owner -> deps.SilentInterruptOwner(SessionId.create owner) |> resultToNative box)
              "onLanesCreated"
              ==> (fun owner parent view lanes ->
                  hooks.OnLanesCreated (SessionId.create owner) (optionalSession parent) view (startedLanes lanes)
                  |> resultToNative box)
              "onFailed"
              ==> (fun owner reason -> hooks.OnFailed (SessionId.create owner) reason) ]

    let admit
        (runtime: FissionAdmissionRuntime)
        (ownerSessionId: SessionId)
        (parsed: ParsedFissionPrompts)
        : Task<Result<FissionAdmission, FissionRejectReason>> =
        task {
            let request =
                createObj
                    [ "owner" ==> SessionId.value ownerSessionId
                      "count" ==> parsed.Count
                      "lanes"
                      ==> (parsed.Lanes
                           |> List.map (fun lane -> createObj [ "index" ==> lane.Index; "prompt" ==> lane.Prompt ])
                           |> List.toArray) ]

            let! (result: obj) = lifecycle?admit (request, effects runtime)

            let rejection () =
                match string result?kind with
                | "TooFewLanes" -> FissionRejectReason.TooFewLanes
                | "InvalidOrigin" -> FissionRejectReason.InvalidOrigin
                | "AlreadyFissioned" -> FissionRejectReason.AlreadyFissioned
                | _ -> FissionRejectReason.RuntimeUnavailable(unbox result?error)

            if unbox<bool> result?ok then
                let value: obj = result?value

                return
                    Ok
                        { OwnerSessionId = SessionId.create (unbox value?owner)
                          ParentSessionId = optionalSession value?parent
                          OwnerView = optionalText value?ownerView
                          Lanes = startedLanes (unbox value?lanes) }
            else
                return Error(rejection ())
        }
