namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks
open Wanxiangshu.Persistence.EventStore

/// CASE-003/010 / KR-010: process-local Casebook session wiring.
module CasebookLifecycle =

    type CasebookObservationCollector =
        new: unit -> CasebookObservationCollector
        member Collect: sessionId: string * toolName: string * args: obj * output: string -> unit
        member RecordFileRead: sessionId: string * path: string * output: string -> unit
        member Drain: sessionId: string -> Observation list
        member Count: sessionId: string -> int

    val collector: CasebookObservationCollector

    val setEnabled: workspaceRoot: string option -> unit

    val isEnabled: unit -> bool

    val notePrompt: delegateSessionId: string -> q: string -> unit

    val noteAnswer: delegateSessionId: string -> a: string -> unit

    val cleanupDraft: delegateSessionId: string -> unit

    val tryFinalizeDraft:
        workspaceRoot: string -> store: IEventStore -> delegateSessionId: string -> Task<CaseFinalizeSettlement>

    val finalizeEngineerCase:
        store: IEventStore ->
        identity: string ->
        sourceTrace: string ->
        q: string ->
        a: string ->
        relatedPaths: string list ->
        completionStateRef: string ->
            Task<CaseFinalizeSettlement>

    val touchAccess:
        workspaceRoot: string -> store: IEventStore -> sessionId: string -> Task<Result<unit, CasebookMutationError>>
