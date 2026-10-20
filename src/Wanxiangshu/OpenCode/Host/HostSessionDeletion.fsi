namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Repository.Knowledge.Casebook

/// SessionDeleted teardown: LoopSensor / SyncDelegate / Quiescence / Dispose.
/// Caller supplies `signalReconciler` so this module never owns the Scheduler.
module HostSessionDeletion =

    type SessionDeletionPreparation =
        private | SessionDeletionPreparation of
            parent: SessionId option *
            delegateStaged: bool *
            delegateToFinalize: SessionId option

    /// Capture parent topology and retire the live Delegate binding synchronously
    /// at Host event admission. Child and owner cleanup may await independently,
    /// but their semantic order is now fixed by the public event stream.
    val prepare:
        scope: PluginRuntimeScope ->
        sessionId: SessionId ->
        parentSessionIdOpt: SessionId option ->
            SessionDeletionPreparation

    val finalizePreparedDelegate:
        scope: PluginRuntimeScope ->
        workspaceDirectory: string option ->
        finalizeDelegate: (string -> string -> Task<CaseFinalizeSettlement>) ->
        preparation: SessionDeletionPreparation ->
            Task

    val handle:
        scope: PluginRuntimeScope ->
        cleanupDelegateDraft: (string -> unit) ->
        signalReconciler: (HostSignal -> unit) ->
        sessionId: SessionId ->
        onSessionDeleted: (SessionId -> unit) option ->
        preparation: SessionDeletionPreparation ->
            Task
