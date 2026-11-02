namespace Wanxiangshu.Persistence.EventStore

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module IntegratorEngine =
    [<Import("LocalIntegrator", "wanxiangli/local-eventstore")>]
    let private integratorClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (_class: obj) (_options: obj) : obj = jsNative

    let private nativeResult convert =
        function
        | Ok value -> createObj [ "ok" ==> true; "value" ==> convert value ]
        | Error reason -> createObj [ "ok" ==> false; "error" ==> reason ]

    let private unitResult (outcome: obj) =
        if unbox<bool> outcome?ok then
            Ok()
        else
            Error(unbox<string> outcome?error)

    let private ruleValue (rule: IntegrationRule) =
        createObj
            [ "name" ==> rule.Name
              "initial" ==> rule.Initial
              "accepts"
              ==> Func<obj, bool>(CanonicalEventCodec.ofLibraryEvent >> rule.Accepts)
              "faultScope"
              ==> Func<obj, string>(CanonicalEventCodec.ofLibraryEvent >> rule.FaultScope)
              "integrate"
              ==> Func<obj, obj, obj>(fun current event ->
                  rule.Integrate current (CanonicalEventCodec.ofLibraryEvent event)
                  |> nativeResult id)
              "planCut"
              ==> Func<obj, obj, string, bool, obj>(fun current event reason afterFullReplay ->
                  rule.PlanCut current (CanonicalEventCodec.ofLibraryEvent event) reason afterFullReplay
                  |> nativeResult (fun plan -> createObj [ "resetJson" ==> plan.ResetJson ]))
              "applyCut"
              ==> Func<obj, string, obj>(fun current resetJson -> rule.ApplyCut current resetJson |> nativeResult id) ]

    let private cutValue (value: obj) : SemanticCut =
        { Rule = unbox<string> value?rule
          FailedEventId = EventId.create (unbox<string> value?failedEventId)
          Reason = unbox<string> value?reason
          CutEventId = EventId.create (unbox<string> value?cutEventId) }

    let private preparedValue (value: obj) : PreparedIntegration =
        { DurableEvents =
            unbox<obj array> value?durableEvents
            |> Array.map CanonicalEventCodec.ofLibraryEvent
            |> Array.toList
          Cuts = unbox<obj array> value?cuts |> Array.map cutValue |> Array.toList
          Commit = fun () -> value?commit () }

    let create (rules: IntegrationRule list) (isEventTypeKnown: string -> bool) : ICanonicalIntegrator =
        let integrator =
            construct
                integratorClass
                (createObj
                    [ "rules" ==> (rules |> List.map ruleValue |> List.toArray)
                      "isKnownEventType" ==> Func<string, bool>(isEventTypeKnown)
                      "formatStorageError"
                      ==> Func<obj, string>(CanonicalEventCodec.invalidOfLibraryError >> sprintf "%A") ])

        { new ICanonicalIntegrator with
            member _.ReloadLocal commonDir =
                integrator?reload (Func<string>(fun () -> ProcessEventLog.eventsDirectory commonDir))
                |> unitResult

            member _.PrepareLive events =
                let outcome: obj =
                    integrator?prepare (events |> List.map CanonicalEventCodec.toLibraryEvent |> List.toArray)

                if unbox<bool> outcome?ok then
                    Ok(preparedValue outcome?value)
                else
                    Error(unbox<string> outcome?error)

            member _.IsEventTypeKnown eventType = integrator?isKnownEventType eventType

            member _.TryCurrent key =
                let value: obj = integrator?current key
                if isNull value then None else Some value

            member _.TryEvent eventId =
                let value: obj = integrator?event (EventId.value eventId)

                if isNull value then
                    None
                else
                    Some(CanonicalEventCodec.ofLibraryEvent value)

            member _.TryPayload payloadRef =
                let value: obj = integrator?payload (PayloadRef.value payloadRef)
                if isNull value then None else Some(unbox<byte[]> value)

            member _.TryHeads streamId =
                integrator?heads (EventStreamId.value streamId)
                |> unbox<string array>
                |> Array.map EventId.create
                |> Array.toList

            member _.TryHead streamId =
                let value: obj = integrator?head (EventStreamId.value streamId)

                if isNull value then
                    None
                else
                    Some(EventId.create (unbox<string> value))

            member _.AllHeads() =
                integrator?allHeads ()
                |> unbox<string array>
                |> Array.map EventId.create
                |> Array.toList }
