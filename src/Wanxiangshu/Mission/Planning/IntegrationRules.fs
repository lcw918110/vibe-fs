namespace Wanxiangshu.Mission.Planning

open Wanxiangshu.Persistence.EventStore

[<RequireQualifiedAccess>]
module PlanIntegrationRules =

    let planRule: IntegrationRule =
        { Name = "Plan"
          Initial = box PlanFold.initialState
          FaultScope = fun envelope -> EventStreamId.value envelope.StreamId
          Accepts = fun envelope -> PlanEventTypes.isPlanEvent envelope.EventType
          Integrate =
            fun current envelope ->
                match PlanEventCodec.tryDecodeEnvelope envelope with
                | Error error -> Error error
                | Ok event ->
                    let currentState = unbox<PlanState> current
                    PlanFold.applyEvent event currentState |> Result.map box
          PlanCut = fun _ _ _ _ -> Ok { ResetJson = "{}" }
          ApplyCut = fun current _ -> Ok current }

    let rules: IntegrationRule list = [ planRule ]
