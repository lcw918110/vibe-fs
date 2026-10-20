namespace Wanxiangshu.Interaction.Attempt

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt

type AttemptExecutionProfile =
    { Authority: PromptAuthority.AuthorityExecutionProfile
      PhysicalUserMessageId: PhysicalUserMessageId
      ProviderRun: ProviderRunIdentity
      Origin: PromptAuthority.PromptOrigin
      SystemPromptId: SystemPromptId
      ToolCapabilitySet: Set<ToolPermission>
      RequestKind: ProviderRequestKind }

    member SessionId: SessionId
    member LogicalRunId: LogicalRunId
    member AuthorityRootUserMessageId: AuthorityRootUserMessageId
    member SelectedAgent: string
    member CanonicalRole: Role

[<RequireQualifiedAccess>]
module InteractionAttempt =
    val buildAttemptExecutionProfile:
        authority: PromptAuthority.AuthorityExecutionProfile ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
        origin: PromptAuthority.PromptOrigin ->
        requestKind: ProviderRequestKind ->
            AttemptExecutionProfile
