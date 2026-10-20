namespace Wanxiangshu.Execution.Session.ChatExecution

open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona

module RecoverySurface =

    let private accepted (suffix: string) : AcceptedChatExecutionEvidence =
        let identity =
            ParticipantIdentity.resolveAtRoot "engineer"
            |> Result.defaultWith (fun error -> invalidOp $"cannot construct proof identity: {error}")

        { SessionId = SessionId.create $"session-{suffix}"
          LogicalRunId = LogicalRunId.create $"run-{suffix}"
          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create $"root-{suffix}"
          AuthorityKind = PromptRootAuthorityKind.HumanRoot
          IdentitySeed = RootSelection identity
          PhysicalUserMessageId = PhysicalUserMessageId.create $"message-{suffix}"
          Origin = PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot }

    let private keyOf (accepted: AcceptedChatExecutionEvidence) : ChatExecutionKey =
        { SessionId = accepted.SessionId
          PhysicalUserMessageId = accepted.PhysicalUserMessageId }

    let private startedEvidence
        (providerRun: string)
        (accepted: AcceptedChatExecutionEvidence)
        : ProviderStartedEvidence =
        { Accepted = accepted
          ProviderRun = ProviderRunIdentity.create providerRun
          RequestKind = ProviderRequestKind.WorkMain }

    let private acceptedState (accepted: AcceptedChatExecutionEvidence) : ChatExecutionState =
        ChatExecutionState.Accepted accepted

    let private startedState (started: ProviderStartedEvidence) : ChatExecutionState =
        ChatExecutionState.Started started

    let private terminalState
        (disposition: ChatExecutionTerminalDisposition)
        (started: ProviderStartedEvidence)
        : ChatExecutionState =
        ChatExecutionState.EndedAfterStart(started, disposition)

    let private policy
        (failure: ExecutionFailure)
        (retry: ProviderRecoveryBudget)
        (started: ProviderStartedEvidence)
        : RecoveryPolicyEvidence =
        ExecutionFailurePolicy.decide
            { Failure = failure
              Lifecycle = DurableExecutionLifecycle.ProviderStarted
              ExecutionKey = keyOf started.Accepted
              Capacity = CapacityOwnership.NoCapacityFence
              Provider =
                { LogicalRun = started.Accepted.LogicalRunId
                  ProviderRun = started.ProviderRun
                  RequestKind = started.RequestKind
                  RetryBudget = retry
                  Breaker = ProviderBreakerState.Closed } }
        |> RecoveryPolicyEvidence.FailureDecision

    let private evidence (scenario: string) : ChatExecutionRecoveryEvidence =
        let accepted = accepted scenario
        let started = startedEvidence $"provider-{scenario}" accepted
        let key = keyOf accepted
        let absent = PhysicalResourceObservation.ResourceAbsent key
        let none = RecoveryPolicyEvidence.NoFailureDecision

        let pending
            (execution: ChatExecutionState)
            (provider: ProviderPhysicalObservation)
            (resource: PhysicalResourceObservation)
            (persistence: PersistenceCommitment)
            (failurePolicy: RecoveryPolicyEvidence)
            : ChatExecutionRecoveryEvidence =
            { ExecutionState = execution
              ProviderObservation = provider
              ResourceObservation = resource
              PersistenceCommitment = persistence
              FailureDecisionEvidence = failurePolicy }

        match scenario with
        | "CrashAfterAcceptance" ->
            pending
                (acceptedState accepted)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                none
        | "AcceptedProviderAlive" ->
            pending
                (acceptedState accepted)
                (ProviderPhysicalObservation.ProviderAlive started)
                absent
                PersistenceCommitment.NotCommitted
                none
        | "AcceptedProviderTerminal" ->
            pending
                (acceptedState accepted)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "ProviderAlive" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAlive started)
                absent
                PersistenceCommitment.NotCommitted
                none
        | "ProviderTerminalCompleted" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "ProviderTerminalFailed" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Failed))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "ProviderTerminalCancelled" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Cancelled))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "ProviderTerminalRejected" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Rejected))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "RetryEligible" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (policy ExecutionFailure.ProviderTransient ProviderRecoveryBudget.Available started)
        | "RetryExhausted" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (policy ExecutionFailure.ProviderTransient ProviderRecoveryBudget.Exhausted started)
        | "Superseded" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (policy ExecutionFailure.Superseded ProviderRecoveryBudget.Available started)
        | "MissingReceipt" ->
            pending
                (startedState started)
                ProviderPhysicalObservation.ReceiptMissing
                absent
                PersistenceCommitment.NotCommitted
                none
        | "AmbiguousReceipt" ->
            pending
                (startedState started)
                ProviderPhysicalObservation.ReceiptAmbiguous
                absent
                PersistenceCommitment.NotCommitted
                none
        | "PhysicalOutcomeUnknown" ->
            pending
                (terminalState ChatExecutionTerminalDisposition.Completed started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                (PhysicalResourceObservation.ResourceUnknown key)
                PersistenceCommitment.Committed
                none
        | "PersistenceUnknown" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.Unknown
                none
        | "DuplicateRecovery" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.Committed
                none
        | "StaleProvider" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAlive(startedEvidence "provider-stale" accepted))
                absent
                PersistenceCommitment.NotCommitted
                none
        | "StaleKey" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent
                    { key with
                        PhysicalUserMessageId = PhysicalUserMessageId.create "message-stale" })
                absent
                PersistenceCommitment.NotCommitted
                none
        | "StalePolicy" ->
            let staleStarted = startedEvidence "provider-stale" accepted

            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (policy ExecutionFailure.ProviderTransient ProviderRecoveryBudget.Available staleStarted)
        | "TerminalResourceHeld" ->
            pending
                (terminalState ChatExecutionTerminalDisposition.Completed started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                (PhysicalResourceObservation.ResourceHeld key)
                PersistenceCommitment.Committed
                none
        | "TerminalResourceReleased" ->
            pending
                (terminalState ChatExecutionTerminalDisposition.Completed started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                (PhysicalResourceObservation.ResourceReleased key)
                PersistenceCommitment.Committed
                none
        | "ProviderAbsentWithoutPolicy" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                none
        | unknown -> invalidArg "scenario" $"unknown recovery scenario '{unknown}'"

    let private dispositionLabel (disposition: ChatExecutionTerminalDisposition) =
        match disposition with
        | ChatExecutionTerminalDisposition.Completed -> "Completed"
        | ChatExecutionTerminalDisposition.Cancelled -> "Cancelled"
        | ChatExecutionTerminalDisposition.Rejected -> "Rejected"
        | ChatExecutionTerminalDisposition.Failed -> "Failed"

    let private decisionView (decision: ChatExecutionRecoveryDecision) : obj =
        match decision with
        | ChatExecutionRecoveryDecision.Ignore(_, reason) ->
            box
                {| kind = "Ignore"
                   request = $"{reason}"
                   disposition = null |}
        | ChatExecutionRecoveryDecision.ReconcilePhysical request ->
            match request with
            | PhysicalReconciliationRequest.PersistProviderStarted _ ->
                box
                    {| kind = "ReconcilePhysical"
                       request = "PersistProviderStarted"
                       disposition = null |}
            | PhysicalReconciliationRequest.PersistProviderStartedAndTerminal(_, disposition) ->
                box
                    {| kind = "ReconcilePhysical"
                       request = "PersistProviderStartedAndTerminal"
                       disposition = box (dispositionLabel disposition) |}
            | PhysicalReconciliationRequest.ReleaseTerminalResource(_, _, disposition) ->
                box
                    {| kind = "ReconcilePhysical"
                       request = "ReleaseTerminalResource"
                       disposition = box (dispositionLabel disposition) |}
        | ChatExecutionRecoveryDecision.ResumePreProvider _ ->
            box
                {| kind = "ResumePreProvider"
                   request = "ResumeAcceptedAdmission"
                   disposition = null |}
        | ChatExecutionRecoveryDecision.Finalize request ->
            box
                {| kind = "Finalize"
                   request = "PersistTerminal"
                   disposition = box (dispositionLabel request.TerminalDisposition) |}
        | ChatExecutionRecoveryDecision.MarkManualIntervention request ->
            box
                {| kind = "MarkManualIntervention"
                   request = $"{request.InterventionReason}"
                   disposition = null |}

    let decideScenario (scenario: string) : obj =
        evidence scenario |> ChatExecutionRecovery.decide |> decisionView
