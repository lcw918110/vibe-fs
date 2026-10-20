namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

/// JS-native boundary for the host-owned terminal event port.  The port stays
/// opaque; only session ids, outcome labels, and terminal text cross the edge.
module EventsSurface =
    // DSL-MUTABLE: resource — synthetic provider-run identity counter for malformed host input
    let mutable private noRun = 0
    let create () : obj = box (Events.HostEventPort())

    let private terminal (sessionId: string) (kind: string) (providerRun: string) (text: string) : TerminalOutcome =
        match kind with
        | "Completed" ->
            let run =
                if String.IsNullOrWhiteSpace providerRun then
                    noRun <- noRun + 1
                    "unidentified-" + string noRun
                else
                    providerRun

            TerminalOutcome.Completed
                { SessionId = SessionId.create sessionId
                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (sessionId + "-root")
                  ProviderRun = ProviderRunIdentity.create run
                  Role = Role.Coder
                  Directory = None
                  TerminalText = text
                  TurnFormalText = text }
        | "Aborted" -> TerminalOutcome.Aborted(TerminalStop.session text)
        | _ -> TerminalOutcome.Failed(TerminalStop.session text)

    let private snapshot (outcome: TerminalOutcome) : obj =
        match outcome with
        | TerminalOutcome.Completed result ->
            box
                {| kind = "Completed"
                   providerRun = ProviderRunIdentity.value result.ProviderRun
                   text = result.TerminalText |}
        | TerminalOutcome.Aborted stop ->
            box
                {| kind = "Aborted"
                   providerRun = ""
                   text = stop.Reason
                   authorityRoot =
                    stop.AuthorityRootUserMessageId
                    |> Option.map AuthorityRootUserMessageId.value
                    |> Option.defaultValue "" |}
        | TerminalOutcome.Failed stop ->
            box
                {| kind = "Failed"
                   providerRun = ""
                   text = stop.Reason
                   authorityRoot =
                    stop.AuthorityRootUserMessageId
                    |> Option.map AuthorityRootUserMessageId.value
                    |> Option.defaultValue "" |}

    let notify (port: obj) (sessionId: string) (kind: string) (providerRun: string) (text: string) : bool =
        let typed = port :?> IEventObservationPort
        typed.NotifyTerminal (SessionId.create sessionId) (terminal sessionId kind providerRun text)

    let notifyForAuthority
        (port: obj)
        (sessionId: string)
        (kind: string)
        (authorityRoot: string)
        (text: string)
        : bool =
        let stop =
            TerminalStop.forAuthority (AuthorityRootUserMessageId.create authorityRoot) text

        let outcome =
            match kind with
            | "Aborted" -> TerminalOutcome.Aborted stop
            | _ -> TerminalOutcome.Failed stop

        (port :?> IEventObservationPort).NotifyTerminal (SessionId.create sessionId) outcome

    let subscribe (port: obj) (listener: obj -> obj -> unit) : obj =
        let typed = port :?> IEventObservationPort

        typed.SubscribeTerminalListener(fun sessionId outcome ->
            listener (box (SessionId.value sessionId)) (snapshot outcome))

    let subscribeFuture (port: obj) (listener: obj -> obj -> unit) : obj =
        let typed = port :?> IEventObservationPort

        typed.SubscribeFutureTerminalListener(fun sessionId outcome ->
            listener (box (SessionId.value sessionId)) (snapshot outcome))

    let dispose (subscription: obj) : unit =
        match subscription with
        | :? IDisposable as disposable -> disposable.Dispose()
        | _ -> ()

    let notifyCompleted
        (port: obj)
        (sessionId: string)
        (terminalText: string)
        (formalText: string)
        (roleLabel: string)
        : bool =
        if String.IsNullOrWhiteSpace roleLabel then
            false
        else
            match Roles.tryParseRole roleLabel with
            | None -> false
            | Some role ->
                let result =
                    { SessionId = SessionId.create sessionId
                      AuthorityRootUserMessageId = AuthorityRootUserMessageId.create (sessionId + "-root")
                      ProviderRun = ProviderRunIdentity.create (sessionId + "-completed")
                      Role = role
                      Directory = None
                      TerminalText = terminalText
                      TurnFormalText = formalText }

                let typed = port :?> IEventObservationPort
                typed.NotifyTerminal (SessionId.create sessionId) (TerminalOutcome.Completed result)

    let acquireSharedForWorkspace (workspace: string) : Task<obj> =
        task {
            match! SharedTerminalBus.tryAcquireForWorkspace (Some workspace) with
            | Some(_, port) -> return box port
            | None -> return null
        }

    let releaseSharedForWorkspace (workspace: string) (port: obj) : Task<unit> =
        task {
            let! key = RuntimePath.forWorkspace workspace
            SharedTerminalBus.release (Some key) (Some(port :?> Events.HostEventPort))
        }
