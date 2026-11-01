namespace Wanxiangshu.Change

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git

/// 工作记录；资源物理生命周期由公开库对象持有。
type ManagerJob =
    { JobId: ManagerJobId
      ManagerSessionId: SessionId
      ManagerAgent: string
      TargetRef: TargetRef
      Worktree: WorktreeResource }

    member this.Handle =
        { JobId = this.JobId
          WorktreePath = this.Worktree.Path }

type private ResultMailboxLibrary =
    abstract start: unit -> unit
    abstract publish: value: obj -> unit
    abstract drain: maxCount: int -> obj array
    abstract hasActive: bool
    abstract pendingCount: int
    abstract beginJoin: maxCount: int * interrupt: Task<JoinInterruptReason> -> obj

module private ResultMailboxBinding =
    [<Import("ResultMailbox", "wanxiangli/result-mailbox")>]
    let constructor: obj = jsNative

    [<Emit("new $0()")>]
    let create (_constructor: obj) : ResultMailboxLibrary = jsNative

    let outcome (value: obj) : JoinWaitOutcome<OrchestratorVerdict> =
        match unbox<string> value?kind with
        | "items" ->
            let items = unbox<OrchestratorVerdict array> value?items
            ResultsAvailable(NonEmptyBatch.ofHeadTail items[0] (items |> Array.skip 1 |> Array.toList))
        | "interrupted" -> Interrupted(unbox value?reason)
        | _ -> ResultsAvailable(NonEmptyBatch.ofHeadTail OrchestratorVerdict.Empty [])

/// 仅保留主体类型、批量上限和等待诊断；队列、计数与等待者归库。
type VerdictMailbox(observer: IWaitObserver) =
    let mailbox = ResultMailboxBinding.create ResultMailboxBinding.constructor

    member _.StartJob() = mailbox.start ()
    member _.Publish(verdict: OrchestratorVerdict) = mailbox.publish (box verdict)

    member _.DrainAvailable(maxCount: int) =
        mailbox.drain maxCount |> Array.map unbox<OrchestratorVerdict> |> Array.toList

    member _.HasActive = mailbox.hasActive
    member _.PendingCount = mailbox.pendingCount

    member _.JoinAvailable
        (maxCount: int, interrupt: Task<JoinInterruptReason>)
        : Task<JoinWaitOutcome<OrchestratorVerdict>> =
        let cap = min (max 0 maxCount) JoinBatch.Max
        let started = mailbox.beginJoin (cap, interrupt)

        if unbox<string> started?kind = "ready" then
            Task.FromResult(ResultMailboxBinding.outcome started?result)
        else
            let descriptor =
                DiagnosticWait.create
                    "orchestrator-manager-join"
                    (CausalOwner.create "OrchestratorJob" [])
                    [ "mailbox", "verdict" ]
                    (WorkflowProducer(CausalOwner.create "ManagerWorkflow" []))
                    [ WaitEscape.CancelledBy(CausalOwner.create "orchestrator-join-interrupt" [])
                      WaitEscape.ProcessLifetime ]
                    "VerdictMailbox.JoinAvailable"

            task {
                let! result = CausalAwait.awaitTask observer descriptor (unbox<Task<obj>> started?completion)
                return ResultMailboxBinding.outcome result
            }
