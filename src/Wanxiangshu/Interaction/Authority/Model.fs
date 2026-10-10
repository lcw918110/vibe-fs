namespace Wanxiangshu.Interaction.Authority

open System
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module PromptAuthority =

    type RootAuthorityKind = PromptRootAuthorityKind
    type ContinuationKind = PromptContinuationKind
    type PromptOrigin = Wanxiangshu.Interaction.Authority.PromptOrigin

    type IdentitySeed = PromptIdentitySeed
    type IdentitySeedInput = PromptIdentitySeedInput
    type IdentitySeedValidationError = PromptIdentitySeedValidationError

    /// What an Authority Root fixes for the whole Logical Run (PROMPT-002).
    ///
    /// SelectedAgent and CanonicalRole are fixed for the Logical Run.
    /// Fixed participant identity and fresh physical requests are strictly bound.
    ///
    /// PROMPT-002 also forbids a model id: there is deliberately no field for
    /// one, so "Authority Root overrides the model" is not expressible.
    type AuthorityExecutionProfile =
        private
            { StoredSessionId: SessionId
              StoredLogicalRunId: LogicalRunId
              StoredAuthorityRootUserMessageId: AuthorityRootUserMessageId
              StoredAuthorityKind: RootAuthorityKind
              StoredIdentitySeed: IdentitySeed }

        member this.SessionId = this.StoredSessionId
        member this.LogicalRunId = this.StoredLogicalRunId
        member this.AuthorityRootUserMessageId = this.StoredAuthorityRootUserMessageId
        member this.AuthorityKind = this.StoredAuthorityKind
        member this.IdentitySeed = this.StoredIdentitySeed

        member this.ParticipantIdentity =
            match this.StoredIdentitySeed with
            | RootSelection identity -> identity
            | InheritedFromOwner witness -> witness.ParticipantIdentity

        member this.SelectedAgent = ParticipantIdentity.selectedAgent this.ParticipantIdentity

        member this.CanonicalRole =
            match ParticipantIdentity.role this.ParticipantIdentity with
            | Some role -> role
            | None -> invalidOp "public authority participant identity cannot be Bookkeeper"

        member this.Persona = ParticipantIdentity.persona this.ParticipantIdentity

        member this.PersonaCatalogVersion =
            ParticipantIdentity.personaCatalogVersion this.ParticipantIdentity

        member this.PersonaOrigin = ParticipantIdentity.origin this.ParticipantIdentity

    let identitySeedParticipantIdentity = PromptIdentitySeed.participantIdentity

    let identitySeedOwner = PromptIdentitySeed.owner

    let issueInheritedIdentitySeed
        (canonicalChildName: string)
        (owner: AuthorityExecutionProfile)
        : Result<IdentitySeed, ParticipantIdentityError> =
        PromptIdentitySeed.inheritFromOwner
            canonicalChildName
            owner.SessionId
            owner.LogicalRunId
            owner.AuthorityRootUserMessageId
            owner.ParticipantIdentity

    let validateInheritedIdentitySeedAgainstActiveOwner
        (ownerOption: AuthorityExecutionProfile option)
        (seed: IdentitySeed)
        : Result<ParticipantIdentityEvidence, IdentitySeedValidationError> =
        match ownerOption, seed with
        | _, RootSelection _ -> Error ExpectedInheritedFromOwner
        | None, InheritedFromOwner witness -> Error(OwnerAuthorityNotActive witness.OwnerSessionId)
        | Some owner, InheritedFromOwner witness when witness.OwnerSessionId <> owner.SessionId ->
            Error(OwnerSessionIdMismatch(owner.SessionId, witness.OwnerSessionId))
        | Some owner, InheritedFromOwner witness when witness.OwnerLogicalRunId <> owner.LogicalRunId ->
            Error(OwnerLogicalRunIdMismatch(owner.LogicalRunId, witness.OwnerLogicalRunId))
        | Some owner, InheritedFromOwner witness when
            witness.OwnerAuthorityRootUserMessageId <> owner.AuthorityRootUserMessageId
            ->
            Error(
                OwnerAuthorityRootUserMessageIdMismatch(
                    owner.AuthorityRootUserMessageId,
                    witness.OwnerAuthorityRootUserMessageId
                )
            )
        | Some owner, InheritedFromOwner witness ->
            ParticipantIdentity.rehydrate
                (Some owner.ParticipantIdentity)
                (ParticipantIdentity.toInput witness.ParticipantIdentity)
            |> Result.mapError InvalidInheritedParticipantIdentity
            |> Result.map (fun _ -> witness.ParticipantIdentity)

    let validateInheritedIdentitySeed owner seed =
        validateInheritedIdentitySeedAgainstActiveOwner (Some owner) seed

    let createAuthorityExecutionProfileFromSeed
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (authorityRootUserMessageId: AuthorityRootUserMessageId)
        (authorityKind: RootAuthorityKind)
        (identitySeed: IdentitySeed)
        : Result<AuthorityExecutionProfile, string> =
        let participantIdentity = identitySeedParticipantIdentity identitySeed

        match authorityKind, identitySeed, ParticipantIdentity.role participantIdentity with
        | _, _, None -> Error "public authority participant identity cannot be Bookkeeper"
        | RootAuthorityKind.HumanRoot, RootSelection _, Some _ when
            ParticipantIdentity.origin participantIdentity = PersonaOrigin.ResolvedAtRoot
            ->
            Ok
                { StoredSessionId = sessionId
                  StoredLogicalRunId = logicalRunId
                  StoredAuthorityRootUserMessageId = authorityRootUserMessageId
                  StoredAuthorityKind = authorityKind
                  StoredIdentitySeed = identitySeed }
        | RootAuthorityKind.AgentOwnerRoot, InheritedFromOwner _, Some _ ->
            Ok
                { StoredSessionId = sessionId
                  StoredLogicalRunId = logicalRunId
                  StoredAuthorityRootUserMessageId = authorityRootUserMessageId
                  StoredAuthorityKind = authorityKind
                  StoredIdentitySeed = identitySeed }
        | RootAuthorityKind.HumanRoot, _, Some _ -> Error "HumanRoot requires a root-selection identity seed"
        | RootAuthorityKind.AgentOwnerRoot, _, Some _ ->
            Error "AgentOwnerRoot requires an inherited owner identity seed"

    let createAuthorityExecutionProfile
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (authorityRootUserMessageId: AuthorityRootUserMessageId)
        (authorityKind: RootAuthorityKind)
        (participantIdentity: ParticipantIdentityEvidence)
        : Result<AuthorityExecutionProfile, string> =
        createAuthorityExecutionProfileFromSeed
            sessionId
            logicalRunId
            authorityRootUserMessageId
            authorityKind
            (RootSelection participantIdentity)

    /// A dispatched prompt before the Host has confirmed anything (PROMPT-005
    /// `Claimed`).
    ///
    /// `LogicalRunId` is optional because the two origins differ in kind:
    /// a Continuation extends a run that already exists, while an Authority Root
    /// *creates* the run — and its id derives from the physical message that
    /// does not exist yet at claim time. An empty-string sentinel would make
    /// "no run yet" and "run with a blank id" the same value.
    /// DSL-state-combination: domain — optional logical/authority/agent/receipt
    /// facets describe one dispatch claim and its evidence, not a continuation
    /// program.
    type PromptClaim =
        {
            PromptKey: PromptKey
            SessionId: SessionId
            Origin: PromptOrigin
            LogicalRunId: LogicalRunId option
            AuthorityRootUserMessageId: AuthorityRootUserMessageId option
            IdentitySeed: IdentitySeed
            /// PROMPT-005 requires the payload digest at claim time so recovery can
            /// tell two dispatches of the same shape apart.
            PayloadDigest: string
            /// PROMPT-005 `Submitted`: the transport receipt, once the Host call has
            /// returned. `None` while the claim is still only `Claimed`.
            ///
            /// PROMPT-011's recovery needs the two states distinguishable: step 4 (a
            /// receipt exists but no physical message was found) and step 5 (not even
            /// a receipt) both stay pending, but they are different diagnoses for an
            /// operator — one means the Host accepted something we cannot locate.
            Receipt: TransportReceipt option
            /// Historical workspace `RuntimeStartCount` observed when this claim
            /// was registered. Retained in the durable shape for audit and exact
            /// replay only; restart count no longer authorizes recovery or abandon.
            ClaimedAtRuntimeStartCount: int
        }

    /// PROMPT-005 typed evidence that one logical dispatch physically landed.
    ///
    /// Folded by the single integrator when `PhysicalAccepted` resolves a claim,
    /// so business layers (process-review assignment reentry) decide resend
    /// from projection evidence instead of scanning the Journal.
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

    /// DSL-state-combination: domain — the two optional authority profiles
    /// represent durable before/active evidence; they are not stage latches.
    type PromptAuthorityProjection =
        {
            LastAuthorityProfile: AuthorityExecutionProfile option
            ActiveLogicalRun: AuthorityExecutionProfile option
            PendingClaims: Map<PromptKey, PromptClaim>
            /// PROMPT-005 accepted dispatch evidence, keyed by
            /// session + payload digest. `Pending` on a claim means the outcome
            /// is undetermined; an entry here means the payload physically
            /// landed. Keyed — never a session scan (PERSIST-008).
            ///
            /// This is the occasion view: it answers "has this payload landed",
            /// so a later landing of the same payload replaces the earlier one.
            AcceptedDispatches: Map<string, AcceptedDispatch>
            /// dispatch-protocol-006: the exact landing of every physical user
            /// message. Two acts with the same payload land on two physical
            /// messages and stay two entries; neither a later same-payload
            /// landing nor an abandoned claim can erase an earlier landing.
            PhysicalLandings: Map<PhysicalUserMessageId, AcceptedDispatch>
            /// Physical message id -> the continuation kind it was accepted as.
            ///
            /// PROMPT-003 and PROMPT-009 only: this answers "was this message a
            /// continuation, and of what kind". REVIEW-003 forbids it as review
            /// confirmation evidence — a continuation being accepted says nothing
            /// about whether a model consumed the challenge.
            AcceptedContinuationIds: Map<PhysicalUserMessageId, ContinuationKind>
            /// PROMPT-011 ClaimSequence, keyed by claim scope digest.
            ///
            /// Counts claims ever registered for one
            /// (LogicalRunId, Origin, PayloadDigest) triple, so "the same Guard
            /// fired twice against the same tree" yields two distinct PromptKeys
            /// instead of one that looks like a duplicate.
            ///
            /// Run closure removes only counters owned by that run. Unlanded
            /// Root scopes belong to the session and keep their consumed count.
            ClaimSequences: Map<string, ClaimSequenceCounter>
        }

    let empty: PromptAuthorityProjection =
        { LastAuthorityProfile = None
          ActiveLogicalRun = None
          PendingClaims = Map.empty
          AcceptedDispatches = Map.empty
          PhysicalLandings = Map.empty
          AcceptedContinuationIds = Map.empty
          ClaimSequences = Map.empty }

    /// Key of one logical dispatch's landing evidence: the session plus the
    /// payload digest both send paths derive identically (`sha256 text`).
    let acceptedDispatchKey (sessionId: SessionId) (payloadDigest: string) =
        SessionId.value sessionId + "\x1f" + payloadDigest

    let originLabel (origin: PromptOrigin) =
        match origin with
        | AuthorityRoot HumanRoot -> "HumanRoot"
        | AuthorityRoot AgentOwnerRoot -> "AgentOwnerRoot"
        | Continuation InteractionRepair -> "InteractionRepair"
        | Continuation JoinGuard -> "JoinGuard"
        | Continuation ManagerGuard -> "ManagerGuard"
        | Continuation BusyAgentNudge -> "BusyAgentNudge"
        | Continuation HumanMessage -> "HumanMessage"
        | Continuation ManagedDelegationAssignment -> "ManagedDelegationAssignment"
        | Continuation ProviderRetryAttempt -> "ProviderRetryAttempt"
        | Continuation DegenerationGuard -> "DegenerationGuard"
        | Continuation FissionHandoff -> "FissionHandoff"
        | Continuation DeferredWorkPresentation -> "DeferredWorkPresentation"
        | HostInternal -> "HostInternal"
        | UnknownOrigin -> "UnknownOrigin"

    let tryParseContinuationKind (value: string) =
        match value with
        | "InteractionRepair" -> Some InteractionRepair
        | "JoinGuard" -> Some JoinGuard
        | "ManagerGuard" -> Some ManagerGuard
        | "BusyAgentNudge" -> Some BusyAgentNudge
        | "HumanMessage" -> Some HumanMessage
        | "ManagedDelegationAssignment" -> Some ManagedDelegationAssignment
        | "ProviderRetryAttempt" -> Some ProviderRetryAttempt
        | "DegenerationGuard" -> Some DegenerationGuard
        | "FissionHandoff" -> Some FissionHandoff
        | "DeferredWorkPresentation" -> Some DeferredWorkPresentation
        | _ -> None

    /// Why a managed agent name was refused.
    ///
    /// Typed rather than a message, because the three cases mean different things to
    /// a caller: a legacy name is a migration error the operator must fix, an unknown
    /// name may be a typo worth a suggestion, and malformed means the shape itself is
    /// wrong. A single string forced every consumer that wanted to distinguish them
    /// to match on prose.
    [<RequireQualifiedAccess>]
    type AgentNameRejection =
        | LegacyAgentName of string
        | UnknownManagedAgent of string
        | Malformed of string

    /// A parsed canonical managed agent name.
    type ParsedAgentName = { Name: string; Role: Role }

    let private parseStructuredAgentName (trimmed: string) : Result<ParsedAgentName, AgentNameRejection> =
        match Roles.tryParseRole (trimmed.ToLowerInvariant()) with
        | Some role when trimmed = Roles.roleLabel role -> Ok { Name = trimmed; Role = role }
        | Some _ -> Error(AgentNameRejection.Malformed trimmed)
        | None -> Error(AgentNameRejection.UnknownManagedAgent trimmed)

    let private parseNonBlankAgentName (trimmed: string) : Result<ParsedAgentName, AgentNameRejection> =
        if ManagedAgentCatalog.isLegacyAgentName (trimmed.ToLowerInvariant()) then
            Error(AgentNameRejection.LegacyAgentName trimmed)
        elif trimmed.Contains("-") then
            Error(AgentNameRejection.Malformed trimmed)
        else
            parseStructuredAgentName trimmed

    let parseAgentNameTyped (value: string) : Result<ParsedAgentName, AgentNameRejection> =
        if String.IsNullOrWhiteSpace value then
            Error(AgentNameRejection.Malformed value)
        else
            parseNonBlankAgentName (value.Trim())

    let parseAgentName (value: string) : Result<string * Role, string> =
        parseAgentNameTyped value
        |> Result.map (fun parsed -> parsed.Name, parsed.Role)
        |> Result.mapError (fun rejection ->
            match rejection with
            | AgentNameRejection.LegacyAgentName name -> ManagedAgentCatalog.formatLegacyNameNotSupported name
            | AgentNameRejection.UnknownManagedAgent name -> sprintf "Unknown managed agent '%s'." name
            | AgentNameRejection.Malformed name -> sprintf "Malformed managed agent name '%s'." name)

    /// Deterministic Logical Run id. PROMPT-011 requires stability across
    /// restarts, so it is derived from durable identities and never generated.
    let stableLogicalRunId
        (sha256: string -> string)
        (runtimeId: RuntimeId)
        (sessionId: SessionId)
        (authorityRoot: AuthorityRootUserMessageId)
        : LogicalRunId =
        LogicalRunId.create (
            sha256 (
                String.Join(
                    "\n",
                    [| RuntimeId.value runtimeId
                       SessionId.value sessionId
                       AuthorityRootUserMessageId.value authorityRoot |]
                )
            )
        )


    // ── PromptKey derivation (PROMPT-011) ───────────────────────────────────
    //
    // The key must be a STABLE idempotency anchor: after a crash, recovery looks
    // for it in Host metadata to decide whether a dispatch physically landed.
    // A random GUID cannot serve that purpose — a restarted process would derive
    // a different key for the same logical dispatch and conclude nothing was
    // sent.

    /// Absent identities participate in the digest as an explicit marker rather
    /// than an empty string, so "no Logical Run yet" cannot collide with "a run
    /// whose id happens to be blank".
    let private digestField (value: string option) =
        match value with
        | Some text -> text
        | None -> "\u0000absent"

    /// PROMPT-011 recovery bounds.
    ///
    /// The tail window exists because a Host session's history is unbounded while
    /// a pending claim is minutes old at most. Scanning further would not find a
    /// message that is genuinely absent, and PROMPT-011 forbids resending either
    /// way — so a wider window buys nothing and costs an unbounded read.
    ///
    /// Plain `let`, not `[<Literal>]`: Fable inlines a literal and emits no export,
    /// leaving the clause value unassertable from a layer 1 test.
    let RecoveryTailWindow = 50

    /// The scope a ClaimSequence counts within.
    ///
    /// PROMPT-011 names (SessionId, LogicalRunId, Origin, PayloadDigest). Two
    /// dispatches agreeing on all four are the same logical act repeated, which
    /// is exactly when a distinct sequence number is needed.
    let claimScopeDigest
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId option)
        (origin: PromptOrigin)
        (payloadDigest: string)
        =
        String.Join(
            "\u001f",
            [| SessionId.value sessionId
               digestField (logicalRunId |> Option.map LogicalRunId.value)
               originLabel origin
               payloadDigest |]
        )

    /// Root claims have no LogicalRunId until physical acceptance. Their
    /// sequence scope belongs to the session, so closing a run cannot reset it.
    let rootClaimSequences (sessionId: SessionId) (projection: PromptAuthorityProjection) =
        projection.ClaimSequences
        |> Map.filter (fun _ counter -> counter.SessionId = sessionId && counter.LogicalRunId.IsNone)

    /// The ClaimSequence this scope's next claim would carry.
    let nextClaimSequence (scope: string) (projection: PromptAuthorityProjection) =
        (Map.tryFind scope projection.ClaimSequences
         |> Option.map (fun counter -> counter.Count)
         |> Option.defaultValue 0)
        + 1

    /// PROMPT-011's key. Deterministic in every input, so the same logical
    /// dispatch derives the same key on any process that folds the same journal.
    let derivePromptKey
        (sha256: string -> string)
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId option)
        (authorityRoot: AuthorityRootUserMessageId option)
        (origin: PromptOrigin)
        (payloadDigest: string)
        (claimSequence: int)
        : PromptKey =
        PromptKey.create (
            sha256 (
                String.Join(
                    "\u001f",
                    [| SessionId.value sessionId
                       digestField (logicalRunId |> Option.map LogicalRunId.value)
                       digestField (authorityRoot |> Option.map AuthorityRootUserMessageId.value)
                       originLabel origin
                       payloadDigest
                       string claimSequence |]
                )
            )
        )


    /// Blogger-request + terminal-scoped repair identity used by the exact-one
    /// chronicle nudge→AABB state machine. Both axes matter: terminal identity
    /// makes same-terminal re-entry idempotent, while BloggerRequestId prevents a
    /// previous request on the same Session/LogicalRun from spending the next
    /// request's protocol budget.
    ///
    /// This request+terminal digest is reserved for Blogger's explicit
    /// nudge→AABB escalation protocol. Ordinary interaction nudges are gate
    /// reminders and use `gateNudgePayloadDigest` instead.
    let repairPayloadDigest
        (requestId: BloggerRequestId)
        (terminalProviderRun: ProviderRunIdentity)
        (repairKind: string)
        =
        String.Join(
            "\u001f",
            [| BloggerRequestId.value requestId
               ProviderRunIdentity.value terminalProviderRun
               repairKind |]
        )

    let repairPayloadBelongsToRequest (requestId: BloggerRequestId) (payloadDigest: string) =
        payloadDigest.StartsWith(BloggerRequestId.value requestId + "\u001f", System.StringComparison.Ordinal)

    /// Gate nudges are not a finite repair budget. They are repeatable while the
    /// guarded condition remains unsatisfied, but one exact terminal occasion is
    /// idempotent across duplicate Host observations / restart replay.
    let gateNudgePayloadDigest (gateKind: string) (terminalProviderRun: ProviderRunIdentity) =
        String.Join("\u001f", [| gateKind; ProviderRunIdentity.value terminalProviderRun |])

    let private exactOccasionIsAdmitted
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (origin: PromptOrigin)
        (payloadDigest: string)
        (projection: PromptAuthorityProjection)
        =
        let pending =
            projection.PendingClaims
            |> Map.exists (fun _ claim ->
                claim.SessionId = sessionId
                && claim.LogicalRunId = Some logicalRunId
                && claim.Origin = origin
                && claim.PayloadDigest = payloadDigest)

        let accepted =
            projection.AcceptedDispatches
            |> Map.tryFind (acceptedDispatchKey sessionId payloadDigest)
            |> Option.exists (fun dispatch -> dispatch.Origin = origin)

        pending || accepted

    let gateNudgeAlreadyAdmitted
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (continuation: ContinuationKind)
        (gateKind: string)
        (terminalProviderRun: ProviderRunIdentity)
        (projection: PromptAuthorityProjection)
        =
        exactOccasionIsAdmitted
            sessionId
            logicalRunId
            (PromptOrigin.Continuation continuation)
            (gateNudgePayloadDigest gateKind terminalProviderRun)
            projection

    let gateNudgeAcceptedPhysical
        (sessionId: SessionId)
        (continuation: ContinuationKind)
        (gateKind: string)
        (terminalProviderRun: ProviderRunIdentity)
        (projection: PromptAuthorityProjection)
        =
        let payloadDigest = gateNudgePayloadDigest gateKind terminalProviderRun

        projection.AcceptedDispatches
        |> Map.tryFind (acceptedDispatchKey sessionId payloadDigest)
        |> Option.filter (fun dispatch ->
            dispatch.Origin = PromptOrigin.Continuation continuation
            && dispatch.PayloadDigest = payloadDigest)
        |> Option.map (fun dispatch -> dispatch.PhysicalUserMessageId)

    /// provider-attempt-recovery-008: has this Blogger request + terminal occasion already spent its one repair.
    /// Blogger protocol repair deliberately uses both axes: request identity
    /// prevents cross-request leakage on a long-lived run, while terminal identity
    /// distinguishes same-terminal re-entry from a new invalid terminal.
    ///
    /// Derived, not stored. `nextClaimSequence` returns 1 for a scope no claim has
    /// ever used, so anything above 1 means a repair was already claimed for this
    /// request+terminal occasion — whether or not it went on to succeed, which is the point:
    /// a failed repair must not license a second attempt.
    let repairAlreadyClaimed
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId)
        (requestId: BloggerRequestId)
        (terminalProviderRun: ProviderRunIdentity)
        (repairKind: string)
        (projection: PromptAuthorityProjection)
        =
        let scope =
            claimScopeDigest
                sessionId
                (Some logicalRunId)
                (PromptOrigin.Continuation ContinuationKind.InteractionRepair)
                (repairPayloadDigest requestId terminalProviderRun repairKind)

        nextClaimSequence scope projection > 1

    /// AGENT-001: Canonical role shares one system prompt, so the prompt
    /// identity is a function of CanonicalRole alone.
    let systemPromptIdFor (role: Role) : SystemPromptId =
        SystemPromptId.create (Roles.roleLabel role)

    /// STRENGTH-004 / PROMPT-008: the request-specific authority is exact, not
    /// inferred by intersecting the ordinary role surface. All active roles in
    /// Roles.all carry exact readonly capabilities under StrengthReplica.
    let private strengthReplicaReadonly =
        set [ ToolPermission.Read; ToolPermission.Glob; ToolPermission.Grep ]

    let private strengthReplicaCapabilities (role: Role) : Set<ToolPermission> =
        if List.contains role Roles.all then
            strengthReplicaReadonly
        else
            Set.empty

    /// AGENT-007: ordinary requests use role permissions; StrengthReplica uses
    /// its own narrower request contract and fails closed for every ineligible role.
    let toolCapabilitiesFor (role: Role) (requestKind: ProviderRequestKind) : Set<ToolPermission> =
        match requestKind with
        | ProviderRequestKind.StrengthReplica -> strengthReplicaCapabilities role
        | ProviderRequestKind.WorkMain
        | ProviderRequestKind.BloggerMain
        | ProviderRequestKind.BloggerSquash
        | ProviderRequestKind.InteractionRepair -> OfficeCapability.permissions role
