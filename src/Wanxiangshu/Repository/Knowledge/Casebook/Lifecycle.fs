namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

/// CASE-003/010 / KR-010: process-local Casebook session wiring.
module CasebookLifecycle =

    [<Import("sessionDraftStore", "wanxiangli/casebook")>]
    let private sessionDraftStore: obj = jsNative

    [<Import("CasebookObservationCollector", "wanxiangli/casebook-access")>]
    type private LibraryObservationCollector() =
        member _.Collect(sessionId: string, toolName: string, args: obj, output: string) : unit = jsNative
        member _.RecordFileRead(sessionId: string, path: string, output: string, ?contentHash: string) : unit = jsNative
        member _.Drain(sessionId: string) : obj array = jsNative
        member _.Count(sessionId: string) : int = jsNative

    type CasebookObservationCollector() =
        let inner = LibraryObservationCollector()

        member _.Collect(sessionId: string, toolName: string, args: obj, output: string) : unit =
            inner.Collect(sessionId, toolName, args, output)

        member _.RecordFileRead(sessionId: string, path: string, output: string) : unit =
            let hash = CasebookCapture.contentHash output
            inner.RecordFileRead(sessionId, path, output, hash)

        member _.Drain(sessionId: string) : Observation list =
            let items = inner.Drain(sessionId)
            items
            |> Array.toList
            |> List.map (fun o -> Observation.FileRead(string o?path, string o?contentHash))

        member _.Count(sessionId: string) : int =
            inner.Count(sessionId)

    let collector = CasebookObservationCollector()

    let private stateGate = obj ()
    // DSL-MUTABLE: resource
    let mutable private enabledWorkspace: string option = None

    let setEnabled (workspaceRoot: string option) : unit =
        lock stateGate (fun () ->
            enabledWorkspace <-
                match workspaceRoot with
                | Some root when CasebookFeature.isEnabled root -> Some root
                | _ -> None)

    let isEnabled () : bool =
        lock stateGate (fun () -> enabledWorkspace.IsSome)

    let notePrompt (delegateSessionId: string) (q: string) : unit =
        sessionDraftStore?setQ (delegateSessionId, q)

    let noteAnswer (delegateSessionId: string) (a: string) : unit =
        sessionDraftStore?setA (delegateSessionId, a)

    let cleanupDraft (delegateSessionId: string) : unit =
        sessionDraftStore?clear (delegateSessionId)
        collector.Drain delegateSessionId |> ignore

    let private probeExistingScope
        (store: IEventStore)
        (delegateSessionId: string)
        : Task<Result<Case option, string>> =
        task {
            match! CasebookWorkflow.fetchCase store 0 delegateSessionId with
            | Error reason -> return Error(sprintf "cannot confirm case scope %s: %s" delegateSessionId reason)
            | Ok existing -> return Ok existing
        }

    let private dispositionOfExistingScope
        (delegateSessionId: string)
        (existing: Result<Case option, string>)
        : CaseFinalizeSettlement option =
        match existing with
        | Error reason -> Some(CaseFinalizeSettlement.notCommitted delegateSessionId reason)
        | Ok(Some case) ->
            Some(
                CaseFinalizeSettlement.phaseConflict
                    delegateSessionId
                    (sprintf "case already finalized for scope %s" case.Identity)
            )
        | Ok None -> None

    let private refreshIndexThenSettle (store: IEventStore) (delegateSessionId: string) : Task<CaseFinalizeSettlement> =
        task {
            try
                let! _ = CasebookIndexSurface.refresh (box (EventStoreHandle.Create store)) 256
                return CaseFinalizeSettlement.finalized delegateSessionId
            with ex ->
                return CaseFinalizeSettlement.unknown delegateSessionId ex.Message
        }

    let private archiveCase
        (store: IEventStore)
        (delegateSessionId: string)
        (case: Case)
        : Task<CaseFinalizeSettlement> =
        task {
            match! CasebookWorkflow.finalizeCase store case with
            | Error(CasebookMutationError.AlreadyFinalized identity) ->
                return
                    CaseFinalizeSettlement.phaseConflict
                        delegateSessionId
                        (CasebookMutationError.describe (CasebookMutationError.AlreadyFinalized identity))
            | Error(CasebookMutationError.AppendFailure failure) ->
                return CaseFinalizeSettlement.persistenceFailed delegateSessionId failure
            | Error error ->
                return CaseFinalizeSettlement.notCommitted delegateSessionId (CasebookMutationError.describe error)
            | Ok() ->
                CasebookIndexSurface.invalidate ()
                return! refreshIndexThenSettle store delegateSessionId
        }

    let private finalizeOkCase
        (workspaceRoot: string)
        (delegateSessionId: string)
        (q': string)
        (a': string)
        (observations: Observation list)
        (store: IEventStore)
        : Task<CaseFinalizeSettlement> =
        task {
            let related =
                observations
                |> List.choose (function
                    | Observation.FileRead(p, _) -> Some p
                    | _ -> None)
                |> List.distinct
                |> List.sort

            let! freezeResult = CasebookCapture.freezeCompletionState store workspaceRoot related

            match freezeResult with
            | Error reason ->
                return
                    CaseFinalizeSettlement.notCommitted delegateSessionId (sprintf "freeze baseline failed: %s" reason)
            | Ok baselineStr ->
                let case: Case =
                    { Identity = delegateSessionId
                      SourceTrace = delegateSessionId
                      Q = q'
                      A = a'
                      RelatedPaths = related
                      CompletionFileState = baselineStr
                      MaintenanceFileState = baselineStr
                      AccessOrder = 0L
                      Observations = observations }

                return! archiveCase store delegateSessionId case
        }

    let private spawnFinalize
        (workspaceRoot: string)
        (delegateSessionId: string)
        (lastQ: string)
        (a: string)
        (observations: Observation list)
        (transcript: string option)
        (store: IEventStore)
        : Task<CaseFinalizeSettlement> =
        task {
            let! spawned =
                BookkeeperRuntime.runTransaction
                    BookkeeperRequest.CaseFinalize
                    (SessionId.create delegateSessionId)
                    lastQ
                    a
                    observations
                    transcript

            match spawned with
            | Error reason -> return CaseFinalizeSettlement.notCommitted delegateSessionId reason
            | Ok(q', a') -> return! finalizeOkCase workspaceRoot delegateSessionId q' a' observations store
        }

    let private continueFinalizeDecision
        (store: IEventStore)
        (workspaceRoot: string)
        (delegateSessionId: string)
        (draft: obj)
        (a: string)
        : Task<CaseFinalizeSettlement> =
        task {
            let observations = collector.Drain delegateSessionId

            let lastQ: string = sessionDraftStore?lastQuestion (draft)
            let transcript: string = sessionDraftStore?transcript (draft)

            let! existing = probeExistingScope store delegateSessionId

            match dispositionOfExistingScope delegateSessionId existing with
            | Some settled -> return settled
            | None -> return! spawnFinalize workspaceRoot delegateSessionId lastQ a observations (Some transcript) store
        }

    let private dispatchFinalize
        (store: IEventStore)
        (workspaceRoot: string)
        (delegateSessionId: string)
        (draft: obj)
        (a: string)
        : Task<CaseFinalizeSettlement> =
        task {
            try
                let! result = continueFinalizeDecision store workspaceRoot delegateSessionId draft a
                return result
            with ex ->
                collector.Drain delegateSessionId |> ignore
                return CaseFinalizeSettlement.unknown delegateSessionId ex.Message
        }

    let private runFinalize
        (store: IEventStore)
        (workspaceRoot: string)
        (delegateSessionId: string)
        (draft: obj)
        (a: string)
        : Task<CaseFinalizeSettlement> =
        dispatchFinalize store workspaceRoot delegateSessionId draft a

    let private finalizeWithDraft
        (store: IEventStore)
        (workspaceRoot: string)
        (delegateSessionId: string)
        (draft: obj)
        (lastAnswer: string option)
        : Task<CaseFinalizeSettlement> =
        task {
            match lastAnswer with
            | None ->
                collector.Drain delegateSessionId |> ignore
                return CaseFinalizeSettlement.nothingToFinalize delegateSessionId
            | Some a -> return! runFinalize store workspaceRoot delegateSessionId draft a
        }

    let private finalizeIfDrafted
        (store: IEventStore)
        (workspaceRoot: string)
        (delegateSessionId: string)
        : Task<CaseFinalizeSettlement> =
        task {
            let draftOpt: obj option = sessionDraftStore?tryTake (delegateSessionId)

            match draftOpt with
            | None ->
                collector.Drain delegateSessionId |> ignore
                return CaseFinalizeSettlement.nothingToFinalize delegateSessionId
            | Some draft ->
                let lastAnswer: string option = sessionDraftStore?lastAnswer (draft)
                return! finalizeWithDraft store workspaceRoot delegateSessionId draft lastAnswer
        }

    let tryFinalizeDraft
        (workspaceRoot: string)
        (store: IEventStore)
        (delegateSessionId: string)
        : Task<CaseFinalizeSettlement> =
        task {
            if CasebookFeature.isEnabled workspaceRoot then
                return! finalizeIfDrafted store workspaceRoot delegateSessionId
            else
                cleanupDraft delegateSessionId
                return CaseFinalizeSettlement.nothingToFinalize delegateSessionId
        }

    let finalizeEngineerCase
        (store: IEventStore)
        (identity: string)
        (sourceTrace: string)
        (q: string)
        (a: string)
        (relatedPaths: string list)
        (completionStateRef: string)
        : Task<CaseFinalizeSettlement> =
        task {
            let case: Case =
                { Identity = identity
                  SourceTrace = sourceTrace
                  Q = q
                  A = a
                  RelatedPaths = relatedPaths
                  CompletionFileState = completionStateRef
                  MaintenanceFileState = completionStateRef
                  AccessOrder = 0L
                  Observations = [] }

            return! archiveCase store identity case
        }

    let private refreshWhenTouched
        (store: IEventStore)
        (touched: Result<unit, CasebookMutationError>)
        : Task<Result<unit, CasebookMutationError>> =
        task {
            match touched with
            | Ok() ->
                CasebookIndexSurface.invalidate ()
                let! _ = CasebookIndexSurface.refresh (box (EventStoreHandle.Create store)) 256
                return Ok()
            | Error error -> return Error error
        }

    let private touchAccessEnabled
        (store: IEventStore)
        (sessionId: string)
        : Task<Result<unit, CasebookMutationError>> =
        task {
            try
                let! touched = CasebookWorkflow.touchCaseAccess store sessionId
                return! refreshWhenTouched store touched
            with ex ->
                return Error(CasebookMutationError.PreparationRejected ex.Message)
        }

    let touchAccess
        (workspaceRoot: string)
        (store: IEventStore)
        (sessionId: string)
        : Task<Result<unit, CasebookMutationError>> =
        task {
            if CasebookFeature.isEnabled workspaceRoot then
                return! touchAccessEnabled store sessionId
            else
                return Ok()
        }
