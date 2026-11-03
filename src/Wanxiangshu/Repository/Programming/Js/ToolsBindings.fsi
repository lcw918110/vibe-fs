namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks

/// Process-live delegation seam for the programming surface.
type JsDelegationHost =
    { Delegate: string -> obj -> Task<obj>
      Fission: obj -> Task<obj>
      Horizon: unit -> Task<obj>
      Review: obj -> obj -> Task<obj> } // (findings, note)

module JsToolsBindings =
    val makeReviewMember: host: JsDelegationHost option -> (string * obj)

    val createInvocation:
        root: string ->
        ledger: JsAsIsLedger ->
        observe: (obj -> Task<unit>) option ->
        checkReturn: (obj -> JsFailure option) ->
            obj

    val createApi:
        capabilities: Set<JsCapability> ->
        root: string ->
        file: obj ->
        delegationHost: JsDelegationHost option ->
        askInvoke: (string -> Task<Result<unit, string>>) option ->
            obj

module JsReviewOnly =
    val scopeManagerReviewOnly: check: (unit -> Task<bool>) -> api: obj -> obj
    val scopePlanWriteOnly: check: (unit -> bool) -> api: obj -> obj
    val scopeReviewOnly: check: (unit -> Task<bool>) -> api: obj -> obj
