namespace Wanxiangshu.Repository.Programming.Js

open Fable.Core
open Fable.Core.JsInterop
open System.Threading.Tasks

/// JS runtime owner boundary for bindings and sandbox execution. The mutable
/// as-is write ledger (persisted at each edit/write call) and sandbox failure
/// union remain opaque; observations cross as plain result objects.
[<RequireQualifiedAccess>]
module JsRuntimeSurface =

    type private JsBindingsHandle
        (api: obj, staging: ResizeArray<JsStagedMutation>, readSnapshots: ResizeArray<JsReadSnapshot>) =

        member _.Api = api
        member _.Staging = staging
        member _.ReadSnapshots = readSnapshots

    let createApi (root: string) : obj =
        // DSL-MUTABLE: resource — JS mutation staging buffer handed off in handle
        let staging = ResizeArray<JsStagedMutation>()
        let readSnapshots = ResizeArray<JsReadSnapshot>()

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
                  JsCapability.Join
                  JsCapability.Fission
                  JsCapability.Horizon
                  JsCapability.Advisor
                  JsCapability.Park ]

        let api =
            JsToolsBindings.createApi
                allCapabilities
                root
                staging
                readSnapshots
                (ResizeArray<JsExplicitFileRead>())
                (JsToolsBindings.createLedger None)
                None
                None

        box (JsBindingsHandle(api, staging, readSnapshots))

    let private capabilityOfLabel (label: string) : JsCapability option =
        match label with
        | "Read" -> Some JsCapability.Read
        | "Write" -> Some JsCapability.Write
        | "Edit" -> Some JsCapability.Edit
        | "Glob" -> Some JsCapability.Glob
        | "Grep" -> Some JsCapability.Grep
        | "Move" -> Some JsCapability.Move
        | "Remove" -> Some JsCapability.Remove
        | "Exec" -> Some JsCapability.Exec
        | "Fork" -> Some JsCapability.Fork
        | "Join" -> Some JsCapability.Join
        | "Fission" -> Some JsCapability.Fission
        | "Horizon" -> Some JsCapability.Horizon
        | "Advisor" -> Some JsCapability.Advisor
        | "Park" -> Some JsCapability.Park
        | _ -> None

    let createApiFor (root: string) (permissionLabels: string array) : obj =
        // DSL-MUTABLE: resource — JS mutation staging buffer handed off in handle
        let staging = ResizeArray<JsStagedMutation>()
        let readSnapshots = ResizeArray<JsReadSnapshot>()
        let capabilities = permissionLabels |> Array.choose capabilityOfLabel |> Set.ofArray

        let api =
            JsToolsBindings.createApi
                capabilities
                root
                staging
                readSnapshots
                (ResizeArray<JsExplicitFileRead>())
                (JsToolsBindings.createLedger None)
                None
                None

        box (JsBindingsHandle(api, staging, readSnapshots))

    let api (handle: obj) : obj = (unbox<JsBindingsHandle> handle).Api

    /// Test hook: install a process-live delegation host for one session.
    let installDelegationHost
        (sessionId: string)
        (fork: obj)
        (join: obj)
        (fission: obj)
        (horizon: obj)
        (advisor: obj)
        : unit =
        JsDelegationBridge.install
            sessionId
            { Fork = fun payload -> unbox<Task<obj>> (emitJsExpr (fork, payload) "$0($1)")
              Join = fun () -> unbox<Task<obj>> (emitJsExpr join "$0()")
              Fission = fun payload -> unbox<Task<obj>> (emitJsExpr (fission, payload) "$0($1)")
              Horizon = fun () -> unbox<Task<obj>> (emitJsExpr horizon "$0()")
              Advisor = fun intent view -> unbox<Task<obj>> (emitJsExpr (advisor, intent, view) "$0($1,$2)") }

    let clearDelegationHost (sessionId: string) : unit = JsDelegationBridge.clear sessionId

    let clearDelegationHosts () : unit = JsDelegationBridge.clearAll ()

    /// Test hook: the raw value a program handed over through `js.deliver`.
    /// Test hook: record a value with an already-shaped deliver view.
    let recordDeliveredView (sessionId: string) (value: obj) (view: obj) : unit =
        JsDeliveredValues.recordView sessionId value (if isNull view then None else Some view)

    /// Test hook: identities already handed back through a deliver view.
    let deliveredReturned (sessionId: string) : string array =
        JsDeliveredValues.alreadyReturned sessionId |> Set.toArray

    let clearDeliveredReturned (sessionId: string) : unit =
        JsDeliveredValues.clearReturned sessionId

    /// Test hook: consume the handoff the completion path reads.
    let takeDeliveredHandoffWith (sessionId: string) (assistantProse: string option) : obj =
        match JsDeliveredValues.tryTakeRenderedWith sessionId assistantProse with
        | Some text -> box text
        | None -> null

    let takeDeliveredHandoff (sessionId: string) : obj =
        match JsDeliveredValues.tryTakeRendered sessionId with
        | Some text -> box text
        | None -> null

    let deliveredValue (sessionId: string) : obj = JsDeliveredValues.peek sessionId

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
        (unbox<JsBindingsHandle> handle).ReadSnapshots
        |> Seq.map _.Path
        |> Seq.distinct
        |> Seq.toArray

    let private failureResult failure =
        box
            {| ok = false
               code = JsFailure.code failure
               reason = JsFailure.reason failure |}

    /// Run one model program with a supplied JS-native API object.
    let run
        (baseClassSource: string)
        (modelSource: string)
        (apiValue: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        : Task<obj> =
        task {
            let! handle =
                Wanxiangshu.Process.JsSandbox.runSurface baseClassSource modelSource apiValue deadlineMs deadlineEpochMs

            let! result = Wanxiangshu.Process.JsSandbox.awaitResult handle outputBoundBytes

            return
                match result with
                | Ok value -> box {| ok = true; value = value |}
                | Error failure -> failureResult failure
        }

    /// Start a sandbox program without awaiting it to completion, exposing the
    /// in-flight result promise and a mutable deadline reset (JS-054.1). The
    /// promise resolves to the same `{ ok, value | code, reason }` shape as
    /// `run`; `resetDeadline(epochMs)` re-arms the async deadline so a caller
    /// may keep an in-flight program alive across calls.
    let start
        (baseClassSource: string)
        (modelSource: string)
        (apiValue: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        : Task<obj> =
        task {
            let! handle =
                Wanxiangshu.Process.JsSandbox.runSurface baseClassSource modelSource apiValue deadlineMs deadlineEpochMs

            let resultTask =
                task {
                    let! result = Wanxiangshu.Process.JsSandbox.awaitResult handle outputBoundBytes

                    return
                        match result with
                        | Ok value -> box {| ok = true; value = value |}
                        | Error failure -> failureResult failure
                }

            return
                createObj
                    [ "promise" ==> resultTask
                      "resetDeadline"
                      ==> fun (epochMs: int64) -> Wanxiangshu.Process.JsSandbox.resetDeadline handle epochMs ]
        }
