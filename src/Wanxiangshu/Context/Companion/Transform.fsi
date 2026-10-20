namespace Wanxiangshu.Context.Companion

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

module CompanionTransform =

    /// Main-session transform: Host view unchanged.
    val handleCompanionTransform:
        companions: Dictionary<string, CompanionHost> ->
        gate: obj ->
        satellites: CompanionLeaseRuntime ->
        sessionPort: ISessionHostPort ->
        journal: AgentJournal option ->
        workspaceDirectory: string option ->
        inObj: obj ->
        rawOutObj: obj ->
            Task<unit>

    val applyCompanionForOrdinaryMaterial:
        companions: Dictionary<string, CompanionHost> ->
        gate: obj ->
        satellites: CompanionLeaseRuntime ->
        sessionPort: ISessionHostPort ->
        journal: AgentJournal option ->
        workspaceDirectory: string option ->
        projectionSessionIdOpt: string option ->
        inObj: obj ->
        outObj: obj ->
            Task<unit>
