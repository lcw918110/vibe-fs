namespace Wanxiangshu.Interaction.Admission

open Wanxiangshu.Execution.Routing

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Interaction.Admission

open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
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

type internal ChatAdmissionLeaseHandoffException =
    inherit System.Exception
    new: cause: exn * acquisition: ExecutionAdmissionAcquisition -> ChatAdmissionLeaseHandoffException
    member Cause: exn
    member Acquisition: ExecutionAdmissionAcquisition

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
    | SupersessionSettlementFailed of Wanxiangshu.Composition.Durable.ManagedChatSupersessionError
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

[<RequireQualifiedAccess>]
module internal ChatAdmissionTransaction =
    val executeWithLeaseOwner:
        observe: (ChatAdmissionTransactionStep -> unit) ->
        withLeaseOwner: ChatAdmissionLeaseOwner ->
        tryInputTarget: (ManagedChatAcceptanceWitness -> ModelRoutingTarget option) ->
        ports: ChatAdmissionTransactionPorts ->
        managed: ChatAdmissionIntent.ManagedIntent ->
            Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>

    val executeWith:
        observe: (ChatAdmissionTransactionStep -> unit) ->
        ports: ChatAdmissionTransactionPorts ->
        managed: ChatAdmissionIntent.ManagedIntent ->
            Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>

    val production:
        journal: AgentJournal ->
        acceptManagedIntent:
            (ChatAdmissionIntent.ManagedIntent -> Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>>) ->
        hostTarget: ModelRoutingTarget ->
        projectHostModel: (HostModel -> Result<unit, exn>) ->
            ChatAdmissionTransactionPorts

    val execute:
        ports: ChatAdmissionTransactionPorts ->
        managed: ChatAdmissionIntent.ManagedIntent ->
            Task<Result<ChatAdmissionTransactionOutcome, ChatAdmissionTransactionError>>
