namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// JS boundary for the process-local degeneration guard.
module LoopSensorSurface =

    type private SensorHandle(sensor: LoopSensor) =
        member _.Sensor = sensor

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'function'")>]
    let private isFunction (value: obj) : bool = jsNative

    [<Emit("$0 instanceof Set")>]
    let private isSet (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("$0($1)")>]
    let private apply1 (fn: obj) (value: obj) : obj = jsNative

    [<Emit("$0($1,$2)")>]
    let private apply2 (fn: obj) (first: obj) (second: obj) : obj = jsNative

    [<Emit("$0.has($1)")>]
    let private has (set: obj) (value: obj) : bool = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private asPromise (value: obj) : JS.Promise<obj> = jsNative

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    let private stringOf (value: obj) =
        if isNullish value then "" else string value

    let private boolOf (value: obj) =
        match value with
        | :? bool as value -> value
        | _ -> String.Equals(string value, "true", StringComparison.OrdinalIgnoreCase)

    let private ownedOf (value: obj) : SessionId -> bool =
        if isFunction value then
            fun sessionId -> boolOf (apply1 value (box (SessionId.value sessionId)))
        elif isSet value then
            fun sessionId -> has value (box (SessionId.value sessionId))
        elif isArray value then
            let owned = unbox<string array> value |> Set.ofArray
            fun sessionId -> owned.Contains(SessionId.value sessionId)
        else
            fun _ -> false

    let private resultOf (value: obj) : Result<unit, string> =
        if isNullish value then
            Ok()
        else
            let ok = property value "ok"

            if isNullish ok || boolOf ok then
                Ok()
            else
                let reason = property value "error"

                Error(
                    if isNullish reason then
                        "operation failed"
                    else
                        stringOf reason
                )

    let private abortOf (value: obj) : SessionId -> Task<Result<unit, string>> =
        if not (isFunction value) then
            invalidArg "options" "LoopSensorSurface.create requires an abort callback"

        fun sessionId ->
            task {
                let! result = unbox<Task<obj>> (asPromise (apply1 value (box (SessionId.value sessionId))))
                return resultOf result
            }

    let private continueOf
        (value: obj)
        : ProviderAttemptSource
              -> DegenerationKind
              -> string option
              -> ContinuationAcceptanceObserver option
              -> Task<Result<unit, string>>
        =
        if not (isFunction value) then
            invalidArg "options" "LoopSensorSurface.create requires a continue callback"

        fun source kind _directory _observer ->
            task {
                let! result =
                    unbox<Task<obj>> (
                        asPromise (
                            apply2 value (box (SessionId.value source.SessionId)) (box (LoopSensor.kindName kind))
                        )
                    )

                return resultOf result
            }

    let private diagnosticOf (value: obj) : string -> (string * string) list -> unit =
        if not (isFunction value) then
            invalidArg "options" "LoopSensorSurface.create requires a diagnostic callback"

        fun operation fields ->
            fields
            |> List.map (fun (name, fieldValue) -> [| box name; box fieldValue |])
            |> List.toArray
            |> box
            |> apply2 value (box operation)
            |> ignore

    let create (options: obj) : obj =
        let owned = property options "owned"
        let abort = property options "abort"
        let continueCallback = property options "continue"
        let diagnostic = property options "diagnostic"

        SensorHandle(LoopSensor(ownedOf owned, abortOf abort, continueOf continueCallback, diagnosticOf diagnostic))
        :> obj

    let observe (sensor: obj) (raw: obj) : unit =
        (sensor :?> SensorHandle).Sensor.Observe raw

    let private sourceField (source: obj) field =
        let value = property source field
        let isString: bool = emitJsExpr value "typeof $0 === 'string'"

        if not isString || String.IsNullOrWhiteSpace(unbox<string> value) then
            invalidArg "source" (sprintf "LoopSensorSurface.consumeAbortCause requires %s" field)

        unbox<string> value

    let private sourceOf (source: obj) : ProviderAttemptSource =
        { SessionId = sourceField source "sessionId" |> SessionId.create
          PhysicalUserMessageId = sourceField source "physicalUserMessageId" |> PhysicalUserMessageId.create
          AuthorityRootUserMessageId =
            sourceField source "authorityRootUserMessageId"
            |> AuthorityRootUserMessageId.create
          ProviderRun = sourceField source "providerRun" |> ProviderRunIdentity.create }

    let consumeAbortCause (sensor: obj) (source: obj) : Task<obj> =
        task {
            let! cause = (sensor :?> SensorHandle).Sensor.ConsumeAbortCause(sourceOf source, None, None)

            return
                match cause with
                | AbortCause.External -> box {| cause = "External" |}
                | AbortCause.DegenerationGuard kind ->
                    box
                        {| cause = "DegenerationGuard"
                           anomaly = LoopSensor.kindName kind |}
        }

    /// Owned interrupt/continuation task for the exact requested run, exposed
    /// as an awaitable. Null when no task is owned for that run; a mismatched
    /// run never observes the stored run's task.
    let activeTask (sensor: obj) (session: string) (run: string) : obj =
        match
            (sensor :?> SensorHandle)
                .Sensor.ActiveInterruptTask(SessionId.create session, ProviderRunIdentity.create run)
        with
        | None -> null
        | Some owned -> box owned

    let dropSession (sensor: obj) (session: string) : unit =
        (sensor :?> SensorHandle).Sensor.DropSession(SessionId.create session)

    let resetDetector (sensor: obj) (session: string) : unit =
        (sensor :?> SensorHandle).Sensor.ResetDetector(SessionId.create session)

    let continuationResourcePath (kindName: string) : string =
        match kindName with
        | "TooRepetitive" -> LoopSensor.continuationPath DegenerationKind.TooRepetitive
        | "TooRandom" -> LoopSensor.continuationPath DegenerationKind.TooRandom
        | unknown -> invalidArg "kindName" (sprintf "unknown DegenerationKind: %s" unknown)

    /// Streaming delta for the exact provider run. A nullish message id
    /// produces a delta without a run: observed into detector scratch only,
    /// never arming an anomaly.
    let textDelta (session: string) (text: string) (messageId: obj) : obj =
        let messageField = if isNullish messageId then null else string messageId

        box
            {| ``type`` = "message.part.delta"
               properties =
                {| sessionID = session
                   messageID = messageField
                   partID = "prt_1"
                   field = "text"
                   delta = text |} |}
