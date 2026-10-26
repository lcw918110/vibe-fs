namespace Wanxiangshu.Repository.Programming.Js

open Fable.Core
open Fable.Core.JsInterop
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// JS runtime owner boundary for bindings and sandbox execution. The mutable
/// as-is write ledger (persisted at each edit/write call) and sandbox failure
/// union remain opaque; observations cross as plain result objects.
[<RequireQualifiedAccess>]
module JsRuntimeSurface =

    type private JsBindingsHandle
        (api: obj, staging: ResizeArray<JsStagedMutation>, reads: ResizeArray<JsExplicitFileRead>) =

        member _.Api = api
        member _.Staging = staging
        member _.Reads = reads

    let createApi (root: string) : obj =
        // DSL-MUTABLE: resource — JS mutation staging buffer handed off in handle
        let staging = ResizeArray<JsStagedMutation>()
        let reads = ResizeArray<JsExplicitFileRead>()

        let allCapabilities =
            set
                [ JsCapability.Read
                  JsCapability.Write
                  JsCapability.Glob
                  JsCapability.Grep
                  JsCapability.Move
                  JsCapability.Remove
                  JsCapability.Exec
                  JsCapability.Fork
                  JsCapability.Resume
                  JsCapability.Commission
                  JsCapability.DevOps
                  JsCapability.Fission
                  JsCapability.Horizon
                  JsCapability.Park ]

        let api =
            JsToolsBindings.createApi allCapabilities root staging reads (JsToolsBindings.createLedger None) None None

        box (JsBindingsHandle(api, staging, reads))

    let createApiFor (root: string) (permissionLabels: string array) : obj =
        // DSL-MUTABLE: resource — JS mutation staging buffer handed off in handle
        let staging = ResizeArray<JsStagedMutation>()
        let reads = ResizeArray<JsExplicitFileRead>()

        let capabilities =
            permissionLabels |> Array.choose JsCapability.ofLabel |> Set.ofArray

        let api =
            JsToolsBindings.createApi capabilities root staging reads (JsToolsBindings.createLedger None) None None

        box (JsBindingsHandle(api, staging, reads))

    let api (handle: obj) : obj = (unbox<JsBindingsHandle> handle).Api

    /// Test hook: install a process-live delegation host for one session. The
    /// delegate callback takes `(name, payload, carry)`.
    let installDelegationHost
        (sessionId: string)
        (delegation: obj)
        (fission: obj)
        (horizon: obj)
        : unit =
        JsDelegationBridge.install
            sessionId
            { Delegate = fun name payload -> unbox<Task<obj>> (emitJsExpr (delegation, name, payload) "$0($1,$2)")
              Fission = fun payload -> unbox<Task<obj>> (emitJsExpr (fission, payload) "$0($1)")
              Horizon = fun () -> unbox<Task<obj>> (emitJsExpr horizon "$0()")
              // The test hook installs no review host: `this.review` is a
              // production-only member wired in ToolRegistry. A program that
              // calls it under this hook gets the unavailable envelope.
              Review =
                fun _ ->
                    task {
                        return
                            box
                                {| ok = false
                                   code = "DELEGATION_UNAVAILABLE"
                                   reason = "review is not wired in this runtime surface" |}
                    } }

    let clearDelegationHost (sessionId: string) : unit = JsDelegationBridge.clear sessionId


    /// Test hook: record a carry array for presentation.
    let recordDeliveredView (sessionId: string) (view: obj) : unit =
        JsDeliveredValues.recordView sessionId (if isNull view then None else Some view)

    /// Test hook: record a delivery carrying the exact call fence that
    /// produced it, so old-terminal/old-consumer fencing is observable.
    let recordFencedDeliveredView (sessionId: string) (providerRun: string) (toolCallId: string) (view: obj) : unit =
        JsDeliveredValues.recordFencedView
            sessionId
            (ProviderRunIdentity.create providerRun)
            (ToolCallId.create toolCallId)
            (if isNull view then None else Some view)

    /// Test hook: identities already handed back through a deliver view.
    let deliveredReturned (sessionId: string) : string array =
        JsDeliveredValues.alreadyReturned sessionId |> Set.toArray

    let clearDeliveredReturned (sessionId: string) : unit =
        JsDeliveredValues.clearReturned sessionId

    /// Test hook: consume the handoff the completion path reads.
    let takeDeliveredHandoffWith (sessionId: string) (assistantProse: string option) : obj =
        match JsDeliveredValues.tryTakeMaterialWith sessionId assistantProse with
        | Some material -> box (ReturnedMaterial.render material)
        | None -> null

    /// Test hook: consume the handoff only when the cell carries this exact
    /// fence — the provider run AND the tool call id. A cell of another call
    /// stays untouched and the returned cursor does not move.
    let takeDeliveredHandoffForCall
        (sessionId: string)
        (providerRun: string)
        (toolCallId: string)
        (assistantProse: string option)
        : obj =
        match
            JsDeliveredValues.tryTakeMaterialForCall
                sessionId
                (ProviderRunIdentity.create providerRun)
                (ToolCallId.create toolCallId)
                assistantProse
        with
        | Some material -> box (ReturnedMaterial.render material)
        | None -> null

    let takeDeliveredHandoff (sessionId: string) : obj =
        match JsDeliveredValues.tryTakeMaterial sessionId with
        | Some material -> box (ReturnedMaterial.render material)
        | None -> null

    /// Test hook: observe whether the current cell belongs to this very
    /// call. Nothing is taken and the returned cursor does not move.
    let deliveredCellIsMine (sessionId: string) (providerRun: string) (toolCallId: string) : bool =
        JsDeliveredValues.ownsCell sessionId (ProviderRunIdentity.create providerRun) (ToolCallId.create toolCallId)

    /// Test hook: the raw value handed over through delivery,
    /// as it stands right now — a delivery or a staged render cache. This is
    /// the staged view: presentation only. The delivery evidence is the
    /// fenced cell, read by `deliveredFenceOf` and consumed by
    /// `takeDeliveredHandoffForCall`.
    let deliveredValue (sessionId: string) : obj = JsDeliveredValues.peekCached sessionId

    let deliveredViewOf (sessionId: string) : obj =
        match JsDeliveredValues.peekView sessionId with
        | Some view -> view
        | None -> null

    let deliveredFenceOf (sessionId: string) : obj =
        match JsDeliveredValues.peekFence sessionId with
        | Some fence ->
            createObj
                [ "providerRun" ==> ProviderRunIdentity.value fence.ProviderRun
                  "toolCallId" ==> ToolCallId.value fence.ToolCallId ]
        | None -> null

    let clearDeliveredValue (sessionId: string) : unit = JsDeliveredValues.clear sessionId

    let stagedCount (handle: obj) : int =
        (unbox<JsBindingsHandle> handle).Staging.Count

    let stagedKinds (handle: obj) : string array =
        (unbox<JsBindingsHandle> handle).Staging
        |> Seq.map (function
            | JsStagedMutation.Rewrite _ -> "Rewrite"
            | JsStagedMutation.Create _ -> "Create"
            | JsStagedMutation.Remove _ -> "Remove"
            | JsStagedMutation.Move _ -> "Move")
        |> Seq.toArray

    let readPaths (handle: obj) : string array =
        (unbox<JsBindingsHandle> handle).Reads
        |> Seq.map _.Path
        |> Seq.distinct
        |> Seq.toArray

    let private failureResult failure =
        box
            {| ok = false
               code = JsFailure.code failure
               reason = JsFailure.reason failure |}

    /// Isolated physical execution for semantic verification of a generated surface.
    let run
        (baseClassSource: string)
        (modelSource: string)
        (apiValue: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        : Task<obj> =
        task {
            let sessionId = "surface-" + System.Guid.NewGuid().ToString("N")

            let options =
                createObj
                    [ "name" ==> "run"
                      "program" ==> modelSource
                      "baseClass" ==> baseClassSource
                      "intent"
                      ==> "Wait for this isolated verification program to finish and preserve its real result."
                      "api" ==> apiValue
                      "capabilities" ==> [| "Read"; "Write"; "Exec" |]
                      "root" ==> "."
                      "executionBudgetMs" ==> max 1 deadlineMs
                      "outputBoundBytes" ==> outputBoundBytes ]

            let session = JsPrograms.forSession sessionId
            let! result = unbox<Task<obj>> (session?execute (options))

            if string result?state = "completed" then
                return createObj [ "ok" ==> true; "value" ==> JS.JSON.stringify result?value ]
            else
                let code = string result?code

                let failure =
                    match code with
                    | "PROGRAM_RESULT_NOT_SERIALIZABLE" -> JsFailure.InvalidReturnValue
                    | "PROGRAM_EXECUTION_LIMIT" -> JsFailure.ProgramTimeout
                    | _ -> JsFailure.ofWire code (string result?reason)

                return failureResult failure
        }
