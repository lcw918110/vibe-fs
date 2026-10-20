namespace Wanxiangshu.Repository.Programming.Js

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

/// JS-native owner boundary for the transaction decision algebra and its one
/// durable EventStore adapter. Mutation records, failures and projections are
/// translated here; Fable unions and lists never cross this edge.
[<RequireQualifiedAccess>]
module JsTransactionSurface =

    let private text (value: obj) =
        if isNull value then "" else string value

    let private failureResult failure =
        box
            {| ok = false
               code = JsFailure.code failure
               reason = JsFailure.reason failure |}

    let private unitResult result =
        match result with
        | Ok _ -> box {| ok = true |}
        | Error failure -> failureResult failure

    let private mutationOf (value: obj) : JsStagedMutation =
        match text (value?kind) with
        | "rewrite" -> JsStagedMutation.Rewrite(text (value?path), text (value?originalText), text (value?newText))
        | "create" -> JsStagedMutation.Create(text (value?path), text (value?text))
        | other -> invalidArg "kind" (sprintf "unknown staged mutation kind: %s" other)

    let private mutationsOf (value: obj) : JsStagedMutation list =
        if isNull value then
            []
        else
            unbox<obj array> value |> Array.toList |> List.map mutationOf

    let private readSnapshotsOf (value: obj) : JsReadSnapshot list =
        if isNull value then
            []
        else
            unbox<obj array> value
            |> Array.toList
            |> List.map (fun snapshot ->
                { Path = text (snapshot?path)
                  Text = text (snapshot?text) })

    let private declarationOf (value: obj) : AnchorDeclaration =
        let spec =
            match text (value?kind) with
            | "exact" -> AnchorSpec.Exact(text (value?text))
            | "regex" -> AnchorSpec.Regex(text (value?text))
            | other -> invalidArg "kind" (sprintf "unknown anchor kind: %s" other)

        let occurrence =
            if isNull value || isNull (value?occurrence) then
                None
            else
                Some(int (value?occurrence))

        { Spec = spec; Occurrence = occurrence }

    let private optionText (value: obj) =
        if isNull value then None else Some(string value)

    /// Stable failure catalog; constructor identity remains private to the
    /// domain while every shipped code and reason is observable as plain data.
    let failureCatalog () : obj array =
        [| JsFailure.InvalidProgram, "InvalidProgram"
           JsFailure.ProgramFailed "", "ProgramFailed"
           JsFailure.ProgramTimeout, "ProgramTimeout"
           JsFailure.ProgramResourceLimit, "ProgramResourceLimit"
           JsFailure.FileNotFound "a", "FileNotFound"
           JsFailure.FileAlreadyExists "a", "FileAlreadyExists"
           JsFailure.InvalidUtf8 "a", "InvalidUtf8"
           JsFailure.AnchorNotFound "missing", "AnchorNotFound"
           JsFailure.AnchorNotUnique, "AnchorNotUnique"
           JsFailure.InvalidEdit "invalid edit", "InvalidEdit"
           JsFailure.EditNotFound "missing edit", "EditNotFound"
           JsFailure.EditAmbiguous "ambiguous edit", "EditAmbiguous"
           JsFailure.EditOverlap "overlapping edit", "EditOverlap"
           JsFailure.DuplicateMutationTarget "a", "DuplicateMutationTarget"
           JsFailure.ResultTooLarge None, "ResultTooLarge"
           JsFailure.InvalidReturnValue, "InvalidReturnValue"
           JsFailure.FileChanged "a", "FileChanged"
           JsFailure.FileReadFailed "a", "FileReadFailed"
           JsFailure.DirectoryNotEmpty "a", "DirectoryNotEmpty"
           JsFailure.DirectoryExists "a", "DirectoryExists"
           JsFailure.ParentDirectoryMissing "a", "ParentDirectoryMissing"
           JsFailure.CrossDeviceMove "a", "CrossDeviceMove"
           JsFailure.InvalidArgument "a", "InvalidArgument"
           JsFailure.TransactionPrepareFailed, "TransactionPrepareFailed"
           JsFailure.TransactionCommitFailed, "TransactionCommitFailed"
           JsFailure.TransactionRecoveryRequired, "TransactionRecoveryRequired"
           JsFailure.UnknownMember, "UnknownMember"
           JsFailure.CallableSpecIncomplete "missing check", "CallableSpecIncomplete"
           JsFailure.CallCommandUnsupported "next() returned an object", "CallCommandUnsupported"
           JsFailure.CallWaitTimeout, "CallWaitTimeout"
           JsFailure.CallChildUnknown "srv", "CallChildUnknown"
           JsFailure.CallParkUnsupported, "CallParkUnsupported"
           JsFailure.CallHostUnavailable, "CallHostUnavailable"
           JsFailure.AskRefused "S2 stage", "AskRefused"
           JsFailure.DelegationUnavailable, "DelegationUnavailable" |]
        |> Array.map (fun (failure, name) ->
            box
                {| name = name
                   code = JsFailure.code failure
                   reason = JsFailure.reason failure |})

    let validateAnchorDeclaration (declaration: obj) : obj =
        AnchorRules.validateDeclaration (declarationOf declaration) |> unitResult

    let validateAnchorOccurrence (declaration: obj) : obj =
        AnchorRules.validateOccurrence (declarationOf declaration) |> unitResult

    let validateSingleIntent (mutations: obj array) : obj =
        JsTransaction.validateSingleIntent (mutationsOf (box mutations)) |> unitResult

    let private existsOf (paths: string array) path = paths |> Array.contains path

    let validateTargets (existing: string array) (mutations: obj array) : obj =
        JsTransaction.validateTargets (existsOf existing) (mutationsOf (box mutations))
        |> unitResult

    let private currentOf (current: obj) path =
        if isNull current then None else optionText (current?(path))

    let validateFreshness (current: obj) (mutations: obj array) : obj =
        JsTransaction.validateFreshness (currentOf current) (mutationsOf (box mutations))
        |> unitResult

    let preflight (existing: string array) (current: obj) (readSnapshots: obj array) (mutations: obj array) : obj =
        JsTransaction.preflight
            (existsOf existing)
            (currentOf current)
            (readSnapshotsOf (box readSnapshots))
            (mutationsOf (box mutations))
        |> unitResult

    let commitPlan (mutations: obj array) : obj array =
        JsTransaction.commitPlan (mutationsOf (box mutations))
        |> List.map (fun mutation ->
            match mutation with
            | JsCommitMutation.RewriteFile(path, expectedCurrent, newText) ->
                box
                    {| kind = "rewrite"
                       path = path
                       expectedCurrent = expectedCurrent
                       newText = newText |}
            | JsCommitMutation.CreateFile(path, newText) ->
                box
                    {| kind = "create"
                       path = path
                       newText = newText |})
        |> List.toArray

    let rollbackPlan (mutations: obj array) : obj array =
        JsTransaction.rollbackPlan (mutationsOf (box mutations))
        |> List.map (fun mutation ->
            match mutation with
            | JsRollbackMutation.RestoreFile(path, expectedCurrent, originalText) ->
                box
                    {| kind = "restore"
                       path = path
                       expectedCurrent = expectedCurrent
                       originalText = originalText |}
            | JsRollbackMutation.RemoveCreatedFile(path, expectedCurrent) ->
                box
                    {| kind = "removeCreated"
                       path = path
                       expectedCurrent = expectedCurrent |})
        |> List.toArray

    let private eventStoreOf (handle: obj) : IEventStore = (unbox<EventStoreHandle> handle).Store

    let private durableMutationToJs (mutation: JsDurableMutation) =
        let kind =
            match mutation.Kind with
            | JsDurableMutationKind.Rewrite -> "rewrite"
            | JsDurableMutationKind.Create -> "create"
            | JsDurableMutationKind.Remove -> "remove"
            | JsDurableMutationKind.Move -> "move"

        box
            {| path = mutation.Path
               originalText = mutation.OriginalText |> Option.toObj
               newText = mutation.NewText
               kind = kind |}

    let private preparedToJs (prepared: JsTransactionPrepared) =
        box
            {| transactionId = JsTransactionId.value prepared.TransactionId
               workspaceRoot = prepared.WorkspaceRoot
               mutation = durableMutationToJs prepared.Mutation |}

    let private preparedOf (value: obj) : JsTransactionPrepared =
        if isNull value || isNull (value?mutation) then
            invalidArg "mutation" "Prepared payload requires a single mutation object"

        let item = value?mutation

        let kind =
            match text (item?kind) with
            | "create" -> JsDurableMutationKind.Create
            | "remove" -> JsDurableMutationKind.Remove
            | "move" -> JsDurableMutationKind.Move
            | _ -> JsDurableMutationKind.Rewrite

        let mutation =
            { Path = text (item?path)
              OriginalText =
                if isNull (item?originalText) then
                    None
                else
                    Some(text (item?originalText))
              NewText = text (item?newText)
              Kind = kind }

        { TransactionId = JsTransactionId.create (text (value?transactionId))
          WorkspaceRoot = text (value?workspaceRoot)
          Mutation = mutation }

    let persistenceFailureToJs (failure: JsTransactionAppendFailure) : obj =
        let eventIds events =
            events
            |> List.map (fun (event: EventEnvelope) -> EventId.value event.EventId)
            |> List.toArray

        let faultToJs (fault: AppendFault) =
            box
                {| phase = string fault.Phase
                   cause = fault.Cause |}

        let fields =
            [ "phase" ==> string failure.Phase
              "transactionId" ==> JsTransactionId.value failure.TransactionId
              "eventId" ==> EventId.value failure.EventId
              "isOriginalError"
              ==> (fun (expected: obj) -> obj.ReferenceEquals(box failure.Error, expected)) ]

        let evidence =
            match failure.Error with
            | AppendError.AppendNotAttempted value ->
                [ "kind" ==> "notAttempted"
                  "requestedEventIds" ==> eventIds value.Requested
                  "preparedEventIds"
                  ==> (value.Prepared |> Option.map (fun p -> eventIds p.DurableEvents) |> Option.toObj)
                  "primary" ==> faultToJs value.Primary
                  "cleanupFailures"
                  ==> (value.CleanupFailures |> List.map faultToJs |> List.toArray) ]
            | AppendError.CommitUnknown value ->
                [ "kind" ==> "unknown"
                  "requestedEventIds" ==> eventIds value.Requested
                  "preparedEventIds" ==> eventIds value.Prepared.DurableEvents
                  "primary" ==> faultToJs value.Primary
                  "cleanupFailures"
                  ==> (value.CleanupFailures |> List.map faultToJs |> List.toArray) ]
            | AppendError.NoNewWriteReleaseFailed value ->
                [ "kind" ==> "noNewWriteReleaseFailed"
                  "requestedEventIds" ==> eventIds value.Requested
                  "preparedEventIds"
                  ==> (value.Prepared |> Option.map (fun p -> eventIds p.DurableEvents) |> Option.toObj)
                  "primary"
                  ==> box
                          {| phase = "StoreRelease"
                             cause = value.Cause |}
                  "cleanupFailures" ==> ([||]: obj array) ]
            | _ -> [ "kind" ==> "rejected" ]

        createObj (fields @ evidence)

    let private appendResult result =
        match result with
        | Ok _ -> box {| ok = true |}
        | Error failure ->
            let domainFailure = JsFailure.TransactionPersistenceFailed failure

            box
                {| ok = false
                   code = JsFailure.code domainFailure
                   error = JsFailure.reason domainFailure
                   persistenceFailure = persistenceFailureToJs failure |}

    /// Append Prepared through the canonical EventStore Current integrator.
    let appendPrepared (store: obj) (prepared: obj) : Task<obj> =
        task {
            let! result = JsToolsTransactionStore.appendPrepared (eventStoreOf store) (preparedOf prepared)
            return appendResult result
        }

    /// Append Committed through the same transaction stream.
    let appendCommitted (store: obj) (transactionId: string) : Task<obj> =
        task {
            let! result =
                JsToolsTransactionStore.appendCommitted (eventStoreOf store) (JsTransactionId.create transactionId)

            return appendResult result
        }

    /// Observe only the Integrator-owned pending projection; no history reader
    /// or recovery mutation is exposed.
    let pending (store: obj) : obj array =
        match eventStoreOf store |> fun value -> value.TryCurrent("JsTransaction") with
        | None -> [||]
        | Some value ->
            JsTransactionProjection.pending (unbox<JsTransactionProjection> value)
            |> List.map preparedToJs
            |> List.toArray

    /// Opaque persistence capability consumed by JsWorkflowSurface.
    let internal persistenceOf (store: obj) : IJsTransactionPersistence =
        JsToolsTransactionStore.createPersistence (eventStoreOf store)

    let createPersistence (store: obj) : obj = persistenceOf store |> box

    // ---- lifecycle audit surface (PLAN S2 step 8 / algorithm H3) -----------

    let private lifecycleAppendResult (result: Result<EventId, JsLifecycleAppendFailure>) : obj =
        match result with
        | Ok _ -> box {| ok = true |}
        | Error failure ->
            box
                {| ok = false
                   code = "LIFECYCLE_AUDIT_APPEND_FAILED"
                   error = AppendError.describe failure.Error
                   eventType = JsToolsTransactionStore.lifecycleEventTypeOf failure.EventType
                   handle = failure.Handle
                   sessionId = failure.SessionId |}

    /// Append one lifecycle audit trace. Callers are park/unpark/abandon/reap
    /// transition points (lane-a-core owns those call sites). Unknown event
    /// types fail closed with a typed rejection instead of throwing.
    let appendLifecycleEvent (store: obj) (value: obj) : Task<obj> =
        task {
            match JsToolsTransactionStore.lifecycleEventTypeOfName (text (value?eventType)) with
            | None ->
                return
                    box
                        {| ok = false
                           code = "LIFECYCLE_AUDIT_APPEND_FAILED"
                           error = sprintf "unknown lifecycle audit event type: %s" (text (value?eventType))
                           eventType = text (value?eventType)
                           handle = text (value?handle)
                           sessionId = text (value?sessionId) |}
            | Some kind ->
                let event =
                    { EventType = kind
                      Handle = text (value?handle)
                      SessionId = text (value?sessionId)
                      At = text (value?at) }

                let! result = JsToolsTransactionStore.appendLifecycleEvent (eventStoreOf store) event
                return lifecycleAppendResult result
        }

    /// Opaque lifecycle audit capability consumed by the park/unpark/abandon/
    /// reap transition points.
    let internal lifecyclePersistenceOf (store: obj) : IJsLifecyclePersistence =
        JsToolsTransactionStore.createLifecyclePersistence (eventStoreOf store)

    let createLifecyclePersistence (store: obj) : obj = lifecyclePersistenceOf store |> box

    let createTransactionContext () : obj =
        let readSnapshots = System.Collections.Generic.HashSet<string>()
        let explicitReads = System.Collections.Generic.HashSet<string>()
        let stagedWrites = System.Collections.Generic.Dictionary<string, string>()
        // DSL-MUTABLE: cancellation — transaction aborted flag
        let mutable isAborted = false
        // DSL-MUTABLE: single-flight — transaction committed flag
        let mutable isCommitted = false

        box
            {| recordGrepScan =
                fun (paths: string array) ->
                    for p in paths do
                        readSnapshots.Add p |> ignore
               recordExplicitRead =
                fun (path: string) ->
                    readSnapshots.Add path |> ignore
                    explicitReads.Add path |> ignore
               stageWrite = fun (path: string) (content: string) -> stagedWrites.[path] <- content
               abort = fun () -> isAborted <- true
               commit =
                fun () ->
                    if not isAborted then
                        isCommitted <- true
               getReadSnapshots = fun () -> readSnapshots |> Seq.toArray
               getSubstantiveAccess = fun () -> explicitReads |> Seq.toArray |}
