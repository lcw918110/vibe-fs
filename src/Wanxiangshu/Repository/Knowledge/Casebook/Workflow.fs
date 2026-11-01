namespace Wanxiangshu.Repository.Knowledge.Casebook

open Fable.Core
open Fable.Core.JsInterop
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Persistence.EventStore

module CasebookFeature =

    let MarkerDirectory = ".wanxiang/casebook"

    [<Import("existsSync", "node:fs")>]
    let private existsSync (path: string) : bool = jsNative

    [<Import("join", "node:path")>]
    let private pathJoin (a: string) (b: string) : string = jsNative

    let isEnabled (workspaceRoot: string) : bool =
        try
            existsSync (pathJoin workspaceRoot MarkerDirectory)
        with _ ->
            false

module CasebookWorkflow =

    let archiveCase (store: IEventStore) (case: Case) : Task<Result<unit, CasebookMutationError>> =
        task {
            let canonical =
                { case with
                    Observations = Observations.normalize case.Observations }

            match! CasebookStore.appendCaptured store canonical with
            | Ok _ -> return Ok()
            | Error err -> return Error(CasebookMutationError.AppendFailure err)
        }

    let fetchCase
        (store: IEventStore)
        (capacity: int)
        (identityOrSessionId: string)
        : Task<Result<Case option, string>> =
        task {
            let cases = CasebookProjection.projectCases store capacity
            return Ok(Map.tryFind identityOrSessionId cases)
        }

    let fetchCaseByIdentity (store: IEventStore) (identity: string) : Task<Result<Case option, string>> =
        fetchCase store 0 identity

    let checkFreshness (stored: Case) (replayed: Observation list) : ReplayResult =
        Observations.classifyReplay stored.Observations replayed

    let private staleNeedsRefresh (case: Case) (root: string) : Task<bool> =
        task {
            let! replayed = CasebookReplay.replayAll root case.Observations

            match checkFreshness case replayed with
            | ReplayResult.Fresh -> return false
            | ReplayResult.Stale -> return true
        }

    let needsRefresh
        (store: IEventStore)
        (capacity: int)
        (sessionId: string)
        (root: string)
        : Task<Result<bool, string>> =
        taskResult {
            let! caseOpt = fetchCase store capacity sessionId

            match caseOpt with
            | None -> return false
            | Some case -> return! staleNeedsRefresh case root |> TaskResultCE.ofTask
        }

    let refreshCase
        (store: IEventStore)
        (identity: string)
        (q: string)
        (a: string)
        (maintenanceFileState: string)
        (relatedPaths: string list)
        (observations: Observation list)
        : Task<Result<unit, CasebookMutationError>> =
        taskResult {
            let! _ =
                CasebookStore.appendRefreshed store identity q a maintenanceFileState relatedPaths observations
                |> TaskResult.mapError CasebookMutationError.AppendFailure

            return ()
        }

    let refreshWithDiff
        (store: IEventStore)
        (identity: string)
        (diff: string)
        (newStateRef: string)
        (q: string)
        (a: string)
        : Task<Result<unit, CasebookMutationError>> =
        taskResult {
            let! caseOpt =
                fetchCase store 0 identity
                |> TaskResult.mapError CasebookMutationError.PreparationRejected

            match caseOpt with
            | None -> return! Error(CasebookMutationError.CaseMissing identity)
            | Some existing ->
                let related = existing.RelatedPaths
                let obs = existing.Observations
                do! refreshCase store identity q a newStateRef related obs
                return ()
        }

    let finalizeCase (store: IEventStore) (case: Case) : Task<Result<unit, CasebookMutationError>> =
        task {
            match! fetchCase store 0 case.Identity with
            | Error err -> return Error(CasebookMutationError.PreparationRejected err)
            | Ok(Some _) -> return Error(CasebookMutationError.AlreadyFinalized case.Identity)
            | Ok None -> return! archiveCase store case
        }

    let touchCaseAccess (store: IEventStore) (identity: string) : Task<Result<unit, CasebookMutationError>> =
        task {
            match! CasebookStore.appendAccessed store identity with
            | Ok _ -> return Ok()
            | Error err -> return Error(CasebookMutationError.AppendFailure err)
        }
