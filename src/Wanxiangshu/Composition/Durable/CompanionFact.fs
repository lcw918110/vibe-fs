namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion

/// Companion fact constructors — bridge from Companion-owned CompanionFactCases
/// into the Composition-owned AgentFact outer routing union.
module CompanionFact =
    let inline OpeningPromptCaptured payload =
        AgentFact.Companion(CompanionFactCases.OpeningPromptCaptured payload)

    let inline XTracePartAppended payload =
        AgentFact.Companion(CompanionFactCases.XTracePartAppended payload)

    let inline TerminalOutputCaptured payload =
        AgentFact.Companion(CompanionFactCases.TerminalOutputCaptured payload)

/// Context fact constructors — bridge from Companion-owned ContextFactCases
/// into the Composition-owned AgentFact outer routing union.
module ContextFact =
    let inline ViewCheckpointCommitted payload =
        AgentFact.Context(ContextFactCases.ViewCheckpointCommitted payload)

    let inline ContextReanchored payload =
        AgentFact.Context(ContextFactCases.ContextReanchored payload)

    let inline TenureReanchored payload =
        AgentFact.Context(ContextFactCases.TenureReanchored payload)
