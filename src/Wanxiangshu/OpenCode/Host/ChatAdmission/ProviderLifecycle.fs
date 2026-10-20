namespace Wanxiangshu.OpenCode

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal

/// Provider-start lifecycle. The transform freezes the exact attempt plan from
/// its real trailing user message; the Host assistant event or the pre-provider
/// public snapshot binds ProviderRunIdentity and persists ProviderStarted. Both operations address the
/// exact (SessionId, PhysicalUserMessageId) key and read only durable facts.
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

    let providerStartObservationErrorCode =
        function
        | ProviderStartObservationError.DurableJournalUnavailable -> "durable-journal-unavailable"
        | ProviderStartObservationError.PhysicalUserMessageMissing _ -> "physical-user-message-missing"
        | ProviderStartObservationError.AttemptPlanFreezeFailed _ -> "attempt-plan-freeze-failed"
        | ProviderStartObservationError.FrozenAttemptPlanMissing _ -> "frozen-attempt-plan-missing"
        | ProviderStartObservationError.AcceptedExecutionMissing _ -> "accepted-execution-missing"
        | ProviderStartObservationError.AcceptedExecutionAlreadyTerminal _ -> "accepted-execution-already-terminal"
        | ProviderStartObservationError.AuthorityEvidenceInvalid _ -> "authority-evidence-invalid"
        | ProviderStartObservationError.PersistenceFailed _ -> "persistence-failed"

    let private freezeOrdinaryPlan<'bindingError>
        (durable: AgentJournal)
        (key: ChatExecutionKey)
        : Result<AcceptedChatExecutionEvidence * PendingAttemptPlan, ProviderStartObservationError<'bindingError>> =
        let projection = AgentJournal.snapshot durable

        result {
            let! execution =
                projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key
                |> Result.requireSome (ProviderStartObservationError.AcceptedExecutionMissing key)

            let accepted = execution.acceptedEvidence
            let requestKind = AttemptPlanner.ordinaryRequestKind accepted.Origin

            let! pending =
                AttemptPlanner.freezeOrdinary accepted requestKind
                |> Result.mapError (fun _ -> ProviderStartObservationError.AuthorityEvidenceInvalid accepted)

            return accepted, pending
        }

    let private freezePhysicalPlan durable freezeAttemptPlan sessionId physicalUserMessageId =
        match physicalUserMessageId with
        | None -> Ok()
        | Some physical ->
            let key =
                { SessionId = sessionId
                  PhysicalUserMessageId = physical }

            result {
                let! _, ordinaryPlan = freezeOrdinaryPlan durable key

                do!
                    freezeAttemptPlan sessionId physical ordinaryPlan
                    |> Result.mapError ProviderStartObservationError.AttemptPlanFreezeFailed

                return ()
            }

    /// Freeze the exact request plan for the provider attempt the transform is
    /// building. The plan is addressed by the physical user message observed in
    /// this request; a missing durable Accepted execution is a hard rejection,
    /// not a silent re-admission.
    let freezeProviderAttemptPlanForTransform
        (journal: AgentJournal option)
        (freezeAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan -> Result<unit, 'bindingError>)
        (projectionSessionIdOpt: string option)
        (physicalUserMessageId: PhysicalUserMessageId option)
        : Task<Result<unit, ProviderStartObservationError<'bindingError>>> =
        task {
            match projectionSessionIdOpt, journal with
            | None, _ -> return Ok()
            | Some _, None -> return Error ProviderStartObservationError.DurableJournalUnavailable
            | Some sessionText, Some durable ->
                let sessionId = SessionId.create sessionText
                return freezePhysicalPlan durable freezeAttemptPlan sessionId physicalUserMessageId
        }

    let private persistObservedProviderStart
        (durable: AgentJournal)
        (bindAttemptPlan: SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> AttemptPlan option)
        (observation: ExactProviderStartObservation)
        =
        let key =
            { SessionId = observation.SessionId
              PhysicalUserMessageId = observation.PhysicalUserMessageId }

        let execution =
            AgentJournal.snapshot durable
            |> fun projection -> projection.AgentProjections.ChatExecutions
            |> ChatExecutionProjection.byKey key

        match execution with
        | Some state when state.terminalDisposition.IsSome ->
            Task.FromResult(Error(ProviderStartObservationError.AcceptedExecutionAlreadyTerminal key))
        | Some state when state.startedEvidence.IsSome -> Task.FromResult(Ok false)
        | _ ->
            taskResult {
                let! acceptedEvidence =
                    execution
                    |> Option.map _.acceptedEvidence
                    |> Result.requireSome (ProviderStartObservationError.AcceptedExecutionMissing key)

                let! plan =
                    bindAttemptPlan observation.SessionId observation.PhysicalUserMessageId observation.ProviderRun
                    |> Result.requireSome (
                        ProviderStartObservationError.FrozenAttemptPlanMissing(key, observation.ProviderRun)
                    )

                let profile = plan.Profile

                let! _ =
                    ManagedChatProviderLifecycle.providerStarted
                        durable
                        key
                        acceptedEvidence
                        profile.ProviderRun
                        profile.RequestKind
                    |> TaskResult.mapError ProviderStartObservationError.PersistenceFailed

                return true
            }

    let persistProviderStartedFromObservation
        (journal: AgentJournal option)
        (bindAttemptPlan: SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> AttemptPlan option)
        (observation: ExactProviderStartObservation)
        : Task<Result<bool, ProviderStartObservationError<unit>>> =
        match journal with
        | None -> Task.FromResult(Error ProviderStartObservationError.DurableJournalUnavailable)
        | Some durable -> persistObservedProviderStart durable bindAttemptPlan observation
