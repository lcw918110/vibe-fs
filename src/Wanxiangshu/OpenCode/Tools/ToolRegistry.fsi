namespace Wanxiangshu.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Contract
open Wanxiangshu.Mission.Work
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Programming.Js

/// Assembly-only registry: tool behavior lives in one vertical verb module;
/// per-session resources live in ToolRuntimeScope.
type ToolRegistration =
    { Tools: obj
      Runtime: ToolRuntimeScope }

module ToolRegistry =

    /// False while a question is waiting for the user, including an empty-ledger ask.
    val planSourceWriteAllowed: waitingForUser: bool -> bool

    /// False while a question is waiting for the user.
    val devopsDispatchAllowed: waitingForUser: bool -> bool

    val create:
        toolModule: obj ->
        sessionPort: ISessionHostPort ->
        waitObserver: IWaitObserver ->
        rootWorkspace: IRootWorkspaceReader ->
        journal: AgentJournal option ->
        workspaceDirectory: string option ->
        sessionParents: Dictionary<string, string> ->
        currentPhysicalUserMessage: (string -> string option) ->
        sessionDirectories: Dictionary<string, string> ->
        onRunStarted: (SessionId -> Role -> string option -> unit) option ->
        viewForSession: (string -> Task<string option>) option ->
        snapshot: ISessionSnapshotPort option ->
        cancelSignals: (SessionId seq -> unit) option ->
        beginToolExecution: (string -> unit) ->
        endToolExecution: (string -> unit) ->
        eventPort: IEventObservationPort option ->
        syncDelegateRuntime: SyncDelegateRuntime option ->

        casebookToolSpecs: ToolSpec list ->
        jsTransactionPersistence: IJsTransactionPersistence option ->
        jsLifecyclePersistence: IJsLifecyclePersistence option ->
        continueManagerLoop: (SessionId -> string -> Task<Result<unit, string>>) ->
        childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) option ->
        userNotify: (string -> string -> unit) option ->
            ToolRegistration
