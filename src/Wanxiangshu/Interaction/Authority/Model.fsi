namespace Wanxiangshu.Interaction.Authority

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt

/// Typed projection of the `wanxiangli/dispatch` claim-ledger law (CUT-11).
[<RequireQualifiedAccess>]
module DispatchLaw =

    val nextClaimSequence: consumed: int option -> int

    [<RequireQualifiedAccess>]
    type Edit =
        | RecordReceipt
        | DropPending
        | LandOccasion
        | LandExact
        | RecordContinuation
        | ClearOccasion

    val settle: event: string -> pending: bool -> continuation: bool -> Edit list

    val answer: sent: string -> string * bool * string

    val reset:
        sessionId: string ->
        claims: (string * string option) list ->
        counters: (string * string * string option) list ->
            Set<string> * Set<string> * bool * bool

    val recover:
        promptKey: string ->
        hasReceipt: bool ->
        messages: (string * string option * string) list ->
            Result<string, bool>

[<RequireQualifiedAccess>]
module PromptAuthority =
    type RootAuthorityKind = PromptRootAuthorityKind
    type ContinuationKind = PromptContinuationKind
    type PromptOrigin = Wanxiangshu.Interaction.Authority.PromptOrigin
    type IdentitySeed = PromptIdentitySeed
    type IdentitySeedInput = PromptIdentitySeedInput
    type IdentitySeedValidationError = PromptIdentitySeedValidationError

    type AuthorityExecutionProfile =
        private
            { StoredSessionId: SessionId
              StoredLogicalRunId: LogicalRunId
              StoredAuthorityRootUserMessageId: AuthorityRootUserMessageId
              StoredAuthorityKind: RootAuthorityKind
              StoredIdentitySeed: IdentitySeed }

        member SessionId: SessionId
        member LogicalRunId: LogicalRunId
        member AuthorityRootUserMessageId: AuthorityRootUserMessageId
        member AuthorityKind: RootAuthorityKind
        member IdentitySeed: IdentitySeed
        member ParticipantIdentity: ParticipantIdentityEvidence
        member SelectedAgent: string
        member CanonicalRole: Role
        member Persona: string
        member PersonaCatalogVersion: int
        member PersonaOrigin: PersonaOrigin

    val withSessionId: sessionId: SessionId -> profile: AuthorityExecutionProfile -> AuthorityExecutionProfile

    val identitySeedParticipantIdentity: (IdentitySeed -> ParticipantIdentityEvidence)
    val identitySeedOwner: (IdentitySeed -> (SessionId * LogicalRunId * AuthorityRootUserMessageId) option)

    val issueInheritedIdentitySeed:
        canonicalChildName: string -> owner: AuthorityExecutionProfile -> Result<IdentitySeed, ParticipantIdentityError>

    val validateInheritedIdentitySeedAgainstActiveOwner:
        ownerOption: AuthorityExecutionProfile option ->
        seed: IdentitySeed ->
            Result<ParticipantIdentityEvidence, IdentitySeedValidationError>

    val validateInheritedIdentitySeed:
        owner: AuthorityExecutionProfile ->
        seed: IdentitySeed ->
            Result<ParticipantIdentityEvidence, IdentitySeedValidationError>

    val createAuthorityExecutionProfileFromSeed:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        authorityRootUserMessageId: AuthorityRootUserMessageId ->
        authorityKind: RootAuthorityKind ->
        identitySeed: IdentitySeed ->
            Result<AuthorityExecutionProfile, string>

    val createAuthorityExecutionProfile:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        authorityRootUserMessageId: AuthorityRootUserMessageId ->
        authorityKind: RootAuthorityKind ->
        participantIdentity: ParticipantIdentityEvidence ->
            Result<AuthorityExecutionProfile, string>

    type PromptClaim =
        { PromptKey: PromptKey
          SessionId: SessionId
          Origin: PromptOrigin
          LogicalRunId: LogicalRunId option
          AuthorityRootUserMessageId: AuthorityRootUserMessageId option
          IdentitySeed: IdentitySeed
          PayloadDigest: string
          Receipt: TransportReceipt option
          ClaimedAtRuntimeStartCount: int }

    type AcceptedDispatch =
        { PromptKey: PromptKey
          SessionId: SessionId
          Origin: PromptOrigin
          IdentitySeed: IdentitySeed
          PayloadDigest: string
          PhysicalUserMessageId: PhysicalUserMessageId }

    type ClaimSequenceCounter =
        { SessionId: SessionId
          LogicalRunId: LogicalRunId option
          Count: int }

    type UserQuestion =
        {
            Request: UserQuestionAskedPayload
            Answer: PhysicalUserMessageId option
            /// LC-015: 提问登记时已准入的所有物理输入（含引发提问的那条）。
            /// 这些输入不能在提问后作为"新答复"解锁提问。
            PriorPhysicalInputs: Set<PhysicalUserMessageId>
        }

    /// interaction-authority-018: the closure contract an accepted authority
    /// root commits to, kept beside the profile so a closure can be judged
    /// after the active run is gone.
    type AcceptedRootClosure =
        { RootKind: string
          ExpectedClosureKind: string
          OwnerLogicalRunId: string option
          AgentOwnerLife: string option
          AuthorityRootUserMessageId: AuthorityRootUserMessageId }

    /// interaction-authority-018: one recorded durable closure.
    type AuthorityClosureRecord =
        { RootKind: string
          ClosureWitness: string }

    /// interaction-authority-018: one durable source terminal.
    type AuthorityTerminalSource =
        { SourceKind: string
          OwnerLogicalRunId: string option
          ChildLogicalRunId: string option
          AttachmentKind: string option
          AssociationGeneration: string option
          DecisionOrTransactionId: string option
          FactId: string }

    type PromptAuthorityProjection =
        { LastAuthorityProfile: AuthorityExecutionProfile option
          ActiveLogicalRun: AuthorityExecutionProfile option
          PendingClaims: Map<PromptKey, PromptClaim>
          AcceptedDispatches: Map<string, AcceptedDispatch>
          PhysicalLandings: Map<PhysicalUserMessageId, AcceptedDispatch>
          AcceptedContinuationIds: Map<PhysicalUserMessageId, ContinuationKind>
          ClaimSequences: Map<string, ClaimSequenceCounter>
          UserQuestions: Map<ToolCallId, UserQuestion>
          AcceptedRootClosures: Map<LogicalRunId, AcceptedRootClosure>
          TerminalSources: Map<LogicalRunId, Map<string, AuthorityTerminalSource>>
          ClosedRuns: Map<LogicalRunId, AuthorityClosureRecord> }

    val empty: PromptAuthorityProjection
    val acceptedDispatchKey: sessionId: SessionId -> payloadDigest: string -> string
    val originLabel: origin: PromptOrigin -> string

    /// The delivery verb the product wants the host to use for this origin.
    val deliveryFor: origin: PromptOrigin -> string option
    val tryParseContinuationKind: value: string -> ContinuationKind option

    [<RequireQualifiedAccess>]
    type AgentNameRejection =
        | LegacyAgentName of string
        | UnknownManagedAgent of string
        | Malformed of string

    type ParsedAgentName = { Name: string; Role: Role }

    val parseAgentName: value: string -> Result<string * Role, string>

    val stableLogicalRunId:
        sha256: (string -> string) ->
        runtimeId: RuntimeId ->
        sessionId: SessionId ->
        authorityRoot: AuthorityRootUserMessageId ->
            LogicalRunId

    val RecoveryTailWindow: int

    val claimScopeDigest:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId option ->
        origin: PromptOrigin ->
        payloadDigest: string ->
            string

    val nextClaimSequence: scope: string -> projection: PromptAuthorityProjection -> int

    val derivePromptKey:
        sha256: (string -> string) ->
        sessionId: SessionId ->
        logicalRunId: LogicalRunId option ->
        authorityRoot: AuthorityRootUserMessageId option ->
        origin: PromptOrigin ->
        payloadDigest: string ->
        claimSequence: int ->
            PromptKey

    val repairPayloadDigest:
        requestId: RepairRequestId -> terminalProviderRun: ProviderRunIdentity -> repairKind: string -> string

    val gateNudgePayloadDigest: gateKind: string -> terminalProviderRun: ProviderRunIdentity -> string

    val gateNudgeAlreadyAdmitted:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        continuation: ContinuationKind ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        projection: PromptAuthorityProjection ->
            bool

    val gateNudgeAcceptedPhysical:
        sessionId: SessionId ->
        continuation: ContinuationKind ->
        gateKind: string ->
        terminalProviderRun: ProviderRunIdentity ->
        projection: PromptAuthorityProjection ->
            PhysicalUserMessageId option

    val repairAlreadyClaimed:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        requestId: RepairRequestId ->
        terminalProviderRun: ProviderRunIdentity ->
        repairKind: string ->
        projection: PromptAuthorityProjection ->
            bool

    val systemPromptIdFor: role: Role -> SystemPromptId
    val toolCapabilitiesFor: role: Role -> requestKind: ProviderRequestKind -> Set<ToolPermission>
