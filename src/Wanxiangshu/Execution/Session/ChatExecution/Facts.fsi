namespace Wanxiangshu.Execution.Session.ChatExecution

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt

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
    val participant: evidence: AcceptedChatExecutionEvidence -> string
    val canonicalRole: evidence: AcceptedChatExecutionEvidence -> Role
    val validate: evidence: AcceptedChatExecutionEvidence -> Result<unit, string>

type ProviderStartedEvidence =
    { Accepted: AcceptedChatExecutionEvidence
      ProviderRun: ProviderRunIdentity
      RequestKind: ProviderRequestKind }

[<RequireQualifiedAccess>]
module ProviderStartedEvidence =
    val validate: evidence: ProviderStartedEvidence -> Result<unit, string>

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
