namespace Wanxiangshu.Interaction.Authority

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Typed projection of the stateless `wanxiangli/dispatch` claim-ledger law
/// (CUT-11): claim scope and PromptKey material, ClaimSequence consumption, the
/// four-state settlement edits, exact-occasion admission and the bounded
/// recovery proof. The projection record, sha256 and the journal stay here.
[<RequireQualifiedAccess>]
module DispatchLaw =

    type private Law =
        abstract recoveryTailWindow: int
        abstract claimScope: input: obj -> string
        abstract promptKeyMaterial: input: obj -> string
        abstract nextClaimSequence: consumed: obj -> int
        abstract alreadyClaimed: consumed: obj -> bool
        abstract settle: input: obj -> string array
        abstract occasionAdmitted: input: obj -> bool
        abstract recover: input: obj -> obj
        abstract answer: sent: string -> obj
        abstract reset: input: obj -> obj

    [<Import("Dispatch", "wanxiangli/dispatch")>]
    let private dispatchClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : Law = jsNative

    let private law: Law = construct dispatchClass

    let private orNull (value: string option) : obj =
        match value with
        | Some text -> box text
        | None -> null

    let private count (consumed: int option) : obj =
        match consumed with
        | Some value -> box value
        | None -> null

    let recoveryTailWindow = law.recoveryTailWindow

    let claimScope (sessionId: string) (logicalRunId: string option) (origin: string) (payloadDigest: string) =
        law.claimScope (
            createObj
                [ "sessionId" ==> sessionId
                  "logicalRunId" ==> orNull logicalRunId
                  "origin" ==> origin
                  "payloadDigest" ==> payloadDigest ]
        )

    let promptKeyMaterial
        (sessionId: string)
        (logicalRunId: string option)
        (authorityRoot: string option)
        (origin: string)
        (payloadDigest: string)
        (claimSequence: int)
        =
        law.promptKeyMaterial (
            createObj
                [ "sessionId" ==> sessionId
                  "logicalRunId" ==> orNull logicalRunId
                  "authorityRoot" ==> orNull authorityRoot
                  "origin" ==> origin
                  "payloadDigest" ==> payloadDigest
                  "claimSequence" ==> claimSequence ]
        )

    let nextClaimSequence (consumed: int option) = law.nextClaimSequence (count consumed)
    let alreadyClaimed (consumed: int option) = law.alreadyClaimed (count consumed)

    [<RequireQualifiedAccess>]
    type Edit =
        | RecordReceipt
        | DropPending
        | LandOccasion
        | LandExact
        | RecordContinuation
        | ClearOccasion

    let private edit (label: string) =
        match label with
        | "RecordReceipt" -> Edit.RecordReceipt
        | "DropPending" -> Edit.DropPending
        | "LandOccasion" -> Edit.LandOccasion
        | "LandExact" -> Edit.LandExact
        | "RecordContinuation" -> Edit.RecordContinuation
        | "ClearOccasion" -> Edit.ClearOccasion
        | other -> failwithf "wanxiangli/dispatch returned an unknown ledger edit %s" other

    /// event: "Submit" | "Accept" | "Abandon".
    let settle (event: string) (pending: bool) (continuation: bool) : Edit list =
        law.settle (createObj [ "event" ==> event; "pending" ==> pending; "continuation" ==> continuation ])
        |> Array.map edit
        |> Array.toList

    let occasionAdmitted (pendingSameOccasion: bool) (landedOrigin: string option) (origin: string) =
        law.occasionAdmitted (
            createObj
                [ "pendingSameOccasion" ==> pendingSameOccasion
                  "landedOrigin" ==> orNull landedOrigin
                  "origin" ==> origin ]
        )

    /// Host send answer label → (ledger action, definitely-not-sent, detached verdict).
    /// Labels: "AdmittedWithReceipt" | "AdmittedWithPhysicalMessage" | "Retryable" |
    /// "Fatal" | "AcceptanceUnknown".
    let answer (sent: string) : string * bool * string =
        let verdict = law.answer sent
        unbox<string> verdict?ledger, unbox<bool> verdict?definitelyNotSent, unbox<string> verdict?detached

    /// Run-boundary reset: (claim keys kept, counter scopes kept, clear
    /// continuations, clear questions). claims: (key, logical run);
    /// counters: (scope, session, logical run).
    let reset
        (sessionId: string)
        (claims: (string * string option) list)
        (counters: (string * string * string option) list)
        : Set<string> * Set<string> * bool * bool =
        let verdict =
            law.reset (
                createObj
                    [ "sessionId" ==> sessionId
                      "claims"
                      ==> (claims
                           |> List.map (fun (key, run) -> createObj [ "key" ==> key; "logicalRunId" ==> orNull run ])
                           |> List.toArray)
                      "counters"
                      ==> (counters
                           |> List.map (fun (scope, session, run) ->
                               createObj [ "scope" ==> scope; "sessionId" ==> session; "logicalRunId" ==> orNull run ])
                           |> List.toArray) ]
            )

        Set.ofArray (unbox<string array> verdict?keepClaims),
        Set.ofArray (unbox<string array> verdict?keepCounters),
        unbox<bool> verdict?clearContinuations,
        unbox<bool> verdict?clearQuestions

    /// messages: (role, PromptKey carrier, physical id), oldest first.
    /// Ok physical = proven; Error hasReceipt = still pending.
    let recover (promptKey: string) (hasReceipt: bool) (messages: (string * string option * string) list) =
        let wire =
            messages
            |> List.map (fun (role, key, id) -> createObj [ "role" ==> role; "promptKey" ==> orNull key; "id" ==> id ])
            |> List.toArray

        let verdict =
            law.recover (createObj [ "promptKey" ==> promptKey; "hasReceipt" ==> hasReceipt; "messages" ==> wire ])

        match unbox<string> verdict?kind with
        | "Proven" -> Ok(unbox<string> verdict?physicalUserMessageId)
        | "StillPending" -> Error(unbox<bool> verdict?hasReceipt)
        | other -> failwithf "wanxiangli/dispatch returned an unknown recovery verdict %s" other

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

    let withSessionId (sessionId: SessionId) (profile: AuthorityExecutionProfile) : AuthorityExecutionProfile =
        { profile with
            StoredSessionId = sessionId }

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
    ///
    /// The profile itself cannot carry it: it is the execution identity, and
    /// the closure contract is the durable answer to "how does this root
    /// end?". A restart must still be able to tell a repeated closure from a
    /// conflicting one, so the contract survives the close.
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
    ///
    /// The fields are the witness fields of the closure kind this terminal
    /// belongs to, so a closure can be checked against the exact source and
    /// not merely against a fact id.
    type AuthorityTerminalSource =
        {
            /// The library's closure kind this terminal is the source of:
            /// "HumanRootManagedRun" | "AgentOwnerChildWork" |
            /// "AgentOwnerAttachedWork" | "AgentOwnerInternalLeaf".
            SourceKind: string
            OwnerLogicalRunId: string option
            ChildLogicalRunId: string option
            AttachmentKind: string option
            AssociationGeneration: string option
            DecisionOrTransactionId: string option
            FactId: string
        }

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
            UserQuestions: Map<ToolCallId, UserQuestion>
            /// interaction-authority-018: the closure contract of every
            /// accepted root, keyed by its logical run.
            ///
            /// The accepted root is the only thing that knows which closure
            /// kind a run may close under, so this map is what a closure is
            /// judged against once the active run has been cleared.
            AcceptedRootClosures: Map<LogicalRunId, AcceptedRootClosure>
            /// interaction-authority-018: the durable source terminals of a
            /// logical run, keyed by the run and then by the source fact id.
            ///
            /// A closure may only cite a source terminal that is already
            /// durable; a closure that cites nothing durable is refused rather
            /// than inferred.
            TerminalSources: Map<LogicalRunId, Map<string, AuthorityTerminalSource>>
            /// interaction-authority-018: the closure already recorded for a
            /// logical run.
            ///
            /// Kept so a replay after the active run is gone can still tell an
            /// identical closure (idempotent) from a conflicting one
            /// (fail-closed).
            ClosedRuns: Map<LogicalRunId, AuthorityClosureRecord>
        }

    let empty: PromptAuthorityProjection =
        { LastAuthorityProfile = None
          ActiveLogicalRun = None
          PendingClaims = Map.empty
          AcceptedDispatches = Map.empty
          PhysicalLandings = Map.empty
          AcceptedContinuationIds = Map.empty
          ClaimSequences = Map.empty
          UserQuestions = Map.empty
          AcceptedRootClosures = Map.empty
          TerminalSources = Map.empty
          ClosedRuns = Map.empty }

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
        | Continuation FissionHandoff -> "FissionHandoff"
        | HostInternal -> "HostInternal"
        | UnknownOrigin -> "UnknownOrigin"

    /// The delivery verb the product wants the host to use for this origin.
    ///
    /// A busy nudge must reach a running session as a steer so the nudge lands on the
    /// next model step instead of waiting for idle; every other origin keeps the host
    /// default. This is a product decision made here, on the send options, so a host
    /// adapter only translates the verb and never infers it from an origin name.
    let deliveryFor (origin: PromptOrigin) =
        match origin with
        | Continuation BusyAgentNudge -> Some "steer"
        | _ -> None

    let tryParseContinuationKind (value: string) =
        match value with
        | "InteractionRepair" -> Some InteractionRepair
        | "JoinGuard" -> Some JoinGuard
        | "ManagerGuard" -> Some ManagerGuard
        | "BusyAgentNudge" -> Some BusyAgentNudge
        | "HumanMessage" -> Some HumanMessage
        | "ManagedDelegationAssignment" -> Some ManagedDelegationAssignment
        | "ProviderRetryAttempt" -> Some ProviderRetryAttempt
        | "FissionHandoff" -> Some FissionHandoff
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

    /// PROMPT-011 recovery bounds, owned by `wanxiangli/dispatch`: a Host
    /// session's history is unbounded while a pending claim is minutes old at
    /// most, and PROMPT-011 forbids resending either way.
    ///
    /// Plain `let`, not `[<Literal>]`: the value stays assertable from a layer 1 test.
    let RecoveryTailWindow = DispatchLaw.recoveryTailWindow

    /// The scope a ClaimSequence counts within: (SessionId, LogicalRunId,
    /// Origin, PayloadDigest), as `wanxiangli/dispatch` spells it.
    let claimScopeDigest
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId option)
        (origin: PromptOrigin)
        (payloadDigest: string)
        =
        DispatchLaw.claimScope
            (SessionId.value sessionId)
            (logicalRunId |> Option.map LogicalRunId.value)
            (originLabel origin)
            payloadDigest

    let private consumedClaims (scope: string) (projection: PromptAuthorityProjection) =
        Map.tryFind scope projection.ClaimSequences
        |> Option.map (fun counter -> counter.Count)

    /// The ClaimSequence this scope's next claim would carry.
    let nextClaimSequence (scope: string) (projection: PromptAuthorityProjection) =
        DispatchLaw.nextClaimSequence (consumedClaims scope projection)

    /// PROMPT-011's key: sha256 of the library's deterministic material, so the
    /// same logical dispatch derives the same key on any process that folds the
    /// same journal.
    let derivePromptKey
        (sha256: string -> string)
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId option)
        (authorityRoot: AuthorityRootUserMessageId option)
        (origin: PromptOrigin)
        (payloadDigest: string)
        (claimSequence: int)
        : PromptKey =
        DispatchLaw.promptKeyMaterial
            (SessionId.value sessionId)
            (logicalRunId |> Option.map LogicalRunId.value)
            (authorityRoot |> Option.map AuthorityRootUserMessageId.value)
            (originLabel origin)
            payloadDigest
            claimSequence
        |> sha256
        |> PromptKey.create

    /// Request + terminal-scoped repair identity. Both axes matter: terminal identity
    /// makes same-terminal re-entry idempotent.
    ///
    /// reminders and use `gateNudgePayloadDigest` instead.
    let repairPayloadDigest
        (requestId: RepairRequestId)
        (terminalProviderRun: ProviderRunIdentity)
        (repairKind: string)
        =
        String.Join(
            "\u001f",
            [| RepairRequestId.value requestId
               ProviderRunIdentity.value terminalProviderRun
               repairKind |]
        )

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

        let landedOrigin =
            projection.AcceptedDispatches
            |> Map.tryFind (acceptedDispatchKey sessionId payloadDigest)
            |> Option.map (fun dispatch -> originLabel dispatch.Origin)

        DispatchLaw.occasionAdmitted pending landedOrigin (originLabel origin)

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

    /// provider-attempt-recovery-008: has this request + terminal occasion already spent its one repair.
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
        (requestId: RepairRequestId)
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

        DispatchLaw.alreadyClaimed (consumedClaims scope projection)

    /// AGENT-001: Canonical role shares one system prompt, so the prompt
    /// identity is a function of CanonicalRole alone.
    let systemPromptIdFor (role: Role) : SystemPromptId =
        SystemPromptId.create (Roles.roleLabel role)

    /// AGENT-007: ordinary requests use role permissions.
    let toolCapabilitiesFor (role: Role) (requestKind: ProviderRequestKind) : Set<ToolPermission> =
        match requestKind with
        | ProviderRequestKind.WorkMain
        | ProviderRequestKind.InteractionRepair -> OfficeCapability.permissions role
