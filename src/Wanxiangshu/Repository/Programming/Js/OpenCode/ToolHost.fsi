namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.Foundation
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Repository.Programming.Js


/// JS-073/JS-074: a generated js-* tool spec — the dynamic counterpart of the
/// static baseSpecs. Built from a generated surface (JS-002); execution goes
/// through JsToolWorkflow (sandbox → staging → preflight → durable facts →
/// commit) and the result renders through JsToolsResult (JS-016).
module JsToolSpec =
    val create:
        factory: HostToolFactory ->
        surface: JsSurface ->
        workspaceRoot: string ->
        persistence: IJsTransactionPersistence option ->
        lifecyclePersistence: IJsLifecyclePersistence option ->
        fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option ->
        rootOf: (string -> string) ->
        settlementOf: (HostToolContext -> Task<DeliverySettlement option>) ->
        epochOf: (HostToolContext -> string) ->
            ToolSpec

    val createWithReviewCheck:
        check: (HostToolContext -> Task<bool>) ->
        factory: HostToolFactory ->
        surface: JsSurface ->
        workspaceRoot: string ->
        persistence: IJsTransactionPersistence option ->
        lifecyclePersistence: IJsLifecyclePersistence option ->
        fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option ->
        rootOf: (string -> string) ->
        settlementOf: (HostToolContext -> Task<DeliverySettlement option>) ->
        epochOf: (HostToolContext -> string) ->
            ToolSpec

    val createWithPlanWriteCheck:
        check: (HostToolContext -> bool) ->
        factory: HostToolFactory ->
        surface: JsSurface ->
        workspaceRoot: string ->
        persistence: IJsTransactionPersistence option ->
        lifecyclePersistence: IJsLifecyclePersistence option ->
        fileAccessObservation: (HostToolContext -> JsExplicitFileRead list -> string list -> Task<unit>) option ->
        rootOf: (string -> string) ->
        settlementOf: (HostToolContext -> Task<DeliverySettlement option>) ->
        epochOf: (HostToolContext -> string) ->
            ToolSpec
