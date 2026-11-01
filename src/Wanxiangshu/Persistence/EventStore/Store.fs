namespace Wanxiangshu.Persistence.EventStore

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module EventStore =
    [<Import("LocalEventStore", "wanxiangli/local-eventstore")>]
    let private storeClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (_class: obj) (_options: obj) : obj = jsNative

    let private nativeResult convert = function
        | Ok value -> createObj [ "ok" ==> true; "value" ==> convert value ]
        | Error reason -> createObj [ "ok" ==> false; "error" ==> reason ]

    let private cutToNative (cut: SemanticCut) =
        createObj
            [ "rule" ==> cut.Rule
              "failedEventId" ==> EventId.value cut.FailedEventId
              "reason" ==> cut.Reason
              "cutEventId" ==> EventId.value cut.CutEventId ]

    let private cutValue (value: obj) : SemanticCut =
        { Rule = unbox<string> value?rule
          FailedEventId = EventId.create (unbox<string> value?failedEventId)
          Reason = unbox<string> value?reason
          CutEventId = EventId.create (unbox<string> value?cutEventId) }

    let private eventsValue (value: obj) =
        unbox<obj array> value |> Array.map CanonicalEventCodec.ofLibraryEvent |> Array.toList

    let private cutsValue (value: obj) =
        unbox<obj array> value |> Array.map cutValue |> Array.toList

    let private preparedToNative (prepared: PreparedIntegration) =
        createObj
            [ "durableEvents" ==>
                  (prepared.DurableEvents |> List.map CanonicalEventCodec.toLibraryEvent |> List.toArray)
              "cuts" ==> (prepared.Cuts |> List.map cutToNative |> List.toArray)
              "commit" ==> Action(prepared.Commit) ]

    let private preparedValue (value: obj) : PreparedAppend =
        { DurableEvents = eventsValue value?durableEvents
          Cuts = cutsValue value?cuts }

    let private optionalPrepared (value: obj) =
        if isNull value then None else Some(preparedValue value)

    let private cleanupValue (value: obj) =
        unbox<obj array> value |> Array.map ProcessEventLog.appendFaultValue |> Array.toList

    let private rejectionValue (value: obj) =
        match unbox<string> value?kind with
        | "StorageInvalid" ->
            AppendPreWriteRejection.StorageInvalid(CanonicalEventCodec.invalidOfLibraryError value?invalid)
        | "PreparationRejected" -> AppendPreWriteRejection.PreparationRejected(unbox<string> value?reason)
        | kind -> invalidOp ("Unknown local append rejection: " + kind)

    let private errorValue (value: obj) =
        match unbox<string> value?kind with
        | "StorageInvalid" -> AppendError.StorageInvalid(CanonicalEventCodec.invalidOfLibraryError value?invalid)
        | "AppendFailed" -> AppendError.AppendFailed(unbox<string> value?reason)
        | "AppendNotAttempted" ->
            AppendError.AppendNotAttempted
                { Requested = eventsValue value?requested
                  Prepared = optionalPrepared value?prepared
                  Primary = ProcessEventLog.appendFaultValue value?primary
                  CleanupFailures = cleanupValue value?cleanupFailures
                  PriorRejection =
                    let rejection: obj = value?priorRejection
                    if isNull rejection then None else Some(rejectionValue rejection) }
        | "CommitUnknown" ->
            AppendError.CommitUnknown
                { Requested = eventsValue value?requested
                  Prepared = preparedValue value?prepared
                  Primary = ProcessEventLog.appendFaultValue value?primary
                  CleanupFailures = cleanupValue value?cleanupFailures }
        | "NoNewWriteReleaseFailed" ->
            AppendError.NoNewWriteReleaseFailed
                { Requested = eventsValue value?requested
                  Prepared = optionalPrepared value?prepared
                  Cause = unbox<exn> value?cause }
        | kind -> invalidOp ("Unknown local append result: " + kind)

    let private integratorValue commonDir (integrator: ICanonicalIntegrator) =
        createObj
            [ "reload" ==>
                  Func<obj, obj>(fun _ -> integrator.ReloadLocal commonDir |> nativeResult box)
              "prepare" ==>
                  Func<obj array, obj>(fun events ->
                      events |> Array.map CanonicalEventCodec.ofLibraryEvent |> Array.toList
                      |> integrator.PrepareLive |> nativeResult preparedToNative)
              "isKnownEventType" ==> Func<string, bool>(integrator.IsEventTypeKnown)
              "current" ==>
                  Func<string, obj>(fun slot -> integrator.TryCurrent slot |> Option.defaultValue null)
              "event" ==>
                  Func<string, obj>(fun identity ->
                      integrator.TryEvent(EventId.create identity)
                      |> Option.map CanonicalEventCodec.toLibraryEvent |> Option.defaultValue null)
              "payload" ==>
                  Func<string, obj>(fun reference ->
                      integrator.TryPayload(PayloadRef.create reference)
                      |> Option.map box |> Option.defaultValue null)
              "heads" ==>
                  Func<string, string array>(fun stream ->
                      integrator.TryHeads(EventStreamId.create stream) |> List.map EventId.value |> List.toArray)
              "head" ==>
                  Func<string, obj>(fun stream ->
                      integrator.TryHead(EventStreamId.create stream)
                      |> Option.map (EventId.value >> box) |> Option.defaultValue null)
              "allHeads" ==>
                  Func<string array>(fun () -> integrator.AllHeads() |> List.map EventId.value |> List.toArray) ]

    let createLocalWithSegmentCap
        (commonDir: string)
        (writerId: string)
        (segmentCap: int)
        (integrator: ICanonicalIntegrator)
        : IEventStore =
        ProcessEventLog.validateWriterRoot commonDir
        let store =
            construct storeClass
                (createObj
                    [ "directory" ==> Func<string>(fun () -> ProcessEventLog.eventsDirectory commonDir)
                      "lockTarget" ==> Func<string>(fun () -> ProcessEventLog.lockDirectory commonDir)
                      "writerId" ==> writerId
                      "segmentCap" ==> segmentCap
                      "integrator" ==> integratorValue commonDir integrator ])

        { new IEventStore with
            member _.Append events =
                task {
                    let! outcome: obj =
                        store?append (events |> List.map CanonicalEventCodec.toLibraryEvent |> List.toArray)
                    if unbox<bool> outcome?ok then
                        return Ok { Cuts = cutsValue outcome?value?cuts }
                    else return Error(errorValue outcome?error)
                }

            member _.WritePayload content =
                task {
                    let! outcome: obj = store?writePayload content
                    if unbox<bool> outcome?ok then return Ok(PayloadRef.create (unbox<string> outcome?value))
                    else return Error(unbox<string> outcome?error)
                }

            member _.ReadPayload reference =
                task {
                    let! outcome: obj = store?readPayload (PayloadRef.value reference)
                    if unbox<bool> outcome?ok then
                        let value: obj = outcome?value
                        return Ok(if isNull value then None else Some(unbox<byte[]> value))
                    else return Error(unbox<string> outcome?error)
                }

            member _.TryCurrent key =
                let value: obj = store?current key
                if isNull value then None else Some value

            member _.TryEvent identity =
                let value: obj = store?event (EventId.value identity)
                if isNull value then None else Some(CanonicalEventCodec.ofLibraryEvent value)

            member _.TryHeads streamId =
                store?heads (EventStreamId.value streamId)
                |> unbox<string array> |> Array.map EventId.create |> Array.toList

            member _.TryHead streamId =
                let value: obj = store?head (EventStreamId.value streamId)
                if isNull value then None else Some(EventId.create (unbox<string> value))

            member _.AllHeads() =
                store?allHeads () |> unbox<string array> |> Array.map EventId.create |> Array.toList

            member _.ReloadLocal() =
                let outcome: obj = store?reload ()
                if unbox<bool> outcome?ok then Ok() else Error(unbox<string> outcome?error) }

    let createLocal (commonDir: string) (writerId: string) (integrator: ICanonicalIntegrator) : IEventStore =
        createLocalWithSegmentCap commonDir writerId ProcessEventLog.DefaultSegmentCapBytes integrator
