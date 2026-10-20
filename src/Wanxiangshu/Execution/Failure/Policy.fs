namespace Wanxiangshu.Execution.Failure

open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module ExecutionFailurePolicy =

    let private requestKindIdentity =
        function
        | ProviderRequestKind.WorkMain -> "WorkMain"
        | ProviderRequestKind.InteractionRepair -> "InteractionRepair"

    let private decisionId (facts: ProviderRecoveryFacts) =
        [ LogicalRunId.value facts.LogicalRun
          ProviderRunIdentity.value facts.ProviderRun
          requestKindIdentity facts.RequestKind ]
        |> List.map (fun value -> $"{value.Length}:{value}")
        |> String.concat "|"
        |> ProviderRecoveryDecisionId.Create

    let private authorization (facts: ProviderRecoveryFacts) : ProviderRecoveryAuthorization =
        ProviderRecoveryAuthorization.Create(decisionId facts, facts.LogicalRun, facts.ProviderRun, facts.RequestKind)

    let private releaseCapacity lifecycle capacity =
        match lifecycle, capacity with
        | _, CapacityOwnership.NoCapacityFence -> CapacitySettlement.NoCapacitySettlement
        | DurableExecutionLifecycle.NoAcceptedFact, CapacityOwnership.OwnsExactFence _ownedFence ->
            CapacitySettlement.NoCapacitySettlement
        | _, CapacityOwnership.OwnsExactFence fence -> CapacitySettlement.ReleaseExactFence fence

    let private retainCapacity capacity =
        match capacity with
        | CapacityOwnership.NoCapacityFence -> CapacitySettlement.NoCapacitySettlement
        | CapacityOwnership.OwnsExactFence fence -> CapacitySettlement.RetainExactFence fence

    let private terminalResolution key disposition lifecycle =
        match lifecycle with
        | DurableExecutionLifecycle.NoAcceptedFact
        | DurableExecutionLifecycle.Terminal -> ExecutionFailureResolution.PreserveCurrentFact
        | DurableExecutionLifecycle.AcceptedBeforeProvider ->
            ExecutionFailureResolution.TerminalizeAcceptedPreProvider(key, disposition)
        | DurableExecutionLifecycle.ProviderStarted ->
            ExecutionFailureResolution.TerminalizeProviderStarted(key, disposition)

    let private retryProviderResolution (key: ChatExecutionKey) (facts: ProviderRecoveryFacts) =
        let licence = authorization facts

        match facts.Breaker, facts.RetryBudget with
        | ProviderBreakerState.Closed, ProviderRecoveryBudget.Available ->
            ExecutionFailureResolution.RetryFreshAttempt licence
        | _, _ ->
            terminalResolution key ChatExecutionTerminalDisposition.Failed DurableExecutionLifecycle.ProviderStarted

    let private providerResolution
        (lifecycle: DurableExecutionLifecycle)
        (key: ChatExecutionKey)
        (facts: ProviderRecoveryFacts)
        : ExecutionFailureResolution =
        match lifecycle with
        | DurableExecutionLifecycle.ProviderStarted -> retryProviderResolution key facts
        | _ -> terminalResolution key ChatExecutionTerminalDisposition.Failed lifecycle

    let private deriveBreaker =
        function
        | ExecutionFailure.ProviderTransient -> BreakerDecision.RecordProviderTransientFailure
        | ExecutionFailure.ProviderPermanent -> BreakerDecision.RecordProviderPermanentFailure
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.ProtocolRejection
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded
        | ExecutionFailure.CapacityQueueFull
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.StreamInterruptedAfterFirstToken
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite -> BreakerDecision.NoBreakerTransition

    let private deriveCapacitySettlement failure lifecycle capacity =
        match failure with
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite -> retainCapacity capacity
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.ProtocolRejection
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded
        | ExecutionFailure.CapacityQueueFull
        | ExecutionFailure.ProviderTransient
        | ExecutionFailure.ProviderPermanent
        | ExecutionFailure.StreamInterruptedAfterFirstToken
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed -> releaseCapacity lifecycle capacity

    let private deriveFatality =
        function
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite -> FatalityDecision.FatalAfterSettlement
        | ExecutionFailure.ProtocolRejection
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded
        | ExecutionFailure.CapacityQueueFull
        | ExecutionFailure.ProviderTransient
        | ExecutionFailure.ProviderPermanent
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.StreamInterruptedAfterFirstToken
        | ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted -> FatalityDecision.NoFatality

    [<Sealed>]
    type private ExecutionFailureDecisionBuilder
        (breaker: BreakerDecision, capacity: CapacitySettlement, fatality: FatalityDecision) =
        member inline _.Return(resolution: ExecutionFailureResolution) : ExecutionFailureDecision =
            { Resolution = resolution
              Breaker = breaker
              CapacitySettlement = capacity
              Fatality = fatality }

    let decideSupersession key lifecycle capacity : ExecutionFailureDecision =
        { Resolution = terminalResolution key ChatExecutionTerminalDisposition.Cancelled lifecycle
          Breaker = deriveBreaker ExecutionFailure.Superseded
          CapacitySettlement = deriveCapacitySettlement ExecutionFailure.Superseded lifecycle capacity
          Fatality = deriveFatality ExecutionFailure.Superseded }

    let decide (input: ExecutionFailureInput) : ExecutionFailureDecision =
        let breaker = deriveBreaker input.Failure
        let capacity = deriveCapacitySettlement input.Failure input.Lifecycle input.Capacity
        let fatality = deriveFatality input.Failure
        let failureDecision = ExecutionFailureDecisionBuilder(breaker, capacity, fatality)

        failureDecision {
            match input.Failure with
            | ExecutionFailure.LocalInvariant
            | ExecutionFailure.CapacityQueueFull
            | ExecutionFailure.StreamInterruptedAfterFirstToken ->
                return terminalResolution input.ExecutionKey ChatExecutionTerminalDisposition.Failed input.Lifecycle
            | ExecutionFailure.ProtocolRejection
            | ExecutionFailure.AuthorizationDenied ->
                return terminalResolution input.ExecutionKey ChatExecutionTerminalDisposition.Rejected input.Lifecycle
            | ExecutionFailure.UserCancelled
            | ExecutionFailure.Superseded ->
                return terminalResolution input.ExecutionKey ChatExecutionTerminalDisposition.Cancelled input.Lifecycle
            | ExecutionFailure.ProviderTransient
            | ExecutionFailure.ProviderPermanent ->
                return providerResolution input.Lifecycle input.ExecutionKey input.Provider
            | ExecutionFailure.AcceptanceUnknown
            | ExecutionFailure.PersistenceFailure PersistenceCommitment.Unknown ->
                return ExecutionFailureResolution.AwaitAcceptanceReconciliation input.ExecutionKey
            | ExecutionFailure.PersistenceFailure PersistenceCommitment.NotCommitted
            | ExecutionFailure.PersistenceFailure PersistenceCommitment.Committed
            | ExecutionFailure.PersistenceFailure PersistenceCommitment.NoNewWrite ->
                return ExecutionFailureResolution.PreserveCurrentFact
        }
