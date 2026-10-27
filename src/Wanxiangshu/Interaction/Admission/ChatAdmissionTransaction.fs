namespace Wanxiangshu.Interaction.Admission

open Wanxiangshu.Execution.Routing

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Interaction.Admission

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
/// DSL-class: ExternalSignal
type internal ChatAdmissionTransactionStep =
    | ResolveState
    | Accept
    | AcceptedWitness
    | AcquireLease
    | LeaseTarget
    | ProjectHost
    | CommitLease
    | TerminalizeAccepted
    | ReleaseBeforeProvider
    | Settled

[<RequireQualifiedAccess>]
type internal ChatAdmissionTransactionOutcome =
    | Settled of ManagedChatAcceptanceWitness
    | DeferredInput of ManagedChatAcceptanceWitness * ModelRoutingTarget
    | Superseded of ManagedChatAcceptanceWitness
    | CapacityQueueFull of ManagedChatAcceptanceWitness
    | Cancelled of ManagedChatAcceptanceWitness
    | AlreadyStarted of ProviderStartedEvidence
    | AlreadyTerminal of ChatExecutionTerminalDisposition

[<RequireQualifiedAccess>]
type internal ChatAdmissionReleaseOutcome =
    | Settled of CapacityTransitionOutcome
    | BoundaryFailed of exn

[<RequireQualifiedAccess>]
type internal ChatAdmissionHandoffSettlement =
    | TerminalCommitted of ChatAdmissionReleaseOutcome
    | SettlementIncomplete of PreProviderSettlementError
    | SettlementBoundaryFailed of exn

type internal ChatAdmissionLeaseHandoffException(cause: exn, acquisition: ExecutionAdmissionAcquisition) =
    inherit Exception("superseded execution settlement failed after lease acquisition", cause)
    member _.Cause = cause
    member _.Acquisition = acquisition

[<RequireQualifiedAccess>]
/// DSL-class: Evidence
type internal ChatAdmissionTransactionError =
    | AdmissionRejected of ChatAdmissionError
    | AcceptanceFailed of ManagedChatAcceptanceError
    | AcceptanceBoundaryFailed of exn
    | PreProviderSettlementFailed of PreProviderSettlementError
    | PreProviderSettlementBoundaryFailed of exn
    | LeaseAcquisitionFailed of exn
    | LeaseHandoffFailed of cause: exn * settlement: ChatAdmissionHandoffSettlement
    | SupersessionSettlementFailed of ManagedChatSupersessionError
    | LeaseTargetFailed of ExecutionAdmissionRejection * release: ChatAdmissionReleaseOutcome
    | LeaseTargetBoundaryFailed of exn * release: ChatAdmissionReleaseOutcome
    | LeaseTargetProjectionFailed of exn * release: ChatAdmissionReleaseOutcome
    | HostProjectionFailed of exn * release: ChatAdmissionReleaseOutcome
    | InputProjectionFailed of exn
    | LeaseCommitFailed of commit: CapacityTransitionOutcome * release: ChatAdmissionReleaseOutcome
    | LeaseCommitBoundaryFailed of exn * release: ChatAdmissionReleaseOutcome

type internal ChatAdmissionLeaseOwner =
    ManagedChatAcceptanceWitness
        -> (unit -> Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>)
        -> Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>

type internal ChatAdmissionTransactionPorts =
    { Accept:
        ChatAdmissionIntent.ManagedIntent -> Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>>
      Acquire: ManagedChatAcceptanceWitness -> Task<Result<ExecutionAdmissionAcquisition, exn>>
      LeaseTarget: ExecutionAdmissionLease -> Result<ModelRoutingTarget, ExecutionAdmissionRejection>
      ProjectHost: HostModel -> Result<unit, exn>
      Commit: ExecutionAdmissionLease -> ExecutionAdmissionExactIdentity -> CapacityTransitionOutcome
      ReleaseBeforeProvider: ExecutionAdmissionLease -> CapacityTransitionOutcome
      SettlePreProvider:
          ChatExecutionKey
              -> AcceptedChatExecutionEvidence
              -> ChatExecutionTerminalDisposition
              -> Task<Result<PreProviderTerminalWitness, PreProviderSettlementError>>
      ReadExact: ChatExecutionKey -> ChatExecutionState option }

type private AdmissionResolution =
    | AdmissionRequired
    | ExistingOutcome of ChatAdmissionTransactionOutcome

type private AdmissionSettlementDecision<'outcome> =
    { Evidence: AcceptedChatExecutionEvidence
      Disposition: ChatExecutionTerminalDisposition
      Outcome: Result<'outcome, ChatAdmissionTransactionError> }

[<RequireQualifiedAccess>]
type private AdmissionAcquisitionOutcome =
    | LeaseAcquired of ExecutionAdmissionLease
    | AdmissionStopped of ChatAdmissionTransactionOutcome

[<RequireQualifiedAccess>]
type private TargetPreparationError =
    | OwnerRejected of ExecutionAdmissionRejection
    | BoundaryFailed of exn
    | ProjectionFailed of exn

[<RequireQualifiedAccess>]
type private CommitError =
    | OwnerRejected of CapacityTransitionOutcome
    | BoundaryFailed of exn

[<RequireQualifiedAccess>]
type private AcquisitionError =
    | BeforeGrant of ChatAdmissionTransactionError
    | AfterGrant of exn * ExecutionAdmissionAcquisition

[<RequireQualifiedAccess>]
module internal ChatAdmissionTransaction =

    let private intentKey (managed: ChatAdmissionIntent.ManagedIntent) : ChatExecutionKey =
        ChatAdmissionIntent.managedKey managed

    let private exactIdentity
        (witness: ManagedChatAcceptanceWitness)
        (target: ModelRoutingTarget)
        : ExecutionAdmissionExactIdentity =
        let evidence = ManagedChatAcceptanceWitness.evidence witness

        { SessionId = SessionId.value evidence.SessionId
          PhysicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId
          Role = AcceptedChatExecutionEvidence.canonicalRole evidence
          Participant = AcceptedChatExecutionEvidence.participant evidence
          Target = target }

    let private keyOfEvidence (evidence: AcceptedChatExecutionEvidence) : ChatExecutionKey =
        { SessionId = evidence.SessionId
          PhysicalUserMessageId = evidence.PhysicalUserMessageId }

    let private settleAccepted observe ports key evidence disposition =
        task {
            observe ChatAdmissionTransactionStep.TerminalizeAccepted

            try
                let! settled = ports.SettlePreProvider key evidence disposition

                return
                    settled
                    |> Result.mapError ChatAdmissionTransactionError.PreProviderSettlementFailed
            with error ->
                return Error(ChatAdmissionTransactionError.PreProviderSettlementBoundaryFailed error)
        }

    let private settleWitness observe ports witness disposition =
        let evidence = ManagedChatAcceptanceWitness.evidence witness
        settleAccepted observe ports (keyOfEvidence evidence) evidence disposition

    let private settleAdmission observe ports decision =
        taskResult {
            let! _ =
                settleAccepted observe ports (keyOfEvidence decision.Evidence) decision.Evidence decision.Disposition

            return! decision.Outcome
        }

    let private release observe ports lease =
        observe ChatAdmissionTransactionStep.ReleaseBeforeProvider

        try
            ports.ReleaseBeforeProvider lease |> ChatAdmissionReleaseOutcome.Settled
        with error ->
            ChatAdmissionReleaseOutcome.BoundaryFailed error

    let private compensate observe ports witness lease disposition createError =
        taskResult {
            let! _ = settleWitness observe ports witness disposition
            let released = release observe ports lease
            return! Error(createError released)
        }

    let private modelFromTarget target =
        try
            Ok(ModelRouting.toHostModel target)
        with error ->
            Error error

    let private accept ports profile =
        task {
            try
                let! accepted = ports.Accept profile
                return accepted |> Result.mapError ChatAdmissionTransactionError.AcceptanceFailed
            with error ->
                return Error(ChatAdmissionTransactionError.AcceptanceBoundaryFailed error)
        }

    let private acquire ports profile =
        task {
            try
                let! acquired = ports.Acquire profile

                return
                    acquired
                    |> Result.mapError (
                        ChatAdmissionTransactionError.LeaseAcquisitionFailed
                        >> AcquisitionError.BeforeGrant
                    )
            with
            | :? ChatAdmissionLeaseHandoffException as error ->
                return Error(AcquisitionError.AfterGrant(error.Cause, error.Acquisition))
            | error ->
                return Error(AcquisitionError.BeforeGrant(ChatAdmissionTransactionError.LeaseAcquisitionFailed error))
        }

    let private effectValue invocation =
        try
            Ok(invocation ())
        with error ->
            Error error

    let private effect invocation =
        effectValue invocation |> Result.bind id

    let private resolve
        observe
        (ports: ChatAdmissionTransactionPorts)
        (key: ChatExecutionKey)
        : Result<AdmissionResolution, ChatAdmissionTransactionError> =
        observe ChatAdmissionTransactionStep.ResolveState

        match ports.ReadExact key with
        | Some(ChatExecutionState.EndedBeforeStart(_, outcome)) ->
            ExistingOutcome(ChatAdmissionTransactionOutcome.AlreadyTerminal(PreStartOutcome.disposition outcome))
            |> Ok
        | Some(ChatExecutionState.EndedAfterStart(_, disposition)) ->
            ExistingOutcome(ChatAdmissionTransactionOutcome.AlreadyTerminal disposition)
            |> Ok
        | Some(ChatExecutionState.Started evidence) ->
            ExistingOutcome(ChatAdmissionTransactionOutcome.AlreadyStarted evidence) |> Ok
        | None
        | Some(ChatExecutionState.Accepted _) -> AdmissionRequired |> Ok

    // semantic-decorator-owner: managed-chat-execution
    // semantic-decorator-WHAT: managed-chat-execution-003
    // semantic-decorator-trace-relation: one Accept step before the acceptance attempt and one AcceptedWitness step only after durable acceptance; business trace unchanged
    // semantic-decorator-proof: requirements/managed-chat-execution/tests/admission-transaction.test.mjs::WHAT[managed-chat-execution-003] managed admission has one fixed success order
    // semantic-decorator-failure-policy: a typed acceptance failure stops the sequence at Accept; the settlement path owns every later step
    // semantic-decorator-cancel-policy: step notification is synchronous and adds no cancellation boundary
    // semantic-decorator-deadline-policy: step notification is time-independent and adds no deadline
    // semantic-decorator-invocation-bound: 2
    let private acceptAdmission observe ports managed =
        task {
            observe ChatAdmissionTransactionStep.Accept
            let! accepted = accept ports managed

            match accepted with
            | Ok witness ->
                observe ChatAdmissionTransactionStep.AcceptedWitness
                return Ok witness
            | Error(ChatAdmissionTransactionError.AcceptanceFailed(ManagedChatAcceptanceError.EstablishedEvidenceConflict(established,
                                                                                                                          _)) as original) ->
                let decision =
                    { Evidence = established
                      Disposition = ChatExecutionTerminalDisposition.Rejected
                      Outcome = Error original }

                return! settleAdmission observe ports decision
            | Error error -> return Error error
        }

    let private stoppedAdmissionSettlement witness outcome =
        let evidence = ManagedChatAcceptanceWitness.evidence witness

        match outcome with
        | ExecutionAdmissionAcquisition.QueueFull ->
            { Evidence = evidence
              Disposition = ChatExecutionTerminalDisposition.Failed
              Outcome =
                ChatAdmissionTransactionOutcome.CapacityQueueFull witness
                |> AdmissionAcquisitionOutcome.AdmissionStopped
                |> Ok }
        | ExecutionAdmissionAcquisition.Cancelled ->
            { Evidence = evidence
              Disposition = ChatExecutionTerminalDisposition.Cancelled
              Outcome =
                ChatAdmissionTransactionOutcome.Cancelled witness
                |> AdmissionAcquisitionOutcome.AdmissionStopped
                |> Ok }
        | ExecutionAdmissionAcquisition.Superseded ->
            { Evidence = evidence
              Disposition = ChatExecutionTerminalDisposition.Cancelled
              Outcome =
                ChatAdmissionTransactionOutcome.Superseded witness
                |> AdmissionAcquisitionOutcome.AdmissionStopped
                |> Ok }
        | ExecutionAdmissionAcquisition.Admitted _
        | ExecutionAdmissionAcquisition.Queued _ -> invalidOp "handled acquisition outcome reached terminal settlement"

    let rec private acquisitionOutcome observe ports witness =
        function
        | ExecutionAdmissionAcquisition.Admitted lease ->
            AdmissionAcquisitionOutcome.LeaseAcquired lease |> Ok |> Task.FromResult
        | ExecutionAdmissionAcquisition.Queued node ->
            task {
                let! completed = node.Completion.Task
                return! acquisitionOutcome observe ports witness completed
            }
        | outcome -> stoppedAdmissionSettlement witness outcome |> settleAdmission observe ports

    let private cancelQueuedAdmission key =
        try
            ModelRouting.cancelPendingPhysicalExecution key
            |> ChatAdmissionReleaseOutcome.Settled
        with error ->
            ChatAdmissionReleaseOutcome.BoundaryFailed error

    let private releaseHandoffAdmission observe ports key =
        function
        | ExecutionAdmissionAcquisition.Admitted lease -> release observe ports lease
        | ExecutionAdmissionAcquisition.Queued _ -> cancelQueuedAdmission key
        | _ -> invalidOp "handoff failure did not carry an owned admission"

    let private handoffSettlement observe ports key acquisition =
        function
        | Error error -> ChatAdmissionHandoffSettlement.SettlementIncomplete error
        | Ok _ ->
            releaseHandoffAdmission observe ports key acquisition
            |> ChatAdmissionHandoffSettlement.TerminalCommitted

    let private settleFailedHandoff observe ports witness acquisition =
        let evidence = ManagedChatAcceptanceWitness.evidence witness
        let key = ManagedChatAcceptanceWitness.key witness

        task {
            observe ChatAdmissionTransactionStep.TerminalizeAccepted

            try
                let! settled = ports.SettlePreProvider key evidence ChatExecutionTerminalDisposition.Failed
                return handoffSettlement observe ports key acquisition settled
            with error ->
                return ChatAdmissionHandoffSettlement.SettlementBoundaryFailed error
        }

    let private acquireAdmission observe ports witness =
        task {
            observe ChatAdmissionTransactionStep.AcquireLease
            let! acquired = acquire ports witness

            match acquired with
            | Error(AcquisitionError.AfterGrant(cause, acquisition)) ->
                let! settlement = settleFailedHandoff observe ports witness acquisition
                return Error(ChatAdmissionTransactionError.LeaseHandoffFailed(cause, settlement))
            | Error(AcquisitionError.BeforeGrant error) ->
                let decision =
                    { Evidence = ManagedChatAcceptanceWitness.evidence witness
                      Disposition = ChatExecutionTerminalDisposition.Failed
                      Outcome = Error error }

                return! settleAdmission observe ports decision
            | Ok acquisition -> return! acquisitionOutcome observe ports witness acquisition
        }

    let private readTarget ports lease =
        try
            ports.LeaseTarget lease |> Result.mapError TargetPreparationError.OwnerRejected
        with error ->
            Error(TargetPreparationError.BoundaryFailed error)

    let private prepareTarget
        (ports: ChatAdmissionTransactionPorts)
        (witness: ManagedChatAcceptanceWitness)
        (lease: ExecutionAdmissionLease)
        : Result<ModelRoutingTarget * ExecutionAdmissionExactIdentity * HostModel, TargetPreparationError> =
        readTarget ports lease
        |> Result.bind (fun target ->
            modelFromTarget target
            |> Result.mapError TargetPreparationError.ProjectionFailed
            |> Result.map (fun model -> (target, exactIdentity witness target, model)))

    let private targetError =
        function
        | TargetPreparationError.OwnerRejected rejection ->
            fun release -> ChatAdmissionTransactionError.LeaseTargetFailed(rejection, release)
        | TargetPreparationError.BoundaryFailed error ->
            fun release -> ChatAdmissionTransactionError.LeaseTargetBoundaryFailed(error, release)
        | TargetPreparationError.ProjectionFailed error ->
            fun release -> ChatAdmissionTransactionError.LeaseTargetProjectionFailed(error, release)

    let private targetAdmission observe ports witness lease =
        observe ChatAdmissionTransactionStep.LeaseTarget

        match prepareTarget ports witness lease with
        | Ok(target, identity, model) -> Task.FromResult(Ok(target, identity, model))
        | Error error ->
            compensate observe ports witness lease ChatExecutionTerminalDisposition.Failed (targetError error)

    let private projectAdmission observe ports witness lease model =
        observe ChatAdmissionTransactionStep.ProjectHost

        match effect (fun () -> ports.ProjectHost model) with
        | Ok() -> Task.FromResult(Ok())
        | Error error ->
            compensate observe ports witness lease ChatExecutionTerminalDisposition.Failed (fun release ->
                ChatAdmissionTransactionError.HostProjectionFailed(error, release))

    let private commit ports lease identity =
        effectValue (fun () -> ports.Commit lease identity)
        |> Result.mapError CommitError.BoundaryFailed
        |> Result.bind (function
            | CapacityTransitionOutcome.Applied
            | CapacityTransitionOutcome.AlreadyApplied -> Ok()
            | CapacityTransitionOutcome.StaleFence ->
                Error(CommitError.OwnerRejected CapacityTransitionOutcome.StaleFence)
            | CapacityTransitionOutcome.Conflict -> Error(CommitError.OwnerRejected CapacityTransitionOutcome.Conflict))

    let private commitError =
        function
        | CommitError.OwnerRejected rejected ->
            fun release -> ChatAdmissionTransactionError.LeaseCommitFailed(rejected, release)
        | CommitError.BoundaryFailed error ->
            fun release -> ChatAdmissionTransactionError.LeaseCommitBoundaryFailed(error, release)

    // semantic-decorator-owner: managed-chat-execution
    // semantic-decorator-WHAT: managed-chat-execution-003
    // semantic-decorator-trace-relation: one CommitLease step before the lease commit and one Settled step only after it succeeds; business trace unchanged
    // semantic-decorator-proof: requirements/managed-chat-execution/tests/admission-transaction.test.mjs::WHAT[managed-chat-execution-003] managed admission has one fixed success order
    // semantic-decorator-failure-policy: a commit failure stops the sequence at CommitLease; the compensation path owns every later step
    // semantic-decorator-cancel-policy: step notification is synchronous and adds no cancellation boundary
    // semantic-decorator-deadline-policy: step notification is time-independent and adds no deadline
    // semantic-decorator-invocation-bound: 2
    let private commitAdmission observe ports witness lease identity =
        observe ChatAdmissionTransactionStep.CommitLease

        match commit ports lease identity with
        | Ok() ->
            observe ChatAdmissionTransactionStep.Settled
            ChatAdmissionTransactionOutcome.Settled witness |> Ok |> Task.FromResult
        | Error error ->
            compensate observe ports witness lease ChatExecutionTerminalDisposition.Failed (commitError error)

    let private projectDeferredInput observe witness target =
        taskResult {
            observe ChatAdmissionTransactionStep.ProjectHost
            observe ChatAdmissionTransactionStep.Settled
            return ChatAdmissionTransactionOutcome.DeferredInput(witness, target)
        }

    let private executeFreshAdmission observe (withLeaseOwner: ChatAdmissionLeaseOwner) ports witness =
        taskResult {
            let! acquisition = acquireAdmission observe ports witness

            match acquisition with
            | AdmissionAcquisitionOutcome.AdmissionStopped outcome -> return outcome
            | AdmissionAcquisitionOutcome.LeaseAcquired lease ->
                return!
                    withLeaseOwner witness (fun () ->
                        taskResult {
                            let! target, identity, model = targetAdmission observe ports witness lease
                            let! _ = projectAdmission observe ports witness lease model
                            return! commitAdmission observe ports witness lease identity
                        })
        }

    let private executeAdmission observe (withLeaseOwner: ChatAdmissionLeaseOwner) tryInputTarget ports managed =
        taskResult {
            let! witness = acceptAdmission observe ports managed

            match tryInputTarget witness with
            | Some target -> return! projectDeferredInput observe witness target
            | None -> return! executeFreshAdmission observe withLeaseOwner ports witness
        }

    let executeWithLeaseOwner
        (observe: ChatAdmissionTransactionStep -> unit)
        (withLeaseOwner: ChatAdmissionLeaseOwner)
        (tryInputTarget: ManagedChatAcceptanceWitness -> ModelRoutingTarget option)
        (ports: ChatAdmissionTransactionPorts)
        (managed: ChatAdmissionIntent.ManagedIntent)
        : Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>> =
        task {
            match resolve observe ports (intentKey managed) with
            | Error error -> return Error error
            | Ok(ExistingOutcome outcome) -> return Ok outcome
            | Ok AdmissionRequired -> return! executeAdmission observe withLeaseOwner tryInputTarget ports managed
        }

    let executeWith observe ports managed =
        executeWithLeaseOwner observe (fun _ operation -> operation ()) (fun _ -> None) ports managed

    let production
        (journal: AgentJournal)
        (acceptManagedIntent:
            ChatAdmissionIntent.ManagedIntent -> Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>>)
        (hostTarget: ModelRoutingTarget)
        (projectHostModel: HostModel -> Result<unit, exn>)
        : ChatAdmissionTransactionPorts =
        { Accept = acceptManagedIntent
          Acquire =
            fun witness ->
                task {
                    let evidence = ManagedChatAcceptanceWitness.evidence witness

                    try
                        let lenderSessionId =
                            PromptAuthority.identitySeedOwner evidence.IdentitySeed
                            |> Option.map (fun (ownerSession, _, _) -> SessionId.value ownerSession)

                        let! acquired =
                            ModelRouting.acquireExecutionAdmission
                                evidence.SessionId
                                evidence.PhysicalUserMessageId
                                (AcceptedChatExecutionEvidence.canonicalRole evidence)
                                (AcceptedChatExecutionEvidence.participant evidence)
                                ModelExecutionPurpose.Normal
                                lenderSessionId
                                (Some hostTarget)

                        return Ok acquired
                    with error ->
                        return Error error
                }
          LeaseTarget = ModelRouting.executionAdmissionTarget
          ProjectHost =
            fun model ->
                try
                    projectHostModel model
                with error ->
                    Error error
          Commit = ModelRouting.commitExecutionAdmission
          ReleaseBeforeProvider = fun lease -> ModelRouting.releaseExecutionAdmissionBeforeProvider lease lease.Identity
          SettlePreProvider = PreProviderSettlement.settle journal
          ReadExact =
            fun key ->
                (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key }

    let execute ports managed = executeWith ignore ports managed
