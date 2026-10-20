namespace Wanxiangshu.Execution.Session.ChatExecution

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona

[<StructuralEquality; StructuralComparison>]
type ChatExecutionKey =
    { SessionId: SessionId
      PhysicalUserMessageId: PhysicalUserMessageId }

type AcceptedChatExecutionEvidence =
    { SessionId: SessionId
      LogicalRunId: LogicalRunId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      AuthorityKind: PromptRootAuthorityKind
      IdentitySeed: PromptIdentitySeed
      PhysicalUserMessageId: PhysicalUserMessageId
      Origin: PromptOrigin }

[<RequireQualifiedAccess>]
module AcceptedChatExecutionEvidence =

    let participant (evidence: AcceptedChatExecutionEvidence) : string =
        let identity = PromptIdentitySeed.participantIdentity evidence.IdentitySeed
        ParticipantIdentity.selectedAgent identity

    let canonicalRole (evidence: AcceptedChatExecutionEvidence) : Role =
        let identity = PromptIdentitySeed.participantIdentity evidence.IdentitySeed

        match ParticipantIdentity.role identity with
        | Some role -> role
        | None -> invalidOp "Attempt execution evidence requires a public participant role"

    let private validatePublicRole identity =
        if ParticipantIdentity.role identity |> Option.isSome then
            Ok()
        else
            Error "Attempt execution evidence requires a public participant role"

    let private validateAuthorityIdentity
        (authorityKind: PromptRootAuthorityKind)
        (identitySeed: PromptIdentitySeed)
        (identity: ParticipantIdentityEvidence)
        =
        match authorityKind, identitySeed, ParticipantIdentity.origin identity with
        | PromptRootAuthorityKind.HumanRoot, RootSelection _, PersonaOrigin.ResolvedAtRoot
        | PromptRootAuthorityKind.AgentOwnerRoot, InheritedFromOwner _, PersonaOrigin.InheritedFromOwner -> Ok()
        | PromptRootAuthorityKind.HumanRoot, InheritedFromOwner _, _ ->
            Error "HumanRoot attempt execution evidence requires a root-selection identity seed"
        | PromptRootAuthorityKind.AgentOwnerRoot, RootSelection _, _ ->
            Error "AgentOwnerRoot attempt execution evidence requires an inherited owner identity seed"
        | PromptRootAuthorityKind.HumanRoot, RootSelection _, _ ->
            Error "HumanRoot attempt execution evidence requires root-resolved participant identity"
        | PromptRootAuthorityKind.AgentOwnerRoot, InheritedFromOwner _, _ ->
            Error "AgentOwnerRoot attempt execution evidence requires owner-inherited participant identity"

    let validate (evidence: AcceptedChatExecutionEvidence) : Result<unit, string> =
        let identity = PromptIdentitySeed.participantIdentity evidence.IdentitySeed

        validatePublicRole identity
        |> Result.bind (fun () -> validateAuthorityIdentity evidence.AuthorityKind evidence.IdentitySeed identity)

type ProviderStartedEvidence =
    { Accepted: AcceptedChatExecutionEvidence
      ProviderRun: ProviderRunIdentity
      RequestKind: ProviderRequestKind }

[<RequireQualifiedAccess>]
module ProviderStartedEvidence =

    let validate (evidence: ProviderStartedEvidence) =
        AcceptedChatExecutionEvidence.validate evidence.Accepted


[<RequireQualifiedAccess>]
type ChatExecutionTerminalEvidence =
    | PreProvider of AcceptedChatExecutionEvidence
    | AfterProviderStart of ProviderStartedEvidence

[<RequireQualifiedAccess>]
type ChatExecutionTerminalDisposition =
    | Completed
    | Cancelled
    | Rejected
    | Failed

type ChatExecutionFactCases =
    | Accepted of
        {| SchemaVersion: int
           Key: ChatExecutionKey
           Evidence: AcceptedChatExecutionEvidence |}
    | ProviderStarted of
        {| SchemaVersion: int
           Key: ChatExecutionKey
           Evidence: ProviderStartedEvidence |}
    | Terminal of
        {| SchemaVersion: int
           Key: ChatExecutionKey
           Evidence: ChatExecutionTerminalEvidence
           Disposition: ChatExecutionTerminalDisposition |}
