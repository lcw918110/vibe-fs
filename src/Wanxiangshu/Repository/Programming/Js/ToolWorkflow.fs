namespace Wanxiangshu.Repository.Programming.Js

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Repository.Programming.Js

/// Parse the sandbox JSON string into LlmFacing reference data and enforce JS-010.
module JsToolsData =

    [<Emit("$0 === null")>]
    let private jsNull (value: obj) : bool = jsNative

    [<Emit("typeof $0")>]
    let private jsType (value: obj) : string = jsNative

    [<Emit("Array.isArray($0)")>]
    let private jsIsArray (value: obj) : bool = jsNative

    [<Emit("Object.keys($0)")>]
    let private jsKeys (value: obj) : string[] = jsNative

    [<Emit("$0[$1]")>]
    let private jsGet (value: obj) (key: obj) : obj = jsNative

    [<Emit("$0.length")>]
    let private jsLength (value: obj) : int = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private jsIsFinite (value: obj) : bool = jsNative

    /// JS-010: a returned or delivered value is JSON-compatible — null, boolean,
    /// finite number, string, or an array/object of such values. An array holds
    /// no null element and is either all objects or one primitive tree.
    /// Validation stays on the native value; only the rendering boundary
    /// converts it into `LlmFacing.Data.Value` (that conversion cannot fail,
    /// so it must not run before the value is accepted).
    let rec private validateJsValue (value: obj) : Result<unit, JsFailure> =
        let ty = jsType value

        if jsNull value then Ok()
        elif ty = "boolean" || ty = "string" then Ok()
        elif ty = "number" then validateJsNumber value
        elif jsIsArray value then validateJsArray value
        elif ty = "object" then validateJsObject value
        else Error JsFailure.InvalidReturnValue

    and isPlainObject (value: obj) : bool =
        not (jsNull value) && jsType value = "object" && not (jsIsArray value)

    and validateJsNumber (value: obj) : Result<unit, JsFailure> =
        if jsIsFinite value then
            Ok()
        else
            Error JsFailure.InvalidReturnValue

    and isPrimitiveTreeValue (value: obj) : bool =
        let ty = jsType value

        if ty = "boolean" || ty = "string" then
            true
        elif ty = "number" then
            jsIsFinite value
        elif jsIsArray value then
            [ 0 .. jsLength value - 1 ]
            |> List.forall (fun index -> isPrimitiveTreeValue (jsGet value index))
        else
            false

    and validateAll (values: obj list) : Result<unit, JsFailure> =
        match values with
        | [] -> Ok()
        | value :: rest -> validateJsValue value |> Result.bind (fun () -> validateAll rest)

    and validateJsArray (value: obj) : Result<unit, JsFailure> =
        let items = [ 0 .. jsLength value - 1 ] |> List.map (fun index -> jsGet value index)

        validateAll items
        |> Result.bind (fun () ->
            if List.isEmpty items then
                Ok()
            elif List.exists jsNull items then
                Error JsFailure.InvalidReturnValue
            elif List.forall isPlainObject items then
                Ok()
            elif List.forall isPrimitiveTreeValue items then
                Ok()
            else
                Error JsFailure.InvalidReturnValue)

    and validateJsObject (value: obj) : Result<unit, JsFailure> =
        let keys = jsKeys value

        [ 0 .. keys.Length - 1 ]
        |> List.map (fun index -> jsGet value keys.[index])
        |> validateAll

    let private validateParsed (value: obj) : Result<obj, JsFailure> =
        validateJsValue value |> Result.map (fun () -> value)

    let parse (json: string) : Result<obj, JsFailure> =
        try
            validateParsed (JS.JSON.parse json)
        with _ ->
            Error JsFailure.InvalidReturnValue

    let parseOutcome (json: string) : Result<obj, JsFailure> = parse json

/// The task a delivery serves. The durable witness names the same three
/// references; this layer states them in its own vocabulary because it runs
/// below the host turn and cannot reach the host's witness type.
[<RequireQualifiedAccess>]
type DeliveryTaskReference =
    | ChildWork of handle: string
    | Invocation of invocationId: string
    | Lifecycle of workId: string * incumbencyId: string

/// The host's attribution of one tool call: the task the call serves, the
/// logical run and the authority it belongs to, and the prose the model
/// wrote before the call. The settle point cannot derive any of it — it runs
/// below the host turn, where only the call fence is visible.
type DeliveryAttribution =
    { LogicalRunId: LogicalRunId
      AuthorityRoot: AuthorityRootUserMessageId
      PhysicalUserMessageId: PhysicalUserMessageId
      Task: DeliveryTaskReference
      RetirementId: string option
      FormalText: string }

/// The delivery witness of one settled call. The host writer maps it onto
/// the durable witness fact — adding the material digest — and commits it;
/// this layer never touches the journal itself.
type DeliveryWitness =
    {
        SessionId: SessionId
        LogicalRunId: LogicalRunId
        AuthorityRoot: AuthorityRootUserMessageId
        PhysicalUserMessageId: PhysicalUserMessageId
        ProviderRun: ProviderRunIdentity
        ToolCallId: ToolCallId
        Task: DeliveryTaskReference
        FormalText: string
        ViewDeclaration: string
        /// Canonical returned material; rendering belongs to the final wire boundary.
        View: string
        RetirementId: string option
    }

/// Required outcome shape of a durable delivery witness commit.
[<RequireQualifiedAccess>]
type DeliveryCommitResult =
    | Committed
    | NotCommitted of reason: string
    | Unknown of reason: string

/// The narrow durable capability the settle point uses to record a delivery
/// witness. A refused commit means the delivery is not established: the
/// caller must learn that instead of reading a success nobody can prove.
type IDeliveryWitnessWriter =
    abstract CommitDeliveryWitness: witness: DeliveryWitness -> Task<DeliveryCommitResult>

/// What the settle point needs to attribute a delivery and to make its
/// evidence durable. A call without a settlement has no durable store behind
/// it (test/dev harness): the process-live cell is then the only evidence.
///
/// A durable host whose call cannot be attributed (no owning assistant prose,
/// no physical input, no logical run) is NOT the harness case: its delivery
/// has no witness to stand on, so the call must fail instead of leaving a
/// process-live cell that no join can receive.
type DeliverySettlement =
    | DurableDelivery of DeliveryAttribution * IDeliveryWitnessWriter
    | UnattributedDelivery of reason: string
    | ContextUnavailable of reason: string
    | DeferredDelivery of resolve: (unit -> Task<DeliverySettlement option>)

/// The review gates for one sandbox run. Manager gates reads (async check);
/// Plan gates draft writes (synchronous check, fresh per mutation).
type JsReviewGates =
    { ReadGate: (unit -> Task<bool>) option
      WriteGate: (unit -> bool) option }

/// The host binds business capabilities and settles their audit effects.
module JsToolWorkflow =
    type FileAccessObservation = JsExplicitFileRead list -> string list -> Task<unit>

    [<RequireQualifiedAccess>]
    type JsToolOutcome =
        | Succeeded of
            value: obj *
            rewritten: string list *
            created: string list *
            removed: string list *
            moved: (string * string) list
        | Failed of
            failure: JsFailure *
            rewritten: string list *
            created: string list *
            removed: string list *
            moved: (string * string) list
        | Parked of name: string * payload: obj * source: string

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0) && Object.keys($0).length === 3 && typeof $0.name === 'string' && $0.name.trim().length > 0 && typeof $0.program === 'string' && $0.program.trim().length > 0 && typeof $0.intent === 'string' && $0.intent.length > 0")>]
    let private validInput (value: obj) : bool = jsNative

    [<Emit("Object.assign({}, $0, $1)")>]
    let private merge (left: obj) (right: obj) : obj = jsNative

    [<Emit("new TextEncoder().encode($0).byteLength")>]
    let private utf8Bytes (value: string) : int = jsNative

    let private rewrittenPaths (mutations: JsStagedMutation list) =
        mutations
        |> List.choose (function
            | JsStagedMutation.Rewrite(path, _, _) -> Some path
            | JsStagedMutation.Create _
            | JsStagedMutation.Remove _
            | JsStagedMutation.Move _ -> None)

    let private createdPaths (mutations: JsStagedMutation list) =
        mutations
        |> List.choose (function
            | JsStagedMutation.Create(path, _) -> Some path
            | JsStagedMutation.Rewrite _
            | JsStagedMutation.Remove _
            | JsStagedMutation.Move _ -> None)

    let private removedPaths (mutations: JsStagedMutation list) =
        mutations
        |> List.choose (function
            | JsStagedMutation.Remove(path) -> Some path
            | JsStagedMutation.Rewrite _
            | JsStagedMutation.Create _
            | JsStagedMutation.Move _ -> None)

    let private movedPaths (mutations: JsStagedMutation list) =
        mutations
        |> List.choose (function
            | JsStagedMutation.Move(fromPath, toPath) -> Some(fromPath, toPath)
            | JsStagedMutation.Rewrite _
            | JsStagedMutation.Create _
            | JsStagedMutation.Remove _ -> None)

    let private mapPersistenceFailure
        (operation: Task<Result<'a, JsTransactionAppendFailure>>)
        : Task<Result<'a, JsFailure>> =
        operation
        |> TaskValue.map (Result.mapError JsFailure.TransactionPersistenceFailed)

    /// Settle the H1 ledger the mutation members fired (PLAN algorithm H1):
    /// await every Prepared/Committed append. The members fire Prepared at
    /// call time (before the write lands) and Committed right after, at
    /// per-mutation granularity with no aggregation; the physical EventStore
    /// append chain settles within the same event-loop turn as the write. A
    /// failed append fails the tool result with the stable
    /// TRANSACTION_PREPARE_*/COMMIT_* codes while the landed as-is writes
    /// stay reported, never rolled back ([013]).
    let private recordFirstAppendFailure
        (currentFailure: JsFailure option)
        (appended: Result<'a, JsTransactionAppendFailure>)
        =
        match currentFailure, appended with
        | None, Error failure -> Some(JsFailure.TransactionPersistenceFailed failure)
        | _ -> currentFailure

    let private settleLedger (ledger: JsAsIsLedger) : Task<Result<unit, JsFailure>> =
        task {
            // DSL-MUTABLE: algorithm-scratch — first append failure for the outcome
            let mutable appendFailure = None

            for pending in ledger.Appends do
                let! appended = pending
                appendFailure <- recordFirstAppendFailure appendFailure appended

            match appendFailure with
            | Some failure -> return Error failure
            | None -> return Ok()
        }

    let private invokeObservation observe readPaths effectPaths : Task<Result<unit, JsFailure>> =
        task {
            try
                do! observe readPaths effectPaths
            with _ ->
                ()

            return Ok()
        }

    let private observeFileAccess
        (observer: FileAccessObservation option)
        readPaths
        effectPaths
        : Task<Result<unit, JsFailure>> =
        match observer with
        | None -> Task.FromResult(Ok())
        | Some observe -> invokeObservation observe readPaths effectPaths


    let private checkReadOnlyMutation
        (hasMutationCapability: bool)
        (mutations: JsStagedMutation list)
        : JsFailure option =
        if not hasMutationCapability && not (List.isEmpty mutations) then
            Some JsFailure.ReadOnlyMutationRejected
        else
            None

    let conflictingInputReason (input: obj) =
        if validInput input then
            None
        else
            Some "exactly name, program and intent are required strings; intent must be non-empty"

    let private applyReadGate gate api =
        match gate with
        | None -> api
        | Some check -> JsReviewOnly.scopeManagerReviewOnly check api

    let private applyWriteGate gate api =
        match gate with
        | None -> api
        | Some check -> JsReviewOnly.scopePlanWriteOnly check api

    let private applyReviewGates (gates: JsReviewGates option) api =
        match gates with
        | None -> api
        | Some gates -> api |> applyReadGate gates.ReadGate |> applyWriteGate gates.WriteGate

    let private appendLifecycle (durable: IJsLifecyclePersistence) event : Task<unit> =
        task {
            match! durable.AppendLifecycleEvent event with
            | Ok _ -> ()
            | Error failure ->
                let error =
                    System.Exception(sprintf "lifecycle event append failed: %A" failure.Error)

                error?code <- "LIFECYCLE_PERSISTENCE_FAILED"
                return raise error
        }

    let private lifecycle persistence sessionId (receipt: obj) : Task<unit> =
        let eventType =
            match string receipt?event with
            | "parked" -> Some JsLifecycleEventType.Parked
            | "unparked" -> Some JsLifecycleEventType.Unparked
            | "abandoned" -> Some JsLifecycleEventType.Abandoned
            | _ -> None

        match persistence, eventType with
        | Some durable, Some kind ->
            appendLifecycle
                durable
                { EventType = kind
                  Handle = string receipt?name
                  SessionId = sessionId
                  At = System.DateTimeOffset.UtcNow.ToString("o") }
        | _ -> Task.FromResult()

    let private parsedValueFailure json =
        match JsToolsData.parse json with
        | Error failure -> Some failure
        | Ok _ -> None

    let private jsonValueFailure outputBoundBytes json =
        if isNull json then
            Some JsFailure.InvalidReturnValue
        elif utf8Bytes json > outputBoundBytes then
            Some(JsFailure.ResultTooLarge None)
        else
            parsedValueFailure json

    let private returnedValueFailure outputBoundBytes (receipt: obj) =
        if string receipt?state = "completed" then
            jsonValueFailure outputBoundBytes (JS.JSON.stringify receipt?value)
        else
            None

    let private settlementResult outputBoundBytes receipt observed appended =
        match observed, appended with
        | Error failure, _
        | _, Error failure -> Some failure
        | _ -> returnedValueFailure outputBoundBytes receipt

    let private receiptFailure (receipt: obj) =
        match string receipt?code with
        | "PROGRAM_RESULT_NOT_SERIALIZABLE" -> JsFailure.InvalidReturnValue
        | "PROGRAM_EXECUTION_LIMIT" -> JsFailure.ProgramTimeout
        | code -> JsFailure.ofWire code (string receipt?reason)

    let private arrayField (value: obj) key : obj array =
        let items = value?(key)
        if isNull items then [||] else unbox items

    let private outcome (receipt: obj) =
        let effects = receipt?effects

        let paths key =
            if isNull effects then
                []
            else
                arrayField effects key |> Array.map string |> Array.toList

        let moved =
            if isNull effects then
                []
            else
                arrayField effects "moved"
                |> Array.map (fun item -> string item?from, string item?destination)
                |> Array.toList

        let rewritten, created, removed =
            paths "rewritten", paths "created", paths "removed"

        match string receipt?state with
        | "completed" when not (isNull receipt?lifecycleError) ->
            let error = receipt?lifecycleError

            JsToolOutcome.Failed(
                JsFailure.ofWire (string error?code) (string error?reason),
                rewritten,
                created,
                removed,
                moved
            )
        | "completed" -> JsToolOutcome.Succeeded(receipt?value, rewritten, created, removed, moved)
        | "failed" -> JsToolOutcome.Failed(receiptFailure receipt, rewritten, created, removed, moved)
        | "parked" -> JsToolOutcome.Parked(string receipt?name, receipt, string receipt?source)
        | _ -> JsToolOutcome.Succeeded(receipt, rewritten, created, removed, moved)

    let private executeValidated
        (reviewGates: JsReviewGates option)
        (settlement: DeliverySettlement option)
        (sessionId: string)
        (fence: (ProviderRunIdentity * ToolCallId) option)
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (input: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: FileAccessObservation option)
        : Task<JsToolOutcome> =
        task {
            let staging = ResizeArray<JsStagedMutation>()
            let reads = ResizeArray<JsExplicitFileRead>()
            let ledger = JsToolsBindings.createLedger persistence
            // DSL-MUTABLE: resource — 保存本次结算失败，供回执映射保留原始原因。
            let mutable settlementFailure: JsFailure option = None

            let api =
                JsToolsBindings.createApi
                    capabilities
                    root
                    staging
                    reads
                    ledger
                    (JsDelegationBridge.hostFor sessionId)
                    (JsDelegationBridge.askFor sessionId)
                |> applyReviewGates reviewGates

            let settle (receipt: obj) : Task<obj> =
                task {
                    let mutations = List.ofSeq staging
                    let rewritten, created = rewrittenPaths mutations, createdPaths mutations
                    let removed, moved = removedPaths mutations, movedPaths mutations

                    let effectPaths =
                        rewritten @ created @ removed @ (moved |> List.collect (fun (a, b) -> [ a; b ]))

                    let! observed =
                        observeFileAccess
                            fileAccessObservation
                            (reads |> Seq.distinctBy (fun read -> read.Path) |> Seq.toList)
                            effectPaths

                    let! appended = settleLedger ledger
                    let failure = settlementResult outputBoundBytes receipt observed appended
                    settlementFailure <- failure

                    let result =
                        match failure with
                        | None -> receipt
                        | Some failure ->
                            merge
                                receipt
                                (createObj
                                    [ "state" ==> "failed"
                                      "code" ==> JsFailure.code failure
                                      "reason" ==> JsFailure.reason failure ])

                    return
                        merge
                            result
                            (createObj
                                [ "effects"
                                  ==> createObj
                                          [ "rewritten" ==> Array.ofList rewritten
                                            "created" ==> Array.ofList created
                                            "removed" ==> Array.ofList removed
                                            "moved"
                                            ==> (moved
                                                 |> List.map (fun (a, b) ->
                                                     createObj [ "from" ==> a; "destination" ==> b ])
                                                 |> Array.ofList) ] ])
                }

            let callId =
                fence
                |> Option.map (fun (run, call) -> ProviderRunIdentity.value run + ":" + ToolCallId.value call)
                |> Option.defaultValue ""

            let! outputRoot =
                if Set.contains JsCapability.Exec capabilities then
                    Wanxiangshu.Persistence.Journal.RuntimePath.initializeStateRoot root
                else
                    Task.FromResult ""

            let spec =
                createObj
                    [ "name" ==> input?name
                      "program" ==> input?program
                      "intent" ==> input?intent
                      "baseClass" ==> baseClassSource
                      "callId" ==> callId
                      "capabilities"
                      ==> (capabilities |> Set.toArray |> Array.map JsCapability.labelOf)
                      "api" ==> api
                      "root" ==> root
                      "outputRoot"
                      ==> (if outputRoot = "" then
                               null
                           else
                               box (outputRoot + "/program-output"))
                      "executionBudgetMs" ==> max 1 deadlineMs
                      "outputBoundBytes" ==> outputBoundBytes
                      "settle" ==> settle
                      "lifecycle" ==> lifecycle lifecyclePersistence sessionId ]

            JsPrograms.noteCaller sessionId callId (string input?name)
            try
                let session = JsPrograms.forSession sessionId
                let! receipt = unbox<Task<obj>> (session?execute (spec))
                let reported = outcome receipt

                match reported, settlementFailure with
                | JsToolOutcome.Failed(_, rewritten, created, removed, moved), Some failure when
                    string receipt?code = JsFailure.code failure
                    ->
                    return JsToolOutcome.Failed(failure, rewritten, created, removed, moved)
                | _ -> return reported
            finally
                JsPrograms.clearCaller sessionId callId
        }

    let executeWithReviewCheckAndDelivery
        (reviewGates: JsReviewGates option)
        (settlement: DeliverySettlement option)
        (sessionId: string)
        (fence: (ProviderRunIdentity * ToolCallId) option)
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (input: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: FileAccessObservation option)
        : Task<JsToolOutcome> =
        match conflictingInputReason input with
        | Some reason -> Task.FromResult(JsToolOutcome.Failed(JsFailure.InvalidArgument reason, [], [], [], []))
        | None ->
            executeValidated
                reviewGates
                settlement
                sessionId
                fence
                capabilities
                root
                baseClassSource
                input
                deadlineMs
                deadlineEpochMs
                outputBoundBytes
                persistence
                lifecyclePersistence
                fileAccessObservation

    let executeWithReviewCheck
        reviewGates
        sessionId
        fence
        capabilities
        root
        baseClassSource
        (input: obj)
        deadlineMs
        deadlineEpochMs
        outputBoundBytes
        persistence
        lifecyclePersistence
        fileAccessObservation
        =
        executeWithReviewCheckAndDelivery
            reviewGates
            None
            sessionId
            fence
            capabilities
            root
            baseClassSource
            input
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            lifecyclePersistence
            fileAccessObservation

    let execute
        sessionId
        fence
        capabilities
        root
        baseClassSource
        (input: obj)
        deadlineMs
        deadlineEpochMs
        outputBoundBytes
        persistence
        lifecyclePersistence
        fileAccessObservation
        =
        executeWithReviewCheck
            None
            sessionId
            fence
            capabilities
            root
            baseClassSource
            input
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            lifecyclePersistence
            fileAccessObservation

    let private runObserved
        capabilities
        root
        baseClassSource
        (modelSource: string)
        deadlineMs
        deadlineEpochMs
        outputBoundBytes
        persistence
        observation
        =
        let sessionId = "harness-" + Guid.NewGuid().ToString("N")

        execute
            sessionId
            None
            capabilities
            root
            baseClassSource
            (createObj
                [ "name" ==> "run"
                  "program" ==> modelSource
                  "intent"
                  ==> "Wait for this isolated verification program to finish and preserve its real result." ])
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            None
            observation

    let run capabilities root baseClassSource modelSource deadlineMs deadlineEpochMs outputBoundBytes persistence =
        runObserved
            capabilities
            root
            baseClassSource
            modelSource
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            None

    let runWithFileAccessObservation
        capabilities
        root
        baseClassSource
        modelSource
        deadlineMs
        deadlineEpochMs
        outputBoundBytes
        persistence
        fileAccessObservation
        =
        runObserved
            capabilities
            root
            baseClassSource
            modelSource
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            (Some fileAccessObservation)

module JsToolsResult =

    let render (outcome: JsToolWorkflow.JsToolOutcome) : string =
        match outcome with
        | JsToolWorkflow.JsToolOutcome.Succeeded(value, rewritten, created, removed, moved) ->
            LlmFacing.instruction "ok"
            |> LlmFacing.withData (
                LlmFacing.Data.structuredValue (LlmFacing.Data.ofJs value)
                @ LlmFacing.Data.fileEffects rewritten created removed moved
            )
            |> LlmFacing.render
        | JsToolWorkflow.JsToolOutcome.Failed(failure, rewritten, created, removed, moved) ->
            let effects =
                if
                    List.isEmpty rewritten
                    && List.isEmpty created
                    && List.isEmpty removed
                    && List.isEmpty moved
                then
                    []
                else
                    LlmFacing.Data.fileEffects rewritten created removed moved

            LlmFacing.instruction "failed"
            |> LlmFacing.withData (
                [ LlmFacing.Data.stringField "code" (JsFailure.code failure)
                  LlmFacing.Data.stringField "reason" (JsFailure.reason failure) ]
                @ effects
            )
            |> LlmFacing.render
        | JsToolWorkflow.JsToolOutcome.Parked(_, payload, _) ->
            LlmFacing.instruction "parked"
            |> LlmFacing.withData (LlmFacing.Data.rootStructuredValue (LlmFacing.Data.ofJs payload))
            |> LlmFacing.render
