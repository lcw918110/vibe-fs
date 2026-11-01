namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

/// Narrow durable capability exposed to js-* workflow/tool wiring. The Host
/// registry never owns both AgentJournal and the raw EventStore capability.
type IJsTransactionPersistence =
    abstract AppendPrepared: prepared: JsTransactionPrepared -> Task<Result<EventId, JsTransactionAppendFailure>>
    abstract AppendCommitted: transactionId: JsTransactionId -> Task<Result<EventId, JsTransactionAppendFailure>>

/// 同一程序跨 park/unpark 持有已发起的追加，终局等完后按发起顺序报告首次失败。
[<Sealed>]
type JsAsIsLedger =
    new: persistence: IJsTransactionPersistence option -> JsAsIsLedger
    member Prepared: root: string * mutation: JsStagedMutation -> JsTransactionId option
    member Committed: transactionId: JsTransactionId option -> unit
    member Settle: unit -> Task<Result<unit, JsFailure>>

/// Narrow durable audit capability for parked-site lifecycle transitions.
/// The audit stream is separate from the mutation-ledger stream.
type IJsLifecyclePersistence =
    abstract AppendLifecycleEvent: event: JsLifecycleEvent -> Task<Result<EventId, JsLifecycleAppendFailure>>

/// JS-012/JS-015: durable transaction facts through the unified EventStore —
/// the only persistence a js-* transaction may use (forbid js-transaction.db
/// / feature store). A transaction is Prepared before any filesystem effect
/// and Committed after; an uncommitted Prepared remains interrupted-tool evidence.
module JsToolsTransactionStore =

    [<RequireQualifiedAccess>]
    type DecodedTransactionEvent =
        | Prepared of JsTransactionPrepared
        | Committed of JsTransactionCommitted

    val isTransactionEventType: eventType: string -> bool
    val tryDecodeEnvelope: envelope: EventEnvelope -> Result<DecodedTransactionEvent, string>

    val appendPrepared:
        store: IEventStore -> prepared: JsTransactionPrepared -> Task<Result<EventId, JsTransactionAppendFailure>>

    val appendCommitted:
        store: IEventStore -> transactionId: JsTransactionId -> Task<Result<EventId, JsTransactionAppendFailure>>

    val createPersistence: store: IEventStore -> IJsTransactionPersistence

    val lifecycleEventTypeOf: eventType: JsLifecycleEventType -> string
    val lifecycleEventTypeOfName: eventType: string -> JsLifecycleEventType option
    val isLifecycleEventType: eventType: string -> bool

    type DecodedLifecycleEvent = Lifecycle of JsLifecycleEvent

    val tryDecodeLifecycleEnvelope: envelope: EventEnvelope -> Result<DecodedLifecycleEvent, string>

    val appendLifecycleEvent:
        store: IEventStore -> event: JsLifecycleEvent -> Task<Result<EventId, JsLifecycleAppendFailure>>

    val createLifecyclePersistence: store: IEventStore -> IJsLifecyclePersistence
