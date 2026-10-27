namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Host.Ports
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Routing
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Resources

module PluginBoot =

    /// Load-time capabilities captured from raw plugin input. This phase may read
    /// durable state but never repairs old work, mutates the workspace, or calls Host sessions.
    type Boot =
        { Input: obj
          PortOpt: IOpenCodePort option
          Journal: AgentJournal option
          Scope: PluginRuntimeScope
          Clock: IClockPort
          Timer: ITimerPort
          CasebookSettlements: CasebookSettlementOwner
          WorkspaceDirectory: string option
          Paths: IHostPaths option
          FamilyParent: SessionId -> SessionId option }

    /// A Casebook-scoped conflict belongs to one case: it is announced once
    /// and the case becomes unavailable. It never terminates the host process
    /// and never revokes finished engineering work.
    let private reportCasebookSemanticCut (incident: CasebookSemanticCutIncident) =
        let failure = incident.Failure

        let cutIds =
            incident.Cuts
            |> List.map (fun cut -> EventId.value cut.CutEventId)
            |> String.concat ","

        Diagnostic.emit
            "casebook-semantic-cut"
            [ "session_id", failure.CaseIdentity
              "result",
              sprintf
                  "%A case=%s event=%s cuts=%s: %s"
                  failure.Operation
                  failure.CaseIdentity
                  (EventId.value failure.EventId)
                  cutIds
                  (Wanxiangshu.Persistence.EventStore.AppendError.describe failure.Error) ]

    let create (input: obj) : Task<Boot> =
        task {
            // HOST-026: the global preference is the only language authority;
            // resolve it once at load so the first request already speaks it.
            Wanxiangshu.Participant.Provider.ProviderLanguageBinding.refreshGlobalLanguage ()

            // HNI P-12: route product diagnostics (`Diagnostic.emit`) through the
            // neutral `IDiagnosticSink` when the host wiring supplies `client.app.log`.
            // No host log verb means no sink: the product's own stderr path stays.
            match DiagnosticsPort.create input with
            | Some sink -> Diagnostic.installSink (fun operation fields -> sink.Emit(operation, fields))
            | None -> Diagnostic.clearSink ()

            // Fail-fast resource load before any consumer (StaticTools / BlogTool / EnforcerHost).
            RuntimeResources.install (RuntimeResourceAssembly.load ())

            do! ModelRouting.initialize ()

            // HNI P-10: `PathsPort` is the single authority for the host's
            // workspace field. The state root resolution is async while the
            // port is a plain property, so it is resolved once here and
            // handed to the adapter as a memoized thunk.
            let! paths =
                task {
                    match PathsPort.workspaceDirectory input with
                    | Some workspace ->
                        let! resolvedStateRoot = RuntimePath.stateRoot workspace
                        return PathsPort.create (fun () -> resolvedStateRoot) input
                    | None -> return None
                }

            let workspaceDirectory = PathsPort.projectRootOf paths

            let portOpt = OpenCodePortAdapter.create workspaceDirectory input

            let! journalResult = PluginHost.createJournal workspaceDirectory

            let journal =
                match journalResult with
                | Ok value -> value
                | Error err -> raise (InvalidOperationException err)

            let jsLifecyclePersistence =
                PathsPort.stateRootOf paths
                |> Option.bind WorkspaceEventStore.tryCurrent
                |> Option.map Wanxiangshu.Repository.Programming.Js.JsToolsTransactionStore.createLifecyclePersistence

            let scope =
                new PluginRuntimeScope(journal, (fun _ -> false), jsLifecyclePersistence)

            let clock = NodeTiming.nodeClockPort ()
            let timer = NodeTiming.nodeTimerPort ()

            let casebookSettlements = CasebookSettlementOwner reportCasebookSemanticCut

            let familyParent (sessionId: SessionId) =
                match scope.Sessions.SessionParents.TryGetValue(SessionId.value sessionId) with
                | true, parentId -> Some(SessionId.create parentId)
                | false, _ -> None

            return
                { Input = input
                  PortOpt = portOpt
                  Journal = journal
                  Scope = scope
                  Clock = clock
                  Timer = timer
                  CasebookSettlements = casebookSettlements
                  WorkspaceDirectory = workspaceDirectory
                  Paths = paths
                  FamilyParent = familyParent }
        }
