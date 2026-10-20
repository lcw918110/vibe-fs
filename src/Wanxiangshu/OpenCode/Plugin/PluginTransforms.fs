namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
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
open Wanxiangshu.Host
open Wanxiangshu.Change
open Wanxiangshu.Change.Host
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.Handle.OpenCode
open Wanxiangshu.Execution.Delegation.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Git
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Investigation.Semble
open Wanxiangshu.Resources
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Resources
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open PluginHostInterop

module PluginTransforms =

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

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
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
          SanitizeMessages: obj -> unit }

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
                    ProviderLifecycle.persistProviderStartedFromObservation journal scope.TryBindAttemptPlan observed

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

        let isPlanSequence (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
            work.Sequence = [ "S1"; "S2"; "S3" ]

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
              IsFreshHandover = not (PrefixEpochProjection.isTenureReanchored incumbentId epoch) }

        // S1/S2/S3 and post-S3 no longer strip assistant/tool history. Context
        // retention belongs to the view declaration. Reanchor still advances the
        // prefix epoch; it does not remove messages.
        let tenureForPostS3
            (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
            (sid: string)
            : Wanxiangshu.Mission.Planning.ActiveTenureInfo =
            { WorkId = sid
              IncumbencyId = "post-s3"
              Stage = "S3"
              OpeningCursor = work.LastRetirementCursor |> Option.defaultValue -1. |> int64 |> (+) 1L
              IsFreshHandover = false }

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
                | Some work when isPlanSequence work && work.ActiveId.IsSome ->
                    return! isolatePlanTenure (tenureForActive work (currentEpochOf sid) sid) sid outObj
                | Some work when isPlanSequence work && work.Delivered ->
                    return! isolatePlanTenure (tenureForPostS3 work sid) sid outObj
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

                    let handleInterruptionError (error: string) : unit =
                        if error.Contains "MANAGED-SESSION-016" then
                            ()
                        else
                            invalidOp ("MANAGER-LOOP-004: retired attempt interrupt failed: " + error)

                    let! lifecycle =
                        match sidOpt with
                        | Some sid -> tryReadLifecycleWork sid
                        | None -> Task.FromResult None

                    let handleInterruptionError (error: string) : unit =
                        if error.Contains "MANAGED-SESSION-016" then
                            ()
                        else
                            invalidOp ("MANAGER-LOOP-004: retired attempt interrupt failed: " + error)

                    return!
                        RelayNarrativeTransform.apply
                            journal
                            acceptedSuccessorRequest
                            (fun sid ->
                                ManagerWorkflow.continueAfterRetiredAttempt
                                    sessionPort
                                    host.RootWorkspace
                                    journal
                                    workspaceDirectory
                                    (fun exactSessionId ->
                                        task {
                                            match physicalUserMessageId with
                                            | None ->
                                                return
                                                    invalidOp
                                                        "MANAGER-LOOP-004: retired attempt has no exact physical user message"
                                            | Some physical ->
                                                ModelRouting.suppressProviderStep exactSessionId physical

                                                let! interruption = sessionPort.InterruptAttempt exactSessionId

                                                return interruption |> Result.defaultWith handleInterruptionError
                                        })
                                    sid)
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

                    match captured.Receipt with
                    | None -> return captured
                    | Some receipt when managerSessionOf projectionSessionIdOpt |> Option.isSome ->
                        let observedHead = XTraceCursor.sequence receipt.CurrentHead

                        do!
                            ManagerWorkflow.ensureManagerWorkOpened
                                workspaceDirectory
                                (Some observedHead)
                                projectionSessionIdOpt

                        return captured
                    | Some _ -> return captured
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
            XWire.applyTransform snapshotOpt wirePort attempts
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
          SanitizeMessages = fun outObj -> HostMessageProjection.sanitizeOutputMessages outObj }

    let defaultBranchCapabilities (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : TransformBranchCapabilities =
        let scope = boot.Scope
        let journal = boot.Journal
        let snapshotOpt = host.SnapshotOpt
        let wired = host.Wired

        { RegisterOwned = wired.RegisterOwned }

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
                  // active tenures (currently Plan); strips prior assistant/tool messages,
                  // and reanchors prefix on fresh handover.
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
                          caps.SanitizeMessages frame.OutObj
                          Task.FromResult(Some frame) } ]

            let frame: TransformFrame =
                { SessionId = projectionSessionIdOpt
                  InObj = inObj
                  OutObj = outObj
                  PhysicalUserMessageId = physicalUserMessageId
                  SessionStartedAt = None
                  TracedXTrace = None }

            do! runOrdinaryStages stages frame

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
