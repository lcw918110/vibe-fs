namespace Wanxiangshu.Execution.Session.ChatExecution

open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Persistence.EventStore
open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Interaction.Authority

/// Proof that the exact execution acceptance is present in the durable projection.
type ManagedChatAcceptanceWitness =
    private | ManagedChatAcceptanceWitness of ChatExecutionKey * AcceptedChatExecutionEvidence

[<RequireQualifiedAccess>]
module ManagedChatAcceptanceWitness =

    let key (ManagedChatAcceptanceWitness(key, _)) = key

    let evidence (ManagedChatAcceptanceWitness(_, evidence)) = evidence

[<RequireQualifiedAccess>]
/// DSL-class: Evidence
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

/// The canonical acceptance operation needs only an exact projection read and
/// the existing durable AgentFact append capability.
type ManagedChatAcceptancePersistence =
    { ReadExact: ChatExecutionKey -> ChatExecutionState option
      AppendAccepted: ChatExecutionKey -> AcceptedChatExecutionEvidence -> Task<Result<unit, JournalAppendFailure>> }

/// Proof that the exact provider step is present in the durable projection.
type ManagedChatProviderStartedWitness = ManagedChatProviderStartedWitness of ChatExecutionKey * ProviderStartedEvidence

[<RequireQualifiedAccess>]
module ManagedChatProviderStartedWitness =

    let key (ManagedChatProviderStartedWitness(key, _)) = key

    let evidence (ManagedChatProviderStartedWitness(_, evidence)) = evidence

/// Proof that the exact terminal disposition is present in the durable projection.
type ManagedChatTerminalWitness =
    | ManagedChatTerminalWitness of ChatExecutionKey * ChatExecutionTerminalEvidence * ChatExecutionTerminalDisposition

[<RequireQualifiedAccess>]
module ManagedChatTerminalWitness =

    let key (ManagedChatTerminalWitness(key, _, _)) = key

    let evidence (ManagedChatTerminalWitness(_, evidence, _)) = evidence

    let disposition (ManagedChatTerminalWitness(_, _, disposition)) = disposition

[<RequireQualifiedAccess>]
/// DSL-class: Evidence
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
module internal ManagedChatExecutionFlight =

    let private gate = obj ()
    let private inFlight = Dictionary<RuntimeId * ChatExecutionKey, Task>()

    let private preceding (flightKey: RuntimeId * ChatExecutionKey) : Task =
        match inFlight.TryGetValue flightKey with
        | true, existing -> existing
        | false, _ -> AsyncSupport.completedTask ()

    let private removeIfCurrent (flightKey: RuntimeId * ChatExecutionKey) (marker: Task) =
        lock gate (fun () ->
            match inFlight.TryGetValue flightKey with
            | true, current when Object.ReferenceEquals(current, marker) -> inFlight.Remove flightKey |> ignore
            | _ -> ())

    let private start
        (flightKey: RuntimeId * ChatExecutionKey)
        (precedingFlight: Task)
        (operation: unit -> Task<'value>)
        : Task<'value> =
        // DSL-MUTABLE: single-flight
        let mutable marker: Task = null

        let started =
            Defer.defer (fun () ->
                task {
                    do! precedingFlight

                    try
                        return! operation ()
                    finally
                        removeIfCurrent flightKey marker
                })

        marker <- started :> Task
        inFlight.[flightKey] <- marker
        started

    let run (runtimeId: RuntimeId) (key: ChatExecutionKey) (operation: unit -> Task<'value>) : Task<'value> =
        lock gate (fun () ->
            let flightKey = runtimeId, key
            start flightKey (preceding flightKey) operation)

module JournalAppendOutcome =

    let toExecutionFailure =
        function
        | JournalAppendFailure.WriterUnavailable _ ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
        | JournalAppendFailure.FactRejected _ -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
        | JournalAppendFailure.WriteUnknown _ -> ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
        | JournalAppendFailure.NoNewWriteReleaseFailed _ ->
            ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite

[<RequireQualifiedAccess>]
module ManagedChatAcceptance =

    let private evidenceKey (evidence: AcceptedChatExecutionEvidence) =
        { SessionId = evidence.SessionId
          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

    let evidenceFromIntent
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (physicalUserMessageId: PhysicalUserMessageId)
        (origin: PromptOrigin)
        : AcceptedChatExecutionEvidence =
        { SessionId = authority.SessionId
          LogicalRunId = authority.LogicalRunId
          AuthorityRootUserMessageId = authority.AuthorityRootUserMessageId
          AuthorityKind = authority.AuthorityKind
          IdentitySeed = authority.IdentitySeed
          PhysicalUserMessageId = physicalUserMessageId
          Origin = origin }

    type private AcceptanceDecision =
        | ExistingWitness of ManagedChatAcceptanceWitness
        | AppendRequired

    let private validate key evidence =
        AcceptedChatExecutionEvidence.validate evidence
        |> Result.mapError ManagedChatAcceptanceError.AttemptEvidenceInvalid
        |> Result.bind (fun () ->
            let supplied = evidenceKey evidence

            if supplied = key then
                Ok()
            else
                Error(ManagedChatAcceptanceError.AttemptKeyMismatch(supplied, key)))

    let private witnessFromProjected key attempted (projected: ChatExecutionState option) =
        match projected with
        | None -> Error(ManagedChatAcceptanceError.ProjectionMissingAfterCommit key)
        | Some established when established.acceptedEvidence = attempted ->
            Ok(ManagedChatAcceptanceWitness(key, established.acceptedEvidence))
        | Some established ->
            Error(ManagedChatAcceptanceError.ProjectionConflictAfterCommit(established.acceptedEvidence, attempted))

    let persistenceError failure =
        match failure with
        | JournalAppendFailure.WriterUnavailable(eventId, unavailable) ->
            ManagedChatAcceptanceError.NotAttempted(eventId, unavailable)
        | JournalAppendFailure.WriteUnknown(eventId, writeFailure) ->
            ManagedChatAcceptanceError.CommitUnknown(eventId, writeFailure)
        | JournalAppendFailure.NoNewWriteReleaseFailed(eventId, failure) ->
            ManagedChatAcceptanceError.NoNewWriteReleaseFailed(eventId, failure)
        | JournalAppendFailure.FactRejected(eventId, rejection) ->
            ManagedChatAcceptanceError.FactRejected(eventId, rejection)

    let private decide key evidence (projected: ChatExecutionState option) =
        validate key evidence
        |> Result.bind (fun () ->
            match projected with
            | Some established when established.acceptedEvidence = evidence ->
                Ok(ExistingWitness(ManagedChatAcceptanceWitness(key, established.acceptedEvidence)))
            | Some established ->
                Error(ManagedChatAcceptanceError.EstablishedEvidenceConflict(established.acceptedEvidence, evidence))
            | None -> Ok AppendRequired)

    let private append persistence key evidence =
        task {
            let! result = persistence.AppendAccepted key evidence
            return result |> Result.mapError persistenceError
        }

    let acceptWith
        (persistence: ManagedChatAcceptancePersistence)
        (key: ChatExecutionKey)
        (evidence: AcceptedChatExecutionEvidence)
        : Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>> =
        taskResult {
            let! decision = decide key evidence (persistence.ReadExact key)

            match decision with
            | ExistingWitness witness -> return witness
            | AppendRequired ->
                do! append persistence key evidence
                return! witnessFromProjected key evidence (persistence.ReadExact key)
        }

[<RequireQualifiedAccess>]
module ManagedChatProviderLifecycle =

    let private evidenceKey (evidence: AcceptedChatExecutionEvidence) =
        { SessionId = evidence.SessionId
          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

    let private validate key evidence =
        AcceptedChatExecutionEvidence.validate evidence
        |> Result.mapError ManagedChatProviderLifecycleError.AttemptEvidenceInvalid
        |> Result.bind (fun () ->
            let supplied = evidenceKey evidence

            if supplied = key then
                Ok()
            else
                Error(ManagedChatProviderLifecycleError.AttemptKeyMismatch(supplied, key)))

    let private persistenceError failure =
        match failure with
        | JournalAppendFailure.WriterUnavailable(eventId, unavailable) ->
            ManagedChatProviderLifecycleError.NotAttempted(eventId, unavailable)
        | JournalAppendFailure.WriteUnknown(eventId, writeFailure) ->
            ManagedChatProviderLifecycleError.CommitUnknown(eventId, writeFailure)
        | JournalAppendFailure.NoNewWriteReleaseFailed(eventId, failure) ->
            ManagedChatProviderLifecycleError.NoNewWriteReleaseFailed(eventId, failure)
        | JournalAppendFailure.FactRejected(eventId, rejection) ->
            ManagedChatProviderLifecycleError.FactRejected(eventId, rejection)

    let private established key evidence persistence =
        validate key evidence
        |> Result.bind (fun () ->
            match persistence.ReadExact key with
            | None -> Error(ManagedChatProviderLifecycleError.MissingAccepted key)
            | Some current when current.acceptedEvidence = evidence -> Ok current
            | Some current ->
                Error(
                    ManagedChatProviderLifecycleError.EstablishedEvidenceConflict(current.acceptedEvidence, evidence)
                ))

    let private append persistence startedEvidence fact =
        task {
            let! appended = persistence.AppendFact startedEvidence fact
            return appended |> Result.mapError persistenceError
        }

    let private startedWitness key startedEvidence persistence =
        match persistence.ReadExact key with
        | Some(ChatExecutionState.Started established) when established = startedEvidence ->
            Ok(ManagedChatProviderStartedWitness(key, established))
        | None -> Error(ManagedChatProviderLifecycleError.ProjectionMissingAfterCommit key)
        | Some current -> Error(ManagedChatProviderLifecycleError.ProjectionConflictAfterCommit current)

    let private terminalWitness key startedEvidence disposition persistence =
        match persistence.ReadExact key with
        | Some(ChatExecutionState.EndedAfterStart(established, projected)) when
            established = startedEvidence && projected = disposition
            ->
            Ok(ManagedChatTerminalWitness(key, ChatExecutionTerminalEvidence.AfterProviderStart established, projected))
        | None -> Error(ManagedChatProviderLifecycleError.ProjectionMissingAfterCommit key)
        | Some current -> Error(ManagedChatProviderLifecycleError.ProjectionConflictAfterCommit current)

    type private StartDecision =
        | AppendStart
        | ExistingStart of ManagedChatProviderStartedWitness

    type private TerminalDecision =
        | AppendTerminal
        | ExistingTerminal of ManagedChatTerminalWitness

    let private exactStartedEvidence
        (current: ChatExecutionState)
        (attempted: ProviderStartedEvidence)
        : Result<ProviderStartedEvidence, ManagedChatProviderLifecycleError> =
        match current.startedEvidence with
        | Some established when established = attempted -> Ok established
        | Some established ->
            Error(ManagedChatProviderLifecycleError.ProviderRunConflict(established.ProviderRun, attempted.ProviderRun))
        | None -> Error(ManagedChatProviderLifecycleError.ProjectionConflictAfterCommit current)

    let private existingStart
        (key: ChatExecutionKey)
        (current: ChatExecutionState)
        (startedEvidence: ProviderStartedEvidence)
        : Result<StartDecision, ManagedChatProviderLifecycleError> =
        exactStartedEvidence current startedEvidence
        |> Result.map (fun established -> ExistingStart(ManagedChatProviderStartedWitness(key, established)))

    let private decideStart
        (key: ChatExecutionKey)
        (current: ChatExecutionState)
        (startedEvidence: ProviderStartedEvidence)
        : Result<StartDecision, ManagedChatProviderLifecycleError> =
        match current with
        | ChatExecutionState.Accepted _ -> Ok AppendStart
        | ChatExecutionState.Started _ -> existingStart key current startedEvidence
        | ChatExecutionState.EndedBeforeStart(_, outcome) ->
            Error(ManagedChatProviderLifecycleError.ProviderStartedAfterTerminal(PreStartOutcome.disposition outcome))
        | ChatExecutionState.EndedAfterStart(_, disposition) ->
            Error(ManagedChatProviderLifecycleError.ProviderStartedAfterTerminal disposition)

    let private existingTerminal
        (key: ChatExecutionKey)
        (current: ChatExecutionState)
        (startedEvidence: ProviderStartedEvidence)
        (establishedDisposition: ChatExecutionTerminalDisposition)
        (attemptedDisposition: ChatExecutionTerminalDisposition)
        : Result<TerminalDecision, ManagedChatProviderLifecycleError> =
        exactStartedEvidence current startedEvidence
        |> Result.bind (fun established ->
            if establishedDisposition = attemptedDisposition then
                Ok(
                    ExistingTerminal(
                        ManagedChatTerminalWitness(
                            key,
                            ChatExecutionTerminalEvidence.AfterProviderStart established,
                            establishedDisposition
                        )
                    )
                )
            else
                Error(ManagedChatProviderLifecycleError.TerminalConflict(establishedDisposition, attemptedDisposition)))

    let private decideTerminal
        (key: ChatExecutionKey)
        (current: ChatExecutionState)
        (startedEvidence: ProviderStartedEvidence)
        (disposition: ChatExecutionTerminalDisposition)
        : Result<TerminalDecision, ManagedChatProviderLifecycleError> =
        match current with
        | ChatExecutionState.Accepted _ -> Error(ManagedChatProviderLifecycleError.ProviderNotStarted key)
        | ChatExecutionState.Started _ ->
            exactStartedEvidence current startedEvidence
            |> Result.map (fun _ -> AppendTerminal)
        | ChatExecutionState.EndedBeforeStart(_, outcome) ->
            existingTerminal key current startedEvidence (PreStartOutcome.disposition outcome) disposition
        | ChatExecutionState.EndedAfterStart(_, establishedDisposition) ->
            existingTerminal key current startedEvidence establishedDisposition disposition

    let startWith
        (persistence: ManagedChatProviderLifecyclePersistence)
        (key: ChatExecutionKey)
        (acceptedEvidence: AcceptedChatExecutionEvidence)
        (providerRun: ProviderRunIdentity)
        (requestKind: ProviderRequestKind)
        : Task<Result<ManagedChatProviderStartedWitness, ManagedChatProviderLifecycleError>> =
        taskResult {
            let! current = established key acceptedEvidence persistence

            let startedEvidence =
                { Accepted = acceptedEvidence
                  ProviderRun = providerRun
                  RequestKind = requestKind }

            do!
                ProviderStartedEvidence.validate startedEvidence
                |> Result.mapError ManagedChatProviderLifecycleError.AttemptEvidenceInvalid

            let! decision = decideStart key current startedEvidence

            match decision with
            | AppendStart ->
                do!
                    append
                        persistence
                        startedEvidence
                        (ChatExecutionFactCases.ProviderStarted
                            {| SchemaVersion = 1
                               Key = key
                               Evidence = startedEvidence |})

                return! startedWitness key startedEvidence persistence
            | ExistingStart witness -> return witness
        }

    let terminalWith
        (persistence: ManagedChatProviderLifecyclePersistence)
        (key: ChatExecutionKey)
        (startedEvidence: ProviderStartedEvidence)
        (disposition: ChatExecutionTerminalDisposition)
        : Task<Result<ManagedChatTerminalWitness, ManagedChatProviderLifecycleError>> =
        taskResult {
            do!
                ProviderStartedEvidence.validate startedEvidence
                |> Result.mapError ManagedChatProviderLifecycleError.AttemptEvidenceInvalid

            let! current = established key startedEvidence.Accepted persistence

            let! decision = decideTerminal key current startedEvidence disposition

            match decision with
            | AppendTerminal ->
                do!
                    append
                        persistence
                        startedEvidence
                        (ChatExecutionFactCases.Terminal
                            {| SchemaVersion = 1
                               Key = key
                               Evidence = ChatExecutionTerminalEvidence.AfterProviderStart startedEvidence
                               Disposition = disposition |})

                return! terminalWitness key startedEvidence disposition persistence
            | ExistingTerminal witness -> return witness
        }
