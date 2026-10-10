namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
type internal PhysicalExecutionReleaseOutcome =
    | Released of CapacityTransitionOutcome
    | HeldForInput

module ModelRouting =
    val internal failureOfExecutionAdmissionAcquisition: ExecutionAdmissionAcquisition -> ExecutionFailure option
    val internal seedBoundDevOpsModel: sessionId: SessionId -> value: string -> unit
    val internal capacityOwnership: lease: ExecutionAdmissionLease -> CapacityOwnership

    /// Read-only Predictor slot existence query result, derived from the
    /// same MJS model configuration. Absent or empty Predictor candidates are
    /// not configured; valid non-empty candidates are configured; a malformed
    /// structure is a configuration error. Capacity and provider health do not
    /// change the configured state.
    [<RequireQualifiedAccess>]
    type PredictorConfiguration =
        | Configured
        | NotConfigured
        | ConfigurationInvalid of reason: string

    val predictorConfiguration: scheduler: obj -> PredictorConfiguration

    val invokeScheduler:
        scheduler: obj ->
        role: string ->
        running: ModelRoutingTarget array ->
        previous: ModelRoutingTarget option ->
        purpose: ModelExecutionPurpose ->
            ModelRoutingTarget option

    val configPath: unit -> string
    val bootstrapAndLoadAt: path: string -> template: string -> Task<obj>
    val bootstrapDefault: unit -> Task<obj>
    val toOpenCodeModel: target: ModelRoutingTarget -> OpencodeModel
    val ofOpenCodeModel: model: OpencodeModel -> ModelRoutingTarget option
    val sameTarget: expected: ModelRoutingTarget -> observed: OpencodeModel -> bool

    type internal ModelRoutingRuntime =
        new: scheduler: obj -> ModelRoutingRuntime

        member AcquireExecutionAdmission:
            sessionId: string *
            physicalUserMessageId: string *
            role: Role *
            participant: string *
            purpose: ModelExecutionPurpose *
            lenderSessionId: string option ->
                Task<ExecutionAdmissionAcquisition>

        member PredictorConfiguration: PredictorConfiguration

        member ExecutionAdmissionTarget:
            lease: ExecutionAdmissionLease -> Result<ModelRoutingTarget, ExecutionAdmissionRejection>

        member internal ContinueExecutionAdmission:
            previous: ExecutionAdmissionLease * physicalUserMessageId: string -> Task<ExecutionAdmissionAcquisition>

        member internal RetainContinuationInput:
            previous: ExecutionAdmissionLease * physicalUserMessageId: string -> ModelRoutingTarget

        member internal TryContinuationInput:
            sessionId: string * physicalUserMessageId: string -> ExecutionAdmissionLease option

        member internal CancelContinuationInput: sessionId: string * physicalUserMessageId: string -> unit

        member internal CancelRetainedInputsForSession: sessionId: string -> unit

        member CommitExecutionAdmission:
            lease: ExecutionAdmissionLease * observed: ExecutionAdmissionExactIdentity -> CapacityTransitionOutcome

        member ReleaseExecutionAdmissionBeforeProvider:
            lease: ExecutionAdmissionLease * observed: ExecutionAdmissionExactIdentity -> CapacityTransitionOutcome

        member ExecutionAdmissionLifecycle:
            lease: ExecutionAdmissionLease -> Result<string, ExecutionAdmissionRejection>

        member TryReserveManaged:
            sessionId: string * role: Role * purpose: ModelExecutionPurpose * lenderSessionId: string option ->
                ModelRoutingTarget option

        member TryLease:
            sessionId: string *
            physicalUserMessageId: string *
            role: Role *
            participant: string *
            purpose: ModelExecutionPurpose *
            lenderSessionId: string option ->
                ModelRoutingTarget option

        member TryReadExecution: sessionId: string * physicalUserMessageId: string -> ExecutionAdmissionLease option

        member WasExecutionSuperseded: sessionId: string * physicalUserMessageId: string -> bool

        member OwnsExecutionAdmission: sessionId: string * physicalUserMessageId: string -> bool

        member BindDevopsTarget: sessionId: string * target: ModelRoutingTarget -> unit
        member SeedBoundDevOpsModel: sessionId: string * value: string -> unit

        member RememberProviderStepIdentity:
            sessionId: string * physicalUserMessageId: string * providerRun: string -> unit

        member TryProviderStepIdentity: providerRun: string -> (string * string) option


        /// Read-only: the physical user message of this session's current
        /// active lease, when the lease is bound to an exact physical message.
        member TryActivePhysical: sessionId: string -> string option

        member BoundDevopsTarget: sessionId: string -> ModelRoutingTarget option

        member internal ReleaseExecution: sessionId: string -> CapacityTransitionOutcome

        member internal ReleasePhysicalExecution:
            sessionId: string * physicalUserMessageId: string -> PhysicalExecutionReleaseOutcome

        member CancelPendingExecution: sessionId: string -> CapacityTransitionOutcome

        member CancelPendingPhysicalExecution:
            sessionId: string * physicalUserMessageId: string -> CapacityTransitionOutcome

        member CapacitySnapshot: unit -> CapacityInvariantEvidence

        member EnterProviderStep:
            sessionId: string * physicalUserMessageId: string * visibleProviderRuns: Set<string> * ?requestKey: string ->
                Task

        member EndProviderStep: sessionId: string * physicalUserMessageId: string * providerRun: string -> unit
        member TakeProviderRunTarget: providerRun: string -> ModelRoutingTarget option

        member CondemnFailedTarget: providerRun: string -> ModelRoutingTarget option

        member RetainFailedTargetForRetry: sessionId: string * providerRun: string -> ModelRoutingTarget option

        member SuppressProviderStep: sessionId: string * physicalUserMessageId: string -> unit
        member SnapshotOccupied: unit -> ModelRoutingTarget array
        member PendingCount: int
        member PendingBound: int
        member PendingContractVersion: int
        member HasTheoreticalCapacity: role: string -> purpose: ModelExecutionPurpose -> bool

    val initialize: unit -> Task

    val internal takeProviderRunTarget: providerRun: ProviderRunIdentity -> ModelRoutingTarget option

    val internal condemnFailedTarget: providerRun: ProviderRunIdentity -> ModelRoutingTarget option

    val internal retainFailedTargetForRetry:
        sessionId: SessionId -> providerRun: ProviderRunIdentity -> ModelRoutingTarget option

    val internal hasTheoreticalCapacity: role: string -> ?purpose: ModelExecutionPurpose -> bool

    val internal acquireExecutionAdmission:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        role: Role ->
        participant: string ->
        purpose: ModelExecutionPurpose ->
        lenderSessionId: string option ->
            Task<ExecutionAdmissionAcquisition>

    val internal executionAdmissionTarget:
        lease: ExecutionAdmissionLease -> Result<ModelRoutingTarget, ExecutionAdmissionRejection>

    val internal commitExecutionAdmission:
        lease: ExecutionAdmissionLease -> observed: ExecutionAdmissionExactIdentity -> CapacityTransitionOutcome

    val internal releaseExecutionAdmissionBeforeProvider:
        lease: ExecutionAdmissionLease -> observed: ExecutionAdmissionExactIdentity -> CapacityTransitionOutcome

    val hasRuntime: unit -> bool

    val tryReserveManaged:
        sessionId: SessionId ->
        role: Role ->
        purpose: ModelExecutionPurpose ->
        lenderSessionId: string option ->
            ModelRoutingTarget option

    val tryLease:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        role: Role ->
        participant: string ->
        purpose: ModelExecutionPurpose ->
        lenderSessionId: string option ->
            ModelRoutingTarget option

    val internal tryReadExecution: key: ChatExecutionKey -> ExecutionAdmissionLease option

    val internal continueExecutionAdmission:
        previous: ExecutionAdmissionLease -> physicalId: PhysicalUserMessageId -> Task<ExecutionAdmissionAcquisition>

    val internal retainContinuationInput:
        previous: ExecutionAdmissionLease -> key: ChatExecutionKey -> ModelRoutingTarget

    val internal retainContinuationInputForPhysical:
        previous: ExecutionAdmissionLease -> physicalUserMessageId: PhysicalUserMessageId -> ModelRoutingTarget

    val internal tryContinuationInput: key: ChatExecutionKey -> ExecutionAdmissionLease option
    val internal cancelContinuationInput: key: ChatExecutionKey -> unit
    val internal cancelRetainedInputsForSession: sessionId: SessionId -> unit

    val internal wasExecutionSuperseded: key: ChatExecutionKey -> bool

    val internal ownsExecutionAdmission: key: ChatExecutionKey -> bool

    val internal sharedPredictorConfiguration: unit -> PredictorConfiguration


    val internal rememberProviderStepIdentity:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> providerRun: ProviderRunIdentity -> unit

    val internal tryProviderStepIdentity: providerRun: ProviderRunIdentity -> (SessionId * PhysicalUserMessageId) option

    val readExecutionAdmission: key: ChatExecutionKey -> (string * ModelRoutingTarget) option
    val internal boundDevopsModel: sessionId: SessionId -> OpencodeModel option
    val internal releaseExecution: sessionId: SessionId -> CapacityTransitionOutcome

    val internal releasePhysicalExecution:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> PhysicalExecutionReleaseOutcome

    val internal observePhysicalResource: key: ChatExecutionKey -> PhysicalResourceObservation
    val internal cancelUnacquiredExecution: sessionId: SessionId -> CapacityTransitionOutcome

    val internal cancelPendingPhysicalExecution: key: ChatExecutionKey -> CapacityTransitionOutcome
    val internal capacitySnapshot: unit -> CapacityInvariantEvidence

    val enterProviderStep:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        visibleProviderRuns: Set<ProviderRunIdentity> ->
        requestKey: string option ->
            Task

    val endProviderStep:
        sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> providerRun: ProviderRunIdentity -> unit

    val suppressProviderStep: sessionId: SessionId -> physicalUserMessageId: PhysicalUserMessageId -> unit

    /// The physical user message of a session's current active lease, when the
    /// lease is bound to an exact physical message. Read-only.
    val tryActivePhysical: sessionId: string -> string option

    val projectHostModel: output: obj -> model: OpencodeModel -> Result<unit, exn>
