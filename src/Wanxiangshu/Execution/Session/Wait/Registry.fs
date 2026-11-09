namespace Wanxiangshu.Execution.Session.Wait

open System
open Fable.Core
open Fable.Core.JsInterop

/// 实现唯一来源：wanxiangli/program 的注册表与诊断运行时（CUT-21）。
/// 此文件是类型桥：主仓 CausalWaitRuntime / IWaitObserver / IWaitSnapshotReader /
/// IWaitDiagnosticSink 契约不变，调用方零改动；F# 词汇与库 JS 描述符在此双向转换。
module private WaitCodec =

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private stringOf (value: obj) =
        if isNullish value then "" else string value

    let private pairsOf (value: obj) : (string * string) list =
        if isNullish value then
            []
        else
            let keys: string array = emitJsExpr value "Object.keys($0)"
            keys |> Array.toList |> List.map (fun key -> key, stringOf (property value key))

    let private objectOfPairs (pairs: (string * string) list) : obj =
        createObj (pairs |> List.map (fun (key, value) -> key, box value))

    let private ownerOf (value: obj) : CausalOwnerRef =
        { Kind = stringOf (property value "kind")
          Identity = pairsOf (property value "identity") }

    let private ownerObject (owner: CausalOwnerRef) : obj =
        createObj [ "kind", box owner.Kind; "identity", box (objectOfPairs owner.Identity) ]

    let private producerOf (value: obj) : CausalProducerRef =
        let kind = stringOf (property value "kind")

        if kind = "workflow" then
            WorkflowProducer(ownerOf (property value "owner"))
        else
            ExternalProducer(stringOf (property value "producerKind"), pairsOf (property value "identity"))

    let private producerObject (producer: CausalProducerRef) : obj =
        match producer with
        | WorkflowProducer owner -> createObj [ "kind", box "workflow"; "owner", box (ownerObject owner) ]
        | ExternalProducer(kind, identity) ->
            createObj
                [ "kind", box "external"
                  "producerKind", box kind
                  "identity", box (objectOfPairs identity) ]

    let private escapeOf (value: obj) : WaitEscape =
        match stringOf (property value "kind") with
        | "deadlineAt" -> WaitEscape.DeadlineAt(DateTimeOffset.Parse(stringOf (property value "at")))
        | "cancelledBy" -> WaitEscape.CancelledBy(ownerOf (property value "owner"))
        | "processLifetime" -> WaitEscape.ProcessLifetime
        | "sessionLifetime" -> WaitEscape.SessionLifetime
        | "openEndedExternal" -> WaitEscape.OpenEndedExternal
        // A wait escape decides when a wait may end, so an unrecognised kind is not a
        // harmless default: silently reading it as OpenEndedExternal would let a durable
        // record written by a newer shape end a wait that should have kept running. Surface
        // already refuses unknown kinds; this decoder has to agree with it.
        | unknown -> invalidArg "escape" ("unknown wait escape: " + unknown)

    let private escapeObject (escape: WaitEscape) : obj =
        match escape with
        | DeadlineAt at -> createObj [ "kind", box "deadlineAt"; "at", box (at.ToString("O")) ]
        | CancelledBy owner -> createObj [ "kind", box "cancelledBy"; "owner", box (ownerObject owner) ]
        | ProcessLifetime -> createObj [ "kind", box "processLifetime" ]
        | SessionLifetime -> createObj [ "kind", box "sessionLifetime" ]
        | OpenEndedExternal -> createObj [ "kind", box "openEndedExternal" ]

    let waitOf (value: obj) : DiagnosticWait =
        { WaitKind = stringOf (property value "waitKind")
          Owner = ownerOf (property value "owner")
          Subject = pairsOf (property value "subject")
          Producer = producerOf (property value "producer")
          Escapes =
            (unbox<obj array> (property value "escapes"))
            |> Array.toList
            |> List.map escapeOf
          Source = stringOf (property value "source") }

    let waitObject (wait: DiagnosticWait) : obj =
        createObj
            [ "waitKind", box wait.WaitKind
              "owner", box (ownerObject wait.Owner)
              "subject", box (objectOfPairs wait.Subject)
              "producer", box (producerObject wait.Producer)
              "escapes", box (wait.Escapes |> List.map escapeObject |> Array.ofList)
              "source", box wait.Source ]

    let exitOf (name: string) =
        match name with
        | "WaitResolved" -> DiagnosticWaitExit.WaitResolved
        | "WaitFailed" -> DiagnosticWaitExit.WaitFailed
        | "WaitCancelled" -> DiagnosticWaitExit.WaitCancelled
        | "WaitTimedOut" -> DiagnosticWaitExit.WaitTimedOut
        | "WaitDisposed" -> DiagnosticWaitExit.WaitDisposed
        | unknown -> invalidArg "exit" ("unknown wait exit: " + unknown)

    let exitName (exit: DiagnosticWaitExit) =
        match exit with
        | DiagnosticWaitExit.WaitResolved -> "WaitResolved"
        | DiagnosticWaitExit.WaitFailed -> "WaitFailed"
        | DiagnosticWaitExit.WaitCancelled -> "WaitCancelled"
        | DiagnosticWaitExit.WaitTimedOut -> "WaitTimedOut"
        | DiagnosticWaitExit.WaitDisposed -> "WaitDisposed"

    let private transitionOf (value: obj) : WaitTransition =
        { Sequence = unbox<int64> (property value "sequence")
          Kind =
            match stringOf (property value "kind") with
            | "Entered" -> WaitTransitionKind.Entered
            | _ -> WaitTransitionKind.Left
          Wait = waitOf (property value "wait")
          Exit =
            let exit = property value "exit"

            (if isNullish exit then
                 None
             else
                 Some(exitOf (stringOf exit))) }

    let private transitionObject (transition: WaitTransition) : obj =
        createObj
            [ "sequence", box transition.Sequence
              "kind",
              box (
                  match transition.Kind with
                  | WaitTransitionKind.Entered -> "Entered"
                  | WaitTransitionKind.Left -> "Left"
              )
              "wait", box (waitObject transition.Wait)
              "exit",
              box (
                  match transition.Exit with
                  | Some exit -> box (exitName exit)
                  | None -> null
              ) ]

    let snapshotOf (value: obj) : DiagnosticWaitSnapshot =
        { Active = (unbox<obj array> (property value "active")) |> Array.toList |> List.map waitOf
          History =
            (unbox<obj array> (property value "history"))
            |> Array.toList
            |> List.map transitionOf
          Sequence = int64 (unbox<int> (property value "sequence")) }

    let snapshotObject (snapshot: DiagnosticWaitSnapshot) : obj =
        createObj
            [ "active", box (snapshot.Active |> List.map waitObject |> Array.ofList)
              "history", box (snapshot.History |> List.map transitionObject |> Array.ofList)
              "sequence", box snapshot.Sequence ]

/// 库容器绑定：wanxiangli/program 的具名 Wait 导出。
module private WaitLibrary =

    [<Import("Wait", "wanxiangli/program")>]
    let library: obj = jsNative

type CausalWaitRegistry private (libraryRegistry: obj) =

    new(?historyCapacity: int) =
        let capacity: obj =
            match historyCapacity with
            | Some value -> box value
            | None -> null

        CausalWaitRegistry(WaitLibrary.library?createRegistry (capacity))

    member _.HistoryCapacity: int =
        unbox<int> (WaitLibrary.library?historyCapacity (libraryRegistry))

    interface IWaitObserver with
        member _.Enter(wait: DiagnosticWait) : IWaitLease =
            let observer = WaitLibrary.library?observerCapability (libraryRegistry)

            let lease: obj =
                WaitLibrary.library?observerEnter (observer, WaitCodec.waitObject wait)

            { new IWaitLease with
                member _.MarkExit(exit: DiagnosticWaitExit) : unit =
                    WaitLibrary.library?markExit (lease, WaitCodec.exitName exit) |> ignore

                member _.Dispose() : unit =
                    WaitLibrary.library?dispose (lease) |> ignore }

    interface IWaitSnapshotReader with
        member _.Snapshot() : DiagnosticWaitSnapshot =
            WaitCodec.snapshotOf (WaitLibrary.library?snapshot (libraryRegistry))

type CausalWaitRuntime(?historyCapacity: int) =
    // 库 registry 只创建一次：Observer / SnapshotReader / BindDiagnosticTarget
    // 共享同一个库实例（与主仓原单 registry 语义一致）。
    let capacity: obj =
        match historyCapacity with
        | Some value -> box value
        | None -> null

    let libraryRegistry: obj = WaitLibrary.library?createRegistry (capacity)

    let observer =
        { new IWaitObserver with
            member _.Enter(wait: DiagnosticWait) : IWaitLease =
                let observerCapability = WaitLibrary.library?observerCapability (libraryRegistry)

                let lease: obj =
                    WaitLibrary.library?observerEnter (observerCapability, WaitCodec.waitObject wait)

                { new IWaitLease with
                    member _.MarkExit(exit: DiagnosticWaitExit) : unit =
                        WaitLibrary.library?markExit (lease, WaitCodec.exitName exit) |> ignore

                    member _.Dispose() : unit =
                        WaitLibrary.library?dispose (lease) |> ignore } }

    let reader =
        { new IWaitSnapshotReader with
            member _.Snapshot() : DiagnosticWaitSnapshot =
                WaitCodec.snapshotOf (WaitLibrary.library?snapshot (libraryRegistry)) }

    member _.BindDiagnosticTarget(target: IWaitDiagnosticSink) : bool =
        let sink: obj =
            createObj [ "Publish", box (fun (snapshot: obj) -> target.Publish(WaitCodec.snapshotOf snapshot)) ]

        unbox<bool> (WaitLibrary.library?bindDiagnosticTarget (libraryRegistry, sink))

    member _.Observer: IWaitObserver = observer

    member _.SnapshotReader: IWaitSnapshotReader = reader

    member _.HistoryCapacity: int =
        unbox<int> (WaitLibrary.library?historyCapacity (libraryRegistry))

/// 进程内单例运行时（与主仓原语义一致）。
module CausalWaitProcess =

    let private localRuntime = CausalWaitRuntime()

    let local () : CausalWaitRuntime = localRuntime
