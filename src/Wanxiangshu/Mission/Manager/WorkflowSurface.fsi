namespace Wanxiangshu.Mission.Manager

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
module ManagerWorkflowSurface =
    val observeIdle:
        port: obj ->
        journal: JournalHandle ->
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit ->
        session: string ->
        physical: string ->
        authorityRoot: string ->
        providerRun: string ->
        directory: string ->
            Task

    /// Read-only probe over the successor-prompt predicate: it reads the fold the
    /// dispatch reads and dispatches nothing.
    val successorPromptContextOf: workspaceDirectory: string -> sessionId: string -> Task<obj>
