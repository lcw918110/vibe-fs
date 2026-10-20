namespace Wanxiangshu.Interaction.Dispatch.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

module HostSessionNudge =
    val tryActiveProfile:
        journal: AgentJournal option -> sessionId: SessionId -> PromptAuthority.AuthorityExecutionProfile option

    [<RequireQualifiedAccess>]
    type GateContinuationOutcome =
        | Sent of PromptKey
        | AlreadyAdmitted
        | Retired
        | Failed of string

    val sendContinuationResult:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        kind: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        awaitMode: PromptDispatcher.AwaitMode ->
        onAccepted: ContinuationAcceptanceObserver option ->
            Task<Result<PromptKey, string>>

    val sendContinuation:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        kind: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
            Task<Result<PromptKey, string>>

    val trySendGateContinuation:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        continuation: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
            Task<GateContinuationOutcome>

    val trySendGateContinuationObserved:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        continuation: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        observer: ContinuationAcceptanceObserver option ->
            Task<GateContinuationOutcome>

    val trySendGateContinuationPhysical:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        continuation: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
            Task<Result<PhysicalUserMessageId, string>>

    val trySendInteractionRepair:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        directory: string option ->
        journal: AgentJournal option ->
        requestId: RepairRequestId ->
        terminalProviderRun: ProviderRunIdentity ->
        repairKind: string ->
            Task<InteractionRepairSendOutcome>

    [<RequireQualifiedAccess>]
    type IdleContinuationOutcome =
        | Sent of PromptKey
        | AdmissionRejected of QuiescencePermitFailure
        | AlreadyAdmitted
        | Retired
        | NotSent of string
        | Failed of string


    val trySendGateContinuationWithAdmission:
        physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) ->
        releaseAdmission: (unit -> Result<unit, QuiescencePermitFailure>) ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        continuation: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        awaitMode: PromptDispatcher.AwaitMode ->
            Task<IdleContinuationOutcome>

    val trySendIdleGateContinuation:
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        continuation: PromptAuthority.ContinuationKind ->
        directory: string option ->
        journal: AgentJournal option ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        awaitMode: PromptDispatcher.AwaitMode ->
            Task<IdleContinuationOutcome>

    val trySendIdleGateRepair:
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        directory: string option ->
        journal: AgentJournal option ->
        repairKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
            Task<IdleContinuationOutcome>

    val trySendIdleGateRepairObserved:
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        directory: string option ->
        journal: AgentJournal option ->
        repairKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        observer: ContinuationAcceptanceObserver option ->
            Task<IdleContinuationOutcome>

    val trySendIdleInteractionRepair:
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit ->
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        sessionId: SessionId ->
        prompt: string ->
        directory: string option ->
        journal: AgentJournal option ->
        requestId: RepairRequestId ->
        terminalProviderRun: ProviderRunIdentity ->
        repairKind: string ->
            Task<IdleContinuationOutcome>
