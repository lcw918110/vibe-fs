namespace Wanxiangshu.Change

open System.Threading.Tasks
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation.Identity

module internal ManagerWorktreePolicy =
    val managerBranchOf: jobId: string -> string
    val staleWorktrees: entries: (string * string option) list -> owned: string list -> string list
    val staleBranches: branches: string list -> owned: string list -> string list

type Orchestrator =
    new:
        observer: IWaitObserver *
        git: GitPort *
        relay: RelayPort *
        repoPath: string *
        targetRef: TargetRef *
        ?journal: OrchestratorJournalPort *
        ?lockRepoPath: string ->
            Orchestrator

    member ForkManager:
        jobId: ManagerJobId *
        managerAgent: string *
        prompt: string *
        ?worktreePath: WorktreePath *
        ?byname: string *
        ?expectedToolCalls: int ->
            Task<Result<OrchestratorHandle, OrchestratorVerdict>>

    member RecoverManagerJob: record: ManagerJobProjection -> unit

    member JoinPublishedBatch:
        maxCount: int * interrupt: Task<JoinInterruptReason> -> Task<JoinWaitOutcome<OrchestratorVerdict>>
