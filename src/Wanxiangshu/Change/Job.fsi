namespace Wanxiangshu.Change

open System.Threading.Tasks
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git

/// One Manager execution plus the worktree resource it owns through publish.
type ManagerJob =
    { JobId: ManagerJobId
      ManagerSessionId: SessionId
      ManagerAgent: string
      TargetRef: TargetRef
      Worktree: WorktreeResource }

    member Handle: OrchestratorHandle

/// 工作终局结果的主体类型和诊断适配；队列、活动计数和等待者归 result-mailbox。
type VerdictMailbox =
    new: observer: IWaitObserver -> VerdictMailbox
    member StartJob: unit -> unit
    member Publish: verdict: OrchestratorVerdict -> unit
    member DrainAvailable: maxCount: int -> OrchestratorVerdict list
    member HasActive: bool
    member PendingCount: int

    member JoinAvailable:
        maxCount: int * interrupt: Task<JoinInterruptReason> -> Task<JoinWaitOutcome<OrchestratorVerdict>>
