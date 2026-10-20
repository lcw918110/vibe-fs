namespace Wanxiangshu.Change.Host

open System.Threading.Tasks
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Mission.Work
open Wanxiangshu.OpenCode
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal

type OrchestratorHostDeps =
    {
        Sessions: ISessionHostPort
        RootWorkspace: IRootWorkspaceReader
        WaitObserver: IWaitObserver
        Journal: AgentJournal option
        /// change-integration-005/013: the head cursor the process observed for one
        /// session, read from the same journal projection the Manager deliver used.
        /// Change never guesses this cursor; a missing observation means the road
        /// cannot open a successor tenure yet and the hand-off fails closed.
        ObservedHeadCursor: SessionId -> int64 option
        SessionSnapshot: ISessionSnapshotPort option
        OnChildCreated: string -> Role -> SessionId -> unit
        RegisterChildDirectory: SessionId -> string -> unit
        OnRunStarted: SessionId -> Role -> string option -> unit
        SendGateContinuation:
            SessionId
                -> string
                -> PromptAuthority.ContinuationKind
                -> string option
                -> AgentJournal option
                -> string
                -> ProviderRunIdentity
                -> Task<Result<Wanxiangshu.Foundation.Identity.PhysicalUserMessageId, string>>
        ContinueManagerLoop: SessionId -> string -> Task<Result<unit, string>>
        RepoPath: string
        TargetBranch: string
    }
