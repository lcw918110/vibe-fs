namespace Wanxiangshu.Interaction.Dispatch

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type PromptPhysicalOutcome =
    | Accepted of PhysicalUserMessageId
    | Rejected of string

/// It carries no business state: all callbacks, waiters, concurrency gate and exact-once
/// physical confirmation lifecycle are owned by wanxiangli/dispatch (PromptPhysicalAcceptance).
module PromptPhysicalAcceptance =

    [<Import("promptPhysicalAcceptance", "wanxiangli/dispatch")>]
    let private pa: obj = jsNative

    [<Emit("$0 && $0.Dispose ? $0.Dispose() : ($0 && $0.dispose ? $0.dispose() : undefined)")>]
    let private callDispose (sub: obj) : unit = jsNative

    [<Emit("Array.isArray($0) ? $0[0] : $0[0]")>]
    let private tupleFst (t: obj) : obj = jsNative

    [<Emit("Array.isArray($0) ? $0[1] : $0[1]")>]
    let private tupleSnd (t: obj) : obj = jsNative

    let register (promptKey: PromptKey) (callback: PhysicalUserMessageId -> unit) : IDisposable =
        let cb = fun (pid: string) -> callback (PhysicalUserMessageId.create pid)
        let rawSub = pa?register (PromptKey.value promptKey, cb)

        { new IDisposable with
            member _.Dispose() = callDispose rawSub }

    let cancel (promptKey: PromptKey) : unit =
        pa?cancel (PromptKey.value promptKey) |> ignore

    let accepted (promptKey: PromptKey) (physicalUserMessageId: PhysicalUserMessageId) : unit =
        pa?accepted (PromptKey.value promptKey, PhysicalUserMessageId.value physicalUserMessageId)
        |> ignore

    let rejected (promptKey: PromptKey) (reason: string) : unit =
        pa?rejected (PromptKey.value promptKey, reason) |> ignore

    let internal beginConfirmation
        (promptKey: PromptKey)
        (timeoutMs: int option)
        : Task<PromptPhysicalOutcome option> * IDisposable =
        let pair: obj = pa?beginConfirmation (PromptKey.value promptKey, timeoutMs)
        let rawTask = unbox<Task<obj option>> (tupleFst pair)
        let rawSub = tupleSnd pair

        let subscription =
            { new IDisposable with
                member _.Dispose() = callDispose rawSub }

        let convertedTask =
            task {
                let! (rawOpt: obj option) = rawTask

                match rawOpt with
                | Some raw when not (isNull raw) && unbox<string> raw?kind = "Accepted" ->
                    let pid = unbox<string> raw?physicalUserMessageId
                    return Some(PromptPhysicalOutcome.Accepted(PhysicalUserMessageId.create pid))
                | Some raw when not (isNull raw) ->
                    let reason = unbox<string> raw?reason
                    return Some(PromptPhysicalOutcome.Rejected reason)
                | _ -> return None
            }

        convertedTask, subscription

    let awaitConfirmation (promptKey: PromptKey) (timeoutMs: int option) : Task<PromptPhysicalOutcome option> =
        beginConfirmation promptKey timeoutMs |> fst
