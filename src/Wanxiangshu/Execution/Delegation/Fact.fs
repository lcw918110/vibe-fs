namespace Wanxiangshu.Execution.Delegation

open Wanxiangshu.Composition.Durable.Fact

/// delegation-029: Durable composition is the only place that wraps delegation fact cases into AgentFact.
/// Execution fact constructors — bridge from Delegation-owned ExecutionFactCases
/// into the Composition-owned AgentFact outer routing union.
module ExecutionFact =
    let inline HandleWorkCompleted payload =
        AgentFact.Execution(ExecutionFactCases.HandleWorkCompleted payload)

    let inline HandleWorkConsumed payload =
        AgentFact.Execution(ExecutionFactCases.HandleWorkConsumed payload)

    let inline HandleWorkAbandoned payload =
        AgentFact.Execution(ExecutionFactCases.HandleWorkAbandoned payload)

    let inline ChildWorkVoided payload =
        AgentFact.Execution(ExecutionFactCases.ChildWorkVoided payload)

    let inline HandleLinked payload =
        AgentFact.Execution(ExecutionFactCases.HandleLinked payload)

    let inline HandleCompleted payload =
        AgentFact.Execution(ExecutionFactCases.HandleCompleted payload)

    let inline HandleRetired payload =
        AgentFact.Execution(ExecutionFactCases.HandleRetired payload)

    let inline HandleAbandoned payload =
        AgentFact.Execution(ExecutionFactCases.HandleAbandoned payload)

    let inline ChildRunVoided payload =
        AgentFact.Execution(ExecutionFactCases.ChildRunVoided payload)

    let inline HandleFalseCompletionRejected payload =
        AgentFact.Execution(ExecutionFactCases.HandleFalseCompletionRejected payload)

    let inline HandleFalseTerminalReported payload =
        AgentFact.Execution(ExecutionFactCases.HandleFalseTerminalReported payload)

    let inline ParentJoinCorrectionRequested payload =
        AgentFact.Execution(ExecutionFactCases.ParentJoinCorrectionRequested payload)

    let inline HostTurnObserved payload =
        AgentFact.Execution(ExecutionFactCases.HostTurnObserved payload)

/// Delegation fact constructors — bridge from Delegation-owned DelegationFactCases
/// into the Composition-owned AgentFact outer routing union.
module DelegationFact =
    let inline DelegatedToolEstimateReplaced payload =
        AgentFact.Delegation(DelegationFactCases.DelegatedToolEstimateReplaced payload)

    let inline DelegatedToolCallObserved payload =
        AgentFact.Delegation(DelegationFactCases.DelegatedToolCallObserved payload)

    let inline DelegationHandoffCompleted payload =
        AgentFact.Delegation(DelegationFactCases.DelegationHandoffCompleted payload)
