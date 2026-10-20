namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Provider-start lifecycle: freeze the exact attempt plan from the transform's
/// physical evidence and persist ProviderStarted from the real Host observation.
/// Both operations work directly on the (SessionId, PhysicalUserMessageId) key;
/// they keep no state table of their own and reuse AttemptPlanner and the
/// managed-chat lifecycle owner.
module ProviderLifecycle =

    [<RequireQualifiedAccess>]
    type ProviderStartObservationError<'bindingError> =
        | DurableJournalUnavailable
        | PhysicalUserMessageMissing of SessionId
        | AttemptPlanFreezeFailed of 'bindingError
        | FrozenAttemptPlanMissing of ChatExecutionKey * ProviderRunIdentity
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
        bindAttemptPlan: (SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> AttemptPlan option) ->
        observation: ExactProviderStartObservation ->
            Task<Result<bool, ProviderStartObservationError<unit>>>
