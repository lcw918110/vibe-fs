namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Routing
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Scope
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Prefix.OpenCode
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Mission.Manager
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open PluginHostInterop

module PluginTransforms =

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    [<Import("deliveryAttemptLedger", "wanxiangli/dispatch")>]
    let private deliveryAttemptLedger: obj = jsNative

    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Import("scopeRetention", "wanxiangli/scope-retention")>]
    let private scopeRetention: obj = jsNative


    // The real Host session contains ONLY its negotiation question. This
    // process-live view is what the model receives instead, reusing the last
    // complete delegator wire as an immutable prefix. No mutation of Host history.
    let private negotiationMessages (outObj: obj) : obj array =
        if isNull outObj || isNull outObj?messages then
            [||]
        else
            unbox<obj array> outObj?messages

    let private negotiationSessionId (input: obj) : string option =
        let session =
            if isNull input then null
            elif isNull input?sessionID then input?sessionId
            else input?sessionID

        if isNull session then
            None
        else
            let id = string session
            if String.IsNullOrWhiteSpace id then None else Some id

    let negotiationToolViolationCount (sessionId: string) : int = Surface.toolViolationCount sessionId

    let negotiationRecordToolViolation (sessionId: string) : unit = Surface.recordToolViolation sessionId

    let negotiationIsTemporary (sessionId: string) : bool = Surface.isTemporary sessionId

    let private modelIdentity (input: obj) : obj option =
        let model = if isNull input then null else input?model
        let provider = if isNull model then null else model?providerID

        let identifier =
            if isNull model then null
            elif isNull model?modelID then model?id
            else model?modelID

        let message = if isNull input then null else input?message
        let messageModel = if isNull message then null else message?model

        let variant =
            if not (isNull messageModel) && not (isNull messageModel?variant) then
                messageModel?variant
            elif not (isNull model) then
                model?variant
            else
                null

        let withVariant =
            if isNull variant then
                [ "providerID", provider; "modelID", identifier ]
            else
                [ "providerID", provider; "modelID", identifier; "variant", variant ]

        if isNull provider || isNull identifier then
            None
        else
            Some(createObj withVariant)

    let negotiationRememberModel (input: obj) : unit =
        match negotiationSessionId input, modelIdentity input with
        | Some sessionId, Some model -> Surface.rememberModel sessionId model
        | _ -> ()

    let negotiationModelFor (sessionId: string) : obj = Surface.modelFor sessionId

    let negotiationRememberSystem (input: obj) (output: obj) : unit =
        let observed = if isNull output then null else output?system

        match negotiationSessionId input with
        | Some sessionId when not (isNull observed) -> Surface.rememberSystem sessionId (unbox<string array> observed)
        | _ -> ()

    let negotiationRememberMessages (sessionId: string) (outObj: obj) : unit =
        let messages = negotiationMessages outObj

        if messages.Length > 0 then
            Surface.rememberMessages sessionId messages

    let private responseErrorReason (response: obj) (fallback: string) : string =
        if isNull response?reason then
            fallback
        else
            string response?reason

    let negotiationStartTemporary (tempId: string) (delegatorId: string) : Result<unit, string> =
        let r = Surface.startTemporary tempId delegatorId

        if not (isNull r?ok) && unbox<bool> r?ok then
            Ok()
        else
            Error(responseErrorReason r "startTemporary failed")

    let consultationStartPrevious (tempId: string) (sourceSessionId: string) (metadata: obj) : Result<unit, string> =
        let response = Surface.startTemporaryPrevious tempId sourceSessionId metadata
        if not (isNull response?ok) && unbox<bool> response?ok then Ok()
        else Error(responseErrorReason response "previous Scope source is unavailable in latest provider view")

    let consultationModelForTemporary (tempId: string) : obj = Surface.modelForTemporary tempId

    let negotiationFinishTemporary (tempId: string) : unit = Surface.finishTemporary tempId

    let negotiationForgetSession (sessionId: string) : unit = Surface.forgetSession sessionId

    let negotiationProjectSystem (input: obj) (output: obj) : bool =
        negotiationSessionId input
        |> Option.bind (fun sid ->
            Surface.projectSystem
                sid
                (if isNull output || isNull output?system then
                     [||]
                 else
                     unbox<string array> output?system))
        |> Option.map (fun system ->
            let existing = if isNull output then null else output?system

            if not (isNull existing) && emitJsExpr existing "Array.isArray($0)" then
                emitJsStatement (existing, system) "$0.length = 0; $0.push(...$1)"
            else
                output?system <- system

            true)
        |> Option.defaultValue false

    let private latestTemporaryQuestion (outObj: obj) : obj option =
        negotiationMessages outObj
        |> Array.tryFindBack (fun message ->
            if isNull message then
                false
            else
                let info = message?info
                not (isNull info) && not (isNull info?role) && string info?role = "user")

    let private applyTemporaryQuestion (outObj: obj) (projected: obj array) : bool =
        HostMessageProjection.replaceMessagesInPlace
            outObj
            (HostMessageProjection.sanitizeMessages (Array.toList projected))

        true

    let negotiationProjectMessages (sessionId: string) (outObj: obj) : bool =
        let questionOpt = latestTemporaryQuestion outObj

        // The temporary turn's own steps after its question (a vetoed tool call
        // and its denial) stay visible, so the model reads the rebuke.
        let trailing =
            let messages = negotiationMessages outObj

            match questionOpt with
            | Some question ->
                match Array.tryFindIndexBack (fun m -> obj.ReferenceEquals(m, question)) messages with
                | Some index -> messages[index + 1 ..]
                | None -> [||]
            | None -> [||]

        let projectedOpt =
            questionOpt
            |> Option.bind (fun q -> Surface.projectMessages sessionId q)
            |> Option.map (fun projected -> Array.append projected trailing)

        match projectedOpt with
        | Some projected -> applyTemporaryQuestion outObj projected
        | None -> false

    type private SessionTermination = SessionId -> string -> Task<Result<unit, string>>

    [<RequireQualifiedAccess>]
    type private ProviderStartBoundaryFailure =
        | SnapshotUnavailable of ChatExecutionKey * string
        | HostRunUnavailable of ChatExecutionKey * ProviderRunBinding.Rejection
        | CommittedAdmissionUnavailable of ChatExecutionKey
        | LifecycleRejected of ProviderLifecycle.ProviderStartObservationError<unit>

    let private providerStartBoundaryErrorCode =
        function
        | ProviderStartBoundaryFailure.SnapshotUnavailable _ -> "host-snapshot-unavailable"
        | ProviderStartBoundaryFailure.HostRunUnavailable _ -> "host-run-unavailable"
        | ProviderStartBoundaryFailure.CommittedAdmissionUnavailable _ -> "committed-admission-unavailable"
        | ProviderStartBoundaryFailure.LifecycleRejected error ->
            ProviderLifecycle.providerStartObservationErrorCode error

    type private ProviderStartBoundaryException(failure: ProviderStartBoundaryFailure) =
        inherit
            Exception(
                sprintf
                    "MANAGED-CHAT-005: provider start boundary rejected (%s): %A"
                    (providerStartBoundaryErrorCode failure)
                    failure
            )

        member _.Failure = failure

    let private rejectProviderStartBoundary failure =
        raise (ProviderStartBoundaryException failure)

    type TraceTransformCapture =
        { RawMessages: obj list
          Current: XTraceProjectionState option
          Receipt: XTraceCaptureReceipt option }

    type DeliveryAttemptFence =
        { SessionId: SessionId
          ProviderRun: ProviderRunIdentity
          CallId: ToolCallId }

    /// 交付裁决（条款 4）：Continue = 仍需继续，本次请求照常放行；
    /// Stop = 无需继续，本次请求在 transform 钩子处被中断。
    [<RequireQualifiedAccess>]
    type DeliveryAdjudication =
        | ContinueDelivery
        | StopDelivery of DeliveryAttemptFence

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
          StopCompletedContinuation: string option -> PhysicalUserMessageId option -> obj -> Task<bool>
          BindSessionStartedAt: string option -> Task<DateTimeOffset option>
          ApplyRelayProjection: string option -> obj -> Task<RelayProjectionDisposition>
          ApplyTenureIsolation: string option -> obj -> Task<unit>
          CaptureXTraceMessages: string option -> obj -> Task<TraceTransformCapture>
          RefreshCompanionXTrace: string option -> XTraceProjectionState option -> unit
          ApplyCompanion: string option -> obj -> obj -> Task<unit>
          ApplyXWire: obj -> Task
          FreezeProviderAttemptPlan: string option -> PhysicalUserMessageId option -> Task<unit>
          InjectPairGuideline: string option -> DateTimeOffset option -> obj -> Task<unit>
          SettleAndReplaceDeferredInspections: string option -> obj -> Task<unit>
          SanitizeMessages: string option -> obj -> unit
          /// Advisor is pure chat: no Pair Hint plan, steer or estimate request.
          IsPureChatSession: string option -> bool
          AdjudicateDelivery: string option -> obj -> Task<DeliveryAdjudication>
          StopAfterDelivery:
              string option -> PhysicalUserMessageId option -> DeliveryAttemptFence -> obj -> Task<unit> }

    type TransformBranchCapabilities = { RegisterOwned: string -> unit }

    let private languageFor (_projectionSessionIdOpt: string option) : ProviderLanguage =
        GlobalProviderLanguage.current ()

    [<Emit("{ ...$0 }")>]
    let private shallowCopyObj (source: obj) : obj = jsNative

    let private canonicalCaptureMessages (journal: AgentJournal option) session rawMessages =
        let restore durable =
            task {
                match!
                    PairProgrammingThoughtTransform.stripCursorSuffixesWithJournal
                        durable
                        session
                        PairProgrammingThoughtTransform.CursorPresentationOwner.PairGuidance
                        []
                        rawMessages
                with
                | Error error -> return Error error
                | Ok stripped -> return Ok stripped
            }

        journal
        |> Option.map restore
        |> Option.defaultWith (fun () -> Task.FromResult(Ok rawMessages))

    let private decodePromptOrigin (label: string) : PromptAuthority.PromptOrigin =
        match label with
        | "HumanRoot" -> PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.HumanRoot
        | "AgentOwnerRoot" ->
            PromptAuthority.PromptOrigin.AuthorityRoot PromptAuthority.RootAuthorityKind.AgentOwnerRoot
        | "HostInternal" -> PromptAuthority.PromptOrigin.HostInternal
        | "UnknownOrigin" -> PromptAuthority.PromptOrigin.UnknownOrigin
        | continuation ->
            continuation
            |> PromptAuthority.tryParseContinuationKind
            |> Option.map PromptAuthority.PromptOrigin.Continuation
            |> Option.defaultValue PromptAuthority.PromptOrigin.UnknownOrigin

    let defaultCapabilities (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : NormalTransformCapabilities =
        let scope = boot.Scope
        let journal = boot.Journal
        let clock = boot.Clock
        let workspaceDirectory = boot.WorkspaceDirectory
        let sessionPort = host.SessionPort
        let eventPort = host.EventPort
        let snapshotOpt = host.SnapshotOpt
        let wired = host.Wired

        let requireProviderAdmission key =
            if ModelRouting.readExecutionAdmission key |> Option.isNone then
                rejectProviderStartBoundary (ProviderStartBoundaryFailure.CommittedAdmissionUnavailable key)

        /// managed-chat-execution-015: the exact execution key a refused
        /// provider start boundary addresses; a missing wire identity means
        /// the refusal carries no exact key and is never guessed.
        let exactProviderStartBoundaryKey
            (projectionSessionIdOpt: string option)
            (physicalUserMessageId: PhysicalUserMessageId option)
            : ChatExecutionKey option =
            match projectionSessionIdOpt, physicalUserMessageId with
            | Some sessionText, Some physical when not (String.IsNullOrWhiteSpace sessionText) ->
                Some
                    { SessionId = SessionId.create sessionText
                      PhysicalUserMessageId = physical }
            | _ -> None

        let signalProviderStartBoundaryRejection (key: ChatExecutionKey) (reason: string) () : Task =
            scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.ProviderStartBoundaryRejected(key, reason))

        /// managed-chat-execution-015: report a refused provider start boundary
        /// for the exact execution to the settlement owner. The report must
        /// never replace the original refusal: a failed report is emitted as a
        /// diagnostic and the caller still raises the hook failure below.
        let reportProviderStartBoundaryRejected
            (projectionSessionIdOpt: string option)
            (physicalUserMessageId: PhysicalUserMessageId option)
            (reason: string)
            : Task =
            task {
                let report () =
                    match exactProviderStartBoundaryKey projectionSessionIdOpt physicalUserMessageId with
                    | Some key -> signalProviderStartBoundaryRejection key reason ()
                    | None -> Task.FromResult(()) :> Task

                try
                    do! report ()
                with reportError ->
                    Diagnostic.emit
                        "provider-start-boundary-rejection-report-failed"
                        [ "provider_error", reason; "result", reportError.Message ]
            }

        let observeProviderRun (key: ChatExecutionKey) =
            task {
                let snapshot =
                    snapshotOpt
                    |> Option.defaultWith (fun () ->
                        rejectProviderStartBoundary (
                            ProviderStartBoundaryFailure.SnapshotUnavailable(key, "snapshot port unavailable")
                        ))

                let! messages = snapshot.GetMessages key.SessionId

                let observed =
                    messages
                    |> Result.mapError (fun reason -> ProviderStartBoundaryFailure.SnapshotUnavailable(key, reason))
                    |> Result.bind (fun messages ->
                        ProviderRunBinding.bindableRun (PhysicalUserMessageId.value key.PhysicalUserMessageId) messages
                        |> Result.mapError (fun rejection ->
                            ProviderStartBoundaryFailure.HostRunUnavailable(key, rejection)))

                match observed with
                | Error failure -> return rejectProviderStartBoundary failure
                | Ok assistant ->
                    return
                        { SessionId = key.SessionId
                          PhysicalUserMessageId = key.PhysicalUserMessageId
                          ProviderRun = ProviderRunIdentity.create assistant.Id }
            }

        let persistProviderRun key observed =
            task {
                let! persisted =
                    ProviderLifecycle.persistProviderStartedFromObservation journal scope.BindAttemptPlan observed

                match persisted with
                | Error error ->
                    return rejectProviderStartBoundary (ProviderStartBoundaryFailure.LifecycleRejected error)
                | Ok _ ->
                    requireProviderAdmission key
                    do! wired.ConfirmProviderStarted observed
                    requireProviderAdmission key
            }

        let confirmProviderStarted projectionSessionIdOpt physicalUserMessageId =
            task {
                match projectionSessionIdOpt, physicalUserMessageId with
                | Some sessionText, Some physical ->
                    let key =
                        { SessionId = SessionId.create sessionText
                          PhysicalUserMessageId = physical }

                    requireProviderAdmission key
                    let! observed = observeProviderRun key
                    do! persistProviderRun key observed
                | _ -> return ()
            }

        let drainTermination sessionId =
            function
            | Error error -> Task.FromResult(Error error)
            | Ok() ->
                task {
                    do! scope.DrainChatRecovery sessionId
                    return Ok()
                }

        let terminatePhysical sessionId reason physical =
            task {
                do! scope.SignalChatRecoverySession sessionId ChatExecutionRecoveryLifecycleEvent.SessionCancelled

                let! termination =
                    ManagedSessionTermination.terminate
                        (fun ownerId -> scope.CancelSessionChildren(SessionId.value ownerId))
                        sessionPort
                        eventPort
                        sessionId
                        (physical
                         |> PhysicalUserMessageId.create
                         |> PhysicalUserMessageId.promoteToAuthorityRoot)
                        reason

                return! drainTermination sessionId termination
            }

        let terminateSession: SessionTermination =
            fun sessionId reason ->
                wired.CurrentPhysicalUserMessage(SessionId.value sessionId)
                |> Option.map (terminatePhysical sessionId reason)
                |> Option.defaultWith (fun () ->
                    Task.FromResult(Error "MANAGED-SESSION-017: current authority root unavailable"))

        let freezeProviderAttemptPlan projectionSessionIdOpt physicalUserMessageId =
            task {
                let conflictingPlan existing attempted =
                    invalidOp (
                        sprintf
                            "HOST-BOUNDARY-008: pending attempt plan conflict: existing=(%s) attempted=(%s)"
                            (PendingAttemptPlanAdmission.requestIdentitySummary existing)
                            (PendingAttemptPlanAdmission.requestIdentitySummary attempted)
                    )

                let replaySpecificPlan existing attempted =
                    if PendingAttemptPlanAdmission.samePhysicalAuthority existing attempted then
                        Ok()
                    else
                        conflictingPlan existing attempted

                let freezeAbsentPlan sessionId physicalUserMessageId plan =
                    match scope.Recovery.FreezePendingAttemptPlan sessionId physicalUserMessageId plan with
                    | PendingAttemptPlanAdmission.Admitted _
                    | PendingAttemptPlanAdmission.ReplayedExisting _ -> Ok()
                    | PendingAttemptPlanAdmission.PlanConflict(existing, attempted) when
                        PendingAttemptPlanAdmission.samePhysicalAuthority existing attempted
                        ->
                        Ok()
                    | PendingAttemptPlanAdmission.PlanConflict(existing, attempted) ->
                        conflictingPlan existing attempted
                    | PendingAttemptPlanAdmission.IdentityMismatch(expectedSession, expectedPhysical, attempted) ->
                        invalidOp (
                            sprintf
                                "HOST-BOUNDARY-008: pending attempt plan identity mismatch: expectedSession=%A expectedPhysical=%A attempted=%A"
                                expectedSession
                                expectedPhysical
                                attempted
                        )

                let adaptedFreezeAttemptPlan sessionId physicalUserMessageId plan =
                    match scope.Recovery.TryPendingAttemptPlan sessionId physicalUserMessageId with
                    | Some existing -> replaySpecificPlan existing plan
                    | None -> freezeAbsentPlan sessionId physicalUserMessageId plan

                match!
                    ProviderLifecycle.freezeProviderAttemptPlanForTransform
                        journal
                        adaptedFreezeAttemptPlan
                        projectionSessionIdOpt
                        physicalUserMessageId
                with
                | Ok _ -> do! confirmProviderStarted projectionSessionIdOpt physicalUserMessageId
                | Error error ->
                    let reason = ProviderLifecycle.providerStartObservationErrorCode error

                    // managed-chat-execution-015: the refused start boundary must be
                    // reported to the settlement owner for the exact execution. The
                    // report never replaces the original refusal: a failed report is
                    // emitted as a diagnostic and the hook failure is raised below.
                    do! reportProviderStartBoundaryRejected projectionSessionIdOpt physicalUserMessageId reason

                    return
                        invalidOp (
                            sprintf "HOST-BOUNDARY-008: provider attempt plan freeze failed (%s): %A" reason error
                        )
            }

        let captureXTraceMessages projectionSessionIdOpt (rawMessages: obj list) =
            task {
                match projectionSessionIdOpt with
                | None ->
                    return
                        { RawMessages = rawMessages
                          Current = None
                          Receipt = None }
                | Some sessionId ->
                    let! canonical = canonicalCaptureMessages journal (SessionId.create sessionId) rawMessages

                    let observations =
                        canonical
                        |> Result.defaultWith (fun reason -> raise (InvalidOperationException reason))
                        |> List.choose (fun rawMessage ->
                            ProviderWireCapture.decodeCapturedMessage rawMessage
                            |> Option.map (fun message ->
                                { Message = message
                                  HostMessageId = ProviderWireDecode.hostMessageId rawMessage
                                  Origin =
                                    ProviderWireDecode.promptOriginOfMessage rawMessage
                                    |> Option.map decodePromptOrigin }))

                    match!
                        XTraceCapture.captureObservedMessagesWithReceipt
                            journal
                            (SessionId.create sessionId)
                            observations
                    with
                    | Ok captured ->
                        return
                            { RawMessages = rawMessages
                              Current = captured.Current
                              Receipt = Some captured.Receipt }
                    | Error(XTraceCaptureError.Refused reason) -> return invalidOp reason
                    | Error(XTraceCaptureError.StorageAppendFailed failure) ->
                        return invalidOp (JournalOutcome.JournalAppendFailure.describe failure)
                    | Error(XTraceCaptureError.StorageFailed reason) -> return invalidOp reason
            }

        let ownerRole (sessionId: string) =
            journal
            |> Option.bind (fun durable ->
                let projections = (AgentJournal.snapshot durable).AgentProjections
                let sid = SessionId.create sessionId

                PromptAuthorityProjectionQueries.activeProfile sid projections
                |> Option.orElseWith (fun () -> PromptAuthorityProjectionQueries.lastAuthorityProfile sid projections))
            |> Option.map (fun profile -> profile.CanonicalRole)

        let planSessionOf (sidOpt: string option) : string option =
            match sidOpt with
            | Some sid when not (String.IsNullOrWhiteSpace sid) && ownerRole sid = Some Role.Plan -> Some sid
            | _ -> None

        // work-002 / work-046: opening is a closed entry set. A Plan session
        // opens S1/S2/S3 and a Manager session opens S4 — the same entry covers
        // the Manager's direct opening, a new commission, and the parentless
        // Manager execution session an authorized delivery spawns. Every other
        // role (DevOps, Engineer, Orchestrator, ...) owns no lifecycle work and
        // must not be handed one here.
        let managerSessionOf (sidOpt: string option) : string option =
            match sidOpt with
            | Some sid when not (String.IsNullOrWhiteSpace sid) && ownerRole sid = Some Role.Manager -> Some sid
            | _ -> None

        let tryReadLifecycleWork
            (sid: string)
            : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
            task {
                match workspaceDirectory with
                | Some workspace ->
                    let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                    return Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected commonDir sid
                | None -> return None
            }


        let tenureReanchorEpoch (durable: AgentJournal) (sessionId: SessionId) : ActivePrefixEpoch =
            match AgentProjection.tryFind sessionId (AgentJournal.snapshot durable).AgentProjections with
            | None -> invalidOp "Plan session projection is unavailable"
            | Some session -> session.PrefixEpoch |> Option.defaultValue PrefixEpochProjection.empty

        let reanchorTenure
            (durable: AgentJournal)
            (sessionId: SessionId)
            (tenure: Wanxiangshu.Mission.Planning.ActiveTenureInfo)
            (epoch: ActivePrefixEpoch)
            =
            task {
                let fact =
                    ContextFact.TenureReanchored
                        {| SessionId = sessionId
                           PreviousEpochId = epoch.EpochId
                           NextEpochId = PrefixEpochId.next epoch.EpochId
                           WorkId = tenure.WorkId
                           IncumbencyId = tenure.IncumbencyId |}

                match! AgentJournal.appendAgent (StreamId.Session sessionId) None fact durable with
                | Ok _ -> ()
                | Error failure -> return raise (JournalAppendException failure)
            }

        // COMPANION-003: the Plan session is a top-level main session; nothing
        // else owns its opening. The opening is the first physical user
        // instruction, captured once; a session that already has opening
        // evidence is never rewritten.
        let planOpeningText (rawMessages: obj list) : string option =
            rawMessages
            |> List.choose (fun raw ->
                ProviderWireCapture.decodeCapturedMessage raw
                |> Option.filter (fun message -> message.Role = "user")
                |> Option.map (fun _ -> ProviderWireCapture.formalText raw))
            |> List.tryFind (String.IsNullOrWhiteSpace >> not)

        let appendPlanOpening (durable: AgentJournal) (sessionId: SessionId) (text: string) =
            task {
                match! XTraceCapture.captureOpeningWithReceipt (Some durable) sessionId text [] with
                | Ok _ -> return ()
                | Error(XTraceCaptureError.Refused reason) ->
                    return invalidOp (sprintf "Plan opening capture refused: %s" reason)
                | Error(XTraceCaptureError.StorageAppendFailed failure) ->
                    return invalidOp (JournalOutcome.JournalAppendFailure.describe failure)
                | Error(XTraceCaptureError.StorageFailed reason) ->
                    return invalidOp (sprintf "Plan opening capture failed: %s" reason)
            }

        let capturePlanSessionOpening
            (durable: AgentJournal)
            (sessionId: SessionId)
            (trace: XTraceProjectionState)
            (rawMessages: obj list)
            =
            match planOpeningText rawMessages with
            | Some text when XTraceProjection.openingEvidence trace |> Option.isNone ->
                appendPlanOpening durable sessionId text
            | _ -> Task.FromResult()

        let tenureMessagesOf (rawMessages: obj list) : Wanxiangshu.Mission.Planning.TenureMessage list =
            rawMessages
            |> List.choose (fun raw ->
                ProviderWireCapture.decodeCapturedMessage raw
                |> Option.map (fun message ->
                    { Wanxiangshu.Mission.Planning.TenureMessage.Id = ProviderWireDecode.hostMessageId raw
                      Role = message.Role
                      IsSynthetic = ProviderWireCapture.isSyntheticMessage raw
                      HasSemanticParts = not (List.isEmpty message.Parts)
                      Raw = raw }))

        let isPlanMilestone (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
            match work.Stage with
            | "事实与取舍"
            | "理解与路径"
            | "流程与伪代码"
            | "待执行" -> true
            | _ -> false

        let tenureForActive
            (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
            (epoch: Wanxiangshu.Context.Prefix.ActivePrefixEpoch)
            (sid: string)
            : Wanxiangshu.Mission.Planning.ActiveTenureInfo =
            let incumbentId = work.ActiveId |> Option.defaultValue ""

            { WorkId = sid
              IncumbencyId = incumbentId
              Stage = work.Stage
              OpeningCursor = work.OpeningCursor |> Option.defaultValue 0. |> int64
              IsFreshHandover = not (PrefixEpochProjection.isTenureReanchored incumbentId epoch)
              BarrierToolCallId = work.BarrierToolCallId
              SuccessorTenureId = work.SuccessorTenureId }

        // Plan milestones keep the committed barrier as the visibility cut.
        // Reanchor still advances the prefix epoch; it does not remove messages.
        let tenureForCompleted
            (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
            (sid: string)
            : Wanxiangshu.Mission.Planning.ActiveTenureInfo =
            { WorkId = sid
              IncumbencyId = work.ActiveId |> Option.defaultValue ""
              Stage = "已完成"
              OpeningCursor = work.LastRetirementCursor |> Option.defaultValue -1. |> int64 |> (+) 1L
              IsFreshHandover = false
              BarrierToolCallId = work.BarrierToolCallId
              SuccessorTenureId = work.SuccessorTenureId }

        let isolatePlanTenure (tenure: Wanxiangshu.Mission.Planning.ActiveTenureInfo) (sid: string) (outObj: obj) =
            task {
                let durable =
                    journal
                    |> Option.defaultWith (fun () -> invalidOp "Plan journal is unavailable")

                let sessionId = SessionId.create sid
                let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj

                let! captured =
                    rawMessages
                    |> List.filter (ProviderWireCapture.isSyntheticMessage >> not)
                    |> captureXTraceMessages (Some sid)

                let trace =
                    captured.Current
                    |> Option.defaultWith (fun () -> invalidOp "Plan XTrace is unavailable")

                do! capturePlanSessionOpening durable sessionId trace rawMessages

                if tenure.OpeningCursor > XTraceCursor.sequence (XTraceProjection.headCursor trace) then
                    invalidOp "Plan opening cursor exceeds the captured XTrace frontier"

                let assembled =
                    Wanxiangshu.Mission.Planning.TenureIsolation.assemble
                        (tenureMessagesOf rawMessages)
                        tenure
                        (XTraceProjection.hostMessageFirstCursors trace)

                if assembled.ReanchorRequested then
                    do! reanchorTenure durable sessionId tenure (tenureReanchorEpoch durable sessionId)

                ignore assembled.Messages
            }

        let currentEpochOf (sid: string) : Wanxiangshu.Context.Prefix.ActivePrefixEpoch =
            match journal with
            | Some durable -> tenureReanchorEpoch durable (SessionId.create sid)
            | None -> PrefixEpochProjection.empty

        let applyResolvedTenure (sid: string) (outObj: obj) =
            task {
                match! tryReadLifecycleWork sid with
                | Some work when isPlanMilestone work && work.ActiveId.IsSome ->
                    return! isolatePlanTenure (tenureForActive work (currentEpochOf sid) sid) sid outObj
                | Some work when isPlanMilestone work && work.Delivered ->
                    return! isolatePlanTenure (tenureForCompleted work sid) sid outObj
                | _ -> return ()
            }

        let applyTenureIsolation (sidOpt: string option) (outObj: obj) : Task<unit> =
            task {
                match planSessionOf sidOpt with
                | None -> ()
                | Some sid ->
                    // work-036: the entry hook runs before resolveTenure so
                    // the first tenure exists when the isolation is assembled.
                    // The opening cursor is the XTrace head observed in this
                    // transform. The EventStore write lives in
                    // PlanWorkOpenWiring; this module writes its facts through
                    // AgentJournal only.
                    let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj

                    let! captured =
                        rawMessages
                        |> List.filter (ProviderWireCapture.isSyntheticMessage >> not)
                        |> captureXTraceMessages (Some sid)

                    let observedHead =
                        captured.Current
                        |> Option.map (fun trace -> XTraceCursor.sequence (XTraceProjection.headCursor trace))

                    do! PlanWorkOpenWiring.ensurePlanWorkOpenedForSession workspaceDirectory sid observedHead
                    do! applyResolvedTenure sid outObj
            }

        let acceptedSuccessorRequestFor (sidOpt: string option) (physicalUserMessageId: PhysicalUserMessageId option) =
            match journal, sidOpt, physicalUserMessageId with
            | Some durable, Some sessionId, Some physical when not (String.IsNullOrWhiteSpace sessionId) ->
                let key: ChatExecutionKey =
                    { SessionId = SessionId.create sessionId
                      PhysicalUserMessageId = physical }

                (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key
                |> Option.isSome
            | _ -> false

        let finishedConsultationTurn (physical: PhysicalUserMessageId option) (outObj: obj) =
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj
            let physicalId = physical |> Option.map PhysicalUserMessageId.value
            let current =
                messages
                |> List.tryFindIndex (fun message -> ProviderWireDecode.hostMessageId message = physicalId)
            match current with
            | None -> false
            | Some index ->
                let message = List.item index messages
                let marked =
                    ProviderWireDecode.rawPartsOf message
                    |> List.exists (fun part -> not (isNull part?metadata) && part?metadata?wanxiangshu_consultation_steer = box true)
                let isAssistant item =
                    ProviderWireDecode.firstString (ProviderWireDecode.infoObject item) [ "role" ] = Some "assistant"
                // The agent already ran a model step after this answer landed:
                // the answer was consumed inside a live loop, so this request is
                // ordinary work, never a late wake-up to suppress.
                let answeredAlready = messages |> List.skip (index + 1) |> List.exists isAssistant
                let previous =
                    messages |> List.take index |> List.rev |> List.tryFind isAssistant
                // WHAT[delegation-041]: Manager, Orchestrator and Plan own long
                // work. Their tool-free stop is no closing report; the answer
                // is the nudge that carries the work on, never a wake-up to stop.
                let agentOf item =
                    let info = ProviderWireDecode.infoObject item
                    ProviderWireDecode.firstString info [ "agent" ]
                    |> Option.orElseWith (fun () -> ProviderWireDecode.firstString info [ "mode" ])
                let longRunning =
                    message :: Option.toList previous
                    |> List.exists (fun item ->
                        agentOf item
                        |> Option.bind Roles.tryParseRole
                        |> Option.exists Roles.isLongRunning)
                marked && not answeredAlready && not longRunning && (previous |> Option.exists (fun assistant ->
                    let info = ProviderWireDecode.infoObject assistant
                    let finished = not (isNull info?time) && not (isNull info?time?completed)
                    let calls = ProviderWireDecode.rawPartsOf assistant |> List.exists (fun part -> ProviderWireDecode.firstString part [ "type" ] = Some "tool")
                    // WHAT[delegation-040]: an empty finished turn (finish=length,
                    // no prose) is no closing report. The answer is then the only
                    // thing that keeps the run alive; stopping it left nothing to
                    // settle and nothing to continue.
                    let prose =
                        ProviderWireDecode.rawPartsOf assistant
                        |> List.exists (fun part ->
                            ProviderWireDecode.firstString part [ "type" ] = Some "text"
                            && (ProviderWireDecode.firstString part [ "text" ]
                                |> Option.exists (fun text -> not (String.IsNullOrWhiteSpace text))))
                    finished && not calls && prose))

        /// 条款 3：本次 attempt 是否存在一次已成立的交付。判据只认真实交付
        /// 事实——交付登记时的物理输入必须仍是当前请求的物理输入，物理输入
        /// 已变就不再命中（不得误停新输入或后任）。view 声明、助手正文与
        /// provider stop 都不构成完成证据。
        ///
        /// 判据以 durable 交付见证为准，进程内台账只是快速路径：台账命中直接
        /// 用（快）；台账不命中必须回查 durable 见证——进程重启后台账为空，
        /// 只认台账会把「已成立的交付」误判成「没交付过」，宿主就会继续发本
        /// 应停止的请求。
        ///
        /// 两个来源都查不到见证时本次请求照常放行：这不是「没交付过」的断言，
        /// 只是「无法证明成立」——条款 3 只认真实交付事实，无法证明成立就不能
        /// 当作已完成，所以这里既不停本次请求，也不把交付呈现为已完成。
        let fenceObjOf (fence: DeliveryAttemptFence) : obj =
            createObj [
                "sessionId" ==> SessionId.value fence.SessionId
                "providerRun" ==> ProviderRunIdentity.value fence.ProviderRun
                "callId" ==> ToolCallId.value fence.CallId
            ]

        let adjudicateDelivery (sessionText: string) (outObj: obj) : DeliveryAdjudication =
            let physical =
                outObj
                |> ProviderWireDecode.messagesFromTransformOutput
                |> ProviderWireCapture.lastUserMessageId

            let sessionId = SessionId.create sessionText
            let physicalStr = physical |> Option.map PhysicalUserMessageId.value

            let inMemory: DeliveryAttemptFence option =
                let found: obj option =
                    deliveryAttemptLedger?tryFindEstablished (SessionId.value sessionId, physicalStr)
                found
                |> Option.bind (fun res ->
                    let fence = res?fence
                    if isNull fence then None
                    else
                        Some
                            { SessionId = SessionId.create (string fence?sessionId)
                              ProviderRun = ProviderRunIdentity.create (string fence?providerRun)
                              CallId = ToolCallId.create (string fence?callId) })

            let durableConfirmed: DeliveryAttemptFence option =
                match journal, physicalStr with
                | Some durable, Some phys ->
                    let deliveries =
                        (AgentJournal.snapshot durable).AgentProjections.Deliveries
                        |> Map.toList
                        |> List.filter (fun ((s, _, _), _) -> s = sessionId)
                        |> List.map (fun ((s, r, c), witness) ->
                            createObj [
                                "sessionId" ==> SessionId.value s
                                "providerRun" ==> ProviderRunIdentity.value r
                                "callId" ==> ToolCallId.value c
                                "physicalUserMessageId" ==> PhysicalUserMessageId.value witness.Fence.PhysicalUserMessageId
                            ])
                        |> List.toArray
                    let confirmed: obj option =
                        deliveryAttemptLedger?tryFindDurableEstablished (deliveries, SessionId.value sessionId, phys)
                    confirmed
                    |> Option.bind (fun f ->
                        if isNull f then None
                        else
                            Some
                                { SessionId = SessionId.create (string f?sessionId)
                                  ProviderRun = ProviderRunIdentity.create (string f?providerRun)
                                  CallId = ToolCallId.create (string f?callId) })
                | _ -> None

            inMemory
            |> Option.orElse durableConfirmed
            |> Option.map DeliveryAdjudication.StopDelivery
            |> Option.defaultValue DeliveryAdjudication.ContinueDelivery

        let deliveryInterruptResultName (outcome: Result<unit, string>) =
            match outcome with
            | Ok() -> "ok"
            | Error _ -> "failed"

        /// 中断请求本身：宿主拒绝或抛错都只记成「已观察到失败」，不向上抛。
        let requestRetiredAttemptInterrupt (exactSessionId: SessionId) : Task<Result<unit, string>> =
            task {
                try
                    let! outcome = sessionPort.InterruptAttempt exactSessionId
                    return outcome
                with ex ->
                    return Error ex.Message
            }

        /// 条款 4/6：发出本次 attempt 的中断。「已发出」与「已观察到结算结果」
        /// 是两个分别登记的事实：请求发出不是成功，宿主拒绝了这次中断也不是
        /// 交付失败。中断未确认时已提交的交付仍然成立。
        let issueDeliveryInterrupt
            (exactSessionId: SessionId)
            (sessionText: string)
            (fence: DeliveryAttemptFence)
            : Task =
            task {
                let fenceObj = fenceObjOf fence
                let facts: obj = deliveryAttemptLedger?interruptFacts fenceObj
                let issued: bool = facts?issued

                match issued with
                | true -> ()
                | false ->
                    let interrupt = requestRetiredAttemptInterrupt exactSessionId

                    deliveryAttemptLedger?recordInterruptIssued (fenceObj, interrupt :> Task) |> ignore

                    let! outcome = interrupt

                    let outcomeObj =
                        match outcome with
                        | Ok() -> createObj [ "ok" ==> true ]
                        | Error reason -> createObj [ "ok" ==> false; "error" ==> reason ]

                    deliveryAttemptLedger?recordInterruptObserved (fenceObj, outcomeObj) |> ignore

                    Diagnostic.emit
                        "delivery-interrupt-observed"
                        [ "session_id", sessionText
                          "result", deliveryInterruptResultName outcome
                          "provider_error",
                          (match outcome with
                           | Ok() -> ""
                           | Error reason -> reason) ]
            }

        /// 退休 attempt 的精确围栏：被退休的那次 deliver 的 provider run 与
        /// tool call id。中断的「已发出 / 已观察到」登记在这个围栏上。
        ///
        /// 选退休的口径与 `RelayNarrativeTransform.staleRetirement` 相同：
        /// 第一个带投影切点、结果为 Relayed 的退休。两处口径必须一致，否则
        /// 中断事实会挂到另一个围栏上。
        let retiredAttemptFence
            (sessionId: SessionId)
            (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
            : DeliveryAttemptFence option =
            let retirement =
                lifecycle
                |> Option.bind (fun work ->
                    work.Retirements
                    |> List.tryFind (fun entry -> entry.ProjectionCut && entry.Outcome = "Relayed"))

            retirement
            |> Option.map (fun entry ->
                { SessionId = sessionId
                  ProviderRun = ProviderRunIdentity.create entry.Fence.ProviderRunId
                  CallId = ToolCallId.create entry.Fence.ToolCallId })

        let recordRetiredInterruptIssued (fence: DeliveryAttemptFence option) (issued: Task) =
            match fence with
            | Some exact ->
                deliveryAttemptLedger?recordInterruptIssued (fenceObjOf exact, issued) |> ignore
            | None -> ()

        let recordRetiredInterruptObserved (fence: DeliveryAttemptFence option) (outcome: Result<unit, string>) =
            match fence with
            | Some exact ->
                let outcomeObj =
                    match outcome with
                    | Ok() -> createObj [ "ok" ==> true ]
                    | Error reason -> createObj [ "ok" ==> false; "error" ==> reason ]
                deliveryAttemptLedger?recordInterruptObserved (fenceObjOf exact, outcomeObj) |> ignore
            | None -> ()

        /// 本次退休 attempt 的中断是否已经发出过。同一退休 attempt 的第二次
        /// 请求不得再发一次旧任期的会话中止（条款 011 禁止后果）。
        let retiredInterruptIssued (fence: DeliveryAttemptFence option) =
            fence
            |> Option.exists (fun exact ->
                let facts: obj = deliveryAttemptLedger?interruptFacts (fenceObjOf exact)
                facts?issued)

        /// 抑制下一次 provider step + 登记「已发出」。
        let releaseRetiredProviderStep
            (exactSessionId: SessionId)
            (physical: PhysicalUserMessageId)
            (fence: DeliveryAttemptFence option)
            (interrupt: Task)
            =
            ModelRouting.suppressProviderStep exactSessionId physical
            recordRetiredInterruptIssued fence interrupt

        /// 只抑制下一次 provider step，不发中断。同一退休 attempt 的第二次
        /// 请求仍须被抑制（本次请求不得放行），但不得再发一次旧任期的会话
        /// 中止（条款 011 禁止后果）。
        let suppressRetiredProviderStepOnly (exactSessionId: SessionId) (physical: PhysicalUserMessageId) =
            ModelRouting.suppressProviderStep exactSessionId physical

        /// 停止（条款 4/6）：抑制下一次 provider step + 发出本次 attempt 的中断。
        ///
        /// 「已发出」与「已观察到中断成功」是两个分别登记的事实。没有精确物理
        /// 围栏时既无法释放这次 provider step，也无法把中断绑定到这次 attempt
        /// ——此时中断可能停住新输入或后任（条款 016）。所以缺围栏只记诊断、
        /// 不中断、不崩溃。
        let stopRetiredAttempt
            (exactSessionId: SessionId)
            (sessionText: string)
            (physicalUserMessageId: PhysicalUserMessageId option)
            (fence: DeliveryAttemptFence option)
            : Task =
            task {
                let alreadyIssued = retiredInterruptIssued fence

                match physicalUserMessageId with
                | None -> Diagnostic.emit "retired-attempt-stop-skipped-no-physical-fence" [ "session_id", sessionText ]
                | Some physical when alreadyIssued ->
                    suppressRetiredProviderStepOnly exactSessionId physical

                    Diagnostic.emit "retired-attempt-stop-already-issued" [ "session_id", sessionText ]
                | Some physical ->
                    let interrupt = requestRetiredAttemptInterrupt exactSessionId

                    releaseRetiredProviderStep exactSessionId physical fence (interrupt :> Task)

                    let! outcome = interrupt

                    recordRetiredInterruptObserved fence outcome

                    Diagnostic.emit
                        "retired-attempt-interrupt-observed"
                        [ "session_id", sessionText
                          "physical_user_message_id", PhysicalUserMessageId.value physical
                          "result", deliveryInterruptResultName outcome ]
            }

        /// 接力（条款 011）：派发后继提示。
        ///
        /// 接力与停止是两个分别取证的动作：停止失败不吞掉接力，接力失败也不
        /// 抹掉停止的证据。派发发生在停止结算之后——在途 Host 中断未完成精确
        /// 的 provider-step 释放与物理中断之前不得派发后继。
        let dispatchSuccessorPrompt (sessionText: string) : Task =
            task {
                try
                    do!
                        ManagerWorkflow.maybeDeliverLoop
                            sessionPort
                            host.RootWorkspace
                            journal
                            workspaceDirectory
                            (Some sessionText)

                    Diagnostic.emit "retired-attempt-successor-dispatched" [ "session_id", sessionText ]
                with ex ->
                    Diagnostic.emit
                        "retired-attempt-successor-dispatch-failed"
                        [ "session_id", sessionText
                          "result", deliveryInterruptResultName (Error ex.Message) ]
            }

        { BeginPhysicalProviderAttempt =
            fun sessionId output ->
                task {
                    do! wired.EnsureVisibleInputAdmission output

                    let physicalUserMessageId =
                        output
                        |> ProviderWireDecode.messagesFromTransformOutput
                        |> ProviderWireCapture.lastUserMessageId

                    let acceptedRequest = acceptedSuccessorRequestFor sessionId physicalUserMessageId

                    let! lifecycle =
                        match sessionId with
                        | Some sid -> tryReadLifecycleWork sid
                        | None -> Task.FromResult None

                    if
                        not (
                            RelayNarrativeTransform.isStaleRetiredAttempt
                                journal
                                acceptedRequest
                                sessionId
                                lifecycle
                                output
                        )
                    then
                        do!
                            SessionExecutionBinding.beginPhysicalProviderAttemptForTransform
                                journal
                                scope.Sessions.Quiescence.BeginProviderAttempt
                                sessionId
                                output
                }
          BindSessionStartedAt =
            let port = journal |> Option.map AgentJournalPortAdapter.forSessionStartedAt
            SessionStartedAtLedger.bindSessionStartedAt port clock terminateSession Diagnostic.emit
          StopCompletedContinuation =
            fun sidOpt physical outObj ->
                task {
                    if sidOpt.IsSome && physical.IsSome && finishedConsultationTurn physical outObj then
                        let sid = SessionId.create (Option.get sidOpt)
                        ModelRouting.suppressProviderStep sid (Option.get physical)
                        // WHAT[delegation-039]: the abort below leaves only an empty
                        // aborted turn after this answer. Record the stop first so the
                        // idle reconcile settles the run on the finished report.
                        Wanxiangshu.Foundation.TerminalSignal.CompletedContinuationStops.record
                            sid
                            (PhysicalUserMessageId.value (Option.get physical))
                        RelayNarrativeTransform.clearRequestMessages outObj
                        let! outcome =
                            match boot.PortOpt with
                            | Some port -> port.AbortSession sid
                            | None -> Task.FromResult(Error "No Host transport: cannot interrupt consultation")

                        Diagnostic.emit
                            "completed-continuation-interrupt-observed"
                            [ "session_id", SessionId.value sid
                              "result", deliveryInterruptResultName outcome ]

                        return true
                    else
                        return false
                }
          ApplyRelayProjection =
            fun sidOpt outObj ->
                task {
                    let physicalUserMessageId =
                        outObj
                        |> ProviderWireDecode.messagesFromTransformOutput
                        |> ProviderWireCapture.lastUserMessageId

                    // External human messages and internal recovery/guard continuations
                    // admitted into ChatExecutions belong to this active iteration.
                    let acceptedSuccessorRequest =
                        acceptedSuccessorRequestFor sidOpt physicalUserMessageId

                    let! lifecycle =
                        match sidOpt with
                        | Some sid -> tryReadLifecycleWork sid
                        | None -> Task.FromResult None

                    return!
                        RelayNarrativeTransform.apply
                            journal
                            acceptedSuccessorRequest
                            (fun sid ->
                                task {
                                    let sessionText = SessionId.value sid

                                    // 停止（条款 4/6）：抑制下一次 provider step +
                                    // 发出本次 attempt 的中断。缺精确物理围栏时只记
                                    // 诊断、不中断、不崩溃——中断若停住新输入或后任，
                                    // 比不停更坏（条款 016）。
                                    do!
                                        stopRetiredAttempt
                                            sid
                                            sessionText
                                            physicalUserMessageId
                                            (retiredAttemptFence sid lifecycle)

                                    // 接力（条款 011）：后继提示是另一个动作，
                                    // 独立取证。派发发生在停止结算之后——在途
                                    // Host 中断未完成精确的 provider-step 释放与
                                    // 物理中断之前不得派发后继。
                                    do! dispatchSuccessorPrompt sessionText
                                })
                            sidOpt
                            lifecycle
                            outObj
                }
          ApplyTenureIsolation = applyTenureIsolation
          CaptureXTraceMessages =
            fun projectionSessionIdOpt outObj ->
                task {
                    let! captured =
                        ProviderWireDecode.messagesFromTransformOutput outObj
                        |> captureXTraceMessages projectionSessionIdOpt

                    let established =
                        projectionSessionIdOpt
                        |> Option.filter (String.IsNullOrWhiteSpace >> not)
                        |> Option.map (fun sessionText -> adjudicateDelivery sessionText outObj)
                        |> Option.defaultValue DeliveryAdjudication.ContinueDelivery

                    let shouldOpenManagerWork =
                        match established with
                        | DeliveryAdjudication.StopDelivery _ -> false
                        | DeliveryAdjudication.ContinueDelivery ->
                            managerSessionOf projectionSessionIdOpt |> Option.isSome

                    match captured.Receipt with
                    | Some receipt when shouldOpenManagerWork ->
                        let observedHead = XTraceCursor.sequence receipt.CurrentHead

                        do!
                            ManagerWorkflow.ensureManagerWorkOpened
                                workspaceDirectory
                                (Some observedHead)
                                projectionSessionIdOpt

                        return captured
                    | _ -> return captured
                }
          RefreshCompanionXTrace =
            fun projectionSessionIdOpt traceState ->
                let sessionId = projectionSessionIdOpt |> Option.defaultValue ""
                let found, companion = scope.Sessions.Companions.TryGetValue sessionId

                if found then
                    traceState |> Option.iter companion.RefreshXTrace
          ApplyCompanion =
            let apply _ _ _ = Task.FromResult()

            apply
          ApplyXWire =
            let attempts: AttemptPlanCapability =
                { TryAttemptPlan = scope.TryAttemptPlan
                  TryBindAttemptPlan = scope.TryBindAttemptPlan
                  ConsumeAttemptPlan = scope.ConsumeAttemptPlan
                  FreezePendingAttemptPlan = scope.Recovery.FreezePendingAttemptPlan
                  TryPendingAttemptPlan = scope.Recovery.TryPendingAttemptPlan }

            let wirePort = journal |> Option.map AgentJournalPortAdapter.forWire

            XWire.applyTransform
                (snapshotOpt
                 |> Option.map (fun s -> TranscriptPort.HostTranscriptPort s :> Wanxiangshu.Host.Ports.ITranscriptPort))
                wirePort
                attempts
          FreezeProviderAttemptPlan = freezeProviderAttemptPlan
          InjectPairGuideline =
            fun projectionSessionIdOpt sessionStartedAt outObj ->
                task {
                    let language = languageFor projectionSessionIdOpt

                    // crash-reconciliation-018: after this process's load-phase
                    // normalization, the next real user instruction carries one
                    // restart status guidance. It rides the same pair marker, so
                    // its delivered bytes are frozen by the anchored MarkerText.
                    let restartGuidance =
                        if scope.RestartGuidancePending then
                            Some(ProviderProse.render language "host/restart-guidance" Map.empty)
                        else
                            None

                    do!
                        PairProgrammingThoughtTransform.maybeInjectGuideline
                            journal
                            projectionSessionIdOpt
                            sessionStartedAt
                            clock
                            terminateSession
                            language
                            restartGuidance
                            scope.MarkRestartGuidanceDelivered
                            outObj
                }
          SettleAndReplaceDeferredInspections =
            fun projectionSessionIdOpt outObj ->
                task {
                    match projectionSessionIdOpt, scope.SyncDelegateRuntime with
                    | Some sid, Some sd -> do! SyncDelegateBatching.settleDeferredInspections sd workspaceDirectory sid
                    | _ -> ()

                    if not (isNull outObj) && not (isNull outObj?messages) then
                        let currentMessages = unbox<obj array> outObj?messages |> Array.toList
                        let replaced = SyncDelegateBatching.applyReplacedResults currentMessages
                        HostMessageProjection.replaceMessagesInPlace outObj replaced
                }
          SanitizeMessages =
            fun sidOpt outObj ->
                match sidOpt with
                | Some sid when ownerRole sid = Some Role.Advisor ->
                    HostMessageProjection.sanitizeAdvisorOutputMessages outObj
                | _ -> HostMessageProjection.sanitizeOutputMessages outObj
          IsPureChatSession =
            fun sidOpt ->
                match sidOpt with
                | Some sid when not (String.IsNullOrWhiteSpace sid) -> ownerRole sid = Some Role.Advisor
                | _ -> false

          // 条款 3：裁决只认真实交付事实——本次 attempt 存在一次已成立的
          // 交付，且登记时的物理输入仍是当前物理输入。view 声明、助手正文与
          // provider stop 都不构成完成证据。
          AdjudicateDelivery =
            fun projectionSessionIdOpt outObj ->
                task {
                    match projectionSessionIdOpt with
                    | Some sessionText when not (String.IsNullOrWhiteSpace sessionText) ->
                        return adjudicateDelivery sessionText outObj
                    | _ -> return DeliveryAdjudication.ContinueDelivery
                }

          // 条款 4/6：无需继续时在 transform 钩子处 interrupt。三件套照退休
          // attempt 的现成做法：抑制下一次 provider step、发出本次 attempt 的
          // 中断、清空本次请求的消息（LLM 因此看不到 deliver 回复）。
          // 「已发出」与「已观察到结算结果」分别登记；中断未确认不推翻已提交
          // 的交付，也不把交付呈现为失败。
          StopAfterDelivery =
            fun projectionSessionIdOpt physicalUserMessageId fence outObj ->
                task {
                    match projectionSessionIdOpt with
                    | Some sessionText when not (String.IsNullOrWhiteSpace sessionText) ->
                        let exactSessionId = SessionId.create sessionText

                        // 0) 交付已提交：先用交付见证结算本次子运行，让 owner
                        //    的 join 拿到返回；随后才中断 attempt。
                        journal
                        |> Option.iter (fun durable ->
                            HostForkRunLifecycle.publishDeliveredReturn
                                durable
                                eventPort
                                exactSessionId
                                fence.ProviderRun
                                fence.CallId)

                        // 1) 抑制下一次 provider step：LLM 不会再收到包含 deliver
                        //    回复的下一次请求。
                        physicalUserMessageId
                        |> Option.iter (fun physical -> ModelRouting.suppressProviderStep exactSessionId physical)

                        // 2) 发出本次 attempt 的中断。
                        do! issueDeliveryInterrupt exactSessionId sessionText fence

                        // 3) 清空消息：本次请求没有内容，LLM 看不到 deliver 回复。
                        RelayNarrativeTransform.clearRequestMessages outObj
                    | _ -> ()
                } }

    let defaultBranchCapabilities (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : TransformBranchCapabilities =
        let scope = boot.Scope
        let journal = boot.Journal
        let snapshotOpt = host.SnapshotOpt
        let wired = host.Wired

        { RegisterOwned = wired.RegisterOwned }

    let private tryGetOutMessages (outObj: obj) : obj array option =
        if not (isNull outObj) && not (isNull outObj?messages) then
            Some(unbox<obj array> outObj?messages)
        else
            None

    // Host shape is decoded once. Scope policy only sees the neutral view.
    let private scopeField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private scopeText (value: obj) =
        if isNull value then "" else string value

    let private scopeRole (message: obj) =
        let direct = scopeField message "role"
        let info = scopeField message "info"
        let roleValue = if isNull direct then scopeField info "role" else direct

        match scopeText roleValue with
        | "user" -> "user"
        | "assistant" -> "assistant"
        | "toolResult"
        | "tool"
        | "tool-result"
        | "tool_result" -> "result"
        | "custom" -> "custom"
        | _ -> "other"

    let private scopeParts (message: obj) : obj array =
        let parts = scopeField message "parts"
        let content = scopeField message "content"

        if not (isNull parts) && emitJsExpr parts "Array.isArray($0)" then
            unbox<obj array> parts
        elif not (isNull content) && emitJsExpr content "Array.isArray($0)" then
            unbox<obj array> content
        else
            [||]

    let private scopeTextContent (message: obj) =
        let content = scopeField message "content"

        if not (isNull content) && emitJsExpr content "typeof $0 === 'string'" then
            scopeText content
        else
            scopeParts message
            |> Array.filter (fun part -> scopeText (scopeField part "type") = "text")
            |> Array.map (fun part -> scopeText (scopeField part "text"))
            |> String.concat "\n"

    let private scopeTimestamp (message: obj) =
        let time = scopeField message "timestamp"
        let info = scopeField message "info"
        let infoTimestamp = scopeField info "timestamp"
        let created = scopeField (scopeField info "time") "created"

        if not (isNull time) then time
        elif not (isNull infoTimestamp) then infoTimestamp
        else created

    let private scopeToolId (part: obj) =
        let own = scopeField part "callID"
        let direct = if isNull own then scopeField part "id" else own

        if isNull direct then
            scopeField part "toolCallId"
        else
            direct

    let private scopeTerminalField (state: obj) : string =
        let status = scopeText (scopeField state "status")
        let output = scopeField state "output"
        let error = scopeField state "error"
        let errorText = scopeField state "errorText"

        let isText v =
            not (isNull v) && emitJsExpr v "typeof $0 === 'string'"

        match status with
        | "completed" when isText output -> "output"
        | "error" when isText error -> "error"
        | "error" when isText output -> "output"
        | "error" when isText errorText -> "errorText"
        | _ -> ""

    /// Pull scope annotation off a tool part for PairHint reset barriers.
    let private scopeAnnotationOf (part: obj) : obj =
        let fromBag (bag: obj) =
            if isNull bag then null else scopeField bag "scope"
        let state = scopeField part "state"
        let candidates =
            [ fromBag part
              fromBag (scopeField part "arguments")
              fromBag (scopeField part "input")
              fromBag (scopeField state "input")
              fromBag state ]
        candidates |> List.tryFind (fun v -> not (isNull v)) |> Option.defaultValue null

    let private neutralPart (part: obj) : obj =
        let kind = scopeText (scopeField part "type")

        if kind = "tool" || kind = "toolCall" || kind = "tool-call" || kind = "tool_call" then
            let tool = scopeField part "tool"
            let name = if isNull tool then scopeField part "name" else tool
            let resultField = scopeTerminalField (scopeField part "state")
            let scope = scopeAnnotationOf part
            if isNull scope then
                createObj [ "id", scopeToolId part; "tool", name; "terminalField", box resultField ]
            else
                createObj
                    [ "id", scopeToolId part
                      "tool", name
                      "terminalField", box resultField
                      "scope", scope ]
        elif kind = "toolResult" || kind = "tool-result" || kind = "tool_result" then
            createObj [ "resultFor", scopeToolId part ]
        else
            createObj [ "type", box kind ]

    let private neutralScopeMessage (message: obj) =
        let kind = scopeRole message
        let info = scopeField message "info"
        let attribution = scopeField message "attribution"

        let authored =
            if isNull attribution then
                scopeField info "attribution"
            else
                attribution

        let operator =
            (kind = "user" && scopeText authored <> "agent")
            || (kind = "custom" && scopeText authored = "user")

        let steering = scopeField message "steering"

        let steering =
            if isNull steering then
                scopeField info "steering"
            else
                steering

        let side = kind = "user" && not operator && not (steering = box true)
        let stamp = scopeTimestamp message

        let key =
            (if isNull stamp then "" else scopeText stamp) + ":" + scopeTextContent message

        let resultId =
            let direct = scopeField message "toolCallId"

            if not (isNull direct) then
                direct
            else
                scopeField message "callID"

        createObj
            [ "kind", box kind
              "operator", box operator
              "sideRequest", box side
              "key", box key
              "timestamp", stamp
              "parts", box (scopeParts message |> Array.map neutralPart)
              "resultFor", box (scopeText resultId)
              "resultField", box (if kind = "result" then "content" else "") ]

    let private copyScope (source: obj) =
        emitJsExpr source "Object.assign({}, $0)"

    let private assignScopeParts (source: obj) (target: obj) (parts: obj array) : unit =
        if not (isNull (scopeField source "parts")) then
            target?parts <- box parts
        else
            target?content <- box parts

    let private appendScopeContentTail (message: obj) (suffix: string) : obj =
        let content = scopeField message "content"
        let updated = Surface.appendTailOnce content suffix

        if obj.ReferenceEquals(content, updated) then
            message
        else
            let rewritten = copyScope message
            rewritten?content <- updated
            rewritten

    let private appendScopePartTail (message: obj) (partIndex: int) (fieldName: string) (suffix: string) : obj =
        let parts = scopeParts message
        let part = parts.[partIndex]
        let state = scopeField part "state"
        let current = scopeField state fieldName
        let changed = Surface.appendTailOnce current suffix

        if obj.ReferenceEquals(current, changed) then
            message
        else
            let nextState = copyScope state
            emitJsExpr (nextState, fieldName, changed) "$0[$1] = $2" |> ignore
            let nextPart = copyScope part
            nextPart?state <- nextState
            let nextParts = Array.copy parts
            nextParts.[partIndex] <- nextPart
            let nextMessage = copyScope message
            assignScopeParts message nextMessage nextParts
            nextMessage

    let private appendScopeResultTail (message: obj) (hint: obj) : obj =
        let partIndex = unbox<int> hint?part
        let fieldName = scopeText hint?field
        let suffix = scopeText hint?tail

        if partIndex < 0 then
            appendScopeContentTail message suffix
        else
            appendScopePartTail message partIndex fieldName suffix

    let private applyNeutralHint (messages: obj array) (hint: obj) : unit =
        let index = unbox<int> hint?index
        let value = messages.[index]

        messages.[index] <-
            if scopeText hint?kind = "user" then
                Surface.writeUserHint value [| scopeText hint?tail |]
            else
                appendScopeResultTail value hint

    let private applyNeutralSlot (messages: obj array) (slot: obj) : obj =
        let index = unbox<int> slot?index
        let message = messages.[index]
        let selected = scopeField slot "parts"

        if isNull selected then
            message
        else
            let parts = scopeParts message
            let kept = unbox<int array> selected |> Array.map (fun j -> parts.[j])
            let rewritten = copyScope message
            assignScopeParts message rewritten kept
            emitJsExpr rewritten "delete $0.providerPayload; delete $0.responseId" |> ignore
            rewritten

    let private openCodeScopeSteer (messages: obj array) (prose: string) (strong: bool) : obj array =
        let last = messages |> Array.tryLast |> Option.defaultValue null
        let originalInfo = scopeField last "info"
        let sessionId = scopeField originalInfo "sessionID"
        let info = createObj [ "role", box "user"; "sessionID", sessionId ]

        let steer =
            createObj
                [ "info", box info
                  "parts", box [| createObj [ "type", box "text"; "text", box prose; "synthetic", box true ] |]
                  "steering", box true
                  "attribution", box "agent"
                  "strong", box strong ]

        Array.append messages [| steer |]

    /// Collapse a retracted beat's assistant message to one text part (removeLast projection).
    /// Match keys: message/info id, responseId, providerRunId, and any tool-call /
    /// step id on the message against BeatRetracted.beatId + aliases.
    let private messageBeatKeys (message: obj) : string list =
        let info = scopeField message "info"
        let top =
            [ scopeField info "id"
              scopeField message "id"
              scopeField message "responseId"
              scopeField info "responseId"
              scopeField message "providerRunId"
              scopeField info "providerRunId"
              scopeField message "provider_run_id"
              scopeField info "runId" ]
            |> List.choose (fun v -> if isNull v then None else Some(scopeText v))
            |> List.filter (fun s -> s <> "")
        let parts = scopeField message "parts"
        let fromParts =
            if isNull parts then []
            else
                try
                    let arr: obj array = unbox parts
                    arr
                    |> Array.toList
                    |> List.collect (fun part ->
                        [ scopeField part "id"
                          scopeField part "callID"
                          scopeField part "callId"
                          scopeField part "toolCallId"
                          scopeField part "providerRunId"
                          scopeField (scopeField part "state") "id" ]
                        |> List.choose (fun v -> if isNull v then None else Some(scopeText v))
                        |> List.filter (fun s -> s <> ""))
                with _ ->
                    []
        (top @ fromParts) |> List.distinct

    let private collapseRetracted (sessionId: string) (messages: obj array) : obj array =
        let rows: obj array = unbox (scopeRetention?retractions(sessionId))
        if isNull (box rows) || rows.Length = 0 then messages
        else
            // Map every alias → replacement text.
            let byKey =
                rows
                |> Array.fold
                    (fun (acc: Map<string, string>) row ->
                        let replacement = scopeText (scopeField row "replacement")
                        let beat = scopeText (scopeField row "beatId")
                        let aliasField = scopeField row "aliases"
                        let aliases =
                            let baseKeys = if beat = "" then [] else [ beat ]
                            if isNull aliasField then baseKeys
                            else
                                try
                                    let arr: obj array = unbox aliasField
                                    baseKeys
                                    @ (arr
                                       |> Array.toList
                                       |> List.map scopeText
                                       |> List.filter (fun s -> s <> ""))
                                with _ ->
                                    baseKeys
                        aliases
                        |> List.distinct
                        |> List.fold (fun m key -> Map.add key replacement m) acc)
                    Map.empty
            if Map.isEmpty byKey then messages
            else
                let mutable used = Set.empty<string>
                messages
                |> Array.map (fun message ->
                    if scopeRole message <> "assistant" then message
                    else
                        let keys = messageBeatKeys message
                        let hit =
                            keys
                            |> List.tryPick (fun key ->
                                match Map.tryFind key byKey with
                                | Some replacement when not (Set.contains key used) -> Some(key, replacement)
                                | _ -> None)
                        match hit with
                        | None -> message
                        | Some(key, replacement) ->
                            used <- Set.add key used
                            let info = scopeField message "info"
                            createObj
                                [ "info", box info
                                  "parts",
                                  box
                                      [| createObj
                                             [ "type", box "text"
                                               "text", box replacement ] |] ])

    let private prepareNeutralScope (sessionId: string) (raw: obj array) : obj array =
        let normal = raw |> Array.map neutralScopeMessage
        let plan = Surface.planRequest sessionId (box normal)
        let staged = Array.copy raw
        unbox<obj array> plan?hints |> Array.iter (applyNeutralHint staged)
        let selected = unbox<obj array> plan?slots |> Array.map (applyNeutralSlot staged)
        let collapsed = collapseRetracted sessionId selected
        let retention: obj = scopeRetention?status(sessionId)
        if not (unbox<bool> plan?active) then collapsed
        elif not (isNull plan?strong) then openCodeScopeSteer collapsed (scopeText plan?strong) true
        elif not (isNull retention?known) && unbox<bool> retention?known
             && not (isNull retention?remaining) && unbox<int> retention?remaining = 0 then
            openCodeScopeSteer collapsed (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/reset-due" Map.empty) true
        elif isNull retention?known || not (unbox<bool> retention?known) then
            // No retention scope yet — nudge scope.kind=resetScope (resource path name is historical).
            openCodeScopeSteer collapsed (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/missing-estimate" Map.empty) true
        else collapsed

    let private purgeAndSuffixSession (sessionId: string) (rawMessages: obj array) (outObj: obj) : unit =
        let sideRequest =
            rawMessages
            |> Array.tryLast
            |> Option.map (fun message ->
                neutralScopeMessage message |> fun neutral -> scopeField neutral "sideRequest")
            |> Option.exists (fun value -> not (isNull value) && unbox<bool> value)

        if not sideRequest then
            Surface.onTurnStart sessionId

        let prepared = prepareNeutralScope sessionId rawMessages

        if not (Surface.isUnchanged prepared rawMessages) then
            HostMessageProjection.replaceMessagesInPlace outObj (Array.toList prepared)

    let private applyScopePurgeAndSuffix (sessionIdOpt: string option) (outObj: obj) : unit =
        match sessionIdOpt, tryGetOutMessages outObj with
        | Some sessionId, Some rawMessages -> purgeAndSuffixSession sessionId rawMessages outObj
        | _ -> ()

    /// 普通分支请求的静态管道上下文：预读的物理消息 id 与各 stage 产出的
    /// 会话起点、重放计划、已捕获 XTrace、horizon 只经这份 frame 传递。
    type private TransformFrame =
        { SessionId: string option
          InObj: obj
          OutObj: obj
          PhysicalUserMessageId: PhysicalUserMessageId option
          SessionStartedAt: DateTimeOffset option
          TracedXTrace: XTraceProjectionState option }

    /// 普通分支 stage：具名、静态列出，Run 直接调用一个或一段 caps 入口。
    /// 返回 None 表示本次请求到此终止（退休旧 attempt 已被拦截）。
    type private TransformStage =
        { Name: string
          Run: TransformFrame -> Task<TransformFrame option> }

    let private runOrdinaryStages (stages: TransformStage list) (frame: TransformFrame) : Task<unit> =
        let rec runStages stages frame =
            task {
                match stages with
                | [] -> ()
                | stage :: rest -> return! runOneStage stage rest frame
            }

        and runOneStage (stage: TransformStage) rest frame =
            task {
                match! stage.Run frame with
                | Some next -> return! runStages rest next
                | None -> return ()
            }

        runStages stages frame

    let normalTransform
        (caps: NormalTransformCapabilities)
        (projectionSessionIdOpt: string option)
        (inObj: obj)
        (outObj: obj)
        : Task<unit> =
        task {
            // One owner view: capture the exact transform input before any Scope purge.
            // The paired output is published only after the real transform finishes.
            let incomingMessages: obj array =
                let raw = scopeField outObj "messages"
                if isNull raw then [||]
                else unbox (emitJsExpr raw "structuredClone($0)")

            // 预读：在 Relay 投影改写消息数组之前捕获原始物理用户消息 id。
            let physicalUserMessageId =
                ProviderWireDecode.messagesFromTransformOutput outObj
                |> ProviderWireCapture.lastUserMessageId

            // host-boundary-019 的普通分支固定次序，不得重排；每个 stage 直接调用
            // 对应 caps 入口，Run 返回 None 时本次请求到此终止。
            let stages: TransformStage list =
                [ // 1. SessionExecutionBinding.beginPhysicalProviderAttemptForTransform (durable-evidence gate)
                  { Name = "begin-physical-provider-attempt"
                    Run =
                      fun frame ->
                          task {
                              do! caps.BeginPhysicalProviderAttempt frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 2. SessionStartedAtLedger.tryBindOrAbort
                  { Name = "bind-session-started-at"
                    Run =
                      fun frame ->
                          task {
                              let! sessionStartedAt = caps.BindSessionStartedAt frame.SessionId

                              return
                                  Some
                                      { frame with
                                          SessionStartedAt = sessionStartedAt }
                          } }
                  // 3. Relay projection cut + manager-loop opening. This MUST run
                  // before every trace/compaction owner so retired raw history
                  // cannot be reintroduced later in the composition.
                  { Name = "work-projection-cut"
                    Run =
                      fun frame ->
                          task {
                              do! caps.SettleAndReplaceDeferredInspections frame.SessionId frame.OutObj
                              let! disposition = caps.ApplyRelayProjection frame.SessionId frame.OutObj

                              if disposition = RelayProjectionDisposition.RetiredAttemptStopped then
                                  return None
                              else
                                  return Some frame
                          } }
                  // 4. Tenure isolation (baton handover). Isolates message set for
                  // active tenures (currently Plan); reanchors the prefix epoch and
                  // validates stable message identity. It does not rewrite the message set.
                  { Name = "tenure-isolation"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyTenureIsolation frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 6. XTraceCapture.captureObservedMessagesWithReceipt
                  { Name = "capture-xtrace"
                    Run =
                      fun frame ->
                          task {
                              let! capture = caps.CaptureXTraceMessages frame.SessionId frame.OutObj

                              return
                                  Some
                                      { frame with
                                          TracedXTrace = capture.Current }
                          } }
                  // 7. 交付裁决（条款 4）。投影裁剪之后、放行本次请求之前：
                  // 本次 attempt 存在一次已成立的交付时，本次请求不再放行——
                  // 抑制下一次 provider step、发出本次 attempt 的中断、清空
                  // 消息，LLM 因此看不到 deliver 回复。放在 XTrace 捕获之后
                  // 是为了让交付那一轮的证据先落进轨迹；放在 attempt 计划冻结
                  // 之前是不让一个被抑制的请求留下「已开始」的 provider 事实。
                  { Name = "delivery-adjudication"
                    Run =
                      fun frame ->
                          task {
                              let! disposition = caps.AdjudicateDelivery frame.SessionId frame.OutObj

                              match disposition with
                              | DeliveryAdjudication.ContinueDelivery -> return Some frame
                              | DeliveryAdjudication.StopDelivery fence ->
                                  do!
                                      caps.StopAfterDelivery
                                          frame.SessionId
                                          frame.PhysicalUserMessageId
                                          fence
                                          frame.OutObj

                                  return None
                          } }
                  // 8. CompanionHost.RefreshXTrace
                  { Name = "refresh-companion-xtrace"
                    Run =
                      fun frame ->
                          caps.RefreshCompanionXTrace frame.SessionId frame.TracedXTrace
                          Task.FromResult(Some frame) }
                  // 9. applyCompanionForOrdinaryMaterial
                  { Name = "apply-companion"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyCompanion frame.SessionId frame.InObj frame.OutObj
                              return Some frame
                          } }
                  // 10. XWire.applyTransform. Admits the attempt plan; message
                  // content is never rewritten.
                  { Name = "apply-xwire"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyXWire frame.OutObj
                              return Some frame
                          } }
                  // 11. ProviderLifecycle.freezeProviderAttemptPlanForTransform.
                  // Freeze the exact plan, then confirm the Host's real assistant
                  // identity and durable ProviderStarted before returning its body.
                  { Name = "freeze-provider-attempt-plan"
                    Run =
                      fun frame ->
                          task {
                              do! caps.FreezeProviderAttemptPlan frame.SessionId frame.PhysicalUserMessageId
                              return Some frame
                          } }
                  { Name = "scope-purge"
                    Run =
                      fun frame ->
                          if not (caps.IsPureChatSession frame.SessionId) then
                              applyScopePurgeAndSuffix frame.SessionId frame.OutObj

                          Task.FromResult(Some frame) }
                  { Name = "current-auxiliaries"
                    Run =
                      fun frame ->
                          task {
                              do! caps.InjectPairGuideline frame.SessionId frame.SessionStartedAt frame.OutObj
                              return Some frame
                          } }
                  // 15. Re-apply replaced inspection results after any intermediate insertions
                  { Name = "reapply-deferred-inspections"
                    Run =
                      fun frame ->
                          task {
                              do! caps.SettleAndReplaceDeferredInspections frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 16. HostMessageProjection.sanitizeMessages
                  { Name = "sanitize-output-messages"
                    Run =
                      fun frame ->
                          caps.SanitizeMessages frame.SessionId frame.OutObj
                          Task.FromResult(Some frame) } ]

            let frame: TransformFrame =
                { SessionId = projectionSessionIdOpt
                  InObj = inObj
                  OutObj = outObj
                  PhysicalUserMessageId = physicalUserMessageId
                  SessionStartedAt = None
                  TracedXTrace = None }

            // The late-consultation stop reads the unprojected Host history only.
            // After the ordinary stages the wire is a projection: a committed
            // resetScope retires the ordinary tool parts (and thinking) before the
            // latest reset while keeping resetScope/askPreviousScope/... calls, so
            // the assistant turn just before the answer (e.g. a todowrite-only
            // step) survives as a completed step-start/step-finish shell and looks
            // like a finished tool-free turn. The predicate only inspects the turn
            // before the answer, so the retained resetScope after it never helps. Re-judging that projection aborted
            // live children right after resetScope with nothing left to resume
            // them (2026-10-27/28 field incidents), so it is judged exactly once.
            let! stopped = caps.StopCompletedContinuation projectionSessionIdOpt physicalUserMessageId outObj

            if not stopped then
                do! runOrdinaryStages stages frame

            projectionSessionIdOpt
            |> Option.iter (fun sid ->
                let transformed = negotiationMessages outObj
                if transformed.Length > 0 then Surface.rememberTransform sid incomingMessages transformed)

            outboundViewStore?recordMessages (outObj?messages) |> ignore

        }

    let createWithCaps
        (caps: NormalTransformCapabilities)
        (branches: TransformBranchCapabilities)
        : obj -> obj -> Task<unit> =
        fun (inObj: obj) (outObj: obj) ->
            task {
                let projectionSessionIdOpt =
                    projectionSessionIdFromMessages outObj
                    |> Option.orElseWith (fun () ->
                        if not (isNull inObj) && not (isNull inObj?sessionID) then
                            let sid = string inObj?sessionID
                            if String.IsNullOrWhiteSpace sid then None else Some sid
                        elif not (isNull inObj) && not (isNull inObj?sessionId) then
                            let sid = string inObj?sessionId
                            if String.IsNullOrWhiteSpace sid then None else Some sid
                        else
                            None)

                match projectionSessionIdOpt with
                | Some sessionId when negotiationIsTemporary sessionId ->
                    negotiationProjectMessages sessionId outObj |> ignore
                | _ ->
                    projectionSessionIdOpt |> Option.iter branches.RegisterOwned
                    do! normalTransform caps projectionSessionIdOpt inObj outObj
            }

    /// Provider-facing transform composition: order only.
    /// Relay cut → Companion/XWire → pair;
    /// retired raw history is removed before any downstream context owner.
    let create (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : obj -> obj -> Task<unit> =
        let caps = defaultCapabilities boot host
        let branches = defaultBranchCapabilities boot host
        createWithCaps caps branches
