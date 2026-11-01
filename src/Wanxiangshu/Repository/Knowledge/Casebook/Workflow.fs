namespace Wanxiangshu.Repository.Knowledge.Casebook

open Fable.Core
open Fable.Core.JsInterop
open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
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

module CasebookServiceBinding =

    [<Import("sessionDraftStore", "wanxiangli/casebook")>]
    let private drafts: obj = jsNative

    [<Import("casebookIndex", "wanxiangli/casebook")>]
    let private index: obj = jsNative

    [<Import("CasebookObservationCollector", "wanxiangli/casebook-access")>]
    let private collectorClass: obj = jsNative

    [<Emit("new $0()")>]
    let private constructCollector (_constructor: obj) : obj = jsNative

    let observationCollector = constructCollector collectorClass

    let drainCollector (collector: obj) (identity: string) : Observation list =
        let items: obj array = collector?Drain identity
        items
        |> Array.toList
        |> List.map (fun item -> Observation.FileRead(string item?path, string item?contentHash))

    let private drain identity =
        drainCollector observationCollector identity |> CasebookValues.observationsToNative

    [<Import("cleanupCasebookDraft", "wanxiangli/casebook")>]
    let private cleanup (_drafts: obj) (_drain: string -> obj array) (_identity: string) : unit = jsNative

    let cleanupDraft identity = cleanup drafts drain identity

    let private nativeResult encode result : obj =
        match result with
        | Ok value -> createObj [ "ok" ==> true; "value" ==> encode value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

    let private appendResult result : obj =
        nativeResult (fun _ -> ()) result

    let private storePort (store: IEventStore) =
        let current () =
            createObj [ "ok" ==> true; "value" ==> CasebookProjection.currentNative store ]

        let captured value =
            task {
                let! result = CasebookStore.appendCaptured store (CasebookValues.caseOfNative value)
                return appendResult result
            }

        let refreshed (change: obj) =
            task {
                let! result =
                    CasebookStore.appendRefreshed
                        store
                        (unbox change?identity)
                        (unbox change?q)
                        (unbox change?a)
                        (unbox change?maintenanceFileState)
                        (unbox<string array> change?relatedPaths |> Array.toList)
                        (CasebookValues.observationsOfNative (unbox change?observations))

                return appendResult result
            }

        let accessed identity =
            task {
                let! result = CasebookStore.appendAccessed store identity
                return appendResult result
            }

        createObj
            [ "current" ==> current
              "appendCaptured" ==> captured
              "appendRefreshed" ==> refreshed
              "appendAccessed" ==> accessed ]

    let private baselinePort store root =
        let capture (paths: string array) =
            task {
                let! result = CasebookCapture.freezeCompletionState store root (Array.toList paths)
                return nativeResult id result
            }

        let compare (paths: string array) (baseline: string) =
            task {
                let! result = CasebookCapture.computeMaintenanceDiff store root (Array.toList paths) baseline

                return
                    nativeResult
                        (fun (value: CasebookCapture.MaintenanceCapture) ->
                            createObj
                                [ "refresh" ==> value.Refresh
                                  "targetState" ==> value.TargetState
                                  "diffSummary" ==> value.DiffSummary ])
                        result
            }

        createObj
            [ "capture" ==> capture
              "compare" ==> Func<string array, string, Task<obj>>(fun paths baseline -> compare paths baseline) ]

    let private runBookkeeper (request: obj) =
        task {
            let kind =
                match unbox<string> request?kind with
                | "finalize" -> BookkeeperRequest.CaseFinalize
                | "refresh" -> BookkeeperRequest.CaseRefresh
                | kind -> invalidOp $"unknown bookkeeper request kind: {kind}"

            let! result =
                BookkeeperRuntime.runTransaction
                    kind
                    (SessionId.create (unbox request?identity))
                    (unbox request?question)
                    (unbox request?answer)
                    (CasebookValues.observationsOfNative (unbox request?observations))
                    (Option.ofObj (unbox<string> request?transcript))

            return nativeResult (fun (question, answer) -> createObj [ "question" ==> question; "answer" ==> answer ]) result
        }

    [<Import("CasebookService", "wanxiangli/casebook")>]
    let private serviceClass: obj = jsNative

    [<Emit("new $0($1, $2, $3, $4, $5, $6, $7)")>]
    let private construct
        (_constructor: obj)
        (_drafts: obj)
        (_index: obj)
        (_store: obj)
        (_baseline: obj)
        (_bookkeeper: obj -> Task<obj>)
        (_drain: string -> obj array)
        (_enabled: unit -> bool)
        : obj = jsNative

    let private create root store =
        construct
            serviceClass
            drafts
            index
            (storePort store)
            (baselinePort store root)
            runBookkeeper
            drain
            (fun () -> CasebookFeature.isEnabled root)

    [<Import("archiveCase", "wanxiangli/casebook")>]
    let private archive (_store: obj) (_case: obj) : Task<obj> = jsNative

    [<Import("finalizeCase", "wanxiangli/casebook")>]
    let private finalize (_store: obj) (_case: obj) : Task<obj> = jsNative

    [<Import("finalizeCaptured", "wanxiangli/casebook")>]
    let private finalizeCapture (_store: obj) (_index: obj) (_case: obj) : Task<obj> = jsNative

    [<Import("refreshWithDiff", "wanxiangli/casebook")>]
    let private refreshDiff (_store: obj) (_change: obj) : Task<obj> = jsNative

    let private mutationError (error: obj) =
        match unbox<string> error?kind with
        | "already-finalized" -> CasebookMutationError.AlreadyFinalized(unbox error?identity)
        | "case-missing" -> CasebookMutationError.CaseMissing(unbox error?identity)
        | "preparation-rejected" -> CasebookMutationError.PreparationRejected(unbox error?reason)
        | "append-failure" -> CasebookMutationError.AppendFailure(unbox error?failure)
        | kind -> invalidOp $"unknown casebook mutation error kind: {kind}"

    let private mutationResult decode (result: obj) =
        if result?ok then Ok(decode result?value) else Error(mutationError result?error)

    let private settlement (result: obj) =
        let identity: string = result?identity

        match unbox<string> result?kind with
        | "finalized" -> CaseFinalizeSettlement.finalized identity
        | "nothing-to-finalize" -> CaseFinalizeSettlement.nothingToFinalize identity
        | "not-committed" -> CaseFinalizeSettlement.notCommitted identity (unbox result?reason)
        | "unknown" -> CaseFinalizeSettlement.unknown identity (unbox result?reason)
        | "phase-conflict" -> CaseFinalizeSettlement.phaseConflict identity (unbox result?reason)
        | "persistence-failed" -> CaseFinalizeSettlement.persistenceFailed identity (unbox result?failure)
        | kind -> invalidOp $"unknown casebook finalization kind: {kind}"

    let finalizeDraft root store (identity: string) : Task<CaseFinalizeSettlement> =
        task {
            let service = create root store
            let! result = service?finalizeDraft identity |> unbox<Task<obj>>
            return settlement result
        }

    let refresh root store (identity: string) : Task<Result<bool, CasebookMutationError>> =
        task {
            let service = create root store
            let! result = service?refresh identity |> unbox<Task<obj>>
            return mutationResult unbox<bool> result
        }

    let touch root store (identity: string) : Task<Result<unit, CasebookMutationError>> =
        task {
            let service = create root store
            let! result = service?touch identity |> unbox<Task<obj>>
            return mutationResult ignore result
        }

    let fetch root store (shelfmark: string) (notify: CasebookAppendFailure -> unit) : Task<obj> =
        let service = create root store
        let notifyFailure (failure: obj) = notify (unbox failure)
        service?fetch (shelfmark, notifyFailure)

    let archiveCase store case : Task<Result<unit, CasebookMutationError>> =
        task {
            let! result = archive (storePort store) (CasebookValues.caseToNative case)
            return mutationResult ignore result
        }

    let finalizeCase store case : Task<Result<unit, CasebookMutationError>> =
        task {
            let! result = finalize (storePort store) (CasebookValues.caseToNative case)
            return mutationResult ignore result
        }

    let finalizeCaptured store case : Task<CaseFinalizeSettlement> =
        task {
            let! result = finalizeCapture (storePort store) index (CasebookValues.caseToNative case)
            return settlement result
        }

    let refreshWithDiff store identity q a maintenanceFileState : Task<Result<unit, CasebookMutationError>> =
        task {
            let change =
                createObj
                    [ "identity" ==> identity
                      "q" ==> q
                      "a" ==> a
                      "maintenanceFileState" ==> maintenanceFileState ]

            let! result = refreshDiff (storePort store) change
            return mutationResult ignore result
        }

module CasebookWorkflow =

    let archiveCase (store: IEventStore) (case: Case) : Task<Result<unit, CasebookMutationError>> =
        CasebookServiceBinding.archiveCase store case

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
        CasebookServiceBinding.refreshWithDiff store identity q a newStateRef

    let finalizeCase (store: IEventStore) (case: Case) : Task<Result<unit, CasebookMutationError>> =
        CasebookServiceBinding.finalizeCase store case

    let touchCaseAccess (store: IEventStore) (identity: string) : Task<Result<unit, CasebookMutationError>> =
        task {
            match! CasebookStore.appendAccessed store identity with
            | Ok _ -> return Ok()
            | Error err -> return Error(CasebookMutationError.AppendFailure err)
        }
