namespace Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode

[<AutoOpen>]
module PromptDispatcherSend =
    type PromptDispatcher.Runtime with
        member SendAgentOwnerRoot:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            identitySeed: PromptAuthority.IdentitySeed ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
                Task<Result<PromptKey, string>>

        member SendAgentOwnerRootDetachedObserved:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            identitySeed: PromptAuthority.IdentitySeed ->
            directory: string option ->
            onFailure: (string -> Task) ->
                Task<Result<PromptKey, string>>

        member SendAgentOwnerRootWithTools:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            identitySeed: PromptAuthority.IdentitySeed ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
            onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option ->
            tools: Map<string, bool> ->
                Task<Result<PromptKey, string>>

        member SendContinuation:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            continuation: PromptAuthority.ContinuationKind ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
                Task<Result<PromptKey, string>>

        member SendGateNudge:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            continuation: PromptAuthority.ContinuationKind ->
            gateKind: string ->
            terminalProviderRun: ProviderRunIdentity ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
                Task<Result<PromptKey, string>>

        member SendContinuationWithTools:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            continuation: PromptAuthority.ContinuationKind ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
            onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option ->
            tools: Map<string, bool> ->
                Task<Result<PromptKey, string>>

        member SendInteractionRepair:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            requestId: RepairRequestId ->
            terminalProviderRun: ProviderRunIdentity ->
            repairKind: string ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
                Task<Result<PromptKey, string>>

        member SendManagedAssignment:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            issueIdentitySeed: (unit -> Result<PromptAuthority.IdentitySeed, string>) ->
            directory: string option ->
            tools: Map<string, bool> option ->
                Task<Result<PromptKey, string>>

        member internal SendIdleContinuation:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            continuation: PromptAuthority.ContinuationKind ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            onAccepted: ContinuationAcceptanceObserver option ->
            physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) ->
                Task<PromptDispatcher.SendAttemptOutcome>

        member internal SendIdleGateNudge:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            continuation: PromptAuthority.ContinuationKind ->
            gateKind: string ->
            terminalProviderRun: ProviderRunIdentity ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) ->
            observer: ContinuationAcceptanceObserver option ->
                Task<PromptDispatcher.SendAttemptOutcome>

        member internal SendIdleInteractionRepair:
            port: IDispatchSessionPort ->
            sessionId: SessionId ->
            text: string ->
            requestId: RepairRequestId ->
            terminalProviderRun: ProviderRunIdentity ->
            repairKind: string ->
            profile: PromptAuthority.AuthorityExecutionProfile ->
            directory: string option ->
            awaitMode: PromptDispatcher.AwaitMode ->
            physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) ->
                Task<PromptDispatcher.SendAttemptOutcome>

        /// Settle a detached PromptKey from the transport's eventual typed
        /// verdict (the DetachedSendListener rail + the caller-visible
        /// sendTask). Refused abandons the claim; OutcomeUnknown leaves it
        /// pending on durable evidence; OwnedSettled is a no-op.
        member internal SettleDetachedSend:
            key: PromptKey ->
            sessionId: SessionId ->
            verdict: DetachedSendVerdict ->
            onFailure: (string -> Task) option ->
                Task
