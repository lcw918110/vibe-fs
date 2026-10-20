namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Context.Companion
open Wanxiangshu.Repository.Knowledge.Casebook
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

/// SessionDeleted teardown: LoopSensor / SyncDelegate / Quiescence / Dispose.
/// Caller supplies `signalReconciler` so this module never owns the Scheduler.
module HostSessionDeletion =

    type SessionDeletionPreparation =
        private | SessionDeletionPreparation of
            parent: SessionId option *
            delegateStaged: bool *
            delegateToFinalize: SessionId option

    let private stageDeletedDelegate
        (runtime: SyncDelegateRuntime)
        (sessionId: SessionId)
        (fallbackParent: SessionId option)
        : SessionId option * bool =
        match runtime.StageDeletedDelegateBySession sessionId with
        | Some ownerSessionId -> Some ownerSessionId, true
        | None ->
            let delegateStaged =
                fallbackParent
                |> Option.exists (fun parentSessionId -> runtime.StageDeletedDelegate(parentSessionId, sessionId))

            fallbackParent, delegateStaged

    /// Capture parent topology and retire the live Delegate binding synchronously
    /// at Host event admission. Child and owner cleanup may await independently,
    /// but their semantic order is now fixed by the public event stream.
    let prepare
        (scope: PluginRuntimeScope)
        (sessionId: SessionId)
        (parentSessionIdOpt: SessionId option)
        : SessionDeletionPreparation =
        let parent =
            parentSessionIdOpt
            |> Option.orElseWith (fun () ->
                match scope.Sessions.SessionParents.TryGetValue(SessionId.value sessionId) with
                | true, parentId -> Some(SessionId.create parentId)
                | false, _ -> None)

        match scope.SyncDelegateRuntime with
        | None -> SessionDeletionPreparation(parent, false, None)
        | Some runtime ->
            let resolvedParent, delegateStaged = stageDeletedDelegate runtime sessionId parent

            let delegateToFinalize =
                runtime.TryFindForScopeClose(sessionId, SyncDelegateRole.Engineer)

            SessionDeletionPreparation(resolvedParent, delegateStaged, delegateToFinalize)

    /// Finalize the staged Delegate case before later session cleanup drops its
    /// physical identity. The exact settlement is captured FIRST; only a durably
    /// settled finalize releases the identity (CaseFinalizeSettlement.
    /// releasesIdentity). NotCommitted/Unknown/PhaseConflict RETAIN the identity
    /// so a later recovery can resume the exact finalize — the outer evidence
    /// lifetime decision below never drops an identity it still needs.
    let private finalizeDelegateAtRoot
        (finalizeDelegate: string -> string -> Task<CaseFinalizeSettlement>)
        (root: string)
        (delegateId: SessionId)
        : Task<CaseFinalizeSettlement> =
        finalizeDelegate root (SessionId.value delegateId)

    let private finalizeDelegateIfRoot
        (workspaceDirectory: string option)
        (finalizeDelegate: string -> string -> Task<CaseFinalizeSettlement>)
        (delegateId: SessionId)
        : Task<CaseFinalizeSettlement option> =
        match workspaceDirectory with
        | Some root ->
            task {
                let! settled = finalizeDelegateAtRoot finalizeDelegate root delegateId
                return Some settled
            }
        | None -> Task.FromResult None

    /// One settle dispatch at top level so the match inside
    /// `finalizeStagedDelegate` stays a single pyramid level.
    let private finalizeDisposition
        (scope: PluginRuntimeScope)
        (delegateId: SessionId)
        (settled: CaseFinalizeSettlement)
        : unit =
        match settled.Commitment with
        | CaseFinalizeCommitment.Finalized
        | CaseFinalizeCommitment.NothingToFinalize -> scope.DropSessionIdentity(SessionId.value delegateId)
        | CaseFinalizeCommitment.NotCommitted reason
        | CaseFinalizeCommitment.Unknown reason ->
            // Retain the identity: a later recovery must be able to
            // resume this exact finalize. Expected/best-effort, never a
            // recovery decision — the commitment itself is the evidence.
            Diagnostic.emit "case-finalization-pending" [ "session_id", SessionId.value delegateId; "result", reason ]
        | CaseFinalizeCommitment.PersistenceFailed failure ->
            Diagnostic.emit
                "case-finalization-pending"
                [ "session_id", SessionId.value delegateId
                  "event_id", EventId.value failure.EventId
                  "result", CasebookMutationError.describe (CasebookMutationError.AppendFailure failure) ]
        | CaseFinalizeCommitment.PhaseConflict reason ->
            Diagnostic.fatal "case-finalization-failed" [ "session_id", SessionId.value delegateId; "result", reason ]

            raise (
                invalidOp (
                    sprintf "CASE-003: delegate %s finalization conflict: %s" (SessionId.value delegateId) reason
                )
            )

    let private finalizeStagedDelegate
        (scope: PluginRuntimeScope)
        (workspaceDirectory: string option)
        (finalizeDelegate: string -> string -> Task<CaseFinalizeSettlement>)
        (delegateId: SessionId)
        : Task =
        task {
            // F35: capture the exact finalize evidence BEFORE deciding identity
            // lifetime. The identity drop below is explicit and owner-driven —
            // it runs only for a durably settled finalize, never in a finally
            // that would also erase the identity a failed finalize still needs.
            let! settlementOpt = finalizeDelegateIfRoot workspaceDirectory finalizeDelegate delegateId

            match settlementOpt with
            | None -> ()
            | Some settled -> finalizeDisposition scope delegateId settled
        }

    let finalizePreparedDelegate
        (scope: PluginRuntimeScope)
        (workspaceDirectory: string option)
        (finalizeDelegate: string -> string -> Task<CaseFinalizeSettlement>)
        (SessionDeletionPreparation(_, _, delegateToFinalize))
        : Task =
        delegateToFinalize
        |> Option.map (finalizeStagedDelegate scope workspaceDirectory finalizeDelegate)
        |> Option.defaultValue (Task.FromResult() :> Task)

    let private cleanupRuntime
        (scope: PluginRuntimeScope)
        (runtimeOpt: SyncDelegateRuntime option)
        (cleanupDelegateDraft: string -> unit)
        (sessionId: SessionId)
        : Task =
        task {
            match runtimeOpt with
            | Some runtime -> runtime.CancelSession sessionId
            | None -> ()

            cleanupDelegateDraft (SessionId.value sessionId)
        }

    let handle
        (scope: PluginRuntimeScope)
        (cleanupDelegateDraft: string -> unit)
        (signalReconciler: HostSignal -> unit)
        (sessionId: SessionId)
        (onSessionDeleted: (SessionId -> unit) option)
        (SessionDeletionPreparation(parentSessionIdOpt, stagedDelegate, _))
        : Task =
        scope.LoopSensor.DropSession sessionId

        // STRENGTH-004/011: owner deletion cancels the decision-local
        // InternalLeaf immediately. CancelOwner completes the waiting
        // decision before its best-effort physical abort, so no deleted
        // owner can keep a Replica eligible for later collection.
        onSessionDeleted |> Option.iter (fun onDeleted -> onDeleted sessionId)

        // OpenCode recursively emits child SessionDeleted before the owner
        // SessionDeleted. An attached Delegate child must retire its live
        // binding without clearing the Casebook draft; the later owner
        // event is the graceful ReuseScope-close signal that finalizes it.
        // A continued owner Invoke consumes the staged child as unexpected
        // deletion and cleans its draft instead of reusing the dead child.
        let signal = SessionDeleted(sessionId, parentSessionIdOpt)

        task {
            if not stagedDelegate then
                do! cleanupRuntime scope scope.SyncDelegateRuntime cleanupDelegateDraft sessionId

            scope.Sessions.Quiescence.DropSession sessionId

            do! scope.DisposeSession(SessionId.value sessionId)

            signalReconciler signal
        }
