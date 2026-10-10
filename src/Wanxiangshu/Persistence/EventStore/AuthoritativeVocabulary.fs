namespace Wanxiangshu.Persistence.EventStore

open Wanxiangshu.Strength
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Sphinx.V2.Core

[<RequireQualifiedAccess>]
module AuthoritativeEventTypes =
    let private builtins =
        set
            [ // Spine-owned durable envelope/cut vocabulary.
              "JournalEnvelope"
              ProjectionCutTailEvent.EventType
              // Legacy job vocabulary: no in-tree producer or consumer remains;
              // the durable-convergence laws still persist these names.
              "JobRequested"
              "JobAccepted"
              "JobRejected"
              "JobConflictResolved"
              // Domain-owned names, joined from the owning vocabulary contracts.
              yield! JsTransactionEventTypes.all
              yield! CasebookEventTypes.all
              yield! StrengthEventTypes.all
              yield! PlanEventTypes.all
              // Version 1 remains readable for the domain's explicit semantic cut.
              // New transitions use the strict canonical version 2 payload.
              yield! SphinxV2EventTypes.all ]

    let isKnown eventType = Set.contains eventType builtins
