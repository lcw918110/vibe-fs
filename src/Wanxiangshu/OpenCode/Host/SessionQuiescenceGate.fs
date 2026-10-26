namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

module private QuiescenceGateInterop =
    [<Import("SessionQuiescenceGate", "wanxiangli/chat-execution")>]
    let sessionQuiescenceGateCtor: obj = jsNative

    [<Emit("new $0()")>]
    let createGate (ctor: obj) : obj = jsNative

    let newGate () : obj = createGate sessionQuiescenceGateCtor

    let toPermitOption (raw: obj) : QuiescencePermit option =
        if isNull raw then None else Some(unbox<QuiescencePermit> raw)

    let mapFailureReason (reason: string) : QuiescencePermitFailure =
        match reason with
        | "WrongOwner" -> QuiescencePermitFailure.WrongOwner
        | "AlreadyConsumed" -> QuiescencePermitFailure.AlreadyConsumed
        | "Revoked" -> QuiescencePermitFailure.Revoked
        | "Superseded" -> QuiescencePermitFailure.Superseded
        | _ -> QuiescencePermitFailure.NoFreshIdle

    let toPermitResult (decision: obj) : Result<unit, QuiescencePermitFailure> =
        if unbox<bool> (decision?ok) then
            Ok()
        else
            Error(mapFailureReason (string (decision?reason)))

type SessionQuiescenceGate() as this =
    let inner = QuiescenceGateInterop.newGate ()

    member _.BeginProviderAttempt(sessionId: SessionId) : unit =
        inner?BeginProviderAttempt(SessionId.value sessionId) |> ignore

    member _.BeginToolExecution(sessionId: SessionId) : unit =
        inner?BeginToolExecution(SessionId.value sessionId) |> ignore

    member _.EndToolExecution(sessionId: SessionId) : unit =
        inner?EndToolExecution(SessionId.value sessionId) |> ignore

    member _.ObservePhysicalUserMessage(sessionId: SessionId, physicalUserMessageId: PhysicalUserMessageId) : unit =
        inner?ObservePhysicalUserMessage(
            SessionId.value sessionId,
            PhysicalUserMessageId.value physicalUserMessageId
        )
        |> ignore

    member _.ObserveIdle(sessionId: SessionId) : QuiescencePermit =
        unbox<QuiescencePermit> (inner?ObserveIdle(SessionId.value sessionId))

    member _.CaptureCurrentAttempt(sessionId: SessionId) : QuiescencePermit =
        unbox<QuiescencePermit> (inner?CaptureCurrentAttempt(SessionId.value sessionId))

    member _.ObserveIdleFor(observation: QuiescencePermit) : QuiescencePermit option =
        QuiescenceGateInterop.toPermitOption (inner?ObserveIdleFor(box observation))

    member _.TryConsume(permit: QuiescencePermit) : Result<unit, QuiescencePermitFailure> =
        QuiescenceGateInterop.toPermitResult (inner?TryConsume(box permit))

    member _.TryRelease(permit: QuiescencePermit) : Result<unit, QuiescencePermitFailure> =
        QuiescenceGateInterop.toPermitResult (inner?TryRelease(box permit))

    member _.RevokeCurrentAttempt(sessionId: SessionId) : unit =
        inner?RevokeCurrentAttempt(SessionId.value sessionId) |> ignore

    member internal _.LivePermitCount: int = unbox<int> (inner?LivePermitCount)

    member _.DropSession(sessionId: SessionId) : unit =
        inner?DropSession(SessionId.value sessionId) |> ignore

    interface ISessionQuiescenceGate with
        member _.BeginProviderAttempt(sessionId) = this.BeginProviderAttempt(sessionId)
        member _.BeginToolExecution(sessionId) = this.BeginToolExecution(sessionId)
        member _.EndToolExecution(sessionId) = this.EndToolExecution(sessionId)
        member _.ObservePhysicalUserMessage(sessionId, physicalUserMessageId) =
            this.ObservePhysicalUserMessage(sessionId, physicalUserMessageId)
        member _.ObserveIdle(sessionId) = this.ObserveIdle(sessionId)
        member _.TryConsume(permit) = this.TryConsume(permit)
        member _.TryRelease(permit) = this.TryRelease(permit)
        member _.RevokeCurrentAttempt(sessionId) = this.RevokeCurrentAttempt(sessionId)
        member _.DropSession(sessionId) = this.DropSession(sessionId)
