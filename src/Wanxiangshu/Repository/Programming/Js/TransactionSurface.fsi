namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks

/// JS-native owner boundary for the transaction decision algebra and its one
/// durable EventStore adapter. Mutation records, failures and projections are
/// translated here; Fable unions and lists never cross this edge.
[<RequireQualifiedAccess>]
module JsTransactionSurface =

    /// Stable failure catalog; constructor identity remains private to the
    /// domain while every shipped code and reason is observable as plain data.
    val failureCatalog: unit -> obj array

    val validateAnchorDeclaration: declaration: obj -> obj
    val validateAnchorOccurrence: declaration: obj -> obj
    val validateSingleIntent: mutations: obj array -> obj
    val validateTargets: existing: string array -> mutations: obj array -> obj
    val validateFreshness: current: obj -> mutations: obj array -> obj

    val preflight: existing: string array -> current: obj -> readSnapshots: obj array -> mutations: obj array -> obj

    val commitPlan: mutations: obj array -> obj array
    val rollbackPlan: mutations: obj array -> obj array

    /// Append Prepared through the canonical EventStore Current integrator.
    val appendPrepared: store: obj -> prepared: obj -> Task<obj>

    /// Append Committed through the same transaction stream.
    val appendCommitted: store: obj -> transactionId: string -> Task<obj>
    val persistenceFailureToJs: failure: JsTransactionAppendFailure -> obj

    /// Observe only the Integrator-owned pending projection; no history reader
    /// or recovery mutation is exposed.
    val pending: store: obj -> obj array

    /// Opaque persistence capability consumed by JsWorkflowSurface.
    val internal persistenceOf: store: obj -> IJsTransactionPersistence

    val createPersistence: store: obj -> obj

    /// Append one lifecycle audit trace (PLAN S2 step 8 / algorithm H3).
    /// Callers are park/unpark/abandon/reap transition points.
    val appendLifecycleEvent: store: obj -> value: obj -> Task<obj>

    /// Opaque lifecycle audit capability consumed by park/unpark/abandon/reap.
    val internal lifecyclePersistenceOf: store: obj -> IJsLifecyclePersistence

    val createLifecyclePersistence: store: obj -> obj

