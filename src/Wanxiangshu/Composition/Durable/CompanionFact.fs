namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion

/// Companion fact constructors — bridge from Companion-owned CompanionFactCases
/// into the Composition-owned AgentFact outer routing union.
module CompanionFact =
    let inline CompanionBloggerLinked payload =
        AgentFact.Companion(CompanionFactCases.CompanionBloggerLinked payload)

    let inline CompanionBloggerClosed payload =
        AgentFact.Companion(CompanionFactCases.CompanionBloggerClosed payload)

    let inline OpeningPromptCaptured payload =
        AgentFact.Companion(CompanionFactCases.OpeningPromptCaptured payload)

    let inline XTracePartAppended payload =
        AgentFact.Companion(CompanionFactCases.XTracePartAppended payload)

    let inline TerminalOutputCaptured payload =
        AgentFact.Companion(CompanionFactCases.TerminalOutputCaptured payload)

/// Context fact constructors — bridge from Companion-owned ContextFactCases
/// into the Composition-owned AgentFact outer routing union.
module ContextFact =
    let inline BlogObservationCommitted payload =
        AgentFact.Context(ContextFactCases.BlogObservationCommitted payload)

    let inline BlogObservationsSquashed payload =
        AgentFact.Context(ContextFactCases.BlogObservationsSquashed payload)

    let inline BloggerRequestMaterialized payload =
        AgentFact.Context(ContextFactCases.BloggerRequestMaterialized payload)

    let inline BloggerRequestAbandoned payload =
        AgentFact.Context(ContextFactCases.BloggerRequestAbandoned payload)

    let inline TodoCheckpointCommitted payload =
        AgentFact.Context(ContextFactCases.TodoCheckpointCommitted payload)

    let inline PrefixRebaseCommitted payload =
        AgentFact.Context(ContextFactCases.PrefixRebaseCommitted payload)

    let inline ContextReanchored payload =
        AgentFact.Context(ContextFactCases.ContextReanchored payload)

    let inline TenureReanchored payload =
        AgentFact.Context(ContextFactCases.TenureReanchored payload)
