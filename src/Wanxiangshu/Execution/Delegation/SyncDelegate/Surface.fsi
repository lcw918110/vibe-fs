namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Threading.Tasks
open Wanxiangshu.OpenCode

/// Delegation-owned opaque runtime harness. Host sessions, journal writers,
/// attached-session state and completion turns never cross into JS; callers
/// observe only invocation promises and child identities.
[<RequireQualifiedAccess>]
module SyncDelegateSurface =
    /// Create a real SyncDelegateRuntime with an opaque journal and Host port.
    /// Every owner must first be admitted as an explicit durable HumanRoot.
    val create: directory: string -> owners: obj -> Task<obj>

    /// Recording at the controlled Host boundary; this does not install an SDK.
    val createForHostRecording: directory: string -> owners: obj -> Task<obj>

    val internal withHostRuntime:
        value: obj -> useRuntime: (SyncDelegateRuntime -> ISessionHostPort -> 'result) -> 'result

    val recordingSnapshot: value: obj -> obj
    val awaitRecordingPromptCount: value: obj -> count: int -> Task
    val returnRecordingPromptOutcome: value: obj -> session: string -> index: int -> outcome: obj -> bool

    /// Same canonical journal and Host port, using the production provider
    /// recovery workflow. Model routing reads the
    /// calling process's already-isolated configuration.
    val createForProviderRecovery: directory: string -> owners: obj -> Task<obj>

    val createForGuardRecovery: directory: string -> owners: obj -> Task<obj>
    val closeRecovery: value: obj -> Task
    val closeGuardRecovery: value: obj -> Task
    val observeGuardDelta: value: obj -> raw: obj -> unit
    val awaitGuardInterrupt: value: obj -> session: string -> providerRun: string -> Task

    val observeExactGuardAbort:
        value: obj -> session: string -> physical: string -> authorityRoot: string -> providerRun: string -> Task

    val observeRepairTurn:
        value: obj ->
        session: string ->
        physical: string ->
        authorityRoot: string ->
        providerRun: string ->
        finish: string ->
        delivery: string ->
        idleEvidence: bool ->
            Task

    /// managed-session-lifecycle-001: drive SyncDelegateRuntime's production child
    /// observation into AttachedSessionRuntime against controlled Host callbacks.
    val managedChildReconciliationScenario: directory: string -> mode: string -> Task<obj>

    /// managed-session-lifecycle-001: two simultaneous callers for one exact key share
    /// the complete physical reconciliation transaction and its result.
    val concurrentAttachedGetOrCreateScenario: unit -> Task<obj>

    /// Run one internal Engineer research charge through the SyncDelegate
    /// runtime; the charge stays data and never becomes a tool-module surface.
    val executeEngineerCharge: value: obj -> owner: string -> charge: string -> Task<string>

    /// Invoke one ordinary managed delegation. The returned promise remains
    /// pending until `settle` receives a reconciled provider turn.
    val invoke: value: obj -> owner: string -> role: string -> question: string -> Task<obj>

    /// Exact formal response from the accepted terminal of this invocation.
    val invokeResponse: value: obj -> owner: string -> question: string -> Task<obj>

    val startObserved: value: obj -> owner: string -> charge: string -> obj
    val startObservedWithPreparationFailure: value: obj -> owner: string -> charge: string -> reason: string -> obj
    val observedAdmission: execution: obj -> Task<obj>
    val observedCompletion: execution: obj -> Task<obj>
    val returnPromptOutcome: value: obj -> owner: string -> role: string -> index: int -> outcome: obj -> bool
    val rejectPrompt: value: obj -> owner: string -> role: string -> index: int -> reason: string -> bool

    val confirmPromptPhysical:
        value: obj -> owner: string -> role: string -> index: int -> physical: string -> Task<bool>

    /// Real managed ingress from a captured PromptKey, including durable
    /// ChatExecution Accepted. Returns only the native witness observation.
    val confirmManagedPromptPhysical:
        value: obj -> owner: string -> role: string -> index: int -> physical: string -> Task<obj>

    /// Controlled exact-stop capability for this harness's own children. This
    /// does not install or prove the real Host terminal subscription.
    val observeProviderFailureStop: value: obj -> session: string -> providerRun: string -> bool

    /// Cardinality of the process-shared production stop fence, not call state.
    val recoveryStopSnapshot: value: obj -> obj

    val promptIdentity: value: obj -> owner: string -> role: string -> index: int -> obj
    val promptClaimState: value: obj -> owner: string -> role: string -> index: int -> obj
    val terminalListenerCount: value: obj -> int

    val settleExactTerminal:
        value: obj ->
        session: string ->
        physical: string ->
        authorityRoot: string ->
        providerRun: string ->
        formalText: string ->
        reasoningText: string ->
            Task<bool>

    val settleExactFallback:
        value: obj ->
        session: string ->
        physical: string ->
        authorityRoot: string ->
        providerRun: string ->
        formalText: string ->
            bool

    val observeExactProviderFailure:
        value: obj ->
        session: string ->
        physical: string ->
        authorityRoot: string ->
        providerRun: string ->
        reason: string ->
            Task<bool>

    /// Settle the current managed child through the real HandleTurn path.
    val settleWithAuthorityRoot:
        value: obj ->
        owner: string ->
        role: string ->
        answer: string ->
        runId: string ->
        authorityRoot: string ->
            Task<bool>

    val settle: value: obj -> owner: string -> role: string -> answer: string -> runId: string -> Task<bool>

    val failWithAuthorityRoot:
        value: obj -> owner: string -> role: string -> reason: string -> authorityRoot: string -> Task<string>

    val observeTurn:
        value: obj ->
        owner: string ->
        role: string ->
        outcomeName: string ->
        answer: string ->
        runId: string ->
            Task<bool>

    val child: value: obj -> owner: string -> role: string -> obj
    val stageDeletedDelegate: value: obj -> owner: string -> bool
    val scopeCloseChild: value: obj -> owner: string -> role: string -> obj
    val cancelSession: value: obj -> session: string -> unit
    val vocabulary: roleName: string -> tierName: string -> scope: string -> obj
    val childCount: value: obj -> int
    val promptCount: value: obj -> owner: string -> role: string -> int
    val awaitPromptCount: value: obj -> owner: string -> role: string -> count: int -> Task
    val acceptPrompt: value: obj -> owner: string -> role: string -> index: int -> bool
    val promptOrigin: value: obj -> owner: string -> role: string -> index: int -> obj
    val prompt: value: obj -> owner: string -> role: string -> index: int -> obj
    val captureOwnerOpening: value: obj -> owner: string -> text: string -> Task
    val captureOwnerDeltaPart: value: obj -> owner: string -> text: string -> providerRun: string -> Task
    val batchOrder: roleName: string -> toolNames: string array -> currentCall: string -> obj

    val invokeBatch:
        value: obj ->
        owner: string ->
        role: string ->
        charge: string ->
        providerRun: string ->
        callId: string ->
        callOrder: string array ->
            Task<obj>

    val serializationDecision: firstScope: string -> secondScope: string -> sameProviderRun: bool -> obj
    val evidenceBoundary: charge: string -> view: string -> obj

    /// Script the retry decorator's verdicts for the next confirmed failures:
    /// `"dispatched"`, `"superseded"` or `"terminal:<reason>"`.
    val scriptRetry: value: obj -> verdicts: string array -> unit

    /// How many times the decorated path asked the retry decorator for a verdict.
    val retryCalls: value: obj -> int

    /// Model the Host accepting the retry attempt the decorator dispatched.
    val dispatchRetryAttempt: value: obj -> owner: string -> role: string -> bool
    val dispose: value: obj -> unit

    /// delegation-031 probe: abandon the pending call for this delegate (parent
    /// supersede guard) so a stale completion afterwards cannot claim it.
    val abandonPendingCall: value: obj -> owner: string -> role: string -> bool

    val stageDeferredInspection:
        sessionId: string -> callId: string -> charge: string -> keywords: string -> estimate: int option -> string

    val applyReplacedResults: messages: obj list -> obj list
