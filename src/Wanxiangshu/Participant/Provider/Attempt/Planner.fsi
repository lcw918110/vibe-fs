namespace Wanxiangshu.Participant.Provider.Attempt

open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Attempt
open Wanxiangshu.Interaction.Authority

type AttemptPlan = { Profile: AttemptExecutionProfile }

type PendingAttemptPlan =
    { Authority: PromptAuthority.AuthorityExecutionProfile
      PhysicalUserMessageId: PhysicalUserMessageId
      Origin: PromptAuthority.PromptOrigin
      RequestKind: ProviderRequestKind }

[<RequireQualifiedAccess>]
module AttemptPlanner =
    val ordinaryRequestKind: origin: PromptAuthority.PromptOrigin -> ProviderRequestKind

    val freezePreInference:
        authority: PromptAuthority.AuthorityExecutionProfile ->
        physicalUserMessageId: PhysicalUserMessageId ->
        origin: PromptAuthority.PromptOrigin ->
        requestKind: ProviderRequestKind ->
            PendingAttemptPlan

    val freezeOrdinary:
        accepted: AcceptedChatExecutionEvidence ->
        requestKind: ProviderRequestKind ->
            Result<PendingAttemptPlan, string>

    val bindProviderRun: providerRun: ProviderRunIdentity -> pending: PendingAttemptPlan -> AttemptPlan

    val plan:
        authority: PromptAuthority.AuthorityExecutionProfile ->
        physicalUserMessageId: PhysicalUserMessageId ->
        providerRun: ProviderRunIdentity ->
        origin: PromptAuthority.PromptOrigin ->
        requestKind: ProviderRequestKind ->
            AttemptPlan
