namespace Wanxiangshu.Change

open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.Journal

/// What one ManagerJob's publication attempt resolved to.
///
/// Typed ids keep the durable Work identity separate from its physical Manager
/// session and Git binding.
type OrchestratorVerdict =
    | Published of jobId: ManagerJobId * head: CommitHash
    | PublishedPendingCleanup of jobId: ManagerJobId * head: CommitHash * cleanupError: string
    | Cancelled of jobId: ManagerJobId
    | RejectedDirty of reason: string
    | IntegrationFailed of jobId: ManagerJobId * errorDetails: string
    | Empty

type OrchestratorHandle =
    { JobId: ManagerJobId
      WorktreePath: WorktreePath }

/// Typed Git verbs.
///
/// `repoPath` is baked in at construction, so no verb takes it: passing it
/// per-call let a caller address a different repository than the one the port was
/// built for, and ORCH-008's frozen target branch belongs to exactly one repo.
type GitPort =
    {
        /// Whether the worktree at `path` has uncommitted, staged, untracked or
        /// dirty-submodule content. The main worktree must be clean before an
        /// isolated worktree is created from it, so the refusal can name the
        /// exact paths that block.
        IsDirty: WorktreePath -> Task<Result<string list, string>>

        Worktrees: GitWorktrees

        /// ORCH-008: freeze the target branch by `symbolic-ref` at fork time.
        ///
        /// A separate verb from `GetTargetHead` because they answer different
        /// questions — which ref, versus where that ref points. Reading HEAD when
        /// the ref cannot be resolved is the fallback ORCH-008 forbids, and a single
        /// combined verb makes that fallback one line away.
        FreezeTargetBranch: unit -> Task<Result<TargetRef, string>>

        Rebase: WorktreePath -> TargetRef -> Task<Result<unit, string>>

        /// ff-only publish with a mandatory CAS expectation (ORCH-005).
        ///
        /// `expectedHead` is not optional: every publish happens inside the short
        /// gate against a head that was just read. An optional expectation made
        /// "publish without checking" expressible, and that is the lost-update the
        /// gate exists to prevent.
        /// `pinnedCandidate` is the exact rebased commit the worktree must still hold;
        /// the merge runs on the pin, never on whatever HEAD happens to be checked out.
        /// CAS (`expectedHead`) and pin (`pinnedCandidate`) stay separate arguments
        /// so a caller cannot conflate "target has not moved" with "candidate is bound".
        FfMerge: WorktreePath -> TargetRef -> CommitHash -> CommitHash -> Task<Result<CommitHash, string>>

        ConflictedFiles: WorktreePath -> Task<Result<string list, string>>
        HasRebaseHead: WorktreePath -> Task<bool>
        ReadHead: WorktreePath -> Task<Result<CommitHash, string>>
        GetTargetHead: TargetRef -> Task<Result<CommitHash, string>>
    }

/// Everything a Manager fork needs, as one value.
///
/// A record, not four positional arguments: `ManagerAgent` and `Prompt` are both
/// `string` and adjacent, which is exactly where positional arguments get swapped —
/// and a swapped pair would fork an agent named after the task text.
type ManagerStart =
    { JobId: ManagerJobId
      ManagerAgent: string
      Worktree: WorktreePath
      RootRequest: string
      ExpectedToolCalls: int option }

[<RequireQualifiedAccess>]
type ManagerLoopSignal =
    | Continue
    | Candidate
    | ExceptionalTerminal of string

type RelayPort =
    {
        CreateManagerSession: ManagerStart -> Task<Result<SessionId, string>>
        ActivateManager: ManagerJobId -> Task<Result<unit, string>>
        AwaitLoopSignal: ManagerJobId -> Task<Result<ManagerLoopSignal, string>>
        /// The continuation the road owes after its last committed retirement: the
        /// successor tenure of a relay, or — after a delivered hand-off — the same
        /// Work's next tenure, which Change opens before anything may publish again
        /// (change-integration-005/013). Every continuation is a real tenure; a work
        /// with no continuation holds no loop signal instead (`AwaitLoopSignal`).
        ContinueLoop: ManagerJobId -> Task<Result<IncumbencyId, string>>
        PrepareCandidate: ManagerJobId -> Task<Result<CommitHash, string>>
        TerminateWorkResources: ManagerJobId -> Task<unit>
    }

type OrchestratorJournalPort =
    { AppendFact: StreamId -> AgentFact -> Task<Result<ProjectionSet, string>>
      Snapshot: unit -> ProjectionSet }

type PublishGateLease = { Release: unit -> Task<unit> }

type OrchestratorProgramDeps =
    { Git: GitPort
      Relay: RelayPort
      AppendFact: StreamId -> AgentFact -> Task<Result<unit, string>>
      Snapshot: unit -> ProjectionSet
      AcquirePublishGate: unit -> Task<PublishGateLease> }

module OrchestratorConstants =
    /// `FfMerge` reports this when the target advanced between the head read and
    /// the ref update. ORCH-005 turns it into certificate invalidation, rebase and
    /// an ordinary manager loop continuation; the old certificate is never reused.
    [<Literal>]
    let targetRefMovedError = "target ref moved"
