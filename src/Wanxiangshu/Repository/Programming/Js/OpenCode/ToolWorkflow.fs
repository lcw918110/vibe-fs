namespace Wanxiangshu.Repository.Programming.Js.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Process

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

    /// `js.deliver(value)` ends the program with an explicit terminal value;
    /// the sandbox reports it as this sentinel instead of a return value.
    let private deliveredPrefix = "{\"__jsDelivered\":"

    let isDelivered (json: string) = json.StartsWith deliveredPrefix

    let private parseDelivered (json: string) : Result<obj, JsFailure> =
        try
            validateParsed ((JS.JSON.parse json)?value)
        with _ ->
            Error JsFailure.InvalidReturnValue

    /// The raw JS value a program handed over through `js.deliver(value)`.
    let parseDeliveredValue (json: string) : obj =
        try
            (JS.JSON.parse json)?value
        with _ ->
            null

    /// One decode for both endings: a delivered value is the program's result.
    let parseOutcome (json: string) : Result<obj, JsFailure> =
        if isDelivered json then parseDelivered json else parse json

    /// Extract the payload from a parked sentinel JSON string. The sentinel is
    /// produced by the park signal path; its payload is the value the program
    /// passed to `await js.park(payload)`.
    let parseParkedPayload (json: string) : obj =
        try
            let payload = JS.JSON.parse json
            jsGet payload "payload"
        with _ ->
            null

/// JS-085: one js-* tool invocation, end to end — the only orchestration of
/// sandbox execution, staging, preflight, durable prepare, commit and the
/// commit fact (JS-012/JS-013/JS-015). With `store = None` the workflow is
/// ephemeral (no durable facts — tests); with a store, the transaction is
/// Prepared BEFORE any filesystem effect and Committed AFTER, so crash
/// recovery can undo only what was provably written.

module JsToolWorkflow =

    type FileAccessObservation = JsExplicitFileRead list -> string list -> Task<unit>

    /// Outcome of one invocation: the program's structured value plus the
    /// commit report — or a stable JsFailure. `Parked` is the top-level parked
    /// payload: the program suspended at `await js.park(payload)` and the site
    /// is registered under `handle` (PLAN S2 step 4).
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
        | Parked of handle: string * payload: obj * source: string

    /// The park payload is created inside the sandbox realm; the host reads it
    /// from ParkState across the vm boundary. A JSON round-trip detaches the
    /// value from the vm prototypes so host-side consumers and semantic tests
    /// compare it structurally as a plain host-realm object.
    [<Emit("JSON.parse(JSON.stringify($0))")>]
    let private hostRealmPayload (payload: obj) : obj = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isStringValue (value: obj) : bool = jsNative

    /// The park signal wins the race by settling the host-synthesized marker
    /// string; the workflow reads it back as the parked marker rather than a
    /// program result.
    let private isParkedSentinel (value: obj) : bool =
        isStringValue value && (unbox<string> value).StartsWith "{\"__jsParked\":"

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

    let private decodeViewDeclaration (raw: obj) : Result<obj option, JsFailure> =
        if isNull raw then
            Ok None
        else
            Wanxiangshu.Repository.Programming.Js.Anchor.AnchorSurface.parse raw
            |> Result.map Some

    let private parseRawView (json: string) : Result<obj, JsFailure> =
        try
            Ok((JS.JSON.parse json)?view)
        with _ ->
            Error(JsFailure.ViewDeclarationInvalid "delivered view did not parse")

    let private deliveredView (json: string) : Result<obj option, JsFailure> =
        if not (JsToolsData.isDelivered json) then
            Ok None
        else
            parseRawView json |> Result.bind decodeViewDeclaration

    let private recordDeliveredValueIfNeeded sessionId (json: string) (parsed: Result<obj, JsFailure>) =
        match parsed, deliveredView json with
        | Ok _, Ok view when JsToolsData.isDelivered json ->
            JsDeliveredValues.recordView sessionId (JsToolsData.parseDeliveredValue json) view
            parsed
        | _, Error failure when JsToolsData.isDelivered json -> Error failure
        | _ -> parsed

    let private decodeRunResult (sessionId: string) (decoded: Result<string, JsFailure>) =
        match decoded with
        | Error failure -> Error failure
        | Ok json ->
            JsToolsData.parseOutcome json
            |> fun parsed -> recordDeliveredValueIfNeeded sessionId json parsed

    let private settleExecutionOutcome
        (observation: Result<unit, JsFailure>)
        (ledgerOutcome: Result<unit, JsFailure>)
        (value: obj)
        (rewritten: string list)
        (created: string list)
        (removed: string list)
        (moved: (string * string) list)
        : JsToolOutcome =
        match observation, ledgerOutcome with
        | Error failure, _ -> Failed(failure, rewritten, created, removed, moved)
        | _, Error failure -> Failed(failure, rewritten, created, removed, moved)
        | Ok(), Ok() -> Succeeded(value, rewritten, created, removed, moved)

    /// Best-effort lifecycle audit trace. A failed audit never fails the tool
    /// call: the parked site is process-live and the audit is only a trace.
    let private recordLifecycle
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (eventType: JsLifecycleEventType)
        (handle: string)
        (sessionId: string)
        : unit =
        match lifecyclePersistence with
        | None -> ()
        | Some durable ->
            let event: JsLifecycleEvent =
                { EventType = eventType
                  Handle = handle
                  SessionId = sessionId
                  At = System.DateTimeOffset.UtcNow.ToString("o") }

            task {
                let! _ = durable.AppendLifecycleEvent event
                return ()
            }
            |> ignore

    let private registerParkedSite
        (site: JsParkedSites.JsParkedSite)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (baseClassSource: string)
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState)
        (rewritten: string list)
        (created: string list)
        (removed: string list)
        (moved: (string * string) list)
        : JsToolOutcome =
        match JsParkedSites.register site with
        | Error failure -> Failed(failure, rewritten, created, removed, moved)
        | Ok _ ->
            recordLifecycle lifecyclePersistence JsLifecycleEventType.Parked site.Handle site.SessionId

            let source =
                JsSandbox.sourceWithExecutionMarker baseClassSource site.ModelSource parkState.ParkStack

            Parked(site.Handle, hostRealmPayload parkState.ParkPayload, source)

    let private registerResumedParkedSite
        (newSite: JsParkedSites.JsParkedSite)
        (baseClassSource: string)
        : JsToolOutcome =
        match JsParkedSites.register newSite with
        | Error failure -> Failed(failure, [], [], [], [])
        | Ok _ ->
            let source =
                JsSandbox.sourceWithExecutionMarker
                    baseClassSource
                    newSite.ModelSource
                    newSite.RunHandle.ParkState.ParkStack

            Parked(newSite.Handle, hostRealmPayload newSite.RunHandle.ParkState.ParkPayload, source)

    let private settleResumedExecutionOutcome (ledgerOutcome: Result<unit, JsFailure>) (value: obj) : JsToolOutcome =
        match ledgerOutcome with
        | Error failure -> Failed(failure, [], [], [], [])
        | Ok() -> Succeeded(value, [], [], [], [])

    /// Run a model program against root. `baseClassSource` is the generated
    /// JsProgram (JS-002); `modelSource` is the model's `class Js ... run()`.
    /// deadlineMs bounds the sandbox; outputBoundBytes bounds the result.
    /// `persistence` enables durable prepare/commit facts (JS-012). Current and
    /// transaction head are owned by the canonical Integrator; no history reader is exposed here.
    let private runCore
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (modelSource: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (fileAccessObservation: FileAccessObservation option)
        : Task<JsToolOutcome> =
        task {
            // DSL-MUTABLE: algorithm-scratch — JS as-is write ledger. Every
            // edit/write is persisted atomically at call time; this buffer
            // records what was actually written, so any termination (throw,
            // timeout, output bound) keeps the already-applied mutations
            // observable and on disk.
            let staging = ResizeArray<JsStagedMutation>()
            let readSnapshots = ResizeArray<JsReadSnapshot>()
            let explicitReads = ResizeArray<JsExplicitFileRead>()
            let ledger = JsToolsBindings.createLedger persistence

            let! outcome =
                taskResult {
                    let callHost: JsToolsBindings.JsCallHost =
                        { Loop =
                            fun parkState callableClass stack -> JsCallLoop.execute parkState root callableClass stack }

                    let api =
                        JsToolsBindings.createApi
                            capabilities
                            root
                            staging
                            readSnapshots
                            explicitReads
                            ledger
                            (Some callHost)
                            None

                    let! resultJson =
                        task {
                            let! handle =
                                JsSandbox.runSurface baseClassSource modelSource api deadlineMs deadlineEpochMs

                            return! JsSandbox.awaitResult handle outputBoundBytes
                        }

                    let! value = JsToolsData.parseOutcome resultJson
                    return value
                }

            let mutations = staging |> Seq.toList
            let rewritten = rewrittenPaths mutations
            let created = createdPaths mutations
            let removed = removedPaths mutations
            let moved = movedPaths mutations
            let readPaths = explicitReads |> Seq.distinct |> Seq.toList
            let effectPaths = mutations |> List.map JsStagedMutation.path |> List.distinct

            let hasMutationCapability =
                Set.contains JsCapability.Edit capabilities
                || Set.contains JsCapability.Write capabilities
                || Set.contains JsCapability.Remove capabilities
                || Set.contains JsCapability.Move capabilities

            let! ledgerOutcome = settleLedger ledger

            match outcome with
            | Error failure ->
                // Program execution or return-value parsing failed; the writes
                // that already landed stay on disk and are reported as-is.
                return Failed(failure, rewritten, created, removed, moved)
            | Ok _ when (checkReadOnlyMutation hasMutationCapability mutations).IsSome ->
                return Failed(JsFailure.ReadOnlyMutationRejected, rewritten, created, removed, moved)
            | Ok value ->
                let! observation = observeFileAccess fileAccessObservation readPaths effectPaths
                return settleExecutionOutcome observation ledgerOutcome value rewritten created removed moved
        }

    let run
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (modelSource: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        : Task<JsToolOutcome> =
        runCore
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
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (modelSource: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (fileAccessObservation: FileAccessObservation)
        : Task<JsToolOutcome> =
        runCore
            capabilities
            root
            baseClassSource
            modelSource
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            (Some fileAccessObservation)

    /// Run a new program with park support. `sessionId` tags the site so
    /// session close can reap it. The park signal is installed before the
    /// program starts; if the program suspends at `await js.park(payload)` the
    /// signal wins the race and the workflow registers the site. A successful
    /// registration records the Parked lifecycle audit trace (best-effort).
    /// The buffers one run owns; `makeApi` decides which members see them.
    type private RunBuffers =
        { Staging: ResizeArray<JsStagedMutation>
          ReadSnapshots: ResizeArray<JsReadSnapshot>
          ExplicitReads: ResizeArray<JsExplicitFileRead>
          Ledger: JsAsIsLedger }

    let private runProgramCore
        (sessionId: string)
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (modelSource: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: FileAccessObservation option)
        (makeApi: RunBuffers -> Wanxiangshu.Process.JsSandbox.ParkState -> obj)
        : Task<JsToolOutcome> =
        task {
            // DSL-MUTABLE: algorithm-scratch — JS as-is write ledger. Every
            // edit/write is persisted atomically at call time; this buffer
            // records what was actually written, so any termination (throw,
            // timeout, output bound, park) keeps the already-applied mutations
            // observable and on disk.
            let staging = ResizeArray<JsStagedMutation>()
            let readSnapshots = ResizeArray<JsReadSnapshot>()
            let explicitReads = ResizeArray<JsExplicitFileRead>()
            let ledger = JsToolsBindings.createLedger persistence

            let buffers: RunBuffers =
                { Staging = staging
                  ReadSnapshots = readSnapshots
                  ExplicitReads = explicitReads
                  Ledger = ledger }

            // DSL-MUTABLE: resource — park lifecycle state shared by the api
            // member and the workflow. Process-live, never durable.
            let parkState: Wanxiangshu.Process.JsSandbox.ParkState =
                { ParkRequested = false
                  ParkPayload = null
                  ParkSignalResolve = null
                  ParkDeferredResolve = null
                  ParkDeferredReject = null
                  ParkStack = null
                  AbandonRequested = false }

            let api = makeApi buffers parkState

            // Install the park signal the workflow races against the program
            // promise before the program starts: the async IIFE body runs
            // synchronously to its first await, so an immediate
            // `await js.park(payload)` must already see ParkSignalResolve.
            // Fable has no Task.WhenAny; Promise.race is the established
            // pattern (Await.fs / FinalityController).
            let parkSignal = JsSandbox.installParkSignal parkState

            let! runHandle =
                JsSandbox.runSurfaceWithPark baseClassSource modelSource api deadlineMs deadlineEpochMs (Some parkState)

            let taggedProgram: Task<obj> =
                task {
                    let! json = runHandle.ProgramPromise
                    return box (Choice1Of2 json)
                }

            let taggedPark: Task<obj> =
                task {
                    let! payload = parkSignal
                    return box (Choice2Of2 payload)
                }

            let! winnerObj = emitJsExpr (taggedProgram, taggedPark) "Promise.race([$0, $1])": Task<obj>

            let outcome =
                match unbox<Choice<string, obj>> winnerObj with
                | Choice1Of2 json ->
                    // The program finished first (Done, or a failure/timeout
                    // sentinel). Decode it like the plain path.
                    let parsed =
                        JsSandbox.classifyResult json outputBoundBytes
                        |> Result.bind JsToolsData.parseOutcome

                    recordDeliveredValueIfNeeded sessionId json parsed
                | Choice2Of2 _ ->
                    // Park won: the program suspended at `await js.park`.
                    Ok(box "{\"__jsParked\":true}")

            let mutations = staging |> Seq.toList
            let rewritten = rewrittenPaths mutations
            let created = createdPaths mutations
            let removed = removedPaths mutations
            let moved = movedPaths mutations
            let readPaths = explicitReads |> Seq.distinct |> Seq.toList
            let effectPaths = mutations |> List.map JsStagedMutation.path |> List.distinct

            let hasMutationCapability =
                Set.contains JsCapability.Edit capabilities
                || Set.contains JsCapability.Write capabilities
                || Set.contains JsCapability.Remove capabilities
                || Set.contains JsCapability.Move capabilities

            match outcome with
            | Error failure ->
                // The program failed after the race; drain the fired ledger
                // (H1) so no append task is left unsettled. The program
                // failure keeps the reported code; landed writes stay as-is.
                let! _ = settleLedger ledger
                return Failed(failure, rewritten, created, removed, moved)
            | Ok parkedValue when isParkedSentinel parkedValue ->
                // The program parked. Register the site under a fresh handle.
                let handle = JsParkedSites.allocateHandle ()

                let site: JsParkedSites.JsParkedSite =
                    { Handle = handle
                      RunHandle = runHandle
                      ReadSnapshots = readSnapshots
                      Ledger = ledger
                      SessionId = sessionId
                      ModelSource = modelSource }

                return
                    registerParkedSite
                        site
                        lifecyclePersistence
                        baseClassSource
                        parkState
                        rewritten
                        created
                        removed
                        moved
            | Ok _ when (checkReadOnlyMutation hasMutationCapability mutations).IsSome ->
                return Failed(JsFailure.ReadOnlyMutationRejected, rewritten, created, removed, moved)
            | Ok value ->
                let! observation = observeFileAccess fileAccessObservation readPaths effectPaths
                let! ledgerOutcome = settleLedger ledger
                return settleExecutionOutcome observation ledgerOutcome value rewritten created removed moved
        }

    /// The generated-surface api factory: bindings own the file members and
    /// the host loop.
    let private bindingsApiFactory
        (sessionId: string)
        (capabilities: Set<JsCapability>)
        (root: string)
        (buffers: RunBuffers)
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState)
        : obj =
        let callHost: JsToolsBindings.JsCallHost =
            { Loop = fun parkState callableClass stack -> JsCallLoop.execute parkState root callableClass stack }

        JsToolsBindings.createApiWithPark
            capabilities
            root
            buffers.Staging
            buffers.ReadSnapshots
            buffers.ExplicitReads
            buffers.Ledger
            parkState
            (Some callHost)
            (JsDelegationBridge.hostFor sessionId)

    let private runProgramWithPark
        (sessionId: string)
        (capabilities: Set<JsCapability>)
        (root: string)
        (baseClassSource: string)
        (modelSource: string)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        (persistence: IJsTransactionPersistence option)
        (lifecyclePersistence: IJsLifecyclePersistence option)
        (fileAccessObservation: FileAccessObservation option)
        : Task<JsToolOutcome> =
        runProgramCore
            sessionId
            capabilities
            root
            baseClassSource
            modelSource
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            persistence
            lifecyclePersistence
            fileAccessObservation
            (fun buffers parkState -> bindingsApiFactory sessionId capabilities root buffers parkState)


    /// Resume a parked site (algorithm E). `handle` must exist; the deferred
    /// promise is resolved with `value`, the handle is removed (one-shot), and
    /// the deadline is reset to now+10s. If the program parks again the result
    /// is a new parked payload with a new handle.
    let private unparkSite
        (site: JsParkedSites.JsParkedSite)
        (value: obj)
        (baseClassSource: string)
        (deadlineMs: int)
        (outputBoundBytes: int)
        : Task<JsToolOutcome> =
        task {
            Wanxiangshu.Process.JsSandbox.unpark site.RunHandle.ParkState value
            JsParkedSites.remove site.Handle

            // Reset the deadline to now+10s (algorithm E5).
            let now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            Wanxiangshu.Process.JsSandbox.resetDeadline site.RunHandle (now + int64 (max 1000 deadlineMs))

            // Install a fresh park signal and race the program promise against
            // it: the resumed program may park again (algorithm E5).
            let parkSignal = JsSandbox.installParkSignal site.RunHandle.ParkState

            let taggedProgram: Task<obj> =
                task {
                    let! json = site.RunHandle.ProgramPromise
                    return box (Choice1Of2 json)
                }

            let taggedPark: Task<obj> =
                task {
                    let! payload = parkSignal
                    return box (Choice2Of2 payload)
                }

            let! winnerObj = emitJsExpr (taggedProgram, taggedPark) "Promise.race([$0, $1])": Task<obj>

            let outcome =
                match unbox<Choice<string, obj>> winnerObj with
                | Choice1Of2 _ ->
                    task {
                        let! decoded = JsSandbox.awaitResult site.RunHandle outputBoundBytes
                        return decodeRunResult site.SessionId decoded
                    }
                | Choice2Of2 _ -> task { return Ok(box "{\"__jsParked\":true}") }

            let! outcome = outcome

            match outcome with
            | Error failure ->
                let! _ = settleLedger site.Ledger
                return Failed(failure, [], [], [], [])
            | Ok parkedValue when isParkedSentinel parkedValue ->
                // The resumed program parked again: a new handle and a new
                // parked payload (algorithm E5). The ledger stays with the
                // site: it is settled when the resumed run finishes.
                let newHandle = JsParkedSites.allocateHandle ()

                let newSite: JsParkedSites.JsParkedSite =
                    { Handle = newHandle
                      RunHandle = site.RunHandle
                      ReadSnapshots = site.ReadSnapshots
                      Ledger = site.Ledger
                      SessionId = site.SessionId
                      ModelSource = site.ModelSource }

                return registerResumedParkedSite newSite baseClassSource
            | Ok value ->
                let! ledgerOutcome = settleLedger site.Ledger
                return settleResumedExecutionOutcome ledgerOutcome value
        }

    /// Abandon a parked site (algorithm F): reject the deferred promise with
    /// the `__jsAbandoned` marker so native try/finally runs, reset the
    /// deadline to the 10s cleanup window, then await the program's settle.
    /// The outcome is Abandoned (no value); the site is removed.
    let private abandonSite
        (site: JsParkedSites.JsParkedSite)
        (deadlineMs: int)
        (outputBoundBytes: int)
        : Task<JsToolOutcome> =
        task {
            Wanxiangshu.Process.JsSandbox.abandon site.RunHandle.ParkState

            // Reset the deadline to the 10s cleanup window (algorithm F2/F5).
            let now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            Wanxiangshu.Process.JsSandbox.resetDeadline site.RunHandle (now + int64 (max 1000 deadlineMs))

            // Await the program's settle; the ABANDONED rejection unwinds the
            // stack so finally blocks run. A timeout force-truncates (F5).
            let! _ = JsSandbox.awaitResult site.RunHandle outputBoundBytes

            // Drain the ledger the finalizer fired (H1): the Abandoned
            // outcome is final regardless, so a failed append is only
            // observed here, never escalated into a different code.
            let! _ = settleLedger site.Ledger

            JsParkedSites.remove site.Handle

            return Failed(JsFailure.Abandoned, [], [], [], [])
        }

    /// Run a program whose api is supplied by the caller but whose park
    /// lifecycle, race and outcome rendering stay workflow-owned. The plan
    /// tenure sandbox uses this so `js.ask` suspends through the same site
    /// table as the generated surfaces.
    let runWithParkApi
        (sessionId: string)
        (baseClassSource: string)
        (modelSource: string)
        (makeApi: Wanxiangshu.Process.JsSandbox.ParkState -> obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (outputBoundBytes: int)
        : Task<JsToolOutcome> =
        runProgramCore
            sessionId
            Set.empty
            ""
            baseClassSource
            modelSource
            deadlineMs
            deadlineEpochMs
            outputBoundBytes
            None
            None
            None
            (fun _ parkState -> makeApi parkState)

    /// Resume a parked site from any owning surface (generated or plan).
    let resumeParked
        (handle: string)
        (value: obj)
        (baseClassSource: string)
        (deadlineMs: int)
        (outputBoundBytes: int)
        : Task<JsToolOutcome> =
        match JsParkedSites.tryFind handle with
        | None -> Task.FromResult(Failed(JsFailure.HandleUnknown handle, [], [], [], []))
        | Some site -> unparkSite site value baseClassSource deadlineMs outputBoundBytes

    /// Abandon a parked site from any owning surface.
    let abandonParked (handle: string) (deadlineMs: int) (outputBoundBytes: int) : Task<JsToolOutcome> =
        match JsParkedSites.tryFind handle with
        | None -> Task.FromResult(Failed(JsFailure.HandleUnknown handle, [], [], [], []))
        | Some site -> abandonSite site deadlineMs outputBoundBytes

    [<RequireQualifiedAccess>]
    type private ToolWorkflowInvocation =
        | Program of source: string
        | Unpark of handle: string * value: obj
        | Abandon of handle: string
        | Invalid

    let private parseWorkflowInvocation (input: obj) : ToolWorkflowInvocation =
        if isNull input then
            ToolWorkflowInvocation.Invalid
        elif not (isNull input?program) then
            ToolWorkflowInvocation.Program(string input?program)
        elif not (isNull input?unpark) then
            let unpark = input?unpark
            ToolWorkflowInvocation.Unpark(string (unpark?handle), unpark?value)
        elif not (isNull input?abandon) then
            ToolWorkflowInvocation.Abandon(string ((input?abandon)?handle))
        else
            ToolWorkflowInvocation.Invalid

    let private resolveParkedSite (handle: string) : Result<JsParkedSites.JsParkedSite, JsFailure> =
        match JsParkedSites.tryFind handle with
        | Some site -> Ok site
        | None -> Error(JsFailure.HandleUnknown handle)

    let private dispatchResolvedParkedSite
        (resolved: Result<JsParkedSites.JsParkedSite, JsFailure>)
        (useSite: JsParkedSites.JsParkedSite -> Task<JsToolOutcome>)
        : Task<JsToolOutcome> =
        match resolved with
        | Error failure -> Task.FromResult(Failed(failure, [], [], [], []))
        | Ok site -> useSite site

    /// Execute one js-* tool invocation under the three-state input protocol
    /// (PLAN S2 step 5). The `input` object carries one of:
    ///   { program: string }
    ///   { unpark: { handle: string, value: obj } }
    ///   { abandon: { handle: string } }
    /// `sessionId` tags newly created parked sites so session close can reap
    /// them. `lifecyclePersistence` records Parked/Unparked/Abandoned audit
    /// traces (best-effort; a failed audit never fails the tool call); Reaped
    /// is recorded by the session-close path (PluginSessionScope).
    let execute
        (sessionId: string)
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
            match parseWorkflowInvocation input with
            | ToolWorkflowInvocation.Program programSource ->
                return!
                    runProgramWithPark
                        sessionId
                        capabilities
                        root
                        baseClassSource
                        programSource
                        deadlineMs
                        deadlineEpochMs
                        outputBoundBytes
                        persistence
                        lifecyclePersistence
                        fileAccessObservation
            | ToolWorkflowInvocation.Unpark(handle, value) ->
                return!
                    dispatchResolvedParkedSite (resolveParkedSite handle) (fun site ->
                        recordLifecycle lifecyclePersistence JsLifecycleEventType.Unparked handle sessionId
                        unparkSite site value baseClassSource deadlineMs outputBoundBytes)
            | ToolWorkflowInvocation.Abandon handle ->
                return!
                    dispatchResolvedParkedSite (resolveParkedSite handle) (fun site ->
                        recordLifecycle lifecyclePersistence JsLifecycleEventType.Abandoned handle sessionId
                        abandonSite site deadlineMs outputBoundBytes)
            | ToolWorkflowInvocation.Invalid ->
                return Failed(JsFailure.InvalidArgument "no program, unpark, or abandon input", [], [], [], [])
        }

/// JS-016: stable LLM-visible result shapes, rendered as Synthetic TOML
/// (the only rendering owner; ARCH-010). Success is `# ok` plus data/fs;
/// failure is `# failed` plus code/reason.
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
        | JsToolWorkflow.JsToolOutcome.Parked(handle, payload, source) ->
            // Top-level parked payload (PLAN S2 step 4): the program suspended
            // at `await js.park(payload)` and the site is registered under
            // handle. The caller resumes with `{ unpark: { handle, value } }`
            // or gives up with `{ abandon: { handle } }`.
            LlmFacing.instruction "parked"
            |> LlmFacing.withData (
                [ LlmFacing.Data.stringField "handle" handle
                  LlmFacing.Data.stringField "source" source ]
                @ LlmFacing.Data.rootStructuredValue (LlmFacing.Data.ofJs payload)
            )
            |> LlmFacing.render
