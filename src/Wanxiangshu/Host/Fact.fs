namespace Wanxiangshu.Host

open Wanxiangshu.Composition.Durable.Fact

module HostFact =
    let inline PairProgrammingGuidelineAnchored payload =
        AgentFact.Host(HostFactCases.PairProgrammingGuidelineAnchored payload)



    let inline SessionStartedAtBound payload =
        AgentFact.Host(HostFactCases.SessionStartedAtBound payload)
