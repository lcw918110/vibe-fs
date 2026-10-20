namespace Wanxiangshu.Execution.Session.ChatExecution

open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Persistence.EventStore
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt

type ManagedChatAcceptanceWitness =
    private | ManagedChatAcceptanceWitness of ChatExecutionKey * AcceptedChatExecutionEvidence

[<RequireQualifiedAccess>]
module ManagedChatAcceptanceWitness =
    val key: ManagedChatAcceptanceWitness -> ChatExecutionKey
    val evidence: ManagedChatAcceptanceWitness -> AcceptedChatExecutionEvidence

[<RequireQualifiedAccess>]
type ManagedChatAcceptanceError =
    | IntentRejected of reason: string
    | AuthorityRegistrationRejected of PromptAuthorityRun.AuthorityRegistrationRejection
    | AttemptEvidenceInvalid of reason: string
    | AttemptKeyMismatch of evidenceKey: ChatExecutionKey * requestedKey: ChatExecutionKey
    | EstablishedEvidenceConflict of
        established: AcceptedChatExecutionEvidence *
        attempted: AcceptedChatExecutionEvidence
    | ProjectionMissingAfterCommit of ChatExecutionKey
    | ProjectionConflictAfterCommit of
        established: AcceptedChatExecutionEvidence *
        attempted: AcceptedChatExecutionEvidence
    | NotAttempted of EventId * JournalUnavailable
    | CommitUnknown of EventId * JournalFailure
    | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure
    | FactRejected of EventId * FoldRejection

type ManagedChatAcceptancePersistence =
    { ReadExact: ChatExecutionKey -> ChatExecutionState option
      AppendAccepted: ChatExecutionKey -> AcceptedChatExecutionEvidence -> Task<Result<unit, JournalAppendFailure>> }

type ManagedChatProviderStartedWitness = ManagedChatProviderStartedWitness of ChatExecutionKey * ProviderStartedEvidence

[<RequireQualifiedAccess>]
module ManagedChatProviderStartedWitness =
    val key: ManagedChatProviderStartedWitness -> ChatExecutionKey
    val evidence: ManagedChatProviderStartedWitness -> ProviderStartedEvidence

type ManagedChatTerminalWitness =
    | ManagedChatTerminalWitness of ChatExecutionKey * ChatExecutionTerminalEvidence * ChatExecutionTerminalDisposition

[<RequireQualifiedAccess>]
module ManagedChatTerminalWitness =
    val key: ManagedChatTerminalWitness -> ChatExecutionKey
    val evidence: ManagedChatTerminalWitness -> ChatExecutionTerminalEvidence
    val disposition: ManagedChatTerminalWitness -> ChatExecutionTerminalDisposition

[<RequireQualifiedAccess>]
type ManagedChatProviderLifecycleError =
    | AttemptEvidenceInvalid of reason: string
    | AttemptKeyMismatch of evidenceKey: ChatExecutionKey * requestedKey: ChatExecutionKey
    | MissingAccepted of ChatExecutionKey
    | EstablishedEvidenceConflict of
        established: AcceptedChatExecutionEvidence *
        attempted: AcceptedChatExecutionEvidence
    | ProviderRunConflict of established: ProviderRunIdentity * attempted: ProviderRunIdentity
    | ProviderNotStarted of ChatExecutionKey
    | ProviderStartedAfterTerminal of ChatExecutionTerminalDisposition
    | TerminalConflict of established: ChatExecutionTerminalDisposition * attempted: ChatExecutionTerminalDisposition
    | ProjectionMissingAfterCommit of ChatExecutionKey
    | ProjectionConflictAfterCommit of ChatExecutionState
    | NotAttempted of EventId * JournalUnavailable
    | CommitUnknown of EventId * JournalFailure
    | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure
    | FactRejected of EventId * FoldRejection

type ManagedChatProviderLifecyclePersistence =
    { ReadExact: ChatExecutionKey -> ChatExecutionState option
      AppendFact: ProviderStartedEvidence -> ChatExecutionFactCases -> Task<Result<unit, JournalAppendFailure>> }

[<RequireQualifiedAccess>]
module ManagedChatAcceptance =
    val evidenceFromIntent:
        authority: PromptAuthority.AuthorityExecutionProfile ->
        physicalUserMessageId: PhysicalUserMessageId ->
        origin: PromptOrigin ->
            AcceptedChatExecutionEvidence

    val persistenceError: failure: JournalAppendFailure -> ManagedChatAcceptanceError

    val acceptWith:
        persistence: ManagedChatAcceptancePersistence ->
        key: ChatExecutionKey ->
        evidence: AcceptedChatExecutionEvidence ->
            Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>>


[<RequireQualifiedAccess>]
module ManagedChatProviderLifecycle =
    val startWith:
        persistence: ManagedChatProviderLifecyclePersistence ->
        key: ChatExecutionKey ->
        acceptedEvidence: AcceptedChatExecutionEvidence ->
        providerRun: ProviderRunIdentity ->
        requestKind: ProviderRequestKind ->
            Task<Result<ManagedChatProviderStartedWitness, ManagedChatProviderLifecycleError>>

    val terminalWith:
        persistence: ManagedChatProviderLifecyclePersistence ->
        key: ChatExecutionKey ->
        startedEvidence: ProviderStartedEvidence ->
        disposition: ChatExecutionTerminalDisposition ->
            Task<Result<ManagedChatTerminalWitness, ManagedChatProviderLifecycleError>>

module JournalAppendOutcome =
    val toExecutionFailure: JournalAppendFailure -> ExecutionFailure
