namespace Wanxiangshu.Execution.Session.Wait

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity

/// 实现唯一来源：wanxiangli/program 的泛化 CompletionMailbox（CUT-21）。
/// 此文件是主仓特化桥：AgentHandleId / PtyJoinItem 原样经库往返
/// （F# 值即 JS 对象），唤醒原因在此转回主仓 MailboxWakeReason 词汇。
module private MailboxLibrary =

    [<Import("Wait", "wanxiangli/program")>]
    let library: obj = jsNative

type CompletionMailbox(gate: obj) =

    // gate 仅为生命周期锚（与主仓原语义一致）；库实例不消费它。
    let view: obj = MailboxLibrary.library?completionMailbox ()

    let decodeInterrupt (interrupt: string) : JoinInterruptReason =
        match interrupt with
        | "OperatorAbort" -> JoinInterruptReason.OperatorAbort
        | "UserMessageArrived" -> JoinInterruptReason.UserMessageArrived
        | "DeadlineExpired" -> JoinInterruptReason.DeadlineExpired
        // The library names exactly these three. An unknown name is not a timeout:
        // reading it as one would end a wait that the producer did not expire.
        | unknown -> invalidArg "interrupt" ("unknown interrupt reason: " + unknown)

    let wakeOf (value: obj) : MailboxWakeReason =
        match string value with
        | "CompletionMayBeAvailable" -> MailboxWakeReason.CompletionMayBeAvailable
        | "MailboxCancelled" -> MailboxWakeReason.MailboxCancelled
        | _ ->
            // { LocalInterrupt: "OperatorAbort" | ... }
            string (emitJsExpr (value, "LocalInterrupt") "$0[$1]")
            |> decodeInterrupt
            |> MailboxWakeReason.LocalInterrupt

    member _.PulseAgentHandle(handle: AgentHandleId) = view?pulseAgent (handle) |> ignore

    member _.PublishPtyCompletion(item: PtyJoinItem) = view?publishPty (item) |> ignore

    member _.PulseWake() = view?pulseWake () |> ignore

    member _.WaitForWake() : Task<MailboxWakeReason> =
        let pending: Task<obj> = unbox<Task<obj>> (view?waitForWake ())

        task {
            let! reason = pending
            return wakeOf reason
        }

    member _.WaitForSignal(interrupt: Task<JoinInterruptReason>) : Task<MailboxWakeReason> =
        let pending: Task<obj> = unbox<Task<obj>> (view?waitForSignal (interrupt))

        task {
            let! reason = pending
            return wakeOf reason
        }

    member _.DrainAgentWakes(maxCount: int) : AgentHandleId list =
        (unbox<obj array> (view?drainAgents (maxCount)))
        |> Array.toList
        |> List.map unbox<AgentHandleId>

    member _.DrainPtyCompletions(maxCount: int) : PtyJoinItem list =
        (unbox<obj array> (view?drainPtys (maxCount)))
        |> Array.toList
        |> List.map unbox<PtyJoinItem>

    member _.Cancel() : bool = unbox<bool> (view?cancel ())

    member _.PendingCount: int = unbox<int> (view?pendingCount ())

    member _.PendingPtyCount: int = unbox<int> (view?pendingPtyCount ())

    member _.IsCancelled: bool = unbox<bool> (view?isCancelled ())

    interface ICompletionMailbox<AgentHandleId, PtyJoinItem, JoinInterruptReason, MailboxWakeReason> with
        member this.PulseAgent handle = this.PulseAgentHandle handle
        member this.PublishPty item = this.PublishPtyCompletion item
        member this.PulseWake() = this.PulseWake()
        member this.WaitForWake() = this.WaitForWake()
        member this.WaitForSignal interrupt = this.WaitForSignal interrupt
        member this.DrainAgents maxCount = this.DrainAgentWakes maxCount
        member this.DrainPtys maxCount = this.DrainPtyCompletions maxCount
        member this.Cancel() = this.Cancel()
        member this.PendingCount = this.PendingCount
        member this.PendingPtyCount = this.PendingPtyCount
        member this.IsCancelled = this.IsCancelled

module CompletionMailboxRuntime =
    let create (gate: obj) : ICompletionMailbox<AgentHandleId, PtyJoinItem, JoinInterruptReason, MailboxWakeReason> =
        CompletionMailbox(gate)
