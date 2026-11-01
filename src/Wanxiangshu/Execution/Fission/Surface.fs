namespace Wanxiangshu.Execution.Fission

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// JS test access to product admission and runtime cleanup.
module FissionSurface =

    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private fissionRuntime: obj = jsNative

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private asPromise (value: obj) : JS.Promise<obj> = jsNative

    [<Emit("$0($1)")>]
    let private apply1 (fn: obj) (a: obj) : obj = jsNative

    [<Emit("$0($1,$2)")>]
    let private apply2 (fn: obj) (a: obj) (b: obj) : obj = jsNative

    [<Emit("$0($1,$2,$3)")>]
    let private apply3 (fn: obj) (a: obj) (b: obj) (c: obj) : obj = jsNative

    let private invoke1 (fn: obj) (a: obj) : Task<obj> = unbox (asPromise (apply1 fn a))
    let private invoke2 (fn: obj) (a: obj) (b: obj) : Task<obj> = unbox (asPromise (apply2 fn a b))
    let private invoke3 (fn: obj) (a: obj) (b: obj) (c: obj) : Task<obj> = unbox (asPromise (apply3 fn a b c))

    let private intOf (value: obj) : int =
        match value with
        | :? int as v -> v
        | :? int64 as v -> int v
        | :? float as v -> int v
        | _ -> int (string value)

    let private isFalse (value: obj) : bool =
        match value with
        | :? bool as v -> not v
        | _ -> string value = "false"

    let private jsFailure (value: obj) : string option =
        if isNullish value then
            None
        elif isNullish (value?ok) || not (isFalse (value?ok)) then
            None
        elif isNullish (value?error) then
            Some "failed"
        else
            Some(string (value?error))

    let private rejectToJs (reason: FissionRejectReason) : obj =
        match reason with
        | FissionRejectReason.AlreadyFissioned ->
            box
                {| ok = false
                   reason = "AlreadyFissioned" |}
        | FissionRejectReason.TooFewLanes -> box {| ok = false; reason = "TooFewLanes" |}
        | FissionRejectReason.EmptyLanePrompt index ->
            box
                {| ok = false
                   reason = "EmptyLanePrompt"
                   laneIndex = index |}
        | FissionRejectReason.CapacityExceeded ->
            box
                {| ok = false
                   reason = "CapacityExceeded" |}
        | FissionRejectReason.InvalidOrigin ->
            box
                {| ok = false
                   reason = "InvalidOrigin" |}
        | FissionRejectReason.RuntimeUnavailable message ->
            box
                {| ok = false
                   reason = "RuntimeUnavailable"
                   message = message |}

    let private laneToJs (lane: FissionLanePrompt) : obj =
        box
            {| index = lane.Index
               prompt = lane.Prompt |}

    let parsePrompt (prompts: string array) : obj =
        match FissionPrompt.parse (Array.toList prompts) with
        | Ok parsed ->
            box
                {| ok = true
                   count = parsed.Count
                   lanes = parsed.Lanes |> List.map laneToJs |> List.toArray |}
        | Error reason -> rejectToJs reason

    let private countOf (value: obj) (lanes: FissionLanePrompt list) : int =
        let raw = value?count
        if isNullish raw then List.length lanes else intOf raw

    let private parsedOfJs (value: obj) : ParsedFissionPrompts =
        if isNullish value then
            failwith "cannot admit a null parse"
        else
            ()

        let ok = value?ok

        if not (isNullish ok) && isFalse ok then
            failwith "cannot admit a failed parse"
        else
            ()

        let lanes =
            unbox<obj array> (value?lanes)
            |> Array.toList
            |> List.map (fun lane ->
                { Index = intOf (lane?index)
                  Prompt = string (lane?prompt) })

        { Count = countOf value lanes
          Lanes = lanes }

    let private jsResult (value: obj) : Result<unit, string> =
        jsFailure value
        |> Option.map (fun message -> Error message)
        |> Option.defaultValue (Ok())

    /// The owner's delivered view is optional context: a JS dependency returns
    /// null/undefined (or a blank string) when the owner has not delivered a view yet.
    let private jsResultToOptionalString (value: obj) : Result<string option, string> =
        jsFailure value
        |> Option.map Error
        |> Option.defaultWith (fun () ->
            if isNullish value then
                Ok None
            else
                let text = string value

                if String.IsNullOrWhiteSpace text then
                    Ok None
                else
                    Ok(Some text))

    let private jsResultToOptionalSessionId (value: obj) : Result<SessionId option, string> =
        jsFailure value
        |> Option.map Error
        |> Option.defaultWith (fun () ->
            Ok(
                if isNullish value then
                    None
                else
                    Some(SessionId.create (string value))
            ))

    let private jsResultToRequiredSessionId (emptyMessage: string) (value: obj) : Result<SessionId, string> =
        jsFailure value
        |> Option.map Error
        |> Option.defaultWith (fun () ->
            if isNullish value then
                Error emptyMessage
            else
                Ok(SessionId.create (string value)))

    let private parentAsJs (parent: SessionId option) : obj =
        match parent with
        | None -> null
        | Some p -> box (SessionId.value p)

    let private dependenciesOfJs (deps: obj) : FissionAdmissionDependencies =
        let parentOfFn = deps?parentOf
        let viewFn = deps?ownerView
        let createFn = deps?createLane
        let startFn = deps?startLane
        let abortFn = deps?abortLane
        let interruptFn = deps?silentInterruptOwner

        { ParentOf =
            fun owner ->
                task {
                    try
                        let! value = invoke1 parentOfFn (box (SessionId.value owner))
                        return jsResultToOptionalSessionId value
                    with ex ->
                        return Error ex.Message
                }
          OwnerView =
            fun owner ->
                task {
                    try
                        let! value = invoke1 viewFn (box (SessionId.value owner))
                        return jsResultToOptionalString value
                    with ex ->
                        return Error ex.Message
                }
          CreateLane =
            fun owner parent lane ->
                task {
                    try
                        let laneJs =
                            box
                                {| index = lane.Index
                                   prompt = lane.Prompt |}

                        let! value = invoke3 createFn (box (SessionId.value owner)) (parentAsJs parent) laneJs
                        return jsResultToRequiredSessionId "lane create returned empty" value
                    with ex ->
                        return Error ex.Message
                }
          StartLane =
            fun laneSession startupText ->
                task {
                    try
                        let! value = invoke2 startFn (box (SessionId.value laneSession)) (box startupText)
                        return jsResult value
                    with ex ->
                        return Error ex.Message
                }
          AbortLane =
            fun laneSession ->
                task {
                    try
                        let! _ = invoke1 abortFn (box (SessionId.value laneSession))
                        return ()
                    with ex ->
                        Diagnostic.emit
                            "fission-lane-abort-failed"
                            [ "session_id", SessionId.value laneSession
                              "result", "fission lane abort failed: " + ex.Message ]

                        return ()
                }
                :> Task
          SilentInterruptOwner =
            fun owner ->
                task {
                    try
                        let! value = invoke1 interruptFn (box (SessionId.value owner))
                        return jsResult value
                    with ex ->
                        return Error ex.Message
                } }

    let private admissionToJs (admission: FissionAdmission) : obj =
        let lanes =
            admission.Lanes
            |> List.map (fun lane ->
                box
                    {| index = lane.Index
                       prompt = lane.Prompt |})
            |> List.toArray

        let ownerView =
            match admission.OwnerView with
            | Some view -> box view
            | None -> null

        box
            {| ok = true
               laneCount = lanes.Length
               ownerView = ownerView
               lanes = lanes |}

    let createAdmission (deps: obj) : FissionAdmissionRuntime =
        FissionAdmission.create (dependenciesOfJs deps)

    let admit (runtime: FissionAdmissionRuntime) (ownerSessionId: string) (parsed: obj) : Task<obj> =
        task {
            match! FissionAdmission.admit runtime (SessionId.create ownerSessionId) (parsedOfJs parsed) with
            | Ok admission -> return admissionToJs admission
            | Error reason -> return rejectToJs reason
        }

    let isActive (runtime: FissionAdmissionRuntime) (ownerSessionId: string) : bool =
        FissionAdmission.isActive runtime (SessionId.create ownerSessionId)

    let release (runtime: FissionAdmissionRuntime) (ownerSessionId: string) : unit =
        FissionAdmission.release runtime (SessionId.create ownerSessionId)

    let markSilentInterrupt (ownerSessionId: string) : unit =
        fissionRuntime?markSilentInterrupt ownerSessionId

    let isSilentInterrupt (ownerSessionId: string) : bool =
        fissionRuntime?isSilentInterrupt ownerSessionId

    let tryConsumeSilentInterrupt (ownerSessionId: string) : bool =
        fissionRuntime?tryConsumeSilentInterrupt ownerSessionId

    let clearSilentInterrupt (ownerSessionId: string) : unit =
        fissionRuntime?clearSilentInterrupt ownerSessionId

    let clearOwner (ownerSessionId: string) : unit =
        fissionRuntime?clearOwner ownerSessionId |> ignore
