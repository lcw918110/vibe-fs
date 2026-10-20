namespace Wanxiangshu.Repository.Knowledge.Casebook.OpenCode

open System.Threading.Tasks
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Repository.Programming.Js.OpenCode

/// CASE-009: assembles the conditional Casebook tool specs. This module is
/// the only place that names the EventStore for tool registration, keeping
/// ToolRegistry / PluginHostInterop / SpikePlugin free of the dual-write
/// token pair (AgentJournal + IEventStore in one file is forbidden).
module CasebookTools =

    /// Marker + EventStore availability → fetch + todowrite-for-bookkeeper, or none.
    /// Acquire failure degrades the surface instead of failing the plugin —
    /// the schema gate and the execution gate stay in agreement.
    let private tryBuildSpecs
        (factory: HostToolFactory)
        (workspaceRoot: string)
        (owner: CasebookSettlementOwner)
        : Task<ToolSpec list> =
        task {
            try
                let! commonDir = RuntimePath.stateRoot workspaceRoot
                let store = WorkspaceEventStore.acquire commonDir

                return
                    [ FetchTool.spec factory workspaceRoot store owner
                      JsBookkeeperTool.spec factory ]
            with _ ->
                return []
        }

    let buildSpecs
        (factory: HostToolFactory)
        (workspaceRoot: string)
        (owner: CasebookSettlementOwner)
        : Task<ToolSpec list> =
        task {
            if isNull (box owner) then
                nullArg "owner"

            if not (CasebookFeature.isEnabled workspaceRoot) then
                return []
            else
                return! tryBuildSpecs factory workspaceRoot owner
        }
