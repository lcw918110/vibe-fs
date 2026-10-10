namespace Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Participant.Persona

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module PromptDispatcher =

    [<RequireQualifiedAccess>]
    type PromptSendObservation =
        | Sending of PromptKey
        | Answered of PromptKey * SendOutcome

    let internal originLabel = PromptAuthority.originLabel

    [<RequireQualifiedAccess>]
    type AuthorityRegistrationFailure =
        | RegistrationRejected of PromptAuthorityRun.AuthorityRegistrationRejection
        | PersistenceRejected of string

    [<RequireQualifiedAccess>]
    type HumanRootAcceptanceFailure =
        | IdentityRejected of string
        | AuthorityRegistrationRejected of AuthorityRegistrationFailure

    let describeAuthorityRegistrationFailure =
        function
        | AuthorityRegistrationFailure.RegistrationRejected rejection ->
            PromptAuthorityRun.describeRegistrationRejection rejection
        | AuthorityRegistrationFailure.PersistenceRejected reason -> reason

    let describeHumanRootAcceptanceFailure =
        function
        | HumanRootAcceptanceFailure.IdentityRejected reason -> reason
        | HumanRootAcceptanceFailure.AuthorityRegistrationRejected failure ->
            describeAuthorityRegistrationFailure failure

    let private authorityRootFact (profile: PromptAuthority.AuthorityExecutionProfile) : PromptSessionFact =
        PromptSessionFact.AuthorityRootAccepted
            { SchemaVersion = 2
              SessionId = profile.SessionId
              LogicalRunId = profile.LogicalRunId
              AuthorityRootUserMessageId = profile.AuthorityRootUserMessageId
              AuthorityKind =
                match profile.AuthorityKind with
                | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"
                | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
              IdentitySeed = profile.IdentitySeed }

    let private registrationDecision
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (projection: PromptAuthority.PromptAuthorityProjection)
        : Result<PromptAuthority.AuthorityExecutionProfile, PromptAuthorityRun.AuthorityRegistrationRejection> =
        PromptAuthorityRun.resolveAuthorityProfile profile projection

    let private appendAuthorityRoot
        (journal: IPromptJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        : Task<Result<unit, JournalAppendFailure>> =
        journal.Append profile.SessionId None (authorityRootFact profile)

    let private appendManagedPromptAccepted
        (journal: IPromptJournal)
        (promptKey: PromptKey)
        (sessionId: SessionId)
        (physicalMessageId: PhysicalUserMessageId)
        : Task<Result<unit, ManagedChatAcceptanceError>> =
        task {
            let! appended =
                journal.Append
                    sessionId
                    None
                    (PromptSessionFact.PromptPhysicalAccepted
                        {| PromptKey = promptKey
                           SessionId = sessionId
                           PhysicalUserMessageId = physicalMessageId |})

            return
                appended
                |> Result.map ignore
                |> Result.mapError ManagedChatAcceptance.persistenceError
        }

    let private persistSessionFact
        (journal: IPromptJournal)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity option)
        (fact: PromptSessionFact)
        : Task<Result<unit, string>> =
        task {
            match! journal.Append sessionId providerRun fact with
            | Ok _ -> return Ok()
            | Error failure -> return Error(JournalAppendFailure.describe failure)
        }

    let private registrationAppendFailure
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (projection: PromptAuthority.PromptAuthorityProjection)
        (failure: JournalAppendFailure)
        : Result<PromptAuthority.AuthorityExecutionProfile, AuthorityRegistrationFailure> =
        match registrationDecision profile projection with
        | Error conflict -> Error(AuthorityRegistrationFailure.RegistrationRejected conflict)
        | Ok canonical when canonical <> profile -> Ok canonical
        | Ok _ -> Error(AuthorityRegistrationFailure.PersistenceRejected(JournalAppendFailure.describe failure))

    let private completeRegistrationAppend
        (canonical: PromptAuthority.AuthorityExecutionProfile)
        (requested: PromptAuthority.AuthorityExecutionProfile)
        (projection: PromptAuthority.PromptAuthorityProjection)
        (appendResult: Result<unit, JournalAppendFailure>)
        : Result<PromptAuthority.AuthorityExecutionProfile, AuthorityRegistrationFailure> =
        match appendResult with
        | Ok() -> Ok canonical
        | Error failure -> registrationAppendFailure requested projection failure

    let private validateAcceptedProfile
        (claim: PromptAuthority.PromptClaim)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        : Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError> =
        if
            profile.IdentitySeed = claim.IdentitySeed
            && claim.LogicalRunId = Some profile.LogicalRunId
            && claim.AuthorityRootUserMessageId = Some profile.AuthorityRootUserMessageId
        then
            Ok profile
        else
            Error(
                ManagedChatAcceptanceError.IntentRejected(
                    "Continuation managed intent identity does not match the active logical run"
                )
            )

    let private requireActiveManagedProfile
        (profile: PromptAuthority.AuthorityExecutionProfile option)
        : Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError> =
        match profile with
        | Some accepted -> Ok accepted
        | None ->
            Error(
                ManagedChatAcceptanceError.IntentRejected("Continuation managed intent requires an active logical run")
            )

    let private tryPendingGateNudgeKey
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (origin: PromptAuthority.PromptOrigin)
        (digest: string)
        (projection: PromptAuthority.PromptAuthorityProjection)
        : PromptKey option =
        let pending =
            projection.PendingClaims
            |> Map.toList
            |> List.filter (fun (_, claim) ->
                claim.SessionId = profile.SessionId
                && claim.LogicalRunId = Some profile.LogicalRunId
                && claim.AuthorityRootUserMessageId = Some profile.AuthorityRootUserMessageId
                && claim.Origin = origin
                && claim.PayloadDigest = digest)

        match pending with
        | [ key, _ ] -> Some key
        | _ -> None

    let private attachPhysicalAcceptanceObserver (observer: ContinuationAcceptanceObserver) (key: PromptKey) =
        let registration = PromptPhysicalAcceptance.register key observer.Notify

        try
            observer.AttachDisposable registration
        with error ->
            registration.Dispose()
            raise error

    /// PROMPT-007: whether the caller waits for PhysicalAccepted.
    ///
    /// Detached = fire-and-forget: claim, authority, persist, idempotence and error
    /// recording still run; the caller does not require a physical message id.
    /// Await = same send path; reserved for callers that bind an acceptance callback.
    [<RequireQualifiedAccess>]
    type AwaitMode =
        | Await
        | Detached

    /// Internal result of the claim→physical-send path. `AdmissionRejected` is only
    /// possible for an idle-derived send carrying a final physical admission
    /// check; ordinary callers continue to consume `Result<PromptKey,string>`.
    [<RequireQualifiedAccess>]
    type internal SendAttemptOutcome =
        | Sent of PromptKey
        | AdmissionRejected of QuiescencePermitFailure
        /// Host definitively rejected before physical acceptance. Idle gate
        /// callers may safely re-open the same quiescence permit.
        | NotSent of string
        /// Acceptance may have happened or durable bookkeeping failed; never
        /// retry automatically because doing so could duplicate physical input.
        | Failed of string

    let internal describeIdentitySeedRejection (rejection: PromptAuthority.IdentitySeedValidationError) =
        sprintf "AgentOwnerRoot identity seed rejected: %A" rejection

    /// The single PROMPT-005 sender.
    ///
    /// Holds no authority state. The previous version kept a `mutable authority`
    /// behind a lock and seeded it by folding *every* session's projection into
    /// one value, which had two consequences worth naming: a claim made in one
    /// session was visible in another, and the in-memory copy could disagree with
    /// the journal it was supposed to mirror. Both are gone because the state is
    /// gone - every read goes to the fold, which is the only writer.
    ///
    /// The journal is not optional. A dispatcher with nowhere to persist would
    /// report `Ok` for facts it silently dropped, and PROMPT-005 is a durability
    /// claim before it is a sequencing one.
    type Runtime(journal: IPromptJournal) =
        /// DSL-cross-callback-proof: physical single-flight — one exact gate-nudge Host send
        let gateNudgeFlights =
            Dictionary<string, TaskCompletionSource<Result<PromptKey, string>>>()

        member _.RuntimeId = journal.RuntimeId

        member _.ProjectionFor(sessionId: SessionId) : PromptAuthority.PromptAuthorityProjection =
            journal.ProjectionFor sessionId

        member private _.ReleaseGateNudge(scope: string, completion) =
            lock gateNudgeFlights (fun () ->
                match gateNudgeFlights.TryGetValue scope with
                | true, current when obj.ReferenceEquals(current, completion) ->
                    gateNudgeFlights.Remove scope |> ignore
                | _ -> ())

        member private _.SettleGateNudge(completion: TaskCompletionSource<Result<PromptKey, string>>, send) : Task =
            task {
                try
                    let! result = send ()
                    completion.SetResult(result)
                with error ->
                    completion.SetException(error)
            }
            :> Task

        member private this.StartGateNudge(scope, completion, send) =
            task {
                try
                    do! this.SettleGateNudge(completion, send)
                finally
                    this.ReleaseGateNudge(scope, completion)
            }
            |> ignore

        member internal this.RunGateNudgeOnce
            (scope: string, send: unit -> Task<Result<PromptKey, string>>)
            : Task<Result<PromptKey, string>> =
            let completion, ownsFlight =
                lock gateNudgeFlights (fun () ->
                    match gateNudgeFlights.TryGetValue scope with
                    | true, running -> running, false
                    | false, _ ->
                        let created =
                            TaskCompletionSource<Result<PromptKey, string>>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        gateNudgeFlights.Add(scope, created)
                        created, true)

            if ownsFlight then
                this.StartGateNudge(scope, completion, send)

            completion.Task

        member private _.AppendManagedPromptAccepted
            (promptKey: PromptKey)
            (sessionId: SessionId)
            (physicalMessageId: PhysicalUserMessageId)
            : Task<Result<unit, ManagedChatAcceptanceError>> =
            appendManagedPromptAccepted journal promptKey sessionId physicalMessageId

        member private this.RegisterManagedAuthority
            (profile: PromptAuthority.AuthorityExecutionProfile)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError>> =
            let projection = this.ProjectionFor profile.SessionId

            match projection.ActiveLogicalRun with
            | Some _ ->
                registrationDecision profile projection
                |> Result.mapError ManagedChatAcceptanceError.AuthorityRegistrationRejected
                |> Task.FromResult
            | None ->
                task {
                    let! appended = appendAuthorityRoot journal profile

                    return
                        appended
                        |> Result.mapError ManagedChatAcceptance.persistenceError
                        |> Result.bind (fun () ->
                            registrationDecision profile (this.ProjectionFor profile.SessionId)
                            |> Result.mapError ManagedChatAcceptanceError.AuthorityRegistrationRejected)
                }

        member private this.PromptAlreadyAccepted(evidence: ChatAdmissionIntent.PendingPromptEvidence) =
            (this.ProjectionFor evidence.Key.SessionId).PhysicalLandings
            |> Map.tryFind evidence.Key.PhysicalUserMessageId
            |> Option.exists (fun accepted ->
                accepted.PromptKey = evidence.PromptKey
                && accepted.SessionId = evidence.Key.SessionId
                && accepted.IdentitySeed = evidence.IdentitySeed)

        // Receipt is transport progress (Submitted), not intent identity.
        // chat.message resolve can race its persist, so equality ignores it.
        member private this.ExactPromptClaimMatches(evidence: ChatAdmissionIntent.PendingPromptEvidence) =
            match Map.tryFind evidence.PromptKey (this.ProjectionFor evidence.Key.SessionId).PendingClaims with
            | Some claim -> { claim with Receipt = None } = { evidence.Claim with Receipt = None }
            | None -> false

        member private this.AcceptExternalManagedRoot
            (evidence: ChatAdmissionIntent.ExternalRootEvidence)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError>> =
            PromptAuthorityRun.createAuthorityRoot
                HostDigest.sha256Hex
                this.RuntimeId
                evidence.Key.SessionId
                PromptAuthority.RootAuthorityKind.HumanRoot
                evidence.Key.PhysicalUserMessageId
                evidence.IdentitySeed
            |> Result.mapError ManagedChatAcceptanceError.IntentRejected
            |> Result.map this.RegisterManagedAuthority
            |> function
                | Ok pending -> pending
                | Error error -> Task.FromResult(Error error)

        member private this.AcceptPendingManagedPrompt
            (evidence: ChatAdmissionIntent.PendingPromptEvidence)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError>> =
            let appendPhysical () : Task<Result<unit, ManagedChatAcceptanceError>> =
                if this.PromptAlreadyAccepted evidence then
                    Task.FromResult(Ok())
                elif this.ExactPromptClaimMatches evidence then
                    this.AppendManagedPromptAccepted
                        evidence.PromptKey
                        evidence.Key.SessionId
                        evidence.Key.PhysicalUserMessageId
                else
                    Task.FromResult(
                        Error(
                            ManagedChatAcceptanceError.IntentRejected(
                                "Pending managed intent no longer matches its durable prompt claim"
                            )
                        )
                    )

            match evidence.Claim.Origin with
            | PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot ->
                taskResult {
                    let! _ =
                        this.ValidateAgentOwnerIdentitySeed evidence.IdentitySeed
                        |> Result.mapError (describeIdentitySeedRejection >> ManagedChatAcceptanceError.IntentRejected)

                    let! profile =
                        PromptAuthorityRun.createAuthorityRoot
                            HostDigest.sha256Hex
                            this.RuntimeId
                            evidence.Key.SessionId
                            PromptAuthority.RootAuthorityKind.AgentOwnerRoot
                            evidence.Key.PhysicalUserMessageId
                            evidence.IdentitySeed
                        |> Result.mapError ManagedChatAcceptanceError.IntentRejected

                    do! appendPhysical ()
                    return! this.RegisterManagedAuthority profile
                }
            | PromptAuthority.PromptOrigin.Continuation _ ->
                taskResult {
                    let! profile = this.ActiveProfile evidence.Key.SessionId |> requireActiveManagedProfile

                    let acceptedProfileDecision
                        : Result<PromptAuthority.AuthorityExecutionProfile, ManagedChatAcceptanceError> =
                        validateAcceptedProfile evidence.Claim profile

                    let! acceptedProfile = acceptedProfileDecision

                    do! appendPhysical ()
                    return acceptedProfile
                }
            | _ ->
                Task.FromResult(
                    Error(
                        ManagedChatAcceptanceError.IntentRejected(
                            "Pending managed intent origin is not an AgentOwnerRoot or continuation"
                        )
                    )
                )

        /// Establish all prompt authority facts first, then durable managed-chat
        /// acceptance, from the one frozen Task14 decision.
        member this.AcceptManagedChatIntent
            (intent: ChatAdmissionIntent.Decision)
            : Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>> =
            let accept profile physicalMessageId origin =
                let evidence =
                    ManagedChatAcceptance.evidenceFromIntent profile physicalMessageId origin

                ManagedChatAcceptance.acceptWith
                    (journal.ChatAcceptancePersistence())
                    { SessionId = evidence.SessionId
                      PhysicalUserMessageId = evidence.PhysicalUserMessageId }
                    evidence

            let acceptEstablishedInput (evidence: AcceptedChatExecutionEvidence) =
                let key: ChatExecutionKey =
                    { SessionId = evidence.SessionId
                      PhysicalUserMessageId = evidence.PhysicalUserMessageId }

                let persistence = journal.ChatAcceptancePersistence()

                match persistence.ReadExact key with
                | Some established when established.acceptedEvidence = evidence ->
                    ManagedChatAcceptance.acceptWith persistence key evidence
                | _ ->
                    Task.FromResult(
                        Error(ManagedChatAcceptanceError.IntentRejected "Input has no exact accepted evidence")
                    )

            match intent with
            | ChatAdmissionIntent.Decision.ExternalRootIntent evidence ->
                taskResult {
                    let! profile = this.AcceptExternalManagedRoot evidence
                    return! accept profile evidence.Key.PhysicalUserMessageId evidence.Origin
                }
            | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent evidence ->
                accept evidence.Authority evidence.Key.PhysicalUserMessageId evidence.Origin
            | ChatAdmissionIntent.Decision.AcceptedInputIntent evidence -> acceptEstablishedInput evidence
            | ChatAdmissionIntent.Decision.PendingPromptIntent evidence ->
                taskResult {
                    let! profile = this.AcceptPendingManagedPrompt evidence

                    do!
                        this.RequireActiveProfile evidence.Key.SessionId profile
                        |> Result.mapError ManagedChatAcceptanceError.IntentRejected

                    let! witness = accept profile evidence.Key.PhysicalUserMessageId evidence.Origin
                    PromptPhysicalAcceptance.accepted evidence.PromptKey evidence.Key.PhysicalUserMessageId
                    return witness
                }
            | _ ->
                Task.FromResult(
                    Error(
                        ManagedChatAcceptanceError.IntentRejected("AcceptManagedChatIntent requires a managed intent")
                    )
                )

        member internal _.Persist
            (sessionId: SessionId)
            (providerRun: ProviderRunIdentity option)
            (fact: PromptSessionFact)
            : Task<Result<unit, string>> =
            persistSessionFact journal sessionId providerRun fact

        /// PROMPT-004: an Authority Root takes effect.
        ///
        /// Returns `Result` rather than raising. The previous version raised
        /// `InvalidOperationException` on a persist failure, which turned a
        /// recoverable journal rejection into a crash in whichever host callback
        /// happened to be on the stack.
        ///
        /// REVIEW-007's review requirement is not written here. The fold derives
        /// it from this fact's `AuthorityKind`, so a HumanRoot cannot be recorded
        /// without its requirement appearing with it.
        member this.RegisterAuthority
            (profile: PromptAuthority.AuthorityExecutionProfile)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, AuthorityRegistrationFailure>> =
            match registrationDecision profile (this.ProjectionFor profile.SessionId) with
            | Error rejection -> Task.FromResult(Error(AuthorityRegistrationFailure.RegistrationRejected rejection))
            | Ok canonical when canonical <> profile -> Task.FromResult(Ok canonical)
            | Ok canonical ->
                task {
                    let! appended = appendAuthorityRoot journal profile

                    return completeRegistrationAppend canonical profile (this.ProjectionFor profile.SessionId) appended
                }

        /// PROMPT-002: a human root carries the one identity resolved at the external boundary.
        /// There is no default and inherited child evidence is not legal for this path.
        member this.AcceptHumanRoot
            (sessionId: SessionId)
            (physicalMessageId: PhysicalUserMessageId)
            (identitySeed: PromptAuthority.IdentitySeed option)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, HumanRootAcceptanceFailure>> =
            let profileResult: Result<PromptAuthority.AuthorityExecutionProfile, HumanRootAcceptanceFailure> =
                identitySeed
                |> Option.map (fun seed ->
                    PromptAuthorityRun.createAuthorityRoot
                        HostDigest.sha256Hex
                        this.RuntimeId
                        sessionId
                        PromptAuthority.RootAuthorityKind.HumanRoot
                        physicalMessageId
                        seed
                    |> Result.mapError HumanRootAcceptanceFailure.IdentityRejected)
                |> Option.defaultValue (
                    Error(
                        HumanRootAcceptanceFailure.IdentityRejected(
                            "HumanRoot requires an explicit root-selection identity seed"
                        )
                    )
                )

            taskResult {
                let! profile = profileResult

                let! registered =
                    task {
                        let! result = this.RegisterAuthority profile
                        return Result.mapError HumanRootAcceptanceFailure.AuthorityRegistrationRejected result
                    }

                return registered
            }

        /// PROMPT-005 `Abandoned` for an explicit current-process send failure.
        /// Restart reconciliation no longer calls this: process death is not authority
        /// to manufacture an abandonment terminal for the old tool.
        member this.Abandon
            (key: PromptKey)
            (sessionId: SessionId)
            (reason: PromptAbandonReason)
            : Task<Result<unit, string>> =
            PromptPhysicalAcceptance.cancel key

            PromptSessionFact.PromptAbandoned
                {| PromptKey = key
                   SessionId = sessionId
                   Reason = reason |}
            |> this.Persist sessionId None

        /// PROMPT-005 `PhysicalAccepted` for an Authority Root claim.
        ///
        /// Two facts in order: the claim resolves, then the root takes effect. The
        /// order is the clause - an Authority Root may not take effect until a
        /// real physical message is proven, so `PhysicalAccepted` cannot come
        /// second.
        member internal this.ValidateAgentOwnerIdentitySeed(identitySeed: PromptAuthority.IdentitySeed) =
            let activeOwner =
                PromptAuthority.identitySeedOwner identitySeed
                |> Option.bind (fun (ownerSessionId, _, _) -> this.ActiveProfile ownerSessionId)

            PromptAuthority.validateInheritedIdentitySeedAgainstActiveOwner activeOwner identitySeed

        member internal this.AcceptPhysicalAgentOwnerRoot
            (key: PromptKey)
            (sessionId: SessionId)
            (physicalMessageId: PhysicalUserMessageId)
            (identitySeed: PromptAuthority.IdentitySeed)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, string>> =
            let authorityClaimDecision: Result<PromptAuthority.AuthorityExecutionProfile, string> =
                this.ValidateAgentOwnerIdentitySeed identitySeed
                |> Result.mapError describeIdentitySeedRejection
                |> Result.bind (fun _ ->
                    PromptAuthorityRun.createAuthorityRoot
                        HostDigest.sha256Hex
                        this.RuntimeId
                        sessionId
                        PromptAuthority.RootAuthorityKind.AgentOwnerRoot
                        physicalMessageId
                        identitySeed)

            taskResult {
                let! profile = authorityClaimDecision

                do!
                    PromptSessionFact.PromptPhysicalAccepted
                        {| PromptKey = key
                           SessionId = sessionId
                           PhysicalUserMessageId = physicalMessageId |}
                    |> this.Persist sessionId None

                let authorityRegistrationDecision: Result<ParticipantIdentityEvidence, string> =
                    this.ValidateAgentOwnerIdentitySeed identitySeed
                    |> Result.mapError describeIdentitySeedRejection

                let! _ = authorityRegistrationDecision

                let! registered =
                    task {
                        let! result = this.RegisterAuthority profile
                        return Result.mapError describeAuthorityRegistrationFailure result
                    }

                PromptPhysicalAcceptance.accepted key physicalMessageId
                return registered
            }

        member this.AcceptAgentOwnerRoot
            (key: PromptKey)
            (sessionId: SessionId)
            (physicalMessageId: PhysicalUserMessageId)
            : Task<Result<PromptAuthority.AuthorityExecutionProfile, string>> =
            let projection = this.ProjectionFor sessionId

            let acceptClaim
                (claim: PromptAuthority.PromptClaim)
                : Task<Result<PromptAuthority.AuthorityExecutionProfile, string>> =
                match claim.Origin with
                | PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot ->
                    this.AcceptPhysicalAgentOwnerRoot key sessionId physicalMessageId claim.IdentitySeed
                | _ ->
                    Task.FromResult(Error(sprintf "PromptKey %s is not a pending AgentOwnerRoot" (PromptKey.value key)))

            let acceptedClaim =
                projection.PhysicalLandings
                |> Map.tryFind physicalMessageId
                |> Option.filter (fun accepted ->
                    accepted.PromptKey = key
                    && accepted.SessionId = sessionId
                    && accepted.Origin = PromptAuthority.PromptOrigin.AuthorityRoot
                        PromptAuthority.RootAuthorityKind.AgentOwnerRoot)

            let acceptExisting
                (accepted: PromptAuthority.AcceptedDispatch)
                : Task<Result<PromptAuthority.AuthorityExecutionProfile, string>> =
                match projection.ActiveLogicalRun with
                | Some profile when
                    profile.AuthorityRootUserMessageId = PhysicalUserMessageId.promoteToAuthorityRoot physicalMessageId
                    && profile.IdentitySeed = accepted.IdentitySeed
                    ->
                    Task.FromResult(Ok profile)
                | Some _ ->
                    Task.FromResult(
                        Error(
                            sprintf "AgentOwnerRoot claim %s does not match the active child run" (PromptKey.value key)
                        )
                    )
                | None -> this.AcceptPhysicalAgentOwnerRoot key sessionId physicalMessageId accepted.IdentitySeed

            match Map.tryFind key projection.PendingClaims, acceptedClaim with
            | Some claim, _ -> acceptClaim claim
            | None, Some accepted -> acceptExisting accepted
            | None, None -> Task.FromResult(Error(sprintf "Unknown AgentOwnerRoot claim: %s" (PromptKey.value key)))

        /// PROMPT-003: a continuation reached physical acceptance. Returns the
        /// kind it was claimed as, read before the fact is written because writing
        /// it retires the claim.
        member this.AcceptContinuation
            (key: PromptKey)
            (sessionId: SessionId)
            (physicalMessageId: PhysicalUserMessageId)
            : Task<Result<PromptAuthority.ContinuationKind option, string>> =
            task {
                let kind =
                    match Map.tryFind key (this.ProjectionFor sessionId).PendingClaims with
                    | Some { Origin = PromptAuthority.PromptOrigin.Continuation c } -> Some c
                    | _ -> None

                match!
                    PromptSessionFact.PromptPhysicalAccepted
                        {| PromptKey = key
                           SessionId = sessionId
                           PhysicalUserMessageId = physicalMessageId |}
                    |> this.Persist sessionId None
                with
                | Error error -> return Error error
                | Ok() ->
                    PromptPhysicalAcceptance.accepted key physicalMessageId
                    return Ok kind
            }

        /// The run a continuation would extend.
        ///
        /// `ActiveLogicalRun` only. The previous version fell back to
        /// `LastAuthorityProfile`, which let a continuation attach to a finished
        /// run - PROMPT-004 scopes continuations to the active run, and a stale
        /// profile is exactly the thing that must not substitute for one.
        member this.ActiveProfile(sessionId: SessionId) =
            (this.ProjectionFor sessionId).ActiveLogicalRun

        member internal this.RequireActiveProfile sessionId (expected: PromptAuthority.AuthorityExecutionProfile) =
            match this.ActiveProfile sessionId with
            | None -> Error "No active authority profile"
            | Some active when active = expected -> Ok()
            | Some active ->
                Error(
                    sprintf
                        "Continuation profile does not match the active logical run: active logical run %s, supplied logical run %s"
                        (LogicalRunId.value active.LogicalRunId)
                        (LogicalRunId.value expected.LogicalRunId)
                )

        member this.ResolveOrigin
            (physicalMessageId: PhysicalUserMessageId)
            (promptKey: PromptKey option)
            (hostCompaction: bool)
            (sessionId: SessionId)
            : PromptAuthority.PromptOrigin =
            PromptAuthorityRun.resolveKnownOrigin
                physicalMessageId
                promptKey
                hostCompaction
                (this.ProjectionFor sessionId)

        /// Physical execution routing reads the same durable claim that owns the
        /// dispatch. Host message fields are never execution authority for a plugin prompt.
        member this.PendingClaim(sessionId: SessionId, promptKey: PromptKey) =
            Map.tryFind promptKey (this.ProjectionFor sessionId).PendingClaims

        /// PhysicalAccepted consumes PendingClaims. Execution capability may be
        /// handed to the provider only after the exact dispatch appears here.
        member this.DispatchAccepted(sessionId: SessionId, claim: PromptAuthority.PromptClaim) =
            let key = PromptAuthority.acceptedDispatchKey sessionId claim.PayloadDigest

            match Map.tryFind key (this.ProjectionFor sessionId).AcceptedDispatches with
            | Some accepted when accepted.PromptKey = claim.PromptKey -> true
            | _ -> false

        /// Has this exact gate + terminal occasion already admitted its reminder?
        /// Fresh ProviderRun identities are deliberately unbounded.
        member this.GateNudgeAlreadyAdmitted
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (continuation: PromptAuthority.ContinuationKind)
            (gateKind: string)
            (terminalProviderRun: ProviderRunIdentity)
            : bool =
            PromptAuthority.gateNudgeAlreadyAdmitted
                profile.SessionId
                profile.LogicalRunId
                continuation
                gateKind
                terminalProviderRun
                (this.ProjectionFor profile.SessionId)

        member this.GateNudgeAcceptedPhysical
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (continuation: PromptAuthority.ContinuationKind)
            (gateKind: string)
            (terminalProviderRun: ProviderRunIdentity)
            =
            PromptAuthority.gateNudgeAcceptedPhysical
                profile.SessionId
                continuation
                gateKind
                terminalProviderRun
                (this.ProjectionFor profile.SessionId)

        member this.ObserveGateNudgeAcceptance
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (continuation: PromptAuthority.ContinuationKind)
            (gateKind: string)
            (terminalProviderRun: ProviderRunIdentity)
            (observer: ContinuationAcceptanceObserver)
            : unit =
            let projection = this.ProjectionFor profile.SessionId
            let digest = PromptAuthority.gateNudgePayloadDigest gateKind terminalProviderRun
            let origin = PromptAuthority.PromptOrigin.Continuation continuation

            let accepted =
                projection.AcceptedDispatches
                |> Map.tryFind (PromptAuthority.acceptedDispatchKey profile.SessionId digest)
                |> Option.filter (fun landing ->
                    landing.SessionId = profile.SessionId
                    && landing.Origin = origin
                    && landing.PayloadDigest = digest
                    && Map.tryFind landing.PhysicalUserMessageId projection.PhysicalLandings = Some landing
                    && Map.tryFind landing.PhysicalUserMessageId projection.AcceptedContinuationIds = Some continuation)

            match projection.ActiveLogicalRun, accepted with
            | Some active, Some landing when active = profile -> observer.Notify landing.PhysicalUserMessageId
            | Some active, None when active = profile ->
                tryPendingGateNudgeKey profile origin digest projection
                |> Option.iter (attachPhysicalAcceptanceObserver observer)
            | _ -> ()

        /// provider-attempt-recovery-008: has this Blogger request + terminal occasion already spent its one interaction repair.
        ///
        /// A read, not a claim. The previous `TryClaimInteractionRepair` mutated a
        /// `RepairClaims` set that no fact ever wrote, so the at-most-once guarantee
        /// lived only in process memory. The budget is now derived from
        /// `ClaimSequences`, which PROMPT-005 `Claimed` does write - so a repair
        /// claimed before a crash is still spent after it.
        member this.RepairAlreadyClaimed
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (requestId: BloggerRequestId)
            (terminalProviderRun: ProviderRunIdentity)
            (repairKind: string)
            : bool =
            PromptAuthority.repairAlreadyClaimed
                profile.SessionId
                profile.LogicalRunId
                requestId
                terminalProviderRun
                repairKind
                (this.ProjectionFor profile.SessionId)

        member internal _.Metadata (key: PromptKey) (origin: string) (logicalRunId: LogicalRunId option) =
            PromptMetadataCodec.create key origin logicalRunId

        /// EXEC-003 requires a terminal listener to exist before a prompt is sent.
        /// This registers the subscription without reacting to it; the reacting
        /// listener belongs to whoever awaits the agent.
        member internal _.SubscribeNoOp (port: IDispatchSessionPort) (sessionId: SessionId) =
            port.SubscribeTerminal(sessionId, (fun _ _ -> ()))

    let forPrompts (journal: IPromptJournal) = Runtime(journal)
