namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Threading.Tasks
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Git
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback


module PluginHost =

    [<Import("pid", "node:process")>]
    let processId: int = jsNative


    let private semanticUnavailable reason : Result<AgentJournal option, string> =
        Diagnostic.emit "journal-semantic-unavailable" [ "result", reason ]
        Ok None

    let private resolveJournalAvailability (availability: Result<AgentJournal, FoldRejection>) =
        match availability with
        | Ok journal -> Ok(Some journal)
        | Error rejection -> semanticUnavailable (sprintf "%s (%s)" rejection.Reason rejection.Fact)

    let private acquireWorkspaceJournal commonDir runtimeDir : Task<Result<AgentJournal option, string>> =
        task {
            try
                let port = WorkspaceEventStore.bootPort commonDir

                let openJournal (runtimeId: RuntimeId) (processId: int) (startedAt: DateTimeOffset) =
                    taskResult {
                        let! writer, _, projection = port.ResumeOrCreate(runtimeId, processId, startedAt)
                        return! AgentJournal.createFromProjection writer projection
                    }

                let! acquired = SharedAgentJournal.acquire runtimeDir processId DateTimeOffset.UtcNow openJournal
                return resolveJournalAvailability acquired
            with ex ->
                return semanticUnavailable ex.Message
        }

    let private createWorkspaceJournal workspace : Task<Result<AgentJournal option, string>> =
        task {
            let! commonDir = RuntimePath.initializeStateRoot workspace
            let! runtimeDir = RuntimePath.forWorkspace workspace
            return! acquireWorkspaceJournal commonDir runtimeDir
        }

    /// Load may read durable bytes, but it never repairs semantic state. Physical
    /// corruption is a load error; a domain fold rejection only disables the journal
    /// capability for this plugin instance.
    let createJournal (workspaceDirectory: string option) : Task<Result<AgentJournal option, string>> =
        match workspaceDirectory with
        | None -> Task.FromResult(Ok None)
        | Some workspace -> createWorkspaceJournal workspace


    let createHost
        (workspaceDirectory: string option)
        (snapshotPort: ISessionSnapshotPort option)
        (portOpt: IOpenCodePort option)
        (familyParent: (SessionId -> SessionId option) option)
        (isLifecycleTerminated: (SessionId -> bool) option)
        (isUserQuestionWaiting: (SessionId -> bool) option)
        : Task<
              Result<
                  IEventObservationPort *
                  ISessionHostPort *
                  ISessionSnapshotPort option *
                  string option *
                  ISessionTerminalSource option,
                  string
               >
           >
        =
        task {
            // Completion/output port is process-local per workspace runtime path so
            // root + worktree plugin instances share AwaitAgent / NotifyTerminal.
            // Host SSE remains only a coarse idle/retry/deleted signal source.
            // The terminal facts and their subscribers are the product's; the
            // adapter only forwards to them. The workspace-shared source is
            // the first registry of that workspace, so the root and worktree
            // plugin instances share one terminal truth.
            let candidate = Events.newTerminalSource ()
            let! shared = TerminalWiring.tryAcquireForWorkspace workspaceDirectory candidate

            let hostEventPort, terminalKey, finalSource =
                match shared with
                | Some(key, source) -> Events.HostEventPort(source), Some key, source
                | None -> Events.HostEventPort(candidate), None, candidate

            let eventPort = hostEventPort :> IEventObservationPort

            let sessionPort =
                InjectedSessionPort(
                    portOpt,
                    hostEventPort :> ISessionTerminalSource,
                    ?familyParent = familyParent,
                    ?isLifecycleTerminated = isLifecycleTerminated,
                    ?isUserQuestionWaiting = isUserQuestionWaiting
                )


            return Ok(eventPort, sessionPort :> ISessionHostPort, snapshotPort, terminalKey, Some finalSource)
        }
