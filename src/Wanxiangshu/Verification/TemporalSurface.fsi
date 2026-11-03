namespace Wanxiangshu.Verification

open System.Threading.Tasks

/// Narrow JS-native owner for deterministic temporal proofs.
///
/// Timer/clock capabilities and journal/fold translation stay here. F# ids,
/// unions, maps, records, and EventStore plumbing never cross this boundary;
/// callers receive plain values or opaque handles only.
module TemporalSurface =

    // ── deterministic timer/clock capabilities ─────────────────────────────

    val createVirtualTimer: unit -> obj
    val timerDelay: timer: obj -> milliseconds: int -> obj
    val timerAwait: handle: obj -> Task<unit>
    val timerCancel: handle: obj -> unit
    val timerAdvance: timer: obj -> milliseconds: int -> unit
    val timerNowMs: timer: obj -> int
    val timerDispose: timer: obj -> unit
    val createVirtualClock: unit -> obj
    val clockNowIso: clock: obj -> string
    val clockNowMs: clock: obj -> int64
    val clockAdvanceMs: clock: obj -> milliseconds: int -> unit
    val clockSet: clock: obj -> iso: string -> unit

    // ── durable temporal world ──────────────────────────────────────────────

    val openJournal: commonDir: string -> runtimeId: string -> processId: int -> startedAt: string -> Task<obj>
    val resumeJournal: commonDir: string -> runtimeId: string -> processId: int -> startedAt: string -> Task<obj>
    val journalDispose: handle: obj -> unit
    val journalAppendAgent: handle: obj -> stream: obj -> run: obj -> fact: obj -> Task<obj>
    val journalSnapshot: handle: obj -> obj
    val journalPersistedEnvelopes: handle: obj -> obj array
    val writerReleaseDrainScenario: unit -> Task<obj>
    val writerPoisonPreservesFirstFailureScenario: unit -> Task<obj>

    /// Host lifecycle proof: scheduler shutdown closes admission immediately,
    /// waits for the already-started pass, and refuses later kicks.
    val reconcileSchedulerStopDrainScenario: unit -> Task<obj>

    /// Poison proof: once the durable substrate becomes unavailable, a new wake
    /// cannot start another reconcile pass even while the first pass is blocked.
    val reconcileSchedulerDurableUnavailableScenario: unit -> Task<obj>

    /// Plugin owner proof: disposal closes Host-work admission and awaits
    /// reconcile plus already-admitted foreground/background work before returning.
    val createPluginWorkScope: release: (unit -> Task) -> obj
    val runPluginWork: value: obj -> work: (unit -> Task) -> Task
    val backgroundPluginWork: value: obj -> work: (unit -> Task) -> Task
    val disposePluginWorkScope: value: obj -> Task

    val pluginScopeStopDrainScenario: unit -> Task<obj>

    /// Plugin owner proof: detached Host failures are not swallowed. Shutdown
    /// drains the task, closes further admission, then returns the original error.
    val pluginScopeBackgroundFailureScenario: unit -> Task<obj>

    // ── pure durable fold ───────────────────────────────────────────────────

    val fold: envelopes: obj array -> obj

    val sessionReuseIdentityScenario: firstAccepted: obj -> secondAccepted: obj -> obj

    // ── ProviderFailureProjection's typed transition, exposed as opaque state ─

    val providerFailureForAuthority: logicalRun: string -> authorityRoot: string -> obj

    val applyFailure: identity: obj -> count: int -> current: obj -> obj

    val providerFailureRead: current: obj -> obj
