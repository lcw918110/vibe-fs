namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open Fable.Core
open Thoth.Json
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

/// Narrow durable capability exposed to js-* workflow/tool wiring. The Host
/// registry never owns both AgentJournal and the raw EventStore capability.
type IJsTransactionPersistence =
    abstract AppendPrepared: prepared: JsTransactionPrepared -> Task<Result<EventId, JsTransactionAppendFailure>>
    abstract AppendCommitted: transactionId: JsTransactionId -> Task<Result<EventId, JsTransactionAppendFailure>>

module private JsLedgerTasks =
    [<Emit("$0.catch(() => undefined)")>]
    let observeRejection (_append: obj) : unit = jsNative

    [<Emit("($0 != null && typeof $0[$1] === 'string' ? $0[$1] : '')")>]
    let private textField (_error: obj) (_name: string) : string = jsNative

    let private rejectedFailure error =
        let code = textField error "code"
        let reason = textField error "reason"
        let message = textField error "message"

        JsFailure.ofWire
            (if code = "" then "PROGRAM_FAILED" else code)
            (if reason <> "" then reason
             elif message <> "" then message
             else "operation failed")

    let settle (pending: Task<Result<EventId, JsTransactionAppendFailure>>) : Task<Result<unit, JsFailure>> =
        task {
            try
                let! appended = pending

                return
                    appended
                    |> Result.map ignore
                    |> Result.mapError JsFailure.TransactionPersistenceFailed
            with error ->
                return Error(rejectedFailure (box error))
        }

/// 同一程序跨 park/unpark 持有已发起的追加，终局等完后按发起顺序报告首次失败。
[<Sealed>]
type JsAsIsLedger(persistence: IJsTransactionPersistence option) =
    let appends = ResizeArray<Task<Result<EventId, JsTransactionAppendFailure>>>()

    let fire pending =
        JsLedgerTasks.observeRejection (box pending)
        appends.Add pending

    member _.Prepared(root: string, mutation: JsStagedMutation) : JsTransactionId option =
        match persistence with
        | None -> None
        | Some durable ->
            let prepared =
                { TransactionId = JsTransactionId.generate ()
                  WorkspaceRoot = root
                  Mutation = JsTransactionFacts.ofMutation mutation }

            durable.AppendPrepared prepared |> fire
            Some prepared.TransactionId

    member _.Committed(transactionId: JsTransactionId option) : unit =
        match persistence, transactionId with
        | Some durable, Some transactionId -> durable.AppendCommitted transactionId |> fire
        | _ -> ()

    member _.Settle() : Task<Result<unit, JsFailure>> =
        task {
            let mutable firstFailure = None

            let remember appended =
                match firstFailure, appended with
                | None, Error failure -> firstFailure <- Some failure
                | _ -> ()

            for pending in appends do
                let! appended = JsLedgerTasks.settle pending

                remember appended

            return
                match firstFailure with
                | Some failure -> Error failure
                | None -> Ok()
        }

/// Narrow durable audit capability for parked-site lifecycle transitions
/// (PLAN S2 step 8 / algorithm H3). The audit stream is separate from the
/// mutation-ledger stream: it records that a lifecycle transition happened and
/// never participates in write replay. Callers (park/unpark/abandon/reap in
/// ToolWorkflow) receive this interface; the append implementation is here.
type IJsLifecyclePersistence =
    abstract AppendLifecycleEvent: event: JsLifecycleEvent -> Task<Result<EventId, JsLifecycleAppendFailure>>

/// JS-012/JS-015: durable transaction facts through the unified EventStore —
/// the only persistence a js-* transaction may use (forbid js-transaction.db
/// / feature store). A transaction is Prepared before any filesystem effect
/// and Committed after; an uncommitted Prepared remains interrupted-tool evidence.
/// PLAN S2 step 8: the Prepared payload is single-mutation — each write has
/// its own Prepared/Committed pair, no multi-mutation aggregation.
module JsToolsTransactionStore =

    /// Single linear stream for transaction facts.
    let TransactionStream = "js-tools/transactions"
    let PreparedEventType = JsTransactionEventTypes.Prepared
    let CommittedEventType = JsTransactionEventTypes.Committed

    // ---- payload codec ----------------------------------------------------

    let private encodeDurableMutation (mutation: JsDurableMutation) : JsonValue =
        Encode.object
            [ "path", Encode.string mutation.Path
              "originalText",
              match mutation.OriginalText with
              | Some text -> Encode.string text
              | None -> Encode.nil
              "newText", Encode.string mutation.NewText
              "kind",
              match mutation.Kind with
              | JsDurableMutationKind.Rewrite -> Encode.string "rewrite"
              | JsDurableMutationKind.Create -> Encode.string "create"
              | JsDurableMutationKind.Remove -> Encode.string "remove"
              | JsDurableMutationKind.Move -> Encode.string "move" ]

    let private decodeDurableMutation: Decoder<JsDurableMutation> =
        Decode.object (fun get ->
            { Path = get.Required.Field "path" Decode.string
              OriginalText = get.Optional.Field "originalText" Decode.string
              NewText = get.Required.Field "newText" Decode.string
              Kind =
                match get.Optional.Field "kind" Decode.string with
                | None -> JsDurableMutationKind.Rewrite
                | Some "create" -> JsDurableMutationKind.Create
                | Some "remove" -> JsDurableMutationKind.Remove
                | Some "move" -> JsDurableMutationKind.Move
                | Some _ -> JsDurableMutationKind.Rewrite })

    /// PLAN S2 step 8 / algorithm H1: the Prepared payload is a single
    /// mutation, not an array.
    let private encodePrepared (prepared: JsTransactionPrepared) : JsonValue =
        Encode.object
            [ "transactionId", Encode.string (JsTransactionId.value prepared.TransactionId)
              "workspaceRoot", Encode.string prepared.WorkspaceRoot
              "mutation", encodeDurableMutation prepared.Mutation ]

    let private decodePrepared: Decoder<JsTransactionPrepared> =
        Decode.object (fun get ->
            { TransactionId = JsTransactionId.create (get.Required.Field "transactionId" Decode.string)
              WorkspaceRoot = get.Required.Field "workspaceRoot" Decode.string
              Mutation = get.Required.Field "mutation" decodeDurableMutation })

    let private encodeCommitted (committed: JsTransactionCommitted) : JsonValue =
        Encode.object [ "transactionId", Encode.string (JsTransactionId.value committed.TransactionId) ]

    let private decodeCommitted: Decoder<JsTransactionCommitted> =
        Decode.object (fun get ->
            { TransactionId = JsTransactionId.create (get.Required.Field "transactionId" Decode.string) })

    let private payload (json: JsonValue) : JsonValue = json

    // ---- append -----------------------------------------------------------

    let isTransactionEventType eventType =
        JsTransactionEventTypes.isTransactionEvent eventType

    type DecodedTransactionEvent =
        | Prepared of JsTransactionPrepared
        | Committed of JsTransactionCommitted

    /// Single-event integration decoder. History iteration belongs exclusively
    /// to CanonicalIntegrator.
    let tryDecodeEnvelope (envelope: EventEnvelope) : Result<DecodedTransactionEvent, string> =
        match envelope.EventType with
        | eventType when eventType = PreparedEventType ->
            Decode.fromValue "$" decodePrepared envelope.Payload |> Result.map Prepared
        | eventType when eventType = CommittedEventType ->
            Decode.fromValue "$" decodeCommitted envelope.Payload |> Result.map Committed
        | other -> Error(sprintf "not a JsTransaction event: %s" other)

    let private tripSettledCut eventType eventId error =
        if
            AppendError.semanticCuts error
            |> List.exists (fun cut -> cut.FailedEventId = eventId)
        then
            FatalProcess.trip
                "js-transaction-semantic-cut"
                (sprintf
                    "%s semantic cut after append %s: %s"
                    eventType
                    (EventId.value eventId)
                    (AppendError.describe error))

    /// Append the Prepared fact using the Integrator-owned structural head.
    let appendPrepared
        (store: IEventStore)
        (prepared: JsTransactionPrepared)
        : Task<Result<EventId, JsTransactionAppendFailure>> =
        task {
            let eventId = EventId.create (System.Guid.NewGuid().ToString("N"))
            let streamId = EventStreamId.create TransactionStream

            let envelope =
                EventEnvelope.normalize
                    { EventId = eventId
                      StreamId = streamId
                      EventType = PreparedEventType
                      Parents = store.TryHead streamId |> Option.toList
                      Payload = payload (encodePrepared prepared)
                      PayloadRefs = []
                      Payloads = Map.empty }

            match! store.Append [ envelope ] with
            | Ok receipt when AppendReceipt.cutFor eventId receipt |> Option.isSome ->
                let cut = AppendReceipt.cutFor eventId receipt |> Option.get
                let reason = "JsTransactionPrepared semantic cut: " + cut.Reason
                FatalProcess.trip "js-transaction-semantic-cut" reason

                return
                    Error
                        { Phase = JsTransactionAppendPhase.Prepared
                          TransactionId = prepared.TransactionId
                          EventId = eventId
                          Error = AppendError.SemanticCut cut }
            | Ok _ -> return Ok eventId
            | Error err ->
                tripSettledCut PreparedEventType eventId err

                return
                    Error
                        { Phase = JsTransactionAppendPhase.Prepared
                          TransactionId = prepared.TransactionId
                          EventId = eventId
                          Error = err }
        }

    /// Append the Committed fact for a prepared transaction.
    let appendCommitted
        (store: IEventStore)
        (transactionId: JsTransactionId)
        : Task<Result<EventId, JsTransactionAppendFailure>> =
        task {
            let eventId = EventId.create (System.Guid.NewGuid().ToString("N"))
            let streamId = EventStreamId.create TransactionStream

            let envelope =
                EventEnvelope.normalize
                    { EventId = eventId
                      StreamId = streamId
                      EventType = CommittedEventType
                      Parents = store.TryHead streamId |> Option.toList
                      Payload = payload (encodeCommitted { TransactionId = transactionId })
                      PayloadRefs = []
                      Payloads = Map.empty }

            match! store.Append [ envelope ] with
            | Ok receipt when AppendReceipt.cutFor eventId receipt |> Option.isSome ->
                let cut = AppendReceipt.cutFor eventId receipt |> Option.get
                let reason = "JsTransactionCommitted semantic cut: " + cut.Reason
                FatalProcess.trip "js-transaction-semantic-cut" reason

                return
                    Error
                        { Phase = JsTransactionAppendPhase.Committed
                          TransactionId = transactionId
                          EventId = eventId
                          Error = AppendError.SemanticCut cut }
            | Ok _ -> return Ok eventId
            | Error err ->
                tripSettledCut CommittedEventType eventId err

                return
                    Error
                        { Phase = JsTransactionAppendPhase.Committed
                          TransactionId = transactionId
                          EventId = eventId
                          Error = err }
        }

    let createPersistence (store: IEventStore) : IJsTransactionPersistence =
        { new IJsTransactionPersistence with
            member _.AppendPrepared(prepared) = appendPrepared store prepared
            member _.AppendCommitted(transactionId) = appendCommitted store transactionId }

    // ---- lifecycle audit stream (PLAN S2 step 8 / algorithm H3) ------------

    /// Independent linear audit stream, deliberately separate from
    /// `js-tools/transactions`: mutation-ledger facts participate in interrupt
    /// audit and write replay; lifecycle audit facts only record "happened".
    let LifecycleAuditStream = "js-tools/lifecycle-audit"
    let ParkedEventType = JsLifecycleAuditEventTypes.Parked
    let UnparkedEventType = JsLifecycleAuditEventTypes.Unparked
    let AbandonedEventType = JsLifecycleAuditEventTypes.Abandoned
    let ReapedEventType = JsLifecycleAuditEventTypes.Reaped

    let lifecycleEventTypeOf (eventType: JsLifecycleEventType) : string =
        match eventType with
        | JsLifecycleEventType.Parked -> ParkedEventType
        | JsLifecycleEventType.Unparked -> UnparkedEventType
        | JsLifecycleEventType.Abandoned -> AbandonedEventType
        | JsLifecycleEventType.Reaped -> ReapedEventType

    let lifecycleEventTypeOfName (eventType: string) : JsLifecycleEventType option =
        match eventType with
        | value when value = ParkedEventType -> Some JsLifecycleEventType.Parked
        | value when value = UnparkedEventType -> Some JsLifecycleEventType.Unparked
        | value when value = AbandonedEventType -> Some JsLifecycleEventType.Abandoned
        | value when value = ReapedEventType -> Some JsLifecycleEventType.Reaped
        | _ -> None

    let isLifecycleEventType eventType =
        JsLifecycleAuditEventTypes.isLifecycleAuditEvent eventType

    /// PLAN H3 field shape: { eventType, handle, sessionId, at } — no site
    /// serialization, no payload/value.
    let private encodeLifecycleEvent (event: JsLifecycleEvent) : JsonValue =
        Encode.object
            [ "eventType", Encode.string (lifecycleEventTypeOf event.EventType)
              "handle", Encode.string event.Handle
              "sessionId", Encode.string event.SessionId
              "at", Encode.string event.At ]

    let private decodeLifecycleEvent: Decoder<JsLifecycleEvent> =
        Decode.object (fun get ->
            let eventType =
                get.Required.Field "eventType" Decode.string |> lifecycleEventTypeOfName

            match eventType with
            | None -> failwith "JsLifecycle eventType is not part of the audit vocabulary"
            | Some kind ->
                { EventType = kind
                  Handle = get.Required.Field "handle" Decode.string
                  SessionId = get.Required.Field "sessionId" Decode.string
                  At = get.Required.Field "at" Decode.string })

    type DecodedLifecycleEvent = Lifecycle of JsLifecycleEvent

    let tryDecodeLifecycleEnvelope (envelope: EventEnvelope) : Result<DecodedLifecycleEvent, string> =
        if isLifecycleEventType envelope.EventType then
            Decode.fromValue "$" decodeLifecycleEvent envelope.Payload
            |> Result.map Lifecycle
        else
            Error(sprintf "not a JsLifecycle audit event: %s" envelope.EventType)

    let private tripSettledLifecycleCut eventType eventId error =
        if
            AppendError.semanticCuts error
            |> List.exists (fun cut -> cut.FailedEventId = eventId)
        then
            FatalProcess.trip
                "js-lifecycle-audit-semantic-cut"
                (sprintf
                    "%s semantic cut after append %s: %s"
                    eventType
                    (EventId.value eventId)
                    (AppendError.describe error))

    /// Append one lifecycle audit trace. Callers are the park/unpark/abandon/
    /// reap transition points in ToolWorkflow (lane-a-core owns those call
    /// sites); this function is the append interface.
    let appendLifecycleEvent
        (store: IEventStore)
        (event: JsLifecycleEvent)
        : Task<Result<EventId, JsLifecycleAppendFailure>> =
        task {
            let eventId = EventId.create (System.Guid.NewGuid().ToString("N"))
            let streamId = EventStreamId.create LifecycleAuditStream
            let eventType = lifecycleEventTypeOf event.EventType

            let envelope =
                EventEnvelope.normalize
                    { EventId = eventId
                      StreamId = streamId
                      EventType = eventType
                      Parents = store.TryHead streamId |> Option.toList
                      Payload = payload (encodeLifecycleEvent event)
                      PayloadRefs = []
                      Payloads = Map.empty }

            match! store.Append [ envelope ] with
            | Ok receipt when AppendReceipt.cutFor eventId receipt |> Option.isSome ->
                let cut = AppendReceipt.cutFor eventId receipt |> Option.get
                let reason = "JsLifecycle audit semantic cut: " + cut.Reason
                FatalProcess.trip "js-lifecycle-audit-semantic-cut" reason

                return
                    Error
                        { EventType = event.EventType
                          Handle = event.Handle
                          SessionId = event.SessionId
                          EventId = eventId
                          Error = AppendError.SemanticCut cut }
            | Ok _ -> return Ok eventId
            | Error err ->
                tripSettledLifecycleCut eventType eventId err

                return
                    Error
                        { EventType = event.EventType
                          Handle = event.Handle
                          SessionId = event.SessionId
                          EventId = eventId
                          Error = err }
        }

    let createLifecyclePersistence (store: IEventStore) : IJsLifecyclePersistence =
        { new IJsLifecyclePersistence with
            member _.AppendLifecycleEvent(event) = appendLifecycleEvent store event }
