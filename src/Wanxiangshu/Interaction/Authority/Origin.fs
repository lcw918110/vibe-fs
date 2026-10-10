namespace Wanxiangshu.Interaction.Authority

type PromptRootAuthorityKind =
    | HumanRoot
    | AgentOwnerRoot

/// PROMPT-003. Every one of these extends an existing Logical Run and may
/// not change the execution profile.
///
/// There is deliberately no compaction continuation: HOST-006 closes Host
/// compaction globally, so a compaction-driven continuation has no origin
/// that could produce it.
type PromptContinuationKind =
    | InteractionRepair
    | JoinGuard
    | ManagerGuard
    | BusyAgentNudge
    /// A later external user message admitted into the same active HumanRoot.
    | HumanMessage
    /// A fresh assignment dispatched to an already-attached managed delegate.
    /// It extends that delegate's exact active owner-root run without rebinding identity.
    | ManagedDelegationAssignment
    | ProviderRetryAttempt
    /// degeneration-guard-011: same-run continuation owned by degeneration-guard after its own interrupt.
    | DegenerationGuard
    /// Same-run Fission delivery: predecessor work or a pre-Fission shared
    /// external completion enters a lane only at a safe provider boundary.
    | FissionHandoff
    /// ATTENTION-005: a natural terminal with pending DeferredWork presents
    /// that work once through a same-run continuation, then consumes it.
    | DeferredWorkPresentation

type PromptOrigin =
    | AuthorityRoot of PromptRootAuthorityKind
    | Continuation of PromptContinuationKind
    | HostInternal
    | UnknownOrigin
