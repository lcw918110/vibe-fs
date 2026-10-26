namespace Wanxiangshu.Repository.Programming.Js

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Process-live delivered-view cell, keyed by session.
/// The state, fence matching and cursor deduplication are owned by `wanxiangli/delivery`.
/// This module is a thin F# type adaptation layer preserving 100% binary/signature compatibility.
module JsDeliveredValues =

    type DeliveryFence =
        { ProviderRun: Wanxiangshu.Foundation.Identity.ProviderRunIdentity
          ToolCallId: Wanxiangshu.Foundation.Identity.ToolCallId }

    type ReturnedPiece = { Key: string; Text: string }

    [<Import("jsDeliveredValues", "wanxiangli/delivery")>]
    let private store: obj = jsNative

    let alreadyReturned (sessionId: string) : Set<string> =
        unbox<string array> (store?alreadyReturned sessionId) |> Set.ofArray

    let recordView (sessionId: string) (view: obj option) : unit =
        store?recordView (sessionId, view |> Option.defaultValue null) |> ignore

    let recordFencedView
        (sessionId: string)
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        (view: obj option)
        : unit =
        let run = ProviderRunIdentity.value providerRun
        let call = ToolCallId.value toolCallId
        let viewObj = view |> Option.defaultValue null
        store?recordFencedView (sessionId, run, call, viewObj) |> ignore

    let take (sessionId: string) : obj = store?take sessionId

    let peek (sessionId: string) : obj = store?peek sessionId

    let peekCached (sessionId: string) : obj = store?peekCached sessionId

    let select (sessionId: string) (view: obj option) (already: Set<string>) : ReturnedPiece list =
        let viewObj = view |> Option.defaultValue null
        let alreadyArr = already |> Set.toArray
        let pieces: obj array = unbox (store?select (sessionId, viewObj, alreadyArr))
        pieces |> Array.toList |> List.map (fun p -> { Key = string p?key; Text = string p?text })

    let private fenceObjOf (fence: DeliveryFence option) : obj =
        match fence with
        | Some f ->
            createObj
                [ "providerRun" ==> ProviderRunIdentity.value f.ProviderRun
                  "toolCallId" ==> ToolCallId.value f.ToolCallId ]
        | None -> null

    let materialize
        (sessionId: string)
        (carry: obj)
        (fence: DeliveryFence option)
        (assistant: string option)
        : string =
        let fenceObj = fenceObjOf fence
        let assistantStr = assistant |> Option.defaultValue ""
        let res: obj = store?materialize (sessionId, carry, fenceObj, assistantStr)
        let body = unbox<string array> res?body |> Array.toList
        let carryList = unbox<string array> res?carry |> Array.toList
        ReturnedMaterial.encode body carryList

    let private encodeHandoffResult (res: obj) : string option =
        if isNull res || jsTypeof res = "undefined" then None
        else
            let body = unbox<string array> res?body |> Array.toList
            let carryList = unbox<string array> res?carry |> Array.toList
            Some (ReturnedMaterial.encode body carryList)

    let tryTakeMaterialWith (sessionId: string) (assistant: string option) : string option =
        let assistantObj = assistant |> Option.toObj
        let res: obj = store?tryTakeMaterialWith (sessionId, assistantObj)
        encodeHandoffResult res

    let tryTakeMaterialForCall
        (sessionId: string)
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        (assistant: string option)
        : string option =
        let run = ProviderRunIdentity.value providerRun
        let call = ToolCallId.value toolCallId
        let assistantObj = assistant |> Option.toObj
        let res: obj = store?tryTakeMaterialForCall (sessionId, run, call, assistantObj)
        encodeHandoffResult res

    let ownsCell
        (sessionId: string)
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        : bool =
        let run = ProviderRunIdentity.value providerRun
        let call = ToolCallId.value toolCallId
        unbox<bool> (store?ownsCell (sessionId, run, call))

    let tryTakeWitnessMaterial
        (sessionId: string)
        (providerRun: ProviderRunIdentity)
        (toolCallId: ToolCallId)
        (material: string)
        (assistant: string)
        : string option =
        let run = ProviderRunIdentity.value providerRun
        let call = ToolCallId.value toolCallId
        let bodyTexts = ReturnedMaterial.body material |> List.toArray
        let carryTexts = ReturnedMaterial.carry material |> List.toArray
        let res: obj = store?tryTakeWitnessMaterial (sessionId, run, call, bodyTexts, carryTexts, assistant)
        encodeHandoffResult res

    let tryTakeMaterial (sessionId: string) : string option =
        let res: obj = store?tryTakeMaterial sessionId
        encodeHandoffResult res

    let tryPeekMaterial (sessionId: string) : string option =
        let res: obj = store?tryPeekMaterial sessionId
        encodeHandoffResult res

    let tryPeekMaterialWith (sessionId: string) (assistant: string option) : string option =
        let assistantObj = assistant |> Option.toObj
        let res: obj = store?tryPeekMaterialWith (sessionId, assistantObj)
        encodeHandoffResult res

    let peekView (sessionId: string) : obj option =
        let res: obj = store?peekView sessionId
        if isNull res || jsTypeof res = "undefined" then None else Some res

    let peekFence (sessionId: string) : DeliveryFence option =
        let res: obj = store?peekFence sessionId
        if isNull res || jsTypeof res = "undefined" then None
        else
            Some
                { ProviderRun = ProviderRunIdentity.create (string res?providerRun)
                  ToolCallId = ToolCallId.create (string res?toolCallId) }

    let clear (sessionId: string) : unit = store?clear sessionId |> ignore

    let clearReturned (sessionId: string) : unit = store?clearReturned sessionId |> ignore

    let clearAll () : unit = store?clearAll () |> ignore
