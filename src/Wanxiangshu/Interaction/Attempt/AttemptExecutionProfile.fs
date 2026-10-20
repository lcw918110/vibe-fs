namespace Wanxiangshu.Interaction.Attempt

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt

/// One provider request (PROMPT-008).
///
/// Every field a request needs comes from this one immutable value. The
/// clause exists because the previous code assembled them separately from a
/// mutable session cache, the last user message, a Role map and the fallback
/// projection — four sources that can disagree, and did.
///
/// Construct ONLY through `buildAttemptExecutionProfile`. The architecture
/// gate rejects a record expression for this type outside its owning module,
/// because a hand-assembled profile is exactly the "temporary assembly" the
/// clause forbids.
type AttemptExecutionProfile =
    {
        Authority: PromptAuthority.AuthorityExecutionProfile
        PhysicalUserMessageId: PhysicalUserMessageId
        ProviderRun: ProviderRunIdentity
        Origin: PromptAuthority.PromptOrigin
        /// AGENT-001: Canonical role shares one system prompt, so this
        /// is derived from CanonicalRole alone.
        SystemPromptId: SystemPromptId
        /// AGENT-007 both layers read this same set: the Host-visible schema
        /// and the ToolRegistry execution gate. Two sources would let an
        /// unauthorised tool into the schema while the gate still refused it,
        /// or worse, the reverse.
        ToolCapabilitySet: Set<ToolPermission>
        /// PROMPT-008: which physical request this is.
        ///
        /// Real request semantics, not a flow stage (ARCH-001). It decides which
        /// projection is built, which instruction is sent, and — through CTX-007
        /// — whether success resets the provider failure budget.
        RequestKind: ProviderRequestKind
    }

    /// Convenience projections. Reading through the authority profile keeps
    /// participant and role visible for the Logical Run.
    member this.SessionId = this.Authority.SessionId
    member this.LogicalRunId = this.Authority.LogicalRunId
    member this.AuthorityRootUserMessageId = this.Authority.AuthorityRootUserMessageId
    member this.SelectedAgent = this.Authority.SelectedAgent
    member this.CanonicalRole = this.Authority.CanonicalRole

[<RequireQualifiedAccess>]
module InteractionAttempt =

    /// The ONLY way to build an AttemptExecutionProfile (PROMPT-008).
    ///
    /// Everything a provider request needs is derived here from two inputs: the
    /// authority profile fixed by the Authority Root, and the physical request identity.
    /// Nothing is passed in that could be derived, so a
    /// caller cannot supply a CanonicalRole that disagrees with the agent name,
    /// or a tool set that disagrees with the role.
    ///
    /// That is the whole clause. The previous code assembled these fields from a
    /// mutable session cache, the last user message, a Role map and the fallback
    /// projection — four sources that can disagree, and did (the provider request
    /// occasionally carried the wrong tool set).
    ///
    /// `requestKind` cannot be derived and so must be supplied. The
    let buildAttemptExecutionProfile
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        (origin: PromptAuthority.PromptOrigin)
        (requestKind: ProviderRequestKind)
        : AttemptExecutionProfile =
        { Authority = authority
          PhysicalUserMessageId = physicalUserMessageId
          ProviderRun = providerRun
          Origin = origin
          SystemPromptId = PromptAuthority.systemPromptIdFor authority.CanonicalRole
          ToolCapabilitySet = PromptAuthority.toolCapabilitiesFor authority.CanonicalRole requestKind
          RequestKind = requestKind }
