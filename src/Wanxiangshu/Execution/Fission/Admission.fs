namespace Wanxiangshu.Execution.Fission

open System
open System.Collections.Generic
open System.Threading.Tasks
open FsToolkit.ErrorHandling
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

    // One process-wide single-flight resource for live admissions only.
    let private gate = obj ()
    // DSL-MUTABLE: single-flight — one active fission admission per owner
    let private activeOwners = HashSet<string>()

    let private noHooks =
        { OnLanesCreated = fun _ _ _ _ -> Task.FromResult(Ok())
          OnFailed = fun _ _ -> Task.FromResult(()) }

    let create deps = FissionAdmissionRuntime(deps, noHooks)

    let createWithHooks deps hooks = FissionAdmissionRuntime(deps, hooks)

    let isActive (_runtime: FissionAdmissionRuntime) (ownerSessionId: SessionId) =
        lock gate (fun () -> activeOwners.Contains(SessionId.value ownerSessionId))

    let release (_runtime: FissionAdmissionRuntime) (ownerSessionId: SessionId) =
        lock gate (fun () -> activeOwners.Remove(SessionId.value ownerSessionId) |> ignore)

    let releaseOwner (ownerSessionId: SessionId) =
        lock gate (fun () -> activeOwners.Remove(SessionId.value ownerSessionId) |> ignore)

    let private reserve (_runtime: FissionAdmissionRuntime) (ownerSessionId: SessionId) =
        lock gate (fun () -> activeOwners.Add(SessionId.value ownerSessionId))

    let private dependencyError message =
        Error(FissionRejectReason.RuntimeUnavailable message)

    let private abortLaneQuietly (runtime: FissionAdmissionRuntime) (lane: FissionStartedLane) =
        task {
            try
                do! runtime.Dependencies.AbortLane lane.SessionId
            with _ ->
                ()
        }

    let private rollback (runtime: FissionAdmissionRuntime) ownerSessionId (created: FissionStartedLane list) =
        task {
            for lane in created do
                do! abortLaneQuietly runtime lane

            release runtime ownerSessionId
        }

    let private dependency prefix (operation: Task<Result<'a, string>>) : Task<Result<'a, FissionRejectReason>> =
        task {
            match! operation with
            | Ok value -> return Ok value
            | Error error -> return dependencyError (prefix + error)
        }

    let private dependencyOrRelease
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        prefix
        (operation: Task<Result<'a, string>>)
        : Task<Result<'a, FissionRejectReason>> =
        task {
            match! dependency prefix operation with
            | Ok value -> return Ok value
            | Error error ->
                release runtime ownerSessionId
                return Error error
        }

    let private mapCreateLaneError createdRev (operation: Task<Result<SessionId, string>>) =
        task {
            match! operation with
            | Ok laneSessionId -> return Ok laneSessionId
            | Error error -> return Error(error, List.rev createdRev)
        }

    let rec private createLanes
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        parentSessionId
        (remaining: FissionLanePrompt list)
        (createdRev: FissionStartedLane list)
        =
        taskResult {
            match remaining with
            | [] -> return List.rev createdRev
            | lane :: rest ->
                let! laneSessionId =
                    mapCreateLaneError createdRev (runtime.Dependencies.CreateLane ownerSessionId parentSessionId lane)

                let started =
                    { Index = lane.Index
                      SessionId = laneSessionId
                      Prompt = lane.Prompt }

                return! createLanes runtime ownerSessionId parentSessionId rest (started :: createdRev)
        }

    let private createAllLanes
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        parentSessionId
        (lanes: FissionLanePrompt list)
        =
        task {
            match! createLanes runtime ownerSessionId parentSessionId lanes [] with
            | Ok created -> return Ok created
            | Error(error, created) ->
                do! rollback runtime ownerSessionId created
                return dependencyError ("lane create failed: " + error)
        }

    let private commitLanesCreated
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        parentSessionId
        ownerView
        (created: FissionStartedLane list)
        =
        task {
            match! runtime.Hooks.OnLanesCreated ownerSessionId parentSessionId ownerView created with
            | Ok() -> return Ok()
            | Error error ->
                do! rollback runtime ownerSessionId created
                return dependencyError ("fission admission commit failed: " + error)
        }

    let private startAllLanes
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        (parsed: ParsedFissionPrompts)
        ownerView
        (created: FissionStartedLane list)
        =
        task {
            let! outcome =
                created
                |> TaskResultList.traverseM (fun started ->
                    let lane =
                        parsed.Lanes |> List.find (fun candidate -> candidate.Index = started.Index)

                    let startup = FissionStartup.render parsed.Count lane ownerView
                    runtime.Dependencies.StartLane started.SessionId startup)

            match outcome with
            | Ok _ -> return Ok()
            | Error error ->
                do! runtime.Hooks.OnFailed ownerSessionId ("lane start failed: " + error)
                do! rollback runtime ownerSessionId created
                return dependencyError ("lane start failed: " + error)
        }

    let private silentInterruptOwner
        (runtime: FissionAdmissionRuntime)
        ownerSessionId
        (created: FissionStartedLane list)
        =
        task {
            match! runtime.Dependencies.SilentInterruptOwner ownerSessionId with
            | Ok() -> return Ok()
            | Error error ->
                do! runtime.Hooks.OnFailed ownerSessionId ("silent owner interrupt failed: " + error)
                do! rollback runtime ownerSessionId created
                return dependencyError ("silent owner interrupt failed: " + error)
        }

    let private admitReserved
        (runtime: FissionAdmissionRuntime)
        (ownerSessionId: SessionId)
        (parentSessionId: SessionId)
        (parsed: ParsedFissionPrompts)
        =
        taskResult {
            let! ownerView =
                dependencyOrRelease
                    runtime
                    ownerSessionId
                    "owner view materialization failed: "
                    (runtime.Dependencies.OwnerView ownerSessionId)

            // The owner's delivered view is optional context for the lanes, not an
            // admission precondition: an owner that has not delivered yet still
            // admits its lanes, and those lanes start from the lane input alone.
            let physicalParent = Some parentSessionId
            // The lanes' owner view is one text: the delivered view, or the empty
            // view when the owner has delivered nothing. The durable commit and the
            // lane startup both read it, so the fact and the lanes cannot disagree.
            let ownerViewText = ownerView |> Option.defaultValue ""
            let! created = createAllLanes runtime ownerSessionId physicalParent parsed.Lanes
            do! commitLanesCreated runtime ownerSessionId physicalParent ownerViewText created
            do! startAllLanes runtime ownerSessionId parsed ownerView created
            do! silentInterruptOwner runtime ownerSessionId created

            return
                { OwnerSessionId = ownerSessionId
                  ParentSessionId = Some parentSessionId
                  OwnerView = ownerView
                  Lanes = created }
        }

    let private admitFromPhysicalParent
        (runtime: FissionAdmissionRuntime)
        (ownerSessionId: SessionId)
        (parsed: ParsedFissionPrompts)
        parentSessionId
        =
        match parentSessionId with
        | None -> Task.FromResult(Error FissionRejectReason.InvalidOrigin)
        | Some _ when not (reserve runtime ownerSessionId) ->
            Task.FromResult(Error FissionRejectReason.AlreadyFissioned)
        | Some parent -> admitReserved runtime ownerSessionId parent parsed

    let admit
        (runtime: FissionAdmissionRuntime)
        (ownerSessionId: SessionId)
        (parsed: ParsedFissionPrompts)
        : Task<Result<FissionAdmission, FissionRejectReason>> =
        taskResult {
            if parsed.Count < 2 then
                return! Error FissionRejectReason.TooFewLanes
            else
                let! parentSessionId =
                    dependency "physical parent lookup failed: " (runtime.Dependencies.ParentOf ownerSessionId)

                return! admitFromPhysicalParent runtime ownerSessionId parsed parentSessionId
        }
