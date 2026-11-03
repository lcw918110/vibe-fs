namespace Wanxiangshu.Change

open System.Threading.Tasks

module ChangeSurface =
    val fact: kind: string -> payload: obj -> obj

    val empty: unit -> obj

    val createJob: projection: obj -> payload: obj -> obj

    val recordFact: projection: obj -> job: string -> value: obj -> obj

    val find: projection: obj -> job: string -> obj

    val activeJobs: projection: obj -> obj array

    val job: state: obj -> jobId: string -> obj

    val jobForSession: state: obj -> sessionId: string -> obj

    val isTerminal: state: obj -> jobId: string -> bool

    val isOutstanding: state: obj -> jobId: string -> bool

    val classifyRebasedCandidate: head: obj -> rebasedCommit: string -> targetHeadSnapshot: string -> obj

    val classifyPublishClaim: head: obj -> rebasedCommit: string -> expectedHead: string -> obj

    val requestWorktree: projection: obj -> identity: string -> path: string -> job: string -> obj

    val acceptWorktree: projection: obj -> identity: string -> path: string -> job: string -> obj

    val worktreeEffect: projection: obj -> identity: string -> obj

    val worktreeReconciliationDecision: job: string -> identity: string -> path: string -> evidence: obj -> obj

    val fold: events: obj array -> obj

    val unwrapFold: result: obj -> obj

    val observeRelayProgram: scenarioName: string -> Task<obj>

    val createGit: repo: string -> runner: obj -> obj

    val gitIsDirty: git: obj -> path: string -> Task<obj>

    val observeManagerLoopBurst: count: int -> Task<obj>
    val gitFreezeTargetBranch: git: obj -> Task<obj>

    val gitFreezeTargetBranchResult: git: obj -> Task<obj>

    val gitRebase: git: obj -> path: string -> targetRef: string -> Task<obj>

    val gitFfMerge:
        git: obj -> path: string -> targetRef: string -> expectedHead: string -> pinnedCandidate: string -> Task<obj>

    val gitConflictedFiles: git: obj -> path: string -> Task<obj>

    val gitConflictedFilesResult: git: obj -> Task<obj>

    val gitHasRebaseHead: git: obj -> path: string -> Task<bool>

    val gitReadHead: git: obj -> path: string -> Task<obj>

    val gitGetTargetHead: git: obj -> targetRef: string -> Task<obj>

    val gitCreateWorktree: git: obj -> job: string -> path: string -> sourceRef: string -> Task<obj>

    val gitRemoveWorktree: git: obj -> path: string -> Task<obj>

    val gitDeleteBranch: git: obj -> identity: string -> Task<obj>

    val worktreeIdentityOf: job: string -> string

    val gitListWorktrees: git: obj -> Task<obj>

    val gitListManagerBranches: git: obj -> Task<obj>

    val worktreeCreate: git: obj -> job: string -> path: string -> sourceRef: string -> Task<obj>

    val worktreeAdopt: git: obj -> identity: string -> path: string -> obj

    val worktreePath: resource: obj -> string

    val worktreeIdentity: resource: obj -> string

    val worktreeMarkDurable: resource: obj -> unit

    val worktreeRelease: resource: obj -> Task<obj>

    val worktreeDispose: resource: obj -> Task<unit>

    val lockPath: repo: string -> branch: string -> string

    val acquireGate: path: string -> Task<obj>

    val releaseGate: gate: obj -> Task<unit>

    val disposeGate: gate: obj -> Task<unit>

    val createVerdictMailbox: unit -> obj

    val verdictMailboxStartJob: mailbox: obj -> unit

    val verdictMailboxPublish: mailbox: obj -> verdict: obj -> unit

    val verdictMailboxPendingCount: mailbox: obj -> int

    val createVerdictInterrupt: unit -> obj

    val fireVerdictInterrupt: handle: obj -> reason: string -> unit

    val rejectVerdictInterrupt: handle: obj -> error: obj -> unit

    val verdictMailboxJoinAvailable: mailbox: obj -> maxCount: int -> interrupt: obj -> Task<obj>

    val verdictMaxBatch: unit -> int
    val managerBranchOf: job: string -> string
    val staleManagerWorktrees: entries: obj array -> owned: string array -> string array
    val staleManagerBranches: branches: string array -> owned: string array -> string array
    val lifecycleJob: journal: Wanxiangshu.Persistence.Journal.JournalHandle -> id: string -> obj
    val createLifecycle: journal: Wanxiangshu.Persistence.Journal.JournalHandle -> options: obj -> obj
    val startLifecycle: runtime: obj -> id: string -> agent: string -> prompt: string -> path: string -> Task<obj>
    val recoverLifecycle: runtime: obj -> job: obj -> unit
    val collectLifecycle: runtime: obj -> Task<obj>
