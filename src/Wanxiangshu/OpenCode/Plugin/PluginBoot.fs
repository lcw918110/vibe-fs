namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
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
          FamilyParent: SessionId -> SessionId option }

    let private bindCasebookSemanticCut (incident: CasebookSemanticCutIncident) =
        let failure = incident.Failure

        let cutIds =
            incident.Cuts
            |> List.map (fun cut -> EventId.value cut.CutEventId)
            |> String.concat ","

        Diagnostic.fatal
            "casebook-semantic-cut"
            [ "result",
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
            Wanxiangshu.OpenCode.ProviderLanguageBinding.refreshGlobalLanguage ()

            // Fail-fast resource load before any consumer (StaticTools / BlogTool / EnforcerHost).
            RuntimeResources.install (RuntimeResourceAssembly.load ())

            // execution-model-routing-001: bootstrap/load the sole model scheduler during Load Phase.
            // This may create the missing user config atomically, but performs no Host call.
            do! ModelRouting.initialize ()

            let portOpt = OpenCodePortAdapter.create input
            let workspaceDirectory = PluginHost.workspaceDirectory input

            let! journalResult = PluginHost.createJournal input

            let journal =
                match journalResult with
                | Ok value -> value
                | Error err -> raise (InvalidOperationException err)

            let! jsLifecyclePersistence =
                task {
                    match workspaceDirectory with
                    | Some workspace ->
                        let! commonDir = RuntimePath.stateRoot workspace

                        return
                            WorkspaceEventStore.tryCurrent commonDir
                            |> Option.map
                                Wanxiangshu.Repository.Programming.Js.JsToolsTransactionStore.createLifecyclePersistence
                    | None -> return None
                }

            let scope =
                new PluginRuntimeScope(journal, (fun _ -> false), jsLifecyclePersistence)

            let clock = NodeTiming.nodeClockPort ()
            let timer = NodeTiming.nodeTimerPort ()

            let casebookSettlements = CasebookSettlementOwner bindCasebookSemanticCut

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
                  FamilyParent = familyParent }
        }
