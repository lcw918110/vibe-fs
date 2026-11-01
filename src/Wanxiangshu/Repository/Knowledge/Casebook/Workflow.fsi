namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks
open Wanxiangshu.Persistence.EventStore

/// CASE-009: feature gating — the product surface lives only when the marker
/// directory exists.
module CasebookFeature =

    val isEnabled: workspaceRoot: string -> bool

module CasebookServiceBinding =

    val observationCollector: obj
    val drainCollector: collector: obj -> identity: string -> Observation list
    val cleanupDraft: identity: string -> unit

    val finalizeCaptured: store: IEventStore -> case: Case -> Task<CaseFinalizeSettlement>

    val finalizeDraft:
        root: string -> store: IEventStore -> identity: string -> Task<CaseFinalizeSettlement>

    val refresh:
        root: string -> store: IEventStore -> identity: string -> Task<Result<bool, CasebookMutationError>>

    val touch:
        root: string -> store: IEventStore -> identity: string -> Task<Result<unit, CasebookMutationError>>

    val fetch:
        root: string -> store: IEventStore -> shelfmark: string -> notify: (CasebookAppendFailure -> unit) -> Task<obj>

/// CASE-003/004/005 / KR-004/005/015: Casebook workflow.
module CasebookWorkflow =

    val archiveCase: store: IEventStore -> case: Case -> Task<Result<unit, CasebookMutationError>>

    val fetchCase:
        store: IEventStore -> capacity: int -> identityOrSessionId: string -> Task<Result<Case option, string>>

    val fetchCaseByIdentity: store: IEventStore -> identity: string -> Task<Result<Case option, string>>

    val needsRefresh:
        store: IEventStore -> capacity: int -> sessionId: string -> root: string -> Task<Result<bool, string>>

    val refreshCase:
        store: IEventStore ->
        identity: string ->
        q: string ->
        a: string ->
        maintenanceFileState: string ->
        relatedPaths: string list ->
        observations: Observation list ->
            Task<Result<unit, CasebookMutationError>>

    val refreshWithDiff:
        store: IEventStore ->
        identity: string ->
        diff: string ->
        newStateRef: string ->
        q: string ->
        a: string ->
            Task<Result<unit, CasebookMutationError>>

    val finalizeCase: store: IEventStore -> case: Case -> Task<Result<unit, CasebookMutationError>>
    val touchCaseAccess: store: IEventStore -> identity: string -> Task<Result<unit, CasebookMutationError>>
