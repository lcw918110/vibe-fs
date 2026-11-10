namespace Wanxiangshu.Interaction.Dispatch

open Wanxiangshu.Foundation.HostSession

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Foundation.Identity

/// 主体准备认领与宿主载荷，库协调一次实际发送。
[<AutoOpen>]
module PromptDispatcherSend =

    [<Import("DispatchAttempt", "wanxiangli/dispatch")>]
    let private attemptClass: obj = jsNative

    [<Import("promptPhysicalAcceptance", "wanxiangli/dispatch")>]
    let private physicalAcceptance: obj = jsNative

    let private attempt: obj =
        emitJsExpr (attemptClass, physicalAcceptance) "new $0($1)"

    [<Emit("$0.dispose()")>]
    let private disposeNative (_subscription: obj) : unit = jsNative

    let private outcomeToNative (outcome: SendOutcome) : obj =
        match outcome with
        | AdmittedWithReceipt receipt ->
            createObj
                [ "kind" ==> "AdmittedWithReceipt"
                  "receipt" ==> TransportReceipt.value receipt ]
        | AdmittedWithPhysicalMessage physical ->
            createObj
                [ "kind" ==> "AdmittedWithPhysicalMessage"
                  "physicalUserMessageId" ==> PhysicalUserMessageId.value physical ]
        | Retryable reason -> createObj [ "kind" ==> "Retryable"; "reason" ==> reason ]
        | Fatal reason -> createObj [ "kind" ==> "Fatal"; "reason" ==> reason ]
        | AcceptanceUnknown reason -> createObj [ "kind" ==> "AcceptanceUnknown"; "reason" ==> reason ]

    let private outcomeFromNative (outcome: obj) : SendOutcome =
        match unbox<string> outcome?kind with
        | "AdmittedWithReceipt" -> AdmittedWithReceipt(TransportReceipt.create (unbox<string> outcome?receipt))
        | "AdmittedWithPhysicalMessage" ->
            AdmittedWithPhysicalMessage(PhysicalUserMessageId.create (unbox<string> outcome?physicalUserMessageId))
        | "Retryable" -> Retryable(unbox<string> outcome?reason)
        | "Fatal" -> Fatal(unbox<string> outcome?reason)
        | "AcceptanceUnknown" -> AcceptanceUnknown(unbox<string> outcome?reason)
        | other -> invalidOp ("wanxiangli/dispatch returned an unknown send outcome: " + other)

    let private verdictToNative (verdict: Wanxiangshu.Host.Ports.DeliveryVerdict) : obj =
        match verdict with
        | Wanxiangshu.Host.Ports.DeliveryVerdict.OwnedSettled -> createObj [ "kind" ==> "OwnedSettled" ]
        | Wanxiangshu.Host.Ports.DeliveryVerdict.Refused reason ->
            createObj [ "kind" ==> "Refused"; "reason" ==> reason ]
        | Wanxiangshu.Host.Ports.DeliveryVerdict.OutcomeUnknown reason ->
            createObj [ "kind" ==> "OutcomeUnknown"; "reason" ==> reason ]

    let private verdictFromNative (verdict: obj) : DetachedSendVerdict =
        match unbox<string> verdict?kind with
        | "OwnedSettled" -> DetachedSendVerdict.OwnedSettled
        | "Refused" -> DetachedSendVerdict.Refused(unbox<string> verdict?reason)
        | "OutcomeUnknown" -> DetachedSendVerdict.OutcomeUnknown(unbox<string> verdict?reason)
        | other -> invalidOp ("wanxiangli/dispatch returned an unknown detached verdict: " + other)

    let private observerToNative (observer: ContinuationAcceptanceObserver) : obj =
        createObj
            [ yield
                  "notify"
                  ==> Action<string>(fun physical -> observer.Notify(PhysicalUserMessageId.create physical))
              yield
                  "own"
                  ==> Action<obj>(fun owned ->
                      observer.AttachDisposable
                          { new IDisposable with
                              member _.Dispose() = disposeNative owned })
              match observer.Budget with
              | Some budget -> yield "budget" ==> budget
              | None -> () ]

    let private observeSend observe (event: obj) =
        let eventKey = PromptKey.create (unbox<string> event?key)

        match unbox<string> event?kind with
        | "Sending" -> observe (PromptDispatcher.PromptSendObservation.Sending eventKey)
        | "Answered" ->
            observe (PromptDispatcher.PromptSendObservation.Answered(eventKey, outcomeFromNative event?outcome))
        | other -> invalidOp ("wanxiangli/dispatch returned an unknown send observation: " + other)

    let private specOf
        (key: PromptKey)
        (mode: PromptDispatcher.AwaitMode)
        (observer: ContinuationAcceptanceObserver option)
        (observed: (PromptDispatcher.PromptSendObservation -> unit) option)
        (onFailure: (string -> Task) option)
        : obj =
        createObj
            [ yield "key" ==> PromptKey.value key
              yield
                  "mode"
                  ==> (match mode with
                       | PromptDispatcher.AwaitMode.Await -> "await"
                       | PromptDispatcher.AwaitMode.Detached -> "detached")
              match observer with
              | Some observer -> yield "observer" ==> observerToNative observer
              | None -> ()
              match observed with
              | Some observe -> yield "observed" ==> Action<obj>(observeSend observe)
              | None -> ()
              match onFailure with
              | Some notify ->
                  yield
                      "onFailure"
                      ==> Func<obj, Task>(fun verdict -> notify (sprintf "%A" (verdictFromNative verdict)))
              | None -> () ]

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

    let private attemptFromNative (result: obj) : PromptDispatcher.SendAttemptOutcome =
        match unbox<string> result?kind with
        | "Sent" -> PromptDispatcher.SendAttemptOutcome.Sent(PromptKey.create (unbox<string> result?key))
        | "NotSent" -> PromptDispatcher.SendAttemptOutcome.NotSent(unbox<string> result?reason)
        | "Failed" -> PromptDispatcher.SendAttemptOutcome.Failed(unbox<string> result?reason)
        | "AdmissionRejected" ->
            PromptDispatcher.SendAttemptOutcome.AdmissionRejected(unbox<QuiescencePermitFailure> result?failure)
        | other -> invalidOp ("wanxiangli/dispatch returned an unknown attempt outcome: " + other)

    let private unitToNative (pending: Task<Result<unit, string>>) : Task<obj> =
        task {
            let! result = pending

            return
                match result with
                | Ok() -> createObj [ "ok" ==> true ]
                | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
        }

    let private diagnostic (sessionId: SessionId) (key: PromptKey) (event: obj) =
        let reason = unbox<string> event?reason

        let name, result =
            match unbox<string> event?kind with
            | "outcome-unknown" ->
                "detached-prompt-outcome-unknown",
                sprintf "PromptKey %s outcome indeterminate: %s" (PromptKey.value key) reason
            | "abandon-uncommitted" -> "detached-prompt-abandon-uncommitted", reason
            | "observer-failed" -> "detached-prompt-observer-failed", reason
            | other -> invalidOp ("wanxiangli/dispatch returned an unknown diagnostic: " + other)

        Diagnostic.emit name [ "session_id", SessionId.value sessionId; "result", result ]

    let private effectsOf
        (runtime: PromptDispatcher.Runtime)
        (port: IDispatchSessionPort)
        (sessionId: SessionId)
        (key: PromptKey)
        (request: Wanxiangshu.Host.Ports.PromptRequest)
        (metadata: obj option)
        (acceptPhysical: PhysicalUserMessageId -> Task<Result<unit, string>>)
        (admit: (unit -> obj) option)
        : obj =
        createObj
            [ yield
                  "subscribe"
                  ==> Func<obj>(fun () ->
                      let subscription = port.SubscribeTerminal(sessionId, (fun _ _ -> ()))
                      createObj [ "dispose" ==> Action(fun () -> subscription.Dispose()) ])
              yield
                  "send"
                  ==> Func<obj, Task<obj>>(fun late ->
                      let listener: Wanxiangshu.Host.Ports.LateDeliveryListener option =
                          match box late with
                          | null -> None
                          | _ -> Some(fun verdict -> unbox<Task> (emitJsExpr (late, verdictToNative verdict) "$0($1)"))

                      let pending = port.SendPrompt(sessionId, request, metadata, listener)

                      task {
                          let! outcome = pending
                          return outcomeToNative outcome
                      })
              yield
                  "submitted"
                  ==> Func<string, Task<obj>>(fun receipt ->
                      PromptSessionFact.PromptSubmitted
                          {| PromptKey = key
                             SessionId = sessionId
                             Receipt = TransportReceipt.create receipt |}
                      |> runtime.Persist sessionId None
                      |> unitToNative)
              yield
                  "acceptPhysical"
                  ==> Func<string, Task<obj>>(fun physical ->
                      acceptPhysical (PhysicalUserMessageId.create physical) |> unitToNative)
              yield
                  "abandon"
                  ==> Func<obj, Task<obj>>(fun command ->
                      let reason =
                          match unbox<string> command?kind with
                          | "send-failed" -> PromptAbandonReason.SendFailed(unbox<string> command?reason)
                          | "superseded" -> PromptAbandonReason.SupersededBeforePhysicalSend
                          | other -> invalidOp ("wanxiangli/dispatch requested an unknown abandonment: " + other)

                      runtime.Abandon key sessionId reason |> unitToNative)
              yield "diagnostic" ==> Action<obj>(diagnostic sessionId key)
              match admit with
              | Some admit -> yield "admit" ==> Func<obj>(admit)
              | None -> () ]

    let private publicResultOfAttempt =
        function
        | PromptDispatcher.SendAttemptOutcome.Sent key -> Ok key
        | PromptDispatcher.SendAttemptOutcome.NotSent error -> Error error
        | PromptDispatcher.SendAttemptOutcome.Failed error -> Error error
        | PromptDispatcher.SendAttemptOutcome.AdmissionRejected failure ->
            Error(sprintf "idle-derived send admission rejected before physical dispatch: %A" failure)

    type PromptDispatcher.Runtime with

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

                let request: Wanxiangshu.Host.Ports.PromptRequest =
                    { Text = text
                      Route = None
                      Role = Some(Wanxiangshu.Host.Ports.AgentRole agent)
                      Workspace = directory |> Option.map (fun path -> Wanxiangshu.Host.Ports.WorkspacePath path)
                      Correlation = Some key
                      ToolPolicy = tools
                      Delivery = PromptAuthority.deliveryFor origin }

                let metadata = Some(this.Metadata key (PromptDispatcher.originLabel origin) None)

                let acceptFn physicalId =
                    this.AcceptPhysicalAgentOwnerRoot key sessionId physicalId claim.IdentitySeed None
                    |> TaskValue.map (Result.map ignore)

                let spec = specOf key awaitMode onAccepted onSendObserved onDetachedFailure
                let effects = effectsOf this port sessionId key request metadata acceptFn None

                return!
                    unbox<Task<obj>> (attempt?sendInitial (spec, effects))
                    |> TaskValue.map (attemptFromNative >> publicResultOfAttempt)
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
            (delivery: string option)
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
                let request: Wanxiangshu.Host.Ports.PromptRequest =
                    { Text = text
                      Route = None
                      Role = Some(Wanxiangshu.Host.Ports.AgentRole profile.SelectedAgent)
                      Workspace = directory |> Option.map (fun path -> Wanxiangshu.Host.Ports.WorkspacePath path)
                      Correlation = Some key
                      ToolPolicy = tools
                      Delivery = delivery }

                let metadata = Some(this.Metadata key originLabel (Some profile.LogicalRunId))

                let acceptFn physicalId =
                    this.AcceptContinuation key sessionId physicalId
                    |> TaskValue.map (Result.map ignore)

                let physicalVerdict () =
                    let admitted =
                        physicalAdmission
                        |> Option.map (fun check -> check ())
                        |> Option.defaultValue (Ok())

                    match admitted with
                    | Ok() -> createObj [ "kind" ==> "allowed" ]
                    | Error failure -> createObj [ "kind" ==> "rejected"; "failure" ==> failure ]

                let admit () =
                    match this.RequireActiveProfile sessionId profile with
                    | Error reason -> createObj [ "kind" ==> "failed"; "reason" ==> reason ]
                    | Ok() -> physicalVerdict ()

                let spec = specOf key awaitMode onAccepted onSendObserved None

                let effects =
                    effectsOf this port sessionId key request metadata acceptFn (Some admit)

                let! result = unbox<Task<obj>> (attempt?sendContinuation (spec, effects))
                return attemptFromNative result
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
                                (PromptAuthority.deliveryFor origin)
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
            let sendRoot () =
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
            | None -> sendRoot ()

        member this.SendManagedAssignmentObserved
            (port: IDispatchSessionPort)
            (sessionId: SessionId)
            (text: string)
            (issueIdentitySeed: unit -> Result<PromptAuthority.IdentitySeed, string>)
            (directory: string option)
            (awaitMode: PromptDispatcher.AwaitMode)
            (onAccepted: ContinuationAcceptanceObserver option)
            (onSendObserved: (PromptDispatcher.PromptSendObservation -> unit) option)
            (tools: Map<string, bool> option)
            : Task<Result<PromptKey, string>> =
            let sendRoot () =
                match issueIdentitySeed () with
                | Error reason -> Task.FromResult(Error reason)
                | Ok identitySeed ->
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
                        tools

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
                    awaitMode
                    onAccepted
                    onSendObserved
                    tools
            | None -> sendRoot ()

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
