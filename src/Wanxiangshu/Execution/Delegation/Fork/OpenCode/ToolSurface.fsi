namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System.Threading.Tasks

/// Opaque JS-native harness for the real Manager fork tool path.
/// Production semantics stay in ForkTool/HostForkRuntime; this surface only
/// supplies a physical Host boundary for executable requirement proofs.
module ForkToolSurface =

    val createRuntime: directory: string -> owners: obj -> Task<obj>
    val createRuntimeWithViewRead: directory: string -> owners: obj -> beforeRead: (string -> Task) -> Task<obj>
    val createRuntimeWithAbort: directory: string -> owners: obj -> abortSession: (string -> Task<obj>) -> Task<obj>

    val createRuntimeWithCancelSignals:
        directory: string -> owners: obj -> cancelSignals: (string array -> unit) -> Task<obj>

    val executeManagerFork:
        value: obj ->
        toolModule: obj ->
        owner: string ->
        calling: string ->
        byname: string ->
        charge: string ->
            Task<string>

    val executeManagerResume:
        value: obj ->
        toolModule: obj ->
        owner: string ->
        calling: string ->
        byname: string ->
        charge: string ->
            Task<string>

    val executeCommission:
        value: obj ->
        toolModule: obj ->
        owner: string ->
        calling: string ->
        byname: string ->
        charge: string ->
            Task<string>

    val captureOwnerOpening: value: obj -> owner: string -> text: string -> Task

    val executeManagerResumeWithAttachment:
        value: obj ->
        toolModule: obj ->
        owner: string ->
        byname: string ->
        charge: string ->
        attach: string ->
            Task<string>

    val captureOwnerDeltaPart: value: obj -> owner: string -> text: string -> providerRun: string -> Task
    val childCount: value: obj -> int
    val abortCount: value: obj -> int
    val child: value: obj -> obj
    val promptCount: value: obj -> int
    val awaitPromptCount: value: obj -> count: int -> Task
    val acceptPrompt: value: obj -> index: int -> bool
    val acceptNextPrompt: value: obj -> unit
    val terminalListenerCount: value: obj -> int
    val prompt: value: obj -> index: int -> obj
    val captureChildPromptSender: value: obj -> owner: string -> byname: string -> onAccepted: (string -> unit) -> obj
    val sendCapturedChildPrompt: captured: obj -> text: string -> Task<obj>
    val promptEvidence: value: obj -> index: int -> obj
    val confirmPromptPhysical: value: obj -> index: int -> physicalMessageId: string -> Task<obj>
    val physicalAcceptanceObservation: value: obj -> index: int -> physicalMessageId: string -> obj
    val nextPromptAcceptanceUnknown: value: obj -> reason: string -> unit
    val nextPromptAdmittedWithReceipt: value: obj -> receipt: string -> unit
    val cancelOwnerChildren: value: obj -> owner: string -> Task
    val detachToolRuntime: value: obj -> Task
    val durableLifecycleByname: value: obj -> owner: string -> byname: string -> obj
    val executeJoin: value: obj -> owner: string -> Task<string>
    val executeJoinReceipt: value: obj -> owner: string -> Task<obj>
    val executeHorizon: value: obj -> owner: string -> Task<string>
    val executeHorizonReceipt: value: obj -> owner: string -> Task<obj>
    val recordDeliveredValue: sessionId: string -> deliveredValue: obj -> unit
    val settle: value: obj -> owner: string -> answer: string -> providerRun: string -> Task<bool>
    val settleUndelivered: value: obj -> owner: string -> terminalText: string -> providerRun: string -> Task<bool>

    val prepareTerminalDelivery:
        value: obj -> owner: string -> answer: string -> providerRun: string -> Task<(unit -> Task)>

    val injectAcceptedAssessment: value: obj -> owner: string -> Task
    val injectAuditPendingIncumbency: value: obj -> owner: string -> Task
    val injectPendingPersistFailure: value: obj -> owner: string -> Task<obj>
    val startUnprepared: obj -> string -> string -> Task<obj>
    val emitStopForRoot: obj -> string -> string -> string -> Task

    /// managed-session-lifecycle-018: deliver a stop with a free-form reason
    /// through the same physical Notify boundary as emitStopForRoot. An empty
    /// root addresses the session-level stop; a non-empty root binds the exact
    /// authority root.
    val emitStopWithReason: obj -> string -> string -> string -> string -> Task
    val replayWorkCompletion: obj -> string -> string -> Task<obj>
    val workSnapshot: obj -> string -> obj array
    val coldWorkSnapshot: string -> string -> Task<obj array>
    val replayBinding: obj -> string -> string -> Task<obj>
    val emitTerminalForRoot: obj -> string -> string -> string -> string -> Task
    val consumeWorkWithOutcome: obj -> string -> string -> string -> Task<obj>
    val coldConsumeWorkWithOutcome: string -> string -> string -> string -> Task<obj>

    val disposeRuntime: value: obj -> unit
