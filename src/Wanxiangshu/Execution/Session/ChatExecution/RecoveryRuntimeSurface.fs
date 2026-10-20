namespace Wanxiangshu.Execution.Session.ChatExecution

open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona

module RecoveryRuntimeSurface =

    let private accepted (suffix: string) : AcceptedChatExecutionEvidence =
        let identity =
            ParticipantIdentity.resolveAtRoot "engineer"
            |> Result.defaultWith (fun error -> invalidOp (sprintf "%A" error))

        { SessionId = SessionId.create $"session-{suffix}"
          LogicalRunId = LogicalRunId.create $"logical-{suffix}"
          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create $"root-{suffix}"
          AuthorityKind = PromptRootAuthorityKind.HumanRoot
          IdentitySeed = RootSelection identity
          PhysicalUserMessageId = PhysicalUserMessageId.create $"message-{suffix}"
          Origin = PromptOrigin.AuthorityRoot PromptRootAuthorityKind.HumanRoot }

    let private keyOf (accepted: AcceptedChatExecutionEvidence) : ChatExecutionKey =
        { SessionId = accepted.SessionId
          PhysicalUserMessageId = accepted.PhysicalUserMessageId }

    let private providerStarted
        (providerRun: string)
        (accepted: AcceptedChatExecutionEvidence)
        : ProviderStartedEvidence =
        { Accepted = accepted
          ProviderRun = ProviderRunIdentity.create providerRun
          RequestKind = ProviderRequestKind.WorkMain }

    let private acceptedState (accepted: AcceptedChatExecutionEvidence) : ChatExecutionState =
        ChatExecutionState.Accepted accepted

    let private startedState (evidence: ProviderStartedEvidence) : ChatExecutionState =
        ChatExecutionState.Started evidence

    let private terminalState
        (disposition: ChatExecutionTerminalDisposition)
        (evidence: ProviderStartedEvidence)
        : ChatExecutionState =
        ChatExecutionState.EndedAfterStart(evidence, disposition)

    let private failurePolicy
        (failure: ExecutionFailure)
        (retry: ProviderRecoveryBudget)
        (evidence: ProviderStartedEvidence)
        : RecoveryPolicyEvidence =
        ExecutionFailurePolicy.decide
            { Failure = failure
              Lifecycle = DurableExecutionLifecycle.ProviderStarted
              ExecutionKey = keyOf evidence.Accepted
              Capacity = CapacityOwnership.NoCapacityFence
              Provider =
                { LogicalRun = evidence.Accepted.LogicalRunId
                  ProviderRun = evidence.ProviderRun
                  RequestKind = evidence.RequestKind
                  RetryBudget = retry
                  Breaker = ProviderBreakerState.Closed } }
        |> RecoveryPolicyEvidence.FailureDecision

    let private scenarioEvidence (scenario: string) : ChatExecutionRecoveryEvidence =
        let accepted = accepted scenario
        let started = providerStarted $"provider-{scenario}" accepted
        let key = keyOf accepted
        let absent = PhysicalResourceObservation.ResourceAbsent key

        let pending
            (state: ChatExecutionState)
            (provider: ProviderPhysicalObservation)
            (resource: PhysicalResourceObservation)
            (persistence: PersistenceCommitment)
            (policy: RecoveryPolicyEvidence)
            : ChatExecutionRecoveryEvidence =
            { ExecutionState = state
              ProviderObservation = provider
              ResourceObservation = resource
              PersistenceCommitment = persistence
              FailureDecisionEvidence = policy }

        match scenario with
        | "ProviderAlive" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAlive started)
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "AcceptedProviderAlive" ->
            pending
                (acceptedState accepted)
                (ProviderPhysicalObservation.ProviderAlive started)
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "CrashAfterAcceptance" ->
            pending
                (acceptedState accepted)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "RetryEligible" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (failurePolicy ExecutionFailure.ProviderTransient ProviderRecoveryBudget.Available started)
        | "ProviderTerminalCompleted" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "TerminalResourceHeld" ->
            pending
                (terminalState ChatExecutionTerminalDisposition.Completed started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                (PhysicalResourceObservation.ResourceHeld key)
                PersistenceCommitment.Committed
                RecoveryPolicyEvidence.NoFailureDecision
        | "TerminalResourceReleased" ->
            pending
                (terminalState ChatExecutionTerminalDisposition.Completed started)
                (ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Completed))
                (PhysicalResourceObservation.ResourceReleased key)
                PersistenceCommitment.Committed
                RecoveryPolicyEvidence.NoFailureDecision
        | "MissingReceipt" ->
            pending
                (startedState started)
                ProviderPhysicalObservation.ReceiptMissing
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "StaleKey" ->
            let stale =
                { key with
                    PhysicalUserMessageId = PhysicalUserMessageId.create "stale-message" }

            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent stale)
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "StaleProvider" ->
            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAlive(providerStarted "stale-provider" accepted))
                absent
                PersistenceCommitment.NotCommitted
                RecoveryPolicyEvidence.NoFailureDecision
        | "StalePolicy" ->
            let staleStarted = providerStarted "stale-provider" accepted

            pending
                (startedState started)
                (ProviderPhysicalObservation.ProviderAbsent key)
                absent
                PersistenceCommitment.NotCommitted
                (failurePolicy ExecutionFailure.ProviderTransient ProviderRecoveryBudget.Available staleStarted)
        | unknown -> invalidArg "scenario" $"unknown lifecycle recovery scenario '{unknown}'"

    let private dispositionName (disposition: ChatExecutionTerminalDisposition) =
        match disposition with
        | ChatExecutionTerminalDisposition.Completed -> "Completed"
        | ChatExecutionTerminalDisposition.Cancelled -> "Cancelled"
        | ChatExecutionTerminalDisposition.Rejected -> "Rejected"
        | ChatExecutionTerminalDisposition.Failed -> "Failed"

    let private effectPorts (effects: ResizeArray<string>) : ChatExecutionRecoveryActionPorts =
        let record effect =
            effects.Add effect
            Task.FromResult(()) :> Task

        { ReconcilePhysical =
            function
            | PhysicalReconciliationRequest.PersistProviderStarted _ ->
                record "ReconcilePhysical:PersistProviderStarted"
            | PhysicalReconciliationRequest.PersistProviderStartedAndTerminal(_, disposition) ->
                record $"ReconcilePhysical:PersistProviderStartedAndTerminal:{dispositionName disposition}"
            | PhysicalReconciliationRequest.ReleaseTerminalResource _ ->
                record "ReconcilePhysical:ReleaseTerminalResource"
          ResumePreProvider = fun _ -> record "ResumePreProvider"
          Finalize = fun request -> record $"Finalize:{dispositionName request.TerminalDisposition}"
          MarkManualIntervention = fun request -> record $"MarkManualIntervention:{request.InterventionReason}" }

    let private decisionName (decision: ChatExecutionRecoveryDecision) =
        match decision with
        | ChatExecutionRecoveryDecision.Ignore _ -> "Ignore"
        | ChatExecutionRecoveryDecision.ReconcilePhysical _ -> "ReconcilePhysical"
        | ChatExecutionRecoveryDecision.ResumePreProvider _ -> "ResumePreProvider"
        | ChatExecutionRecoveryDecision.Finalize _ -> "Finalize"
        | ChatExecutionRecoveryDecision.MarkManualIntervention _ -> "MarkManualIntervention"

    let private run (effects: ResizeArray<string>) (scenarios: string array) =
        task {
            let ports = effectPorts effects
            let decisions = ResizeArray<string>()

            for scenario in scenarios do
                let! decision = ChatExecutionRecoveryRuntime.recover ports (scenarioEvidence scenario)
                decisions.Add(decisionName decision)

            return
                box
                    {| decisions = decisions.ToArray()
                       effects = effects.ToArray() |}
        }

    let recoverScenarios (scenarios: string array) : Task<obj> = run (ResizeArray<string>()) scenarios

    let interpretFailurePolicy
        (failureLabel: string)
        (retryBudget: string)
        (commitment: string)
        (observation: string)
        : Task<obj> =
        task {
            let failure =
                match failureLabel with
                | "LocalInvariant" -> ExecutionFailure.LocalInvariant
                | "UserCancelled" -> ExecutionFailure.UserCancelled
                | "Superseded" -> ExecutionFailure.Superseded
                | "CapacityQueueFull" -> ExecutionFailure.CapacityQueueFull
                | "ProviderTransient" -> ExecutionFailure.ProviderTransient
                | "ProviderPermanent" -> ExecutionFailure.ProviderPermanent
                | "StreamInterruptedAfterFirstToken" -> ExecutionFailure.StreamInterruptedAfterFirstToken
                | value -> invalidArg "failure" $"unknown recovery proof failure '{value}'"

            let budget fieldName =
                function
                | "Available" -> ProviderRecoveryBudget.Available
                | "Exhausted" -> ProviderRecoveryBudget.Exhausted
                | value -> invalidArg fieldName $"unknown recovery proof budget '{value}'"

            let persistence =
                match commitment with
                | "NotCommitted" -> PersistenceCommitment.NotCommitted
                | "Committed" -> PersistenceCommitment.Committed
                | "Unknown" -> PersistenceCommitment.Unknown
                | value -> invalidArg "commitment" $"unknown recovery proof commitment '{value}'"

            let acceptedEvidence = accepted $"policy-{failureLabel}"
            let started = providerStarted $"provider-policy-{failureLabel}" acceptedEvidence
            let key = keyOf acceptedEvidence

            let policy = failurePolicy failure (budget "retryBudget" retryBudget) started

            let providerObservation =
                match observation with
                | "ExactAbsent" -> ProviderPhysicalObservation.ProviderAbsent key
                | "ExactTerminal" ->
                    ProviderPhysicalObservation.ProviderTerminal(started, ChatExecutionTerminalDisposition.Failed)
                | "LateOldExecution" ->
                    ProviderPhysicalObservation.ProviderTerminal(
                        providerStarted "provider-late-old-execution" acceptedEvidence,
                        ChatExecutionTerminalDisposition.Failed
                    )
                | value -> invalidArg "observation" $"unknown recovery proof observation '{value}'"

            let evidence =
                { ExecutionState = startedState started
                  ProviderObservation = providerObservation
                  ResourceObservation = PhysicalResourceObservation.ResourceAbsent key
                  PersistenceCommitment = persistence
                  FailureDecisionEvidence = policy }

            let effects = ResizeArray<string>()
            let! decision = ChatExecutionRecoveryRuntime.recover (effectPorts effects) evidence

            return
                box
                    {| decision = decisionName decision
                       effects = effects.ToArray() |}
        }

    let admissionPhaseSamples (cuts: string array) (commitment: string) (capacityOutcome: string) : Task<obj> =
        task {
            let persistence =
                match commitment with
                | "NotCommitted" -> PersistenceCommitment.NotCommitted
                | "Committed" -> PersistenceCommitment.Committed
                | "Unknown" -> PersistenceCommitment.Unknown
                | value -> invalidArg "commitment" $"unknown persistence commitment '{value}'"

            let outcomes = ResizeArray<obj>()

            for cut in cuts do
                if cut = "A" then
                    outcomes.Add(
                        box
                            {| cut = cut
                               decisions = [| "NoDurableExecution"; "NoDurableExecution" |]
                               effects = [||]
                               commitment = commitment
                               capacityOutcome = capacityOutcome |}
                    )
                else
                    let scenario =
                        match cut with
                        | "B"
                        | "C"
                        | "D"
                        | "E" -> "CrashAfterAcceptance"
                        | "F" -> "ProviderAlive"
                        | "G" -> "TerminalResourceHeld"
                        | "H"
                        | "I" -> "TerminalResourceReleased"
                        | value -> invalidArg "cut" $"unknown admission crash cut '{value}'"

                    let original = scenarioEvidence scenario

                    let resource =
                        match capacityOutcome with
                        | "Applied"
                        | "AlreadyApplied" -> original.ResourceObservation
                        | "Conflict"
                        | "StaleFence"
                        | "Unknown" -> PhysicalResourceObservation.ResourceUnknown original.ExecutionState.key
                        | value -> invalidArg "capacityOutcome" $"unknown capacity outcome '{value}'"

                    let evidence =
                        { original with
                            PersistenceCommitment = persistence
                            ResourceObservation = resource }

                    let effects = ResizeArray<string>()
                    let ports = effectPorts effects
                    let decisions = ResizeArray<string>()

                    for _ in 1..2 do
                        let! decision = ChatExecutionRecoveryRuntime.recover ports evidence
                        decisions.Add(decisionName decision)

                    outcomes.Add(
                        box
                            {| cut = cut
                               decisions = decisions.ToArray()
                               effects = effects.ToArray()
                               commitment = commitment
                               capacityOutcome = capacityOutcome |}
                    )

            return box {| scenarios = outcomes.ToArray() |}
        }
