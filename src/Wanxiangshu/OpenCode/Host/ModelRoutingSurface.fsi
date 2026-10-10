namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module ModelRoutingSurface =
    val initialize: unit -> Task

    val acquireSharedExecutionAdmission:
        sessionId: string ->
        physicalUserMessageId: string ->
        role: string ->
        participant: string ->
        lenderSessionId: string ->
        purpose: obj ->
            Task<obj>

    val sharedExecutionAdmissionTarget: token: obj -> obj
    val sharedCapacitySnapshot: unit -> obj
    val commitSharedExecutionAdmission: token: obj -> observed: obj -> obj
    val releaseSharedExecutionAdmissionBeforeProvider: token: obj -> observed: obj -> obj
    val sharedRetainContinuationInput: token: obj -> physicalUserMessageId: string -> obj
    val releasePhysical: sessionId: string -> physicalUserMessageId: string -> obj
    val bootstrapAndLoadAt: path: string -> template: string -> Task<obj>

    /// Enter a provider step on the process-shared runtime — the same
    /// execution-scoped admission the real transform boundary must satisfy.
    val sharedEnterProviderStep:
        sessionId: string -> physicalUserMessageId: string -> visibleProviderRuns: string array -> Task

    val invokeScheduler: scheduler: obj -> role: string -> running: obj -> previous: obj -> purpose: obj -> obj
    val createRuntime: scheduler: obj -> obj

    val predictorConfiguration: scheduler: obj -> obj
    val sharedPredictorConfiguration: unit -> obj

    val acquireExecutionAdmission:
        runtime: obj ->
        sessionId: string ->
        physicalUserMessageId: string ->
        role: string ->
        participant: string ->
        lenderSessionId: string ->
        purpose: obj ->
            Task<obj>

    val beginExecutionAdmission:
        runtime: obj ->
        sessionId: string ->
        physicalUserMessageId: string ->
        role: string ->
        participant: string ->
        lenderSessionId: string ->
        purpose: obj ->
            Task<obj>

    val continueExecutionAdmission: runtime: obj -> token: obj -> physicalUserMessageId: string -> Task<obj>
    val retainContinuationInput: runtime: obj -> token: obj -> physicalUserMessageId: string -> obj
    val awaitQueuedExecutionAdmission: queueToken: obj -> Task<obj>
    val executionAdmissionTarget: runtime: obj -> token: obj -> obj
    val commitExecutionAdmission: runtime: obj -> token: obj -> observed: obj -> obj
    val releaseExecutionAdmissionBeforeProvider: runtime: obj -> token: obj -> observed: obj -> obj
    val executionAdmissionLifecycle: runtime: obj -> token: obj -> obj

    val tryReserveManaged:
        runtime: obj -> sessionId: string -> role: string -> lenderSessionId: string -> purpose: obj -> obj

    val tryLease:
        runtime: obj ->
        sessionId: string ->
        physicalUserMessageId: string ->
        role: string ->
        participant: string ->
        lenderSessionId: string ->
        purpose: obj ->
            obj

    val tryReadExecution: runtime: obj -> sessionId: string -> physicalUserMessageId: string -> obj

    val rememberProviderStepIdentity:
        runtime: obj -> sessionId: string -> physicalUserMessageId: string -> providerRun: string -> unit

    val tryProviderStepIdentity: runtime: obj -> providerRun: string -> obj

    val bindDevopsTarget: runtime: obj -> sessionId: string -> target: obj -> unit
    val boundDevopsTarget: runtime: obj -> sessionId: string -> obj
    val seedDevOpsModelTarget: runtime: obj -> sessionId: string -> target: string -> unit

    val releasePhysicalExecution: runtime: obj -> sessionId: string -> physicalUserMessageId: string -> obj
    val releaseExecution: runtime: obj -> sessionId: string -> obj
    val cancelPendingExecution: runtime: obj -> sessionId: string -> obj

    val enterProviderStep:
        runtime: obj ->
        sessionId: string ->
        physicalUserMessageId: string ->
        visibleProviderRuns: string array ->
        requestKey: string option ->
            Task

    val endProviderStep:
        runtime: obj -> sessionId: string -> physicalUserMessageId: string -> providerRun: string -> unit

    val takeProviderRunTarget: runtime: obj -> providerRun: string -> obj

    val condemnFailedTarget: runtime: obj -> providerRun: string -> obj

    val retainFailedTargetForRetry: runtime: obj -> sessionId: string -> providerRun: string -> obj

    val suppressProviderStep: runtime: obj -> sessionId: string -> physicalUserMessageId: string -> unit
    val snapshotOccupied: runtime: obj -> obj array
    val capacitySnapshot: runtime: obj -> obj
    val reconcileCapacityEvidence: evidence: obj -> obj
    val pendingCount: runtime: obj -> int
    val admissionSnapshot: routingRuntime: obj -> sessionId: string -> physicalUserMessageId: string -> obj
    val pendingBound: runtime: obj -> int
    val pendingContractVersion: runtime: obj -> int
    val createSdkClientPort: client: obj -> obj
    val sendPrompt: port: obj -> sessionId: string -> text: string -> options: obj -> Task<obj>
