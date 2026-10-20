namespace Wanxiangshu.OpenCode

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

    let private nonBlankDirectory (directory: string) : string option =
        if String.IsNullOrWhiteSpace directory then
            None
        else
            Some directory

    let workspaceDirectory (input: obj) : string option =
        if isNull input || isNull input?directory then
            None
        else
            input?directory |> unbox<string> |> nonBlankDirectory

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
    let createJournal (input: obj) : Task<Result<AgentJournal option, string>> =
        match workspaceDirectory input with
        | None -> Task.FromResult(Ok None)
        | Some workspace -> createWorkspaceJournal workspace

    let createHost
        (input: obj)
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
                  Events.HostEventPort option,
                  string
               >
           >
        =
        task {
            // Completion/output port is process-local per workspace runtime path so
            // root + worktree plugin instances share AwaitAgent / NotifyTerminal.
            // Host SSE remains only a coarse idle/retry/deleted signal source.
            let! shared = SharedTerminalBus.tryAcquireForWorkspace (workspaceDirectory input)

            let hostEventPort, terminalKey =
                match shared with
                | Some(key, port) -> port, Some key
                | None -> Events.HostEventPort(), None

            let eventPort = hostEventPort :> IEventObservationPort

            let sessionPort =
                InjectedSessionPort(
                    portOpt,
                    eventPort,
                    ?familyParent = familyParent,
                    ?isLifecycleTerminated = isLifecycleTerminated,
                    ?isUserQuestionWaiting = isUserQuestionWaiting
                )

            let snapshotPort = SessionSnapshotPort.create input

            return Ok(eventPort, sessionPort :> ISessionHostPort, snapshotPort, terminalKey, Some hostEventPort)
        }
