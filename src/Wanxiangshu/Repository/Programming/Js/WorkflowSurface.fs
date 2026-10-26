namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// Opaque workflow outcome. The semantic observation functions expose the
/// commit report and failure algebra without leaking JsToolOutcome's union.
[<RequireQualifiedAccess>]
module JsWorkflowSurface =

    type private JsWorkflowOutcomeHandle(outcome: JsToolWorkflow.JsToolOutcome) =
        member _.Value = outcome

    let private outcomeOf (value: obj) =
        (unbox<JsWorkflowOutcomeHandle> value).Value

    [<Emit("$0($1,$2)")>]
    let private apply2 (callback: obj) (readPaths: obj) (effectPaths: obj) : obj = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private promiseOf (value: obj) : JS.Promise<obj> = jsNative

    let private observationOf callback (reads: JsExplicitFileRead list) effectPaths =
        task {
            let! _ =
                unbox<Task<obj>> (
                    let observed =
                        reads
                        |> List.map (fun read -> createObj [ "path" ==> read.Path; "resultBytes" ==> read.ResultBytes ])
                        |> List.toArray

                    promiseOf (apply2 callback (box observed) (box (List.toArray effectPaths)))
                )

            return ()
        }

    let private runWithObservation
        (workspaceRoot: string)
        (role: string)
        (language: string)
        (program: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (store: obj)
        (fileAccessObservation: JsToolWorkflow.FileAccessObservation option)
        : Task<obj> =
        task {
            match JsGeneratorSurface.typedRole role language with
            | None -> return null
            | Some surface ->
                let persistence =
                    if isNull store then
                        None
                    else
                        Some(JsTransactionSurface.persistenceOf store)

                let! outcome =
                    match fileAccessObservation with
                    | None ->
                        JsToolWorkflow.run
                            surface.Capabilities
                            workspaceRoot
                            surface.BaseClassSource
                            program
                            deadlineMs
                            deadlineEpochMs
                            outputBoundBytes
                            persistence
                    | Some observe ->
                        JsToolWorkflow.runWithFileAccessObservation
                            surface.Capabilities
                            workspaceRoot
                            surface.BaseClassSource
                            program
                            deadlineMs
                            deadlineEpochMs
                            outputBoundBytes
                            persistence
                            observe

                return box (JsWorkflowOutcomeHandle outcome)
        }

    let run
        (workspaceRoot: string)
        (role: string)
        (language: string)
        (program: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (store: obj)
        : Task<obj> =
        runWithObservation workspaceRoot role language program deadlineMs deadlineEpochMs outputBoundBytes store None

    /// Execute the exact named-program input { name, program, intent }.
    /// Lifecycle control uses members inside another named program.
    let execute
        (workspaceRoot: string)
        (role: string)
        (language: string)
        (sessionId: string)
        (input: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (store: obj)
        : Task<obj> =
        task {
            match JsGeneratorSurface.typedRole role language with
            | None -> return null
            | Some surface ->
                let persistence =
                    if isNull store then
                        None
                    else
                        Some(JsTransactionSurface.persistenceOf store)

                let lifecyclePersistence =
                    if isNull store then
                        None
                    else
                        Some(JsTransactionSurface.lifecyclePersistenceOf store)

                let! outcome =
                    JsToolWorkflow.execute
                        sessionId
                        // The harness runs a program outside any host call: it
                        // holds no provider run and no tool call of its own,
                        // so there is no fence to bind here. Its deliveries
                        // land as render caches — presentable, never delivery
                        // evidence. A harness that does know the calling
                        // identity passes it in place of this `None`.
                        None
                        surface.Capabilities
                        workspaceRoot
                        surface.BaseClassSource
                        input
                        deadlineMs
                        deadlineEpochMs
                        outputBoundBytes
                        persistence
                        lifecyclePersistence
                        None

                return box (JsWorkflowOutcomeHandle outcome)
        }

    let runObserved
        (workspaceRoot: string)
        (role: string)
        (language: string)
        (program: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (store: obj)
        (fileAccessObservation: obj)
        : Task<obj> =
        runWithObservation
            workspaceRoot
            role
            language
            program
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            store
            (Some(observationOf fileAccessObservation))

    let caseName (value: obj) : string =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded _ -> "Succeeded"
        | JsToolWorkflow.JsToolOutcome.Failed _ -> "Failed"
        | JsToolWorkflow.JsToolOutcome.Parked _ -> "Parked"

    let parkedName (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Parked(handle, _, _) -> box handle
        | _ -> null

    let parkedPayload (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Parked(_, payload, _) -> payload
        | _ -> null

    let parkedSource (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Parked(_, _, source) -> box source
        | _ -> null

    let rewritten (value: obj) : string array =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded(_, paths, _, _, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Failed(_, paths, _, _, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Parked _ -> [||]

    let created (value: obj) : string array =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded(_, _, paths, _, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Failed(_, _, paths, _, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Parked _ -> [||]

    let removed (value: obj) : string array =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded(_, _, _, paths, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Failed(_, _, _, paths, _) -> paths |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Parked _ -> [||]

    let moved (value: obj) : string array =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded(_, _, _, _, paths) ->
            paths
            |> List.map (fun (fromPath, toPath) -> fromPath + " -> " + toPath)
            |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Failed(_, _, _, _, paths) ->
            paths
            |> List.map (fun (fromPath, toPath) -> fromPath + " -> " + toPath)
            |> List.toArray
        | JsToolWorkflow.JsToolOutcome.Parked _ -> [||]

    let failureCode (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded _ -> null
        | JsToolWorkflow.JsToolOutcome.Failed(failure, _, _, _, _) -> box (JsFailure.code failure)
        | JsToolWorkflow.JsToolOutcome.Parked _ -> null

    let failureReason (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Succeeded _ -> null
        | JsToolWorkflow.JsToolOutcome.Failed(failure, _, _, _, _) -> box (JsFailure.reason failure)
        | JsToolWorkflow.JsToolOutcome.Parked _ -> null

    let persistenceFailure (value: obj) : obj =
        match outcomeOf value with
        | JsToolWorkflow.JsToolOutcome.Failed(JsFailure.TransactionPersistenceFailed failure, _, _, _, _) ->
            JsTransactionSurface.persistenceFailureToJs failure
        | _ -> null

    /// Close test programs through resource-confirmed session cleanup.
    let closePrograms () : Task<unit> = JsPrograms.closeAll ()

    let render (value: obj) : string = JsToolsResult.render (outcomeOf value)
