namespace Wanxiangshu.Participant.Provider.Attempt

open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Interaction.Attempt
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// PROMPT-008 / CTX-006 / CTX-010: everything one provider request needs, decided once.
///
/// This is the single call site of `buildAttemptExecutionProfile`. Before it existed
/// the constructor had none at all — every send path read `ActiveLogicalRun` and
/// assembled its own fields, which is exactly what PROMPT-008 forbids, and the
/// `single-constructor` gate could not see it because a function nobody calls has
/// nothing bypassing it.
///
/// The plan bundles the profile with the prefix decision because the two are one
/// choice: CTX-010 makes the probe part of the immutable profile, so a caller that
/// received them separately could send a request whose projection
/// differed from its message list.
type AttemptPlan = { Profile: AttemptExecutionProfile }

/// Pre-inference half of an AttemptPlan.
///
/// The purpose plan is frozen before binding the Host-created assistant message;
/// ProviderRunIdentity therefore cannot be an input to this constructor. The
/// remaining decision is nevertheless immutable:
/// authority/physical request identity/request kind/prefix choice are all
/// frozen here, then bound exactly once from the Host-created assistant message
/// present at the transform admission boundary.
type PendingAttemptPlan =
    { Authority: PromptAuthority.AuthorityExecutionProfile
      PhysicalUserMessageId: PhysicalUserMessageId
      Origin: PromptAuthority.PromptOrigin
      RequestKind: ProviderRequestKind }

[<RequireQualifiedAccess>]
module AttemptPlanner =

    let ordinaryRequestKind (origin: PromptAuthority.PromptOrigin) =
        match origin with
        | PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.InteractionRepair ->
            ProviderRequestKind.InteractionRepair
        | _ -> ProviderRequestKind.WorkMain

    /// Freeze every provider-request decision available before inference. The
    /// assistant run is deliberately absent: the Host has not created it yet.
    let freezePreInference
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (physicalUserMessageId: PhysicalUserMessageId)
        (origin: PromptAuthority.PromptOrigin)
        (requestKind: ProviderRequestKind)
        : PendingAttemptPlan =
        { Authority = authority
          PhysicalUserMessageId = physicalUserMessageId
          Origin = origin
          RequestKind = requestKind }

    let freezeOrdinary
        (accepted: AcceptedChatExecutionEvidence)
        (requestKind: ProviderRequestKind)
        : Result<PendingAttemptPlan, string> =
        PromptAuthority.createAuthorityExecutionProfileFromSeed
            accepted.SessionId
            accepted.LogicalRunId
            accepted.AuthorityRootUserMessageId
            accepted.AuthorityKind
            accepted.IdentitySeed
        |> Result.map (fun authority ->
            freezePreInference authority accepted.PhysicalUserMessageId accepted.Origin requestKind)

    /// Binds the immutable pre-inference decision to the
    /// exact assistant run for the already-frozen physical request.
    let bindProviderRun (providerRun: ProviderRunIdentity) (pending: PendingAttemptPlan) : AttemptPlan =
        { Profile =
            InteractionAttempt.buildAttemptExecutionProfile
                pending.Authority
                pending.PhysicalUserMessageId
                providerRun
                pending.Origin
                pending.RequestKind }

    /// Build the profile for one attempt. Recovery does not select a second projection.
    let plan
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        (origin: PromptAuthority.PromptOrigin)
        (requestKind: ProviderRequestKind)
        : AttemptPlan =
        freezePreInference authority physicalUserMessageId origin requestKind
        |> bindProviderRun providerRun
