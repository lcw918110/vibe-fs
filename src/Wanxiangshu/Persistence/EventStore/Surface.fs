namespace Wanxiangshu.Persistence.EventStore

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Thoth.Json
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Sphinx.V2.Composition

/// Process-local EventStore owner surface. JS callers receive unprefixed
/// operations; EventStoreHandle remains an opaque capability.
module Surface =

    let private str (value: obj) : string =
        if isNull value then "" else string value

    let private payloadJson (value: obj) : string =
        match value with
        | null -> "null"
        | :? string as text -> text
        | _ -> JS.JSON.stringify value

    let private parentIds (value: obj) : EventId list =
        if isNull value then
            []
        else
            unbox<string array> value |> Array.toList |> List.map EventId.create

    let private payloadRefsOf (value: obj) : PayloadRef list =
        if isNull value then
            []
        else
            unbox<string array> value |> Array.toList |> List.map PayloadRef.create

    [<Emit("Buffer.from($0, 'base64')")>]
    let private bytesOfBase64 (value: string) : byte[] = jsNative

    let private payloadsOfJs (value: obj) : Map<PayloadRef, byte[]> =
        if isNull value then
            Map.empty
        else
            emitJsExpr value "Object.keys($0 || {}).map(function (key) { return [key, $0[key]]; })"
            |> unbox<(string * string) array>
            |> Array.fold
                (fun acc (key, base64) -> Map.add (PayloadRef.create key) (bytesOfBase64 base64) acc)
                Map.empty

    let private eventOfJs (value: obj) : EventEnvelope =
        { EventId = EventId.create (str (value?id))
          StreamId = EventStreamId.create (str (value?stream))
          EventType = str (value?``type``)
          Parents = parentIds (value?parents)
          Payload = unbox<JsonValue> (JS.JSON.parse (payloadJson (value?payload)))
          PayloadRefs = payloadRefsOf (value?payloadRefs)
          Payloads = payloadsOfJs (value?payloads) }
        |> EventEnvelope.normalize

    let private envelopeToJs (envelope: EventEnvelope) : obj =
        let payloadObject =
            CanonicalEventCodec.encode envelope
            |> (fun text -> text.TrimEnd('\n'))
            |> JS.JSON.parse
            |> fun eventObject -> eventObject?payload

        box
            {| id = EventId.value envelope.EventId
               stream = EventStreamId.value envelope.StreamId
               ``type`` = envelope.EventType
               parents = envelope.Parents |> List.map EventId.value |> List.toArray
               payload = payloadObject
               payloadRefs = envelope.PayloadRefs |> List.map PayloadRef.value |> List.toArray |}

    let private cutToJs (cut: SemanticCut) : obj =
        box
            {| failedEventId = EventId.value cut.FailedEventId
               rule = cut.Rule
               cutEventId = EventId.value cut.CutEventId
               reason = cut.Reason |}

    let private storageInvalidToJs (error: StorageInvalid) : obj =
        match error with
        | StorageInvalid.IdentityCollision eventId ->
            box
                {| code = "IdentityCollision"
                   eventId = EventId.value eventId |}
        | StorageInvalid.NonCanonical reason ->
            box
                {| code = "NonCanonical"
                   reason = reason |}
        | StorageInvalid.MalformedEnvelope reason ->
            box
                {| code = "MalformedEnvelope"
                   reason = reason |}
        | StorageInvalid.MissingParent eventId ->
            box
                {| code = "MissingParent"
                   eventId = EventId.value eventId |}
        | StorageInvalid.CyclicParents -> box {| code = "CyclicParents" |}
        | StorageInvalid.MissingPayload payloadRef ->
            box
                {| code = "MissingPayload"
                   payloadRef = PayloadRef.value payloadRef |}
        | StorageInvalid.UnknownEventType eventType ->
            box
                {| code = "UnknownEventType"
                   eventType = eventType |}

    let private phaseToJs phase =
        match phase with
        | AppendPhase.GateAcquire -> "GateAcquire"
        | AppendPhase.Preparation -> "Preparation"
        | AppendPhase.BeforePhysicalAppend -> "BeforePhysicalAppend"
        | AppendPhase.PhysicalAppend -> "PhysicalAppend"
        | AppendPhase.DurabilityOpen -> "DurabilityOpen"
        | AppendPhase.DurabilityBarrier -> "DurabilityBarrier"
        | AppendPhase.DurabilityClose -> "DurabilityClose"
        | AppendPhase.CurrentCommit -> "CurrentCommit"
        | AppendPhase.StoreRelease -> "StoreRelease"

    let private preparedToJs (prepared: PreparedAppend) : obj =
        box
            {| durableEvents = prepared.DurableEvents |> List.map envelopeToJs |> List.toArray
               cuts = prepared.Cuts |> List.map cutToJs |> List.toArray |}

    let private optionalPreparedToJs prepared =
        prepared |> Option.map preparedToJs |> Option.defaultValue null

    let private faultToJs (fault: AppendFault) : obj =
        box
            {| phase = phaseToJs fault.Phase
               cause = fault.Cause |}

    let private rejectionToJs rejection : obj =
        match rejection with
        | AppendPreWriteRejection.StorageInvalid invalid ->
            box
                {| code = "StorageInvalid"
                   error = storageInvalidToJs invalid |}
        | AppendPreWriteRejection.PreparationRejected reason ->
            box
                {| code = "AppendFailed"
                   reason = reason |}

    let appendErrorToJs (error: AppendError) : obj =
        match error with
        | AppendError.StorageInvalid invalid ->
            box
                {| code = "StorageInvalid"
                   error = storageInvalidToJs invalid |}
        | AppendError.SemanticCut cut ->
            box
                {| code = "SemanticCut"
                   cut = cutToJs cut |}
        | AppendError.AppendFailed reason ->
            box
                {| code = "AppendFailed"
                   reason = reason |}
        | AppendError.AppendNotAttempted evidence ->
            box
                {| code = "AppendNotAttempted"
                   phase = phaseToJs evidence.Primary.Phase
                   cause = evidence.Primary.Cause
                   cleanupFailures = evidence.CleanupFailures |> List.map faultToJs |> List.toArray
                   requested = evidence.Requested |> List.map envelopeToJs |> List.toArray
                   prepared = optionalPreparedToJs evidence.Prepared
                   priorRejection = evidence.PriorRejection |> Option.map rejectionToJs |> Option.defaultValue null |}
        | AppendError.CommitUnknown evidence ->
            box
                {| code = "CommitUnknown"
                   phase = phaseToJs evidence.Primary.Phase
                   cause = evidence.Primary.Cause
                   cleanupFailures = evidence.CleanupFailures |> List.map faultToJs |> List.toArray
                   requested = evidence.Requested |> List.map envelopeToJs |> List.toArray
                   prepared = preparedToJs evidence.Prepared
                   priorRejection = null |}
        | AppendError.NoNewWriteReleaseFailed evidence ->
            box
                {| code = "NoNewWriteReleaseFailed"
                   phase = "StoreRelease"
                   cause = evidence.Cause
                   cleanupFailures = ([||]: obj array)
                   requested = evidence.Requested |> List.map envelopeToJs |> List.toArray
                   prepared = optionalPreparedToJs evidence.Prepared
                   priorRejection = null |}

    let private createIntegrator () =
        // Historical full program, assembled explicitly in registration order:
        // Structural, Journal, Sphinx, SphinxGeneric, Casebook, JsTransaction, JsLifecycleAudit.

        let program =
            CanonicalIntegrator.baseRules
            @ Wanxiangshu.Sphinx.V2.Composition.Bind.rules
            @ Wanxiangshu.Repository.Knowledge.Casebook.CasebookIntegrationRules.rules
            @ Wanxiangshu.Repository.Programming.Js.JsTransactionIntegrationRules.rules
            @ Wanxiangshu.Repository.Programming.Js.JsLifecycleIntegrationRules.rules

        CanonicalIntegrator.createWithRules program AuthoritativeEventTypes.isKnown

    /// Create a process-local writer capability. The caller owns its lifecycle.
    let create (commonDir: string, writerId: string) : EventStoreHandle =
        EventStoreHandle.Create(EventStore.createLocal commonDir writerId (createIntegrator ()))

    /// Create a process-local writer capability with an injected single-segment
    /// byte cap (durable-events-005). Production callers use `create`.
    let createWithSegmentCap (commonDir: string, writerId: string, capBytes: int) : EventStoreHandle =
        EventStoreHandle.Create(EventStore.createLocalWithSegmentCap commonDir writerId capBytes (createIntegrator ()))

    /// Actual canonical preparation and append; only its returned Commit boundary faults.
    let createWithCurrentCommitFault
        (commonDir: string, writerId: string, cause: obj, commitBeforeFailure: bool)
        : EventStoreHandle =
        let integrator = createIntegrator ()

        let failCommit (prepared: PreparedIntegration) =
            { prepared with
                Commit =
                    fun () ->
                        if commitBeforeFailure then
                            prepared.Commit()

                        raise (unbox<exn> cause) }

        let observed =
            { new ICanonicalIntegrator with
                member _.PrepareLive events =
                    integrator.PrepareLive events |> Result.map failCommit

                member _.ReloadLocal directory = integrator.ReloadLocal directory
                member _.IsEventTypeKnown eventType = integrator.IsEventTypeKnown eventType
                member _.TryCurrent key = integrator.TryCurrent key
                member _.TryEvent eventId = integrator.TryEvent eventId
                member _.TryPayload payloadRef = integrator.TryPayload payloadRef
                member _.TryHeads streamId = integrator.TryHeads streamId
                member _.TryHead streamId = integrator.TryHead streamId
                member _.AllHeads() = integrator.AllHeads() }

        EventStoreHandle.Create(EventStore.createLocal commonDir writerId observed)

    let private phaseOfJs (value: obj) =
        match str value with
        | "GateAcquire" -> AppendPhase.GateAcquire
        | "Preparation" -> AppendPhase.Preparation
        | "BeforePhysicalAppend" -> AppendPhase.BeforePhysicalAppend
        | "PhysicalAppend" -> AppendPhase.PhysicalAppend
        | "DurabilityOpen" -> AppendPhase.DurabilityOpen
        | "DurabilityBarrier" -> AppendPhase.DurabilityBarrier
        | "DurabilityClose" -> AppendPhase.DurabilityClose
        | "CurrentCommit" -> AppendPhase.CurrentCommit
        | "StoreRelease" -> AppendPhase.StoreRelease
        | _ -> invalidArg "phase" "unknown append phase"

    let private controlledAppendError (options: obj) requested =
        let code = str options?code
        let cause = unbox<exn> options?cause

        let cleanup =
            if isNull options?cleanupFailures then
                []
            else
                unbox<obj array> options?cleanupFailures
                |> Array.toList
                |> List.map (fun fault ->
                    { Phase = phaseOfJs fault?phase
                      Cause = unbox<exn> fault?cause })

        match code with
        | "AppendNotAttempted" ->
            AppendError.AppendNotAttempted
                { Requested = requested
                  Prepared = None
                  Primary =
                    { Phase = phaseOfJs options?phase
                      Cause = cause }
                  CleanupFailures = cleanup
                  PriorRejection = None }
        | "CommitUnknown" ->
            AppendError.CommitUnknown
                { Requested = requested
                  Prepared = { DurableEvents = requested; Cuts = [] }
                  Primary =
                    { Phase = phaseOfJs options?phase
                      Cause = cause }
                  CleanupFailures = cleanup }
        | "NoNewWriteReleaseFailed" ->
            AppendError.NoNewWriteReleaseFailed
                { Requested = requested
                  Prepared = None
                  Cause = cause }
        | _ -> invalidArg "code" "unknown controlled append failure"

    let private observeAppendResult onAppend ordinal requested (result: Result<AppendReceipt, AppendError>) =
        let error, originalError =
            match result with
            | Ok _ -> null, null
            | Error failure -> appendErrorToJs failure, box failure

        let cuts =
            match result with
            | Ok receipt -> receipt.Cuts
            | Error failure -> AppendError.semanticCuts failure

        onAppend (
            box
                {| ordinal = ordinal
                   requested = requested |> List.map envelopeToJs |> List.toArray
                   error = error
                   cuts = cuts |> List.map cutToJs |> List.toArray
                   originalError = originalError |}
        )

    /// Controlled result mapping only. Other appends and all reads use the caller's actual store.
    let createAppendFailureStore
        (baseHandle: EventStoreHandle, options: obj, onAppend: obj -> unit)
        : EventStoreHandle =
        let store = baseHandle.Store

        let failAt =
            if isNull options?failAt then
                1
            else
                unbox<int> options?failAt

        if failAt <= 0 then
            invalidArg "failAt" "append ordinal must be positive"

        // DSL-MUTABLE: resource — controlled port invocation ordinal, not durable truth.
        let mutable ordinal = 0

        let controlled =
            { new IEventStore with
                member _.Append events =
                    task {
                        ordinal <- ordinal + 1
                        let current = ordinal

                        let! result =
                            if current = failAt then
                                Task.FromResult(Error(controlledAppendError options events))
                            else
                                store.Append events

                        observeAppendResult onAppend current events result
                        return result
                    }

                member _.WritePayload content = store.WritePayload content
                member _.ReadPayload payloadRef = store.ReadPayload payloadRef
                member _.TryCurrent key = store.TryCurrent key
                member _.TryEvent eventId = store.TryEvent eventId
                member _.TryHeads streamId = store.TryHeads streamId
                member _.TryHead streamId = store.TryHead streamId
                member _.AllHeads() = store.AllHeads()
                member _.ReloadLocal() = store.ReloadLocal() }

        EventStoreHandle.Create controlled

    let private createAppendPayloadStoreWhen
        (baseHandle: EventStoreHandle, alterPayload: int -> bool, onAppend: obj -> unit)
        : EventStoreHandle =
        let store = baseHandle.Store
        // DSL-MUTABLE: resource — invocation ordinal of this borrowed append port.
        let mutable ordinal = 0

        let observed =
            { new IEventStore with
                member _.Append events =
                    task {
                        ordinal <- ordinal + 1
                        let current = ordinal

                        let requested =
                            if alterPayload current then
                                events
                                |> List.map (fun event ->
                                    { event with
                                        Payload = Encode.object [] })
                            else
                                events

                        let! result = store.Append requested

                        observeAppendResult
                            (fun append ->
                                onAppend (
                                    box
                                        {| originalRequested = events |> List.map envelopeToJs |> List.toArray
                                           append = append |}
                                ))
                            current
                            requested
                            result

                        return result
                    }

                member _.WritePayload content = store.WritePayload content
                member _.ReadPayload reference = store.ReadPayload reference
                member _.TryCurrent key = store.TryCurrent key
                member _.TryEvent eventId = store.TryEvent eventId
                member _.TryHeads stream = store.TryHeads stream
                member _.TryHead stream = store.TryHead stream
                member _.AllHeads() = store.AllHeads()
                member _.ReloadLocal() = store.ReloadLocal() }

        EventStoreHandle.Create observed

    /// Physical boundary probe: alter only payload bytes and forward the real append settlement.
    let createAppendPayloadStore
        (baseHandle: EventStoreHandle, malformed: bool, onAppend: obj -> unit)
        : EventStoreHandle =
        createAppendPayloadStoreWhen (baseHandle, (fun _ -> malformed), onAppend)

    let createAppendPayloadStoreAt
        (baseHandle: EventStoreHandle, malformedAt: int, onAppend: obj -> unit)
        : EventStoreHandle =
        if malformedAt <= 0 then
            invalidArg "malformedAt" "append ordinal must be positive"

        createAppendPayloadStoreWhen (baseHandle, ((=) malformedAt), onAppend)

    /// Release a writer capability. Further operations fail rather than using a
    /// stale resource.
    let dispose (handle: EventStoreHandle) : unit = handle.Dispose()

    /// Append JS-native events and return only the durable receipt.
    let append (handle: EventStoreHandle, events: obj array) : Task<obj> =
        task {
            let parsed = events |> Array.toList |> List.map eventOfJs
            let! result = handle.Store.Append parsed

            return
                match result with
                | Ok receipt ->
                    box
                        {| ok = true
                           cuts = receipt.Cuts |> List.map cutToJs |> List.toArray |}
                | Error error ->
                    box
                        {| ok = false
                           error = appendErrorToJs error |}
        }

    /// Read one durable event by identity. A missing event is `null`.
    let read (handle: EventStoreHandle, eventId: string) : obj =
        match handle.Store.TryEvent(EventId.create eventId) with
        | None -> null
        | Some envelope -> envelopeToJs envelope

    /// Read every stream id that owns a durable head. Offline tooling
    /// enumerates the store through this owner surface instead of reading
    /// writer files; the head-to-stream hop reuses the same read path.
    let streams (handle: EventStoreHandle) : string array =
        handle.Store.AllHeads()
        |> List.choose (fun eventId -> handle.Store.TryEvent eventId)
        |> List.map (fun envelope -> EventStreamId.value envelope.StreamId)
        |> List.distinct
        |> List.sort
        |> List.toArray

    /// Read all structural heads for one stream.
    let heads (handle: EventStoreHandle, streamId: string) : string array =
        handle.Store.TryHeads(EventStreamId.create streamId)
        |> List.map EventId.value
        |> List.toArray

    /// Read the unique structural head, or `null` when the stream is forked/empty.
    let head (handle: EventStoreHandle, streamId: string) : obj =
        match handle.Store.TryHead(EventStreamId.create streamId) with
        | None -> null
        | Some eventId -> box (EventId.value eventId)

    let readPayload (handle: EventStoreHandle, payloadRef: string) : Task<obj> =
        task {
            let! result = handle.ReadPayload payloadRef

            match result with
            | None -> return null
            | Some bytes -> return box bytes
        }

    /// The canonical remote store ref owned by persistence infrastructure.
    let canonicalStoreRef = StoreRef.canonical
