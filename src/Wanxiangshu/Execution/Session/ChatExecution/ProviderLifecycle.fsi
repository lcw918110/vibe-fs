namespace Wanxiangshu.Execution.Session.ChatExecution

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Provider-start lifecycle: freeze the exact attempt plan from the transform's
/// physical evidence and persist ProviderStarted from the real Host observation.
/// The bound plan comes from the process-local frozen plan when present and is
/// otherwise rebuilt from the durable Accepted fact, so a real Host run is never
/// refused for a missing cache entry.
/// Both operations work directly on the (SessionId, PhysicalUserMessageId) key;
/// they keep no state table of their own and reuse AttemptPlanner and the
/// managed-chat lifecycle owner.
module ProviderLifecycle =

    [<RequireQualifiedAccess>]
    type ProviderStartObservationError<'bindingError> =
        | DurableJournalUnavailable
        | PhysicalUserMessageMissing of SessionId
        | AttemptPlanFreezeFailed of 'bindingError
        | AcceptedExecutionMissing of ChatExecutionKey
        | AcceptedExecutionAlreadyTerminal of ChatExecutionKey
        | AuthorityEvidenceInvalid of AcceptedChatExecutionEvidence
        | PersistenceFailed of ManagedChatProviderLifecycleError

    val providerStartObservationErrorCode: ProviderStartObservationError<'bindingError> -> string

    val freezeProviderAttemptPlanForTransform:
        journal: AgentJournal option ->
        freezeAttemptPlan: (SessionId -> PhysicalUserMessageId -> PendingAttemptPlan -> Result<unit, 'bindingError>) ->
        projectionSessionIdOpt: string option ->
        physicalUserMessageId: PhysicalUserMessageId option ->
            Task<Result<unit, ProviderStartObservationError<'bindingError>>>

    val persistProviderStartedFromObservation:
        journal: AgentJournal option ->
        bindAttemptPlan: (SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> PendingAttemptPlan -> AttemptPlan) ->
        observation: ExactProviderStartObservation ->
            Task<Result<bool, ProviderStartObservationError<unit>>>
