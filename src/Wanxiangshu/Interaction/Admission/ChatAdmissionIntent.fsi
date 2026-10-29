namespace Wanxiangshu.Interaction.Admission

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Interaction.Authority

[<RequireQualifiedAccess>]
module ChatAdmissionIntent =
    type DecodedMessage =
        { SessionId: SessionId option
          PhysicalUserMessageId: PhysicalUserMessageId option
          ExplicitAgent: string option
          PromptKey: PromptKey option
          IsHostCompaction: bool
          IsHostSynthetic: bool
          Text: string option }

    type DurableSnapshot =
        { Authority: PromptAuthority.PromptAuthorityProjection option
          AcceptedExecutionEvidence: AcceptedChatExecutionEvidence option }

    [<RequireQualifiedAccess>]
    type NoManagedExecutionReason =
        | UnmanagedMessage
        | AlreadyAcceptedHostMessage of PromptAuthority.PromptOrigin

    [<RequireQualifiedAccess>]
    type Rejection =
        | ManagedIntentMissingSessionId
        | ManagedIntentMissingPhysicalUserMessageId
        | InvalidExplicitAgent of string
        | AcceptedParticipantConflict of ChatExecutionKey * acceptedAgent: string * requestedAgent: string
        | PromptKeyNotClaimed of PromptKey
        | AgentOwnerRootPromptNotClaimed of PromptKey * PromptAuthority.IdentitySeed
        | PromptClaimSessionMismatch of expectedSessionId: SessionId * claimedSessionId: SessionId
        | PromptClaimOriginNotAdmissible of PromptKey * PromptAuthority.PromptOrigin
        | UnknownOriginWhileActive

    type ExternalRootEvidence =
        { Key: ChatExecutionKey
          ExplicitAgent: string
          Origin: PromptAuthority.PromptOrigin
          IdentitySeed: PromptAuthority.IdentitySeed }

    type PendingPromptEvidence =
        { Key: ChatExecutionKey
          PromptKey: PromptKey
          Claim: PromptAuthority.PromptClaim
          Origin: PromptAuthority.PromptOrigin
          IdentitySeed: PromptAuthority.IdentitySeed }

    type ActiveHumanContinuationEvidence =
        { Key: ChatExecutionKey
          Origin: PromptAuthority.PromptOrigin
          Authority: PromptAuthority.AuthorityExecutionProfile }

    type HostInternalEvidence =
        { SessionId: SessionId option
          PhysicalUserMessageId: PhysicalUserMessageId option
          Origin: PromptAuthority.PromptOrigin }

    [<RequireQualifiedAccess>]
    type Decision =
        | NoManagedExecution of NoManagedExecutionReason
        | ExternalRootIntent of ExternalRootEvidence
        | ActiveHumanContinuationIntent of ActiveHumanContinuationEvidence
        | PendingPromptIntent of PendingPromptEvidence
        | AcceptedInputIntent of AcceptedChatExecutionEvidence
        | HostInternal of HostInternalEvidence
        | Reject of Rejection

    [<RequireQualifiedAccess>]
    type ManagedIntent =
        | ExternalRoot of ExternalRootEvidence
        | ActiveHumanContinuation of ActiveHumanContinuationEvidence
        | PendingPrompt of PendingPromptEvidence
        | AcceptedInput of AcceptedChatExecutionEvidence

    val tryManaged: decision: Decision -> ManagedIntent option
    val ofManaged: managed: ManagedIntent -> Decision
    val managedKey: managed: ManagedIntent -> ChatExecutionKey
    val isHostInternal: message: DecodedMessage -> bool

    val resolve: message: DecodedMessage -> snapshot: DurableSnapshot -> Decision
