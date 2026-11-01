namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Persistence.EventStore

module CasebookSurface =

    let private storeOf (value: obj) : IEventStore = (unbox<EventStoreHandle> value).Store

    let private stringsOf (v: obj) : string array =
        if isNull v then [||] else unbox<string array> v

    let private arrayOf (v: obj) : obj array =
        if isNull v then [||] else unbox<obj array> v

    // ── Observation translation (JS ↔ F#) ────────────────────────────────────

    let private observationToJs (observation: Observation) : obj =
        match observation with
        | Observation.FileRead(path, contentHash) ->
            box
                {| kind = "file-read"
                   path = path
                   contentHash = contentHash |}
        | Observation.GlobResult(pattern, paths) ->
            box
                {| kind = "glob-result"
                   pattern = pattern
                   paths = List.toArray paths |}
        | Observation.GrepResult(pattern, matches) ->
            let flat =
                matches
                |> List.map (fun (path, index, text) -> box [| box path; box index; box text |])
                |> List.toArray

            box
                {| kind = "grep-result"
                   pattern = pattern
                   matches = flat |}

    let contentHash (text: string) : string = CasebookCapture.contentHash text

    let ofExecCommand (command: string) : obj =
        match CasebookCapture.ofExecCommand command with
        | None -> null
        | Some observation -> observationToJs observation

    let private observationOfJs (value: obj) : Result<Observation, string> =
        let kind = string (value?kind)
        let intOfJs (v: obj) : int = int (string v)

        match kind with
        | "file-read" -> Ok(Observation.FileRead(string (value?path), string (value?contentHash)))
        | "glob-result" ->
            let paths = stringsOf (value?paths) |> Array.toList
            Ok(Observation.GlobResult(string (value?pattern), paths))
        | "grep-result" ->
            let matches =
                arrayOf (value?matches)
                |> Array.toList
                |> List.map (fun m ->
                    let arr = arrayOf m
                    (string arr[0], intOfJs arr[1], string arr[2]))

            Ok(Observation.GrepResult(string (value?pattern), matches))
        | other -> Error $"unknown observation kind: {other}"

    // ── Case translation (JS ↔ F#) ───────────────────────────────────────────

    let private caseToJs (case: Case) : obj =
        box
            {| identity = case.Identity
               sessionId = case.Identity
               sourceTrace = case.SourceTrace
               q = case.Q
               a = case.A
               relatedPaths = case.RelatedPaths |> List.toArray
               completionFileState = case.CompletionFileState
               maintenanceFileState = case.MaintenanceFileState
               accessOrder = case.AccessOrder
               lastAccessOrder = case.AccessOrder
               observations = case.Observations |> List.map observationToJs |> List.toArray |}

    let private observationsOfJs (value: obj) : Result<Observation list, string> =
        let rec loop (acc: Observation list) (remaining: obj list) =
            match remaining with
            | [] -> Ok(List.rev acc)
            | item :: rest ->
                observationOfJs item
                |> Result.bind (fun observation -> loop (observation :: acc) rest)

        loop [] (arrayOf value |> Array.toList)

    let private caseOfJs (value: obj) : Result<Case, string> =
        let identity =
            if
                not (isNull (value?identity))
                && not (String.IsNullOrWhiteSpace(string value?identity))
            then
                string value?identity
            elif
                not (isNull (value?sessionId))
                && not (String.IsNullOrWhiteSpace(string value?sessionId))
            then
                string value?sessionId
            else
                ""

        let sourceTrace =
            if not (isNull (value?sourceTrace)) then
                string value?sourceTrace
            else
                ""

        let q = if not (isNull (value?q)) then string value?q else ""
        let a = if not (isNull (value?a)) then string value?a else ""

        let relatedPaths =
            if not (isNull (value?relatedPaths)) then
                stringsOf (value?relatedPaths) |> Array.toList
            else
                []

        let completionFileState =
            if not (isNull (value?completionFileState)) then
                string value?completionFileState
            else
                ""

        let maintenanceFileState =
            if not (isNull (value?maintenanceFileState)) then
                string value?maintenanceFileState
            else
                completionFileState

        let accessOrder =
            if not (isNull (value?accessOrder)) then
                int64 (string value?accessOrder)
            elif not (isNull (value?lastAccessOrder)) then
                int64 (string value?lastAccessOrder)
            else
                0L

        let observationsResult =
            if not (isNull (value?observations)) then
                observationsOfJs (value?observations)
            else
                Ok []

        observationsResult
        |> Result.map (fun obs ->
            { Identity = identity
              SourceTrace = sourceTrace
              Q = q
              A = a
              RelatedPaths = relatedPaths
              CompletionFileState = completionFileState
              MaintenanceFileState = maintenanceFileState
              AccessOrder = accessOrder
              Observations = Observations.normalize obs })

    // ── Normalization & Replay ───────────────────────────────────────────────

    let normalize (observations: obj array) : obj array =
        observations
        |> Array.toList
        |> List.map observationOfJs
        |> List.choose (fun item ->
            match item with
            | Ok o -> Some o
            | Error _ -> None)
        |> Observations.normalize
        |> List.map observationToJs
        |> List.toArray

    let classifyReplay (stored: obj array) (replayed: obj array) : string =
        let storedObs =
            stored
            |> Array.toList
            |> List.map observationOfJs
            |> List.choose (fun item ->
                match item with
                | Ok o -> Some o
                | Error _ -> None)

        let replayedObs =
            replayed
            |> Array.toList
            |> List.map observationOfJs
            |> List.choose (fun item ->
                match item with
                | Ok o -> Some o
                | Error _ -> None)

        match Observations.classifyReplay storedObs replayedObs with
        | ReplayResult.Fresh -> "fresh"
        | ReplayResult.Stale -> "stale"

    // ── Workflows ────────────────────────────────────────────────────────────

    let private runWorkflowTask
        (workflow: IEventStore -> Case -> Task<Result<unit, CasebookMutationError>>)
        (store: IEventStore)
        (parsed: Case)
        : Task<obj> =
        task {
            match! workflow store parsed with
            | Ok() -> return box {| ok = true |}
            | Error error -> return CasebookAppendSurface.mutationErrorToJs error
        }

    let private runStoreWorkflow
        (workflow: IEventStore -> Case -> Task<Result<unit, CasebookMutationError>>)
        (store: IEventStore)
        (case: obj)
        : Task<obj> =
        match caseOfJs case with
        | Error message -> Task.FromResult(box {| ok = false; error = message |})
        | Ok parsed -> runWorkflowTask workflow store parsed

    let private runUnitResult (operation: Task<Result<unit, CasebookMutationError>>) : Task<obj> =
        task {
            match! operation with
            | Ok() -> return box {| ok = true |}
            | Error error -> return CasebookAppendSurface.mutationErrorToJs error
        }

    let fetchCase (store: obj) (capacity: int) (sessionId: string) : Task<obj> =
        let internalStore = storeOf store

        task {
            match! CasebookWorkflow.fetchCase internalStore capacity sessionId with
            | Error message -> return box {| ok = false; error = message |}
            | Ok None ->
                let value: obj = null
                return box {| ok = true; value = value |}
            | Ok(Some case) -> return box {| ok = true; value = caseToJs case |}
        }

    let fetchCaseByIdentity (store: obj) (identity: string) : Task<obj> =
        let internalStore = storeOf store

        task {
            match! CasebookWorkflow.fetchCaseByIdentity internalStore identity with
            | Error _
            | Ok None ->
                let value: obj = null
                return value
            | Ok(Some case) -> return caseToJs case
        }

    let refresh (store: obj) (sessionId: string) (q: string) (a: string) (observations: obj array) : Task<obj> =
        let internalStore = storeOf store

        match observationsOfJs (box observations) with
        | Error message -> Task.FromResult(box {| ok = false; error = message |})
        | Ok parsed -> runUnitResult (CasebookWorkflow.refreshCase internalStore sessionId q a "" [] parsed)

    let refreshWithDiff
        (store: obj)
        (identity: string)
        (diff: string)
        (newStateRef: string)
        (updates: obj)
        : Task<obj> =
        let internalStore = storeOf store
        let argCount = emitJsExpr () "arguments.length" |> unbox<int>

        let q, a =
            if argCount >= 6 then
                let qVal = emitJsExpr () "arguments[4]" |> string
                let aVal = emitJsExpr () "arguments[5]" |> string
                qVal, aVal
            else
                let qVal = if isNull (updates?q) then "" else string updates?q
                let aVal = if isNull (updates?a) then "" else string updates?a
                qVal, aVal

        runUnitResult (CasebookWorkflow.refreshWithDiff internalStore identity diff newStateRef q a)

    let needsRefresh (store: obj) (capacity: int) (sessionId: string) (root: string) : Task<obj> =
        let internalStore = storeOf store

        task {
            match! CasebookWorkflow.needsRefresh internalStore capacity sessionId root with
            | Ok value -> return box {| ok = true; value = value |}
            | Error message -> return box {| ok = false; error = message |}
        }

    let touchAccess (store: obj) (sessionId: string) : Task<obj> =
        let internalStore = storeOf store
        runUnitResult (CasebookWorkflow.touchCaseAccess internalStore sessionId)

    let evictCase (store: obj) (sessionId: string) : Task<obj> =
        let internalStore = storeOf store

        task {
            match! CasebookStore.appendEvicted internalStore sessionId with
            | Ok _ -> return box {| ok = true |}
            | Error failure ->
                return CasebookAppendSurface.mutationErrorToJs (CasebookMutationError.AppendFailure failure)
        }

    let featureEnabled (workspaceRoot: string) : bool = CasebookFeature.isEnabled workspaceRoot

    let finalize (store: obj) (case: obj) : Task<obj> =
        let internalStore = storeOf store
        runStoreWorkflow CasebookWorkflow.finalizeCase internalStore case

    let archive (store: obj) (case: obj) : Task<obj> =
        let internalStore = storeOf store
        runStoreWorkflow CasebookWorkflow.archiveCase internalStore case

    let archiveCase (store: obj) (case: obj) : Task<obj> = archive store case

    let finalizeEngineerCase
        (store: obj)
        (identity: string)
        (trace: string)
        (question: string)
        (answer: string)
        (relatedPathsRaw: obj)
        (baselineJson: string)
        : Task<obj> =
        task {
            let internalStore = storeOf store
            let paths = stringsOf relatedPathsRaw |> Array.toList

            let case: Case =
                { Identity = identity
                  SourceTrace = trace
                  Q = question
                  A = answer
                  RelatedPaths = paths
                  CompletionFileState = baselineJson
                  MaintenanceFileState = baselineJson
                  AccessOrder = 0L
                  Observations = [] }

            match! CasebookWorkflow.archiveCase internalStore case with
            | Ok() ->
                try
                    let! _ = CasebookIndexSurface.refresh (box (EventStoreHandle.Create internalStore)) 256
                    ()
                with _ ->
                    ()

                return box {| kind = "finalized" |}
            | Error(CasebookMutationError.AppendFailure failure) ->
                return CasebookAppendSurface.finalizeFailureToJs failure
            | Error error ->
                return
                    box
                        {| kind = "notCommitted"
                           error = CasebookMutationError.describe error |}
        }

    // ── KR-003, KR-004, KR-010, KR-014, KR-015 Exports ──────────────────────

    let recordSubstantiveAccess (tracker: obj) (toolName: string) (args: obj) (committed: bool) : unit =
        let t =
            if not (isNull tracker) && emitJsExpr tracker "$0._tracker !== undefined" then
                unbox<AccessTracker> (tracker?_tracker)
            else
                unbox<AccessTracker> tracker

        CasebookCapture.recordSubstantiveAccess t toolName args committed

    let createAccessTracker () : obj =
        let tracker = CasebookCapture.createAccessTracker ()

        box
            {| recordRead = fun (path: string) (hash: string) -> tracker.RecordRead(path, hash)
               recordCreate = fun (path: string) -> tracker.RecordCreate(path)
               recordEdit = fun (path: string) -> tracker.RecordEdit(path)
               recordDelete = fun (path: string) -> tracker.RecordDelete(path)
               recordMove = fun (src: string) (dst: string) -> tracker.RecordMove(src, dst)
               recordGrep = fun (pat: string) (path: string) -> tracker.RecordGrep(pat, path)
               recordGlob = fun (pat: string) -> tracker.RecordGlob(pat)
               recordAttemptedMutation =
                fun (path: string) (committed: bool) -> tracker.RecordAttemptedMutation(path, committed)
               getRelatedPaths = fun () -> tracker.GetRelatedPaths() |> List.toArray
               RecordRead = fun (path: string) (hash: string) -> tracker.RecordRead(path, hash)
               RecordCreate = fun (path: string) -> tracker.RecordCreate(path)
               RecordEdit = fun (path: string) -> tracker.RecordEdit(path)
               RecordDelete = fun (path: string) -> tracker.RecordDelete(path)
               RecordMove = fun (src: string) (dst: string) -> tracker.RecordMove(src, dst)
               RecordGrep = fun (pat: string) (path: string) -> tracker.RecordGrep(pat, path)
               RecordGlob = fun (pat: string) -> tracker.RecordGlob(pat)
               RecordAttemptedMutation =
                fun (path: string) (committed: bool) -> tracker.RecordAttemptedMutation(path, committed)
               GetRelatedPaths = fun () -> tracker.GetRelatedPaths() |> List.toArray
               _tracker = tracker |}

    let isSubstantiveTool (toolName: string) : bool =
        CasebookCapture.isSubstantiveTool toolName

    let freezeCompletionState (store: obj) (workspaceRoot: string) (pathsRaw: obj) : Task<obj> =
        task {
            let paths = stringsOf pathsRaw |> Array.toList

            match! CasebookCapture.freezeCompletionState (storeOf store) workspaceRoot paths with
            | Ok json -> return box json
            | Error err -> return box {| ok = false; error = err |}
        }

    let computeMaintenanceDiff (store: obj) (workspaceRoot: string) (pathsRaw: obj) (baseline: string) : Task<obj> =
        task {
            let paths = stringsOf pathsRaw |> Array.toList

            match! CasebookCapture.computeMaintenanceDiff (storeOf store) workspaceRoot paths baseline with
            | Ok captured ->
                return
                    box
                        {| hasDiff = captured.Refresh
                           diffSummary = captured.DiffSummary
                           targetState = captured.TargetState |}
            | Error err -> return box {| ok = false; error = err |}
        }

    let caseIdentityForInvocation (sessionId: string) (invocationId: string) : string =
        CasebookCapture.caseIdentityForInvocation sessionId invocationId

    let mergeFissionSubstantiveAccess (preFission: obj) (laneAccesses: obj) : obj =
        let pre = stringsOf preFission |> Array.toList

        let lanes =
            arrayOf laneAccesses
            |> Array.map (fun l -> stringsOf l |> Array.toList)
            |> Array.toList

        CasebookCapture.mergeFissionSubstantiveAccess pre lanes |> List.toArray |> box

    let truncateDiffForBudget (diff: string) (budget: int) : obj =
        CasebookCapture.truncateDiffForBudget diff budget
