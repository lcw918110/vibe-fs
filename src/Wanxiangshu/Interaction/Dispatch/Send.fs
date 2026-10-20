namespace Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.OpenCode
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Interaction.Dispatch.OpenCode

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation.Identity

/// PROMPT-005's four-fact send protocol, in one place.
///
/// Claimed → Submitted → PhysicalAccepted, or Claimed → Abandoned. Both members
/// below return the `PromptKey` rather than a message id: at send time no
/// physical message exists yet, and the key is what the caller can later use to
/// recognise the message when `chat.message` delivers it (PROMPT-011).
[<AutoOpen>]
module PromptDispatcherSend =

    let private observeHostSend
        (key: PromptKey)
        (observer: (PromptDispatcher.PromptSendObservation -> unit) option)
        (send: unit -> Task<SendOutcome>)
        : Task<SendOutcome> =
        match observer with
        | None -> send ()
        | Some observe ->
            observe (PromptDispatcher.PromptSendObservation.Sending key)
            let pending = send ()

            task {
                let! outcome = pending
                observe (PromptDispatcher.PromptSendObservation.Answered(key, outcome))
                return outcome
            }

    /// PROMPT-011: the key is derived, never generated.
    ///
    /// Every input comes from the journal fold or the payload, so the same logical
    /// dispatch produces the same key on any process — which is the only reason
    /// recovery can match a Host message back to a pending claim.
    let private deriveKey
        (projection: PromptAuthority.PromptAuthorityProjection)
        (sessionId: SessionId)
        (logicalRunId: LogicalRunId option)
        (authorityRoot: AuthorityRootUserMessageId option)
        (origin: PromptAuthority.PromptOrigin)
        (payloadDigest: string)
        : PromptKey =
        PromptAuthority.claimScopeDigest sessionId logicalRunId origin payloadDigest
        |> fun scope -> PromptAuthority.nextClaimSequence scope projection
        |> PromptAuthority.derivePromptKey
            HostDigest.sha256Hex
            sessionId
            logicalRunId
            authorityRoot
            origin
            payloadDigest

    let private handleAdmittedPhysical
        (submitted: TransportReceipt -> Task<Result<unit, string>>)
        (acceptPhysical: PhysicalUserMessageId -> Task<Result<unit, string>>)
        (physicalId: PhysicalUserMessageId)
        (key: PromptKey)
        : Task<Result<PromptKey, string>> =
        taskResult {
            let! _ = submitted (TransportReceipt.create (PhysicalUserMessageId.value physicalId))
            let! _ = acceptPhysical physicalId
            return key
        }

    let private registerAcceptance (key: PromptKey) (observer: ContinuationAcceptanceObserver option) =
        observer
        |> Option.map (fun owner ->
            let registration = PromptPhysicalAcceptance.register key owner.Notify

            try
                owner.AttachDisposable registration
                registration
            with error ->
                registration.Dispose()
                raise error)

    let private releaseRegistration (registration: IDisposable option) =
        registration |> Option.iter (fun owned -> owned.Dispose())

    let private beginOwnedConfirmation key registration (observer: ContinuationAcceptanceObserver) =
        let outcome, subscription = PromptPhysicalAcceptance.beginConfirmation key None

        try
            observer.AttachDisposable subscription
            outcome, subscription
        with error ->
            subscription.Dispose()
            releaseRegistration registration
            raise error

    let private invokeObservedHostSend key onSendObserved registration send =
        try
            observeHostSend key onSendObserved send
        with error ->
            releaseRegistration registration
            raise error

    let private releaseFailedResult registration result =
        if Result.isError result then
            releaseRegistration registration

    let private releaseFailedAttempt registration result =
        match result with
        | PromptDispatcher.SendAttemptOutcome.Sent _ -> ()
        | _ -> releaseRegistration registration

    let private persistSubmittedFact
        (key: PromptKey)
        (sessionId: SessionId)
        (persist: PromptSessionFact -> Task<Result<unit, string>>)
        (receipt: TransportReceipt)
        : Task<Result<unit, string>> =
        persist (
            PromptSessionFact.PromptSubmitted
                {| PromptKey = key
                   SessionId = sessionId
                   Receipt = receipt |}
        )

    let private admissionVerdict
        (key: PromptKey)
        (confirmation: PromptPhysicalOutcome option)
        : Result<PromptKey, string> =
        match confirmation with
        | Some(PromptPhysicalOutcome.Accepted _) -> Ok key
        | Some(PromptPhysicalOutcome.Rejected reason) -> Error(sprintf "Prompt admission rejected: %s" reason)
        | None -> Error(sprintf "Acceptance unknown for PromptKey %s: confirmation timed out" (PromptKey.value key))

    let private awaitAdmissionConfirmation
        (key: PromptKey)
        (confirmationWaiterOpt: Task<PromptPhysicalOutcome option> option)
        : Task<Result<PromptKey, string>> =
        match confirmationWaiterOpt with
        | None -> Task.FromResult(Ok key)
        | Some confirmationTask ->
            task {
                let! confirmation = confirmationTask
                return admissionVerdict key confirmation
            }

    let private settleAdmittedReceipt
        (key: PromptKey)
        (sessionId: SessionId)
        (persist: PromptSessionFact -> Task<Result<unit, string>>)
        (confirmationWaiterOpt: Task<PromptPhysicalOutcome option> option)
        (receipt: TransportReceipt)
        : Task<Result<PromptKey, string>> =
        task {
            let! persisted = persistSubmittedFact key sessionId persist receipt

            match persisted with
            | Error err -> return Error err
            | Ok() -> return! awaitAdmissionConfirmation key confirmationWaiterOpt
        }

    let private awaitPhysicalAwareSend
        (key: PromptKey)
        (sessionId: SessionId)
        (persist: PromptSessionFact -> Task<Result<unit, string>>)
        (acceptPhysical: PhysicalUserMessageId -> Task<Result<unit, string>>)
        (abandon: PromptAbandonReason -> string -> Task<Result<unit, string>>)
        (sendTask: Task<SendOutcome>)
        (confirmationWaiterOpt: Task<PromptPhysicalOutcome option> option)
        : Task<Result<PromptKey, string>> =
        let settle =
            task {
                let! outcome = sendTask

                match outcome with
                | AdmittedWithReceipt receipt ->
                    return! settleAdmittedReceipt key sessionId persist confirmationWaiterOpt receipt
                | AdmittedWithPhysicalMessage physicalId ->
                    return!
                        handleAdmittedPhysical
                            (persistSubmittedFact key sessionId persist)
                            acceptPhysical
                            physicalId
                            key
                | Retryable error ->
                    let! _ = abandon (PromptAbandonReason.SendFailed error) error
                    return Error error
                | Fatal error ->
                    let! _ = abandon (PromptAbandonReason.SendFailed error) error
                    return Error error
                | AcceptanceUnknown reason ->
                    return Error(sprintf "Acceptance unknown for PromptKey %s: %s" (PromptKey.value key) reason)
            }

        task {
            try
                return! settle
            with ex ->
                return Error ex.Message
        }

    let private publicResultOfAttempt =
        function
        | PromptDispatcher.SendAttemptOutcome.Sent key -> Ok key
        | PromptDispatcher.SendAttemptOutcome.NotSent error -> Error error
        | PromptDispatcher.SendAttemptOutcome.Failed error -> Error error
        | PromptDispatcher.SendAttemptOutcome.AdmissionRejected failure ->
            Error(sprintf "idle-derived send admission rejected before physical dispatch: %A" failure)

    let private continuationAttemptOutcome (outcome: SendOutcome) (result: Result<PromptKey, string>) =
        match outcome, result with
        | (Retryable _ | Fatal _), Error error -> PromptDispatcher.SendAttemptOutcome.NotSent error
        | _, Ok sentKey -> PromptDispatcher.SendAttemptOutcome.Sent sentKey
        | _, Error error -> PromptDispatcher.SendAttemptOutcome.Failed error

    let private awaitPhysicalAwareContinuationAttempt
        (sendTask: Task<SendOutcome>)
        (record: SendOutcome -> Task<Result<PromptKey, string>>)
        : Task<PromptDispatcher.SendAttemptOutcome> =
        task {
            try
                let! outcome = sendTask
                let! result = record outcome
                return continuationAttemptOutcome outcome result
            with ex ->
                return raise ex
        }

    let private physicalSendAdmission (admission: (unit -> Result<unit, QuiescencePermitFailure>) option) =
        admission |> Option.map (fun admit -> admit ()) |> Option.defaultValue (Ok())

    /// Detached dispatch observer: the success rail stays silent, the
    /// failure rail receives exactly one string form of the typed verdict.
    /// Pulled out as a module-level helper to flatten the consumer's pyramid
    /// — the try/wrap stays at one level in both this file's bodies.
    let private notifyDetachedFailureListener
        (sessionId: SessionId)
        (verdict: DetachedSendVerdict)
        (callback: string -> Task)
        : Task =
        task {
            try
                do! callback (sprintf "%A" verdict)
            with ex ->
                Diagnostic.emit
                    "detached-prompt-observer-failed"
                    [ "session_id", SessionId.value sessionId; "result", ex.Message ]
        }

    type PromptDispatcher.Runtime with

        /// Record the Host's answer and report what the caller may conclude.
        ///
        /// `AcceptanceUnknown` deliberately writes nothing. PROMPT-011 keeps such a
        /// key Pending so recovery can look for the physical message later;
        /// abandoning it here would license a resend, and resending is exactly how
        /// one logical prompt becomes two physical ones.
        member private this.RecordSendOutcome
            (key: PromptKey)
            (sessionId: SessionId)
            (outcome: SendOutcome)
            (acceptPhysical: PhysicalUserMessageId -> Task<Result<unit, string>>)
            : Task<Result<PromptKey, string>> =
            task {
                let submitted (receipt: TransportReceipt) =
                    PromptSessionFact.PromptSubmitted
                        {| PromptKey = key
                           SessionId = sessionId
                           Receipt = receipt |}
                    |> this.Persist sessionId None

                // `Abandoned` is written by `Runtime.Abandon` (PROMPT-005 single writer).
                // Constructing the fact here as well would make PROMPT-011's recovery a
                // second writer of the same fact with its own copy of the payload shape.
                let abandon (reason: PromptAbandonReason) (error: string) =
                    task {
                        match! this.Abandon key sessionId reason with
                        | Ok() -> return Error error
                        | Error persistError -> return Error persistError
                    }

                match outcome with
                | AdmittedWithReceipt receipt ->
                    // PROMPT-005: an `accepted-*` receipt is not a message identity, so
                    // the chain stops at Submitted. `chat.message` supplies the physical
                    // id later and PromptIngress writes PhysicalAccepted then.
                    // PROMPT-007 Detached: this is already a complete success for the caller.
                    let! persisted = submitted receipt
                    return persisted |> Result.map (fun () -> key)

                | AdmittedWithPhysicalMessage physicalId ->
                    // The Host answered with a real id. That answer is still the
                    // transport receipt — it is simply not admission-shaped — so the
                    // four-stage chain stays intact instead of skipping Submitted.
                    return! handleAdmittedPhysical submitted acceptPhysical physicalId key

                | Retryable error -> return! abandon (PromptAbandonReason.SendFailed error) error
                | Fatal error -> return! abandon (PromptAbandonReason.SendFailed error) error

                | AcceptanceUnknown reason ->
                    return Error(sprintf "Acceptance unknown for PromptKey %s: %s" (PromptKey.value key) reason)
            }

        member private this.PersistDetachedInvocation(key: PromptKey, sessionId: SessionId) =
            // PROMPT-007: this is a local invocation receipt, not a physical
            // message id and not the eventual SDK Promise result. It is durable
            // before the Detached caller returns so immediate reuse/recovery sees
            // the claim as already handed to Host async enqueue.
            PromptSessionFact.PromptSubmitted
                {| PromptKey = key
                   SessionId = sessionId
                   Receipt = TransportReceipt.create ("accepted-detached-" + PromptKey.value key) |}
            |> this.Persist sessionId None

        /// The detached envelope's eventual verdict routed to this exact
        /// PromptKey: OwnedSettled means the caller-visible path already
        /// decided; Refused licenses aborting the claim (nothing reached the
        /// Host); OutcomeUnknown keeps the claim pending on durable evidence.
        /// Nothing else — no fatal, no resend, no fabricated success.
        member internal this.SettleDetachedSend
            (key: PromptKey)
            (sessionId: SessionId)
            (verdict: DetachedSendVerdict)
            (onFailure: (string -> Task) option)
            : Task =
            task {
                match verdict with
                | DetachedSendVerdict.OwnedSettled ->
                    // The sendTask or a synchronous throw already decided the
                    // caller-visible outcome; a late arrival must not re-settle.
                    ()
                | DetachedSendVerdict.Refused reason -> return! this.SettleDetachedSendRefused key sessionId reason
                | DetachedSendVerdict.OutcomeUnknown reason ->
                    // PROMPT-011: never resent, never abandoned — the claim
                    // stays Pending on durable evidence until chat.message
                    // proves physical acceptance or a later explicit abandon
                    // closes it.
                    Diagnostic.emit
                        "detached-prompt-outcome-unknown"
                        [ "session_id", SessionId.value sessionId
                          "result", sprintf "PromptKey %s outcome indeterminate: %s" (PromptKey.value key) reason ]

                match verdict, onFailure with
                | DetachedSendVerdict.OwnedSettled, _
                | _, None -> ()
                | verdict, Some callback -> return! notifyDetachedFailureListener sessionId verdict callback
            }

        /// PROMPT-007: observe a Host send after a Detached caller has already
        /// received its PromptKey. The synchronous sendTask verdict and the
        /// eventual detached verdict both route through SettleDetachedSend —
        /// the same typed owner evidence. Nothing fails the whole process for
        /// a late Host transport result.
        member private this.ObserveDetachedSend
            (key: PromptKey)
            (sessionId: SessionId)
            (sendTask: Task<SendOutcome>)
            (onFailure: (string -> Task) option)
            : unit =
            let classifyDetachedOutcome outcome =
                match outcome with
                | AdmittedWithReceipt _
                | AdmittedWithPhysicalMessage _ ->
                    // Detached never races chat.message by writing
                    // PhysicalAccepted from an SDK return value. The exact
                    // Host ingress is the sole physical-identity authority.
                    DetachedSendVerdict.OwnedSettled
                | Retryable error
                | Fatal error -> DetachedSendVerdict.Refused error
                | AcceptanceUnknown reason -> DetachedSendVerdict.OutcomeUnknown reason

            task {
                let! verdict =
                    task {
                        try
                            let! outcome = sendTask
                            return classifyDetachedOutcome outcome
                        with ex ->
                            return DetachedSendVerdict.OutcomeUnknown ex.Message
                    }

                do! this.SettleDetachedSend key sessionId verdict onFailure
            }
            |> ignore

        /// PROMPT-002: a plugin-owned Authority Root.
        ///
        /// The root's LogicalRunId and AuthorityRootUserMessageId are both `None` in
        /// the key derivation because neither exists yet — this send is what creates
        /// them. Substituting empty strings would make "no run yet" and "a run named
        /// empty" derive the same key.
        member private this.SendAgentOwnerRootCore
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (identitySeed: PromptAuthority.IdentitySeed)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (onDetachedFailure: (string -> Task) option)
            (tools: Map<string, bool> option)
            : Task<Result<PromptKey, string>> =
            taskResult {
                let! participantIdentity =
                    this.ValidateAgentOwnerIdentitySeed identitySeed
                    |> Result.mapError PromptDispatcher.describeIdentitySeedRejection

                let agent = ParticipantIdentity.selectedAgent participantIdentity
                let payloadDigest = HostDigest.sha256Hex text

                let origin =
                    PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot

                let key =
                    deriveKey (this.ProjectionFor sessionId) sessionId None None origin payloadDigest

                let! claim = PromptAuthorityRun.claimAgentOwnerRoot key sessionId payloadDigest identitySeed

                let claimed =
                    PromptSessionFact.PromptClaimed
                        {| PromptKey = key
                           SessionId = sessionId
                           ContinuationKind = PromptDispatcher.originLabel origin
                           LogicalRunId = None
                           AuthorityRootUserMessageId = None
                           IdentitySeed = claim.IdentitySeed
                           PayloadDigest = payloadDigest |}

                let! _ = this.Persist sessionId None claimed

                // EXEC-003: the terminal listener must exist before the prompt
                // does, or a fast completion has nobody to deliver to.
                use _listener = this.SubscribeNoOp port sessionId

                let options =
                    { Model = None
                      Agent = Some agent
                      Directory = directory
                      Metadata = Some(this.Metadata key (PromptDispatcher.originLabel origin) None)
                      Tools = tools
                      DetachedListener =
                        match awaitMode with
                        | PromptDispatcher.AwaitMode.Detached ->
                            Some(fun verdict -> this.SettleDetachedSend key sessionId verdict onDetachedFailure)
                        | PromptDispatcher.AwaitMode.Await -> None }

                let registration = registerAcceptance key onAccepted

                let confirmation =
                    match awaitMode, onAccepted with
                    | PromptDispatcher.AwaitMode.Await, Some observer ->
                        Some(beginOwnedConfirmation key registration observer)
                    | _ -> None

                let acceptFn physicalId =
                    this.AcceptPhysicalAgentOwnerRoot key sessionId physicalId claim.IdentitySeed
                    |> TaskValue.map (Result.map ignore)

                let awaitRootSend sendTask =
                    task {
                        let! outcome =
                            awaitPhysicalAwareSend
                                key
                                sessionId
                                (fun fact -> this.Persist sessionId None fact)
                                acceptFn
                                (fun reason error ->
                                    this.Abandon key sessionId reason |> TaskValue.map (fun _ -> Error error))
                                sendTask
                                (confirmation |> Option.map fst)

                        releaseFailedResult registration outcome
                        return outcome
                    }

                let sendClaimedRoot sendTask =
                    match awaitMode with
                    | PromptDispatcher.AwaitMode.Detached ->
                        taskResult {
                            let! _ = this.PersistDetachedInvocation(key, sessionId)
                            this.ObserveDetachedSend key sessionId sendTask onDetachedFailure
                            return key
                        }
                    | PromptDispatcher.AwaitMode.Await -> awaitRootSend sendTask

                try
                    let sendTask =
                        invokeObservedHostSend key onSendObserved registration (fun () ->
                            port.SendPrompt(sessionId, text, options))

                    let! result = sendClaimedRoot sendTask
                    return result
                finally
                    confirmation |> Option.iter (fun (_, owned) -> owned.Dispose())
            }

        member this.SendAgentOwnerRoot
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (identitySeed: PromptAuthority.IdentitySeed)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            : Task<Result<PromptKey, string>> =
            this.SendAgentOwnerRootCore port sessionId text identitySeed directory awaitMode onAccepted None None None

        member this.SendAgentOwnerRootDetachedObserved
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (identitySeed: PromptAuthority.IdentitySeed)
            (directory: string option)
            (onFailure: string -> Task)
            : Task<Result<PromptKey, string>> =
            this.SendAgentOwnerRootCore
                port
                sessionId
                text
                identitySeed
                directory
                PromptDispatcher.AwaitMode.Detached
                None
                None
                (Some onFailure)
                None

        member this.SendAgentOwnerRootWithTools
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (identitySeed: PromptAuthority.IdentitySeed)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool>)
            : Task<Result<PromptKey, string>> =
            this.SendAgentOwnerRootCore
                port
                sessionId
                text
                identitySeed
                directory
                awaitMode
                onAccepted
                onSendObserved
                None
                (Some tools)

        /// PROMPT-003: a continuation of an existing Logical Run.
        ///
        /// Inherits the run and root from the profile, so its key derivation has both.
        ///
        /// `payloadDigest` is a parameter rather than `sha256 text` computed here,
        /// because provider-attempt-recovery-008 needs one continuation kind to digest something
        /// other than its text. See `SendInteractionRepair`.
        member private this.SendClaimedContinuation
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (originLabel: string)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool> option)
            (physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) option)
            (key: PromptKey)
            : Task<PromptDispatcher.SendAttemptOutcome> =
            task {
                use _listener = this.SubscribeNoOp port sessionId

                let options =
                    { Model = None
                      Agent = Some profile.SelectedAgent
                      Directory = directory
                      Metadata = Some(this.Metadata key originLabel (Some profile.LogicalRunId))
                      Tools = tools
                      DetachedListener =
                        match awaitMode with
                        | PromptDispatcher.AwaitMode.Detached ->
                            Some(fun verdict -> this.SettleDetachedSend key sessionId verdict None)
                        | PromptDispatcher.AwaitMode.Await -> None }


                let registration = registerAcceptance key onAccepted

                // The admission check and the Host call are deliberately
                // synchronous neighbours. No await may reopen a window where
                // newer physical material can arrive after quiescence was
                // proven but before SendPrompt is invoked.
                let sendAdmitted () : Task<PromptDispatcher.SendAttemptOutcome> =
                    task {
                        let sendTask =
                            observeHostSend key onSendObserved (fun () -> port.SendPrompt(sessionId, text, options))

                        let acceptFn physicalId =
                            this.AcceptContinuation key sessionId physicalId
                            |> TaskValue.map (Result.map ignore)

                        let detachedOutcome () : Task<PromptDispatcher.SendAttemptOutcome> =
                            task {
                                let! persisted = this.PersistDetachedInvocation(key, sessionId)

                                match persisted with
                                | Error error -> return PromptDispatcher.SendAttemptOutcome.Failed error
                                | Ok() ->
                                    this.ObserveDetachedSend key sessionId sendTask None
                                    return PromptDispatcher.SendAttemptOutcome.Sent key
                            }

                        let sendAfterAdmission () : Task<PromptDispatcher.SendAttemptOutcome> =
                            match awaitMode with
                            | PromptDispatcher.AwaitMode.Detached -> detachedOutcome ()
                            | PromptDispatcher.AwaitMode.Await ->
                                awaitPhysicalAwareContinuationAttempt sendTask (fun outcome ->
                                    this.RecordSendOutcome key sessionId outcome acceptFn)

                        return! sendAfterAdmission ()
                    }

                let dispatchClaimedContinuation () =
                    let admission =
                        match this.RequireActiveProfile sessionId profile with
                        | Error error -> Error(PromptDispatcher.SendAttemptOutcome.Failed error)
                        | Ok() ->
                            physicalSendAdmission physicalAdmission
                            |> Result.mapError PromptDispatcher.SendAttemptOutcome.AdmissionRejected

                    match admission with
                    | Error failure ->
                        releaseRegistration registration

                        this.Abandon key sessionId PromptAbandonReason.SupersededBeforePhysicalSend
                        |> TaskValue.map (function
                            | Ok() -> failure
                            | Error error -> PromptDispatcher.SendAttemptOutcome.Failed error)
                    | Ok() ->
                        task {
                            let! result = sendAdmitted ()
                            releaseFailedAttempt registration result
                            return result
                        }

                try
                    return! dispatchClaimedContinuation ()
                with error ->
                    releaseRegistration registration
                    return raise error
            }

        member private this.SendContinuationWithDigestAttempt
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (payloadDigest: string)
            (continuation: PromptAuthority.ContinuationKind)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool> option)
            (physicalAdmission: (unit -> Result<unit, QuiescencePermitFailure>) option)
            : Task<PromptDispatcher.SendAttemptOutcome> =
            let claimAndSend () =
                task {
                    let origin = PromptAuthority.PromptOrigin.Continuation continuation
                    let originLabel = PromptDispatcher.originLabel origin

                    let key =
                        deriveKey
                            (this.ProjectionFor sessionId)
                            sessionId
                            (Some profile.LogicalRunId)
                            (Some profile.AuthorityRootUserMessageId)
                            origin
                            payloadDigest

                    let claim =
                        PromptAuthorityRun.claimContinuation key sessionId continuation profile payloadDigest

                    let claimed =
                        PromptSessionFact.PromptClaimed
                            {| PromptKey = key
                               SessionId = sessionId
                               ContinuationKind = originLabel
                               LogicalRunId = claim.LogicalRunId
                               AuthorityRootUserMessageId = claim.AuthorityRootUserMessageId
                               IdentitySeed = claim.IdentitySeed
                               PayloadDigest = payloadDigest |}

                    match! this.Persist sessionId None claimed with
                    | Error error -> return PromptDispatcher.SendAttemptOutcome.Failed error
                    | Ok() ->
                        return!
                            this.SendClaimedContinuation
                                port
                                sessionId
                                text
                                originLabel
                                profile
                                directory
                                awaitMode
                                onAccepted
                                onSendObserved
                                tools
                                physicalAdmission
                                key
                }

            match this.RequireActiveProfile sessionId profile with
            | Error error -> Task.FromResult(PromptDispatcher.SendAttemptOutcome.Failed error)
            | Ok() -> claimAndSend ()

        member private this.SendContinuationWithDigest
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (payloadDigest: string)
            (continuation: PromptAuthority.ContinuationKind)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool> option)
            : Task<Result<PromptKey, string>> =
            this.SendContinuationWithDigestAttempt
                port
                sessionId
                text
                payloadDigest
                continuation
                profile
                directory
                awaitMode
                onAccepted
                onSendObserved
                tools
                None
            |> TaskValue.map publicResultOfAttempt

        member this.SendContinuation
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (continuation: PromptAuthority.ContinuationKind)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            : Task<Result<PromptKey, string>> =
            this.SendContinuationWithDigest
                port
                sessionId
                text
                (HostDigest.sha256Hex text)
                continuation
                profile
                directory
                awaitMode
                onAccepted
                None
                None

        /// Non-idle gate reminder with exact terminal occasion identity. Used by
        /// terminal-subscriber gates (for example Relay exit-required)
        /// that do not derive authority from SessionIdle.
        member this.SendGateNudge
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (continuation: PromptAuthority.ContinuationKind)
            (gateKind: string)
            (terminalProviderRun: ProviderRunIdentity)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            : Task<Result<PromptKey, string>> =
            let payloadDigest =
                PromptAuthority.gateNudgePayloadDigest gateKind terminalProviderRun

            let scope =
                PromptAuthority.claimScopeDigest
                    sessionId
                    (Some profile.LogicalRunId)
                    (PromptAuthority.PromptOrigin.Continuation continuation)
                    payloadDigest

            this.RunGateNudgeOnce(
                scope,
                fun () ->
                    this.SendContinuationWithDigest
                        port
                        sessionId
                        text
                        payloadDigest
                        continuation
                        profile
                        directory
                        awaitMode
                        onAccepted
                        None
                        None
            )

        member this.SendContinuationWithTools
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (continuation: PromptAuthority.ContinuationKind)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool>)
            : Task<Result<PromptKey, string>> =
            this.SendContinuationWithDigest
                port
                sessionId
                text
                (HostDigest.sha256Hex text)
                continuation
                profile
                directory
                awaitMode
                onAccepted
                onSendObserved
                (Some tools)

        member private this.SendAgentOwnerRootWithSeed
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (directory: string option)
            (tools: Map<string, bool> option)
            (issueIdentitySeed: unit -> Result<PromptAuthority.IdentitySeed, string>)
            : Task<Result<PromptKey, string>> =
            match issueIdentitySeed () with
            | Error reason -> Task.FromResult(Error reason)
            | Ok identitySeed ->
                this.SendAgentOwnerRootCore
                    port
                    sessionId
                    text
                    identitySeed
                    directory
                    PromptDispatcher.AwaitMode.Detached
                    None
                    None
                    None
                    tools

        member this.SendManagedAssignment
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (issueIdentitySeed: unit -> Result<PromptAuthority.IdentitySeed, string>)
            (directory: string option)
            (tools: Map<string, bool> option)
            : Task<Result<PromptKey, string>> =
            match (this.ProjectionFor sessionId).ActiveLogicalRun with
            | Some profile ->
                this.SendContinuationWithDigest
                    port
                    sessionId
                    text
                    (HostDigest.sha256Hex text)
                    PromptAuthority.ContinuationKind.ManagedDelegationAssignment
                    profile
                    directory
                    PromptDispatcher.AwaitMode.Detached
                    None
                    None
                    tools
            | None -> this.SendAgentOwnerRootWithSeed port sessionId text directory tools issueIdentitySeed

        /// provider-attempt-recovery-008: the one request + terminal-scoped interaction repair an unusable terminal earns.
        ///
        /// Its payload digest names the occasion (RepairRequestId + terminal
        /// provider run + repair kind), not the prompt text. Request identity
        /// prevents an earlier request on the same long-lived run from
        /// spending the next request's nudge/AABB budget.
        ///
        /// Deriving the digest this way is also what makes the budget durable: it
        /// enters the claim scope, so the `ClaimSequences` that PROMPT-005 `Claimed`
        /// already writes is the counter `RepairAlreadyClaimed` reads back.
        member this.SendInteractionRepair
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (requestId: RepairRequestId)
            (terminalProviderRun: ProviderRunIdentity)
            (repairKind: string)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            : Task<Result<PromptKey, string>> =
            this.SendContinuationWithDigest
                port
                sessionId
                text
                (PromptAuthority.repairPayloadDigest requestId terminalProviderRun repairKind)
                PromptAuthority.ContinuationKind.InteractionRepair
                profile
                directory
                awaitMode
                onAccepted
                None
                None

        /// HOST-004: idle-derived continuation whose quiescence permit is
        /// consumed at the final physical SendPrompt boundary, after durable
        /// claim persistence. This is the only continuation send surface that
        /// may return `Superseded`.
        member internal this.SendIdleContinuation
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (continuation: PromptAuthority.ContinuationKind)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
            : Task<PromptDispatcher.SendAttemptOutcome> =
            this.SendContinuationWithDigestAttempt
                port
                sessionId
                text
                (HostDigest.sha256Hex text)
                continuation
                profile
                directory
                awaitMode
                onAccepted
                None
                None
                (Some physicalAdmission)

        /// Gate reminder: exactly-once for one terminal occasion, intentionally
        /// unbounded across fresh terminals while the business gate remains open.
        member internal this.SendIdleGateNudge
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (continuation: PromptAuthority.ContinuationKind)
            (gateKind: string)
            (terminalProviderRun: ProviderRunIdentity)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
            (observer: ContinuationAcceptanceObserver option)
            : Task<PromptDispatcher.SendAttemptOutcome> =
            this.SendContinuationWithDigestAttempt
                port
                sessionId
                text
                (PromptAuthority.gateNudgePayloadDigest gateKind terminalProviderRun)
                continuation
                profile
                directory
                awaitMode
                observer
                None
                None
                (Some physicalAdmission)

        member internal this.SendIdleInteractionRepair
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (requestId: RepairRequestId)
            (terminalProviderRun: ProviderRunIdentity)
            (repairKind: string)
            (profile: PromptAuthority.AuthorityExecutionProfile)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (physicalAdmission: unit -> Result<unit, QuiescencePermitFailure>)
            : Task<PromptDispatcher.SendAttemptOutcome> =
            this.SendContinuationWithDigestAttempt
                port
                sessionId
                text
                (PromptAuthority.repairPayloadDigest requestId terminalProviderRun repairKind)
                PromptAuthority.ContinuationKind.InteractionRepair
                profile
                directory
                awaitMode
                None
                None
                None
                (Some physicalAdmission)

        /// Refused arm of SettleDetachedSend — kept flat so the outer match
        /// stays a single level; persistence errors report through the
        /// diagnostic lane rather than a second nested match arm.
        member private this.SettleDetachedSendRefused (key: PromptKey) (sessionId: SessionId) (reason: string) : Task =
            task {
                let! result = this.Abandon key sessionId (PromptAbandonReason.SendFailed reason)

                match result with
                | Ok() -> ()
                | Error persistError ->
                    Diagnostic.emit
                        "detached-prompt-abandon-uncommitted"
                        [ "session_id", SessionId.value sessionId; "result", persistError ]
            }
