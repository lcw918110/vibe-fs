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
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Cycle
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
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Mission.Relay.OpenCode
open Wanxiangshu.Mission.Manager
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Strength
open Wanxiangshu.Strength.Replica
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Host
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.Change
open Wanxiangshu.Change.Host
open Wanxiangshu.Context.Companion.Blogger.OpenCode
open Wanxiangshu.Enforcer
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.Handle.OpenCode
open Wanxiangshu.Execution.Delegation.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Git
open Wanxiangshu.Git.Hook
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Investigation.Semble
open Wanxiangshu.Resources
open Wanxiangshu.Strength.OpenCode
open Wanxiangshu.Strength.Persistence
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session
open Wanxiangshu.Enforcer
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Resources
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Cycle
open Wanxiangshu.Enforcer.Guidance
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
open Wanxiangshu.Strength
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
          Current: XTraceProjectionState option }

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
          BindSessionStartedAt: string option -> Task<DateTimeOffset option>
          ApplyStrengthReplay: string option -> obj -> Task<StrengthReplayPlan list>
          RestoreProtocolArguments: obj -> Task<unit>
          ApplyRelayProjection: string option -> obj -> Task<RelayProjectionDisposition>
          ApplyTenureIsolation: string option -> obj -> Task<unit>
          CaptureXTraceMessages: string option -> obj -> Task<TraceTransformCapture>
          CommitStrengthTrace: string option -> XTraceProjectionState option -> StrengthReplayPlan list -> Task<unit>
          RefreshCompanionXTrace: string option -> XTraceProjectionState option -> unit
          ApplyCompanion: string option -> obj -> obj -> Task<unit>
          ApplyXWire: obj -> Task<PrefixPresentationHorizon>
          FreezeProviderAttemptPlan: string option -> obj -> Task<unit>
          ApplyEnforcerContinuation: string option -> obj -> Task<unit>
          ApplyReadonlyDelegation: string option -> obj -> Task<unit>
          InjectPairGuideline: string option -> DateTimeOffset option -> obj -> Task<unit>
          ProjectRequirementGrounding: string option -> obj -> Task<unit>
          InjectBloggerChronicle:
              string option -> Wanxiangshu.Foundation.Identity.PhysicalUserMessageId option -> obj -> unit
          SettleAndReplaceDeferredInspections: string option -> obj -> Task<unit>
          SanitizeMessages: obj -> unit }

    type TransformBranchCapabilities =
        { RegisterOwned: string -> unit
          ReplicaRuntime: string option -> StrengthReplicaRuntime option
          ReplicaXWire: obj -> Task<unit>
          ReplicaSanitize: obj -> unit }


    let private languageFor (_projectionSessionIdOpt: string option) : ProviderLanguage =
        GlobalProviderLanguage.current ()

    // Explicit composition mode — replaces the previous implicit helper dispatch
    // (strengthReplicaRuntime / ordinaryProviderTransform).
    // This type is representation-level (composition topology), not a foreign domain decision.
    type private TransformMode =
        | StrengthReplica of StrengthReplicaRuntime
        | Ordinary

    let private failIfReplicaDecisionLost (handled: bool) : unit =
        if not handled then
            raise (InvalidOperationException "StrengthReplica transform lost its live decision binding")

    [<Emit("{ ...$0 }")>]
    let private shallowCopyObj (source: obj) : obj = jsNative

    let private raiseFailClosed (fuse: string -> unit) (reason: string) : 'a =
        fuse reason
        raise (InvalidOperationException reason)

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
                | Ok paired ->
                    return!
                        PairProgrammingThoughtTransform.stripCursorSuffixesWithJournal
                            durable
                            session
                            PairProgrammingThoughtTransform.CursorPresentationOwner.RequirementGrounding
                            []
                            paired
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

    let private determineTransformMode
        (branches: TransformBranchCapabilities)
        (projectionSessionIdOpt: string option)
        (outObj: obj)
        : TransformMode =
        match branches.ReplicaRuntime projectionSessionIdOpt with
        | Some runtime -> StrengthReplica runtime
        | None -> Ordinary

    let defaultCapabilities (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : NormalTransformCapabilities =
        let scope = boot.Scope
        let journal = boot.Journal
        let clock = boot.Clock
        let workspaceDirectory = boot.WorkspaceDirectory
        let sessionPort = host.SessionPort
        let eventPort = host.EventPort
        let snapshotOpt = host.SnapshotOpt
        let strengthDurability = host.StrengthDurability
        let wired = host.Wired
        let strengthFailFuse = boot.StrengthFailFuse

        let requireProviderAdmission key =
            if ModelRouting.readExecutionAdmission key |> Option.isNone then
                rejectProviderStartBoundary (ProviderStartBoundaryFailure.CommittedAdmissionUnavailable key)

        /// managed-chat-execution-015: the exact execution key a refused
        /// provider start boundary addresses; a missing wire identity means
        /// the refusal carries no exact key and is never guessed.
        let exactProviderStartBoundaryKey
            (projectionSessionIdOpt: string option)
            (outObj: obj)
            : ChatExecutionKey option =
            match projectionSessionIdOpt with
            | Some sessionText when not (String.IsNullOrWhiteSpace sessionText) ->
                outObj
                |> ProviderWireDecode.messagesFromTransformOutput
                |> ProviderWireCapture.lastUserMessageId
                |> Option.map (fun physical ->
                    { SessionId = SessionId.create sessionText
                      PhysicalUserMessageId = physical })
            | _ -> None

        let signalProviderStartBoundaryRejection (key: ChatExecutionKey) (reason: string) () : Task =
            scope.SignalChatRecovery(ChatExecutionRecoveryLifecycleEvent.ProviderStartBoundaryRejected(key, reason))

        /// managed-chat-execution-015: report a refused provider start boundary
        /// for the exact execution to the settlement owner. The report must
        /// never replace the original refusal: a failed report is emitted as a
        /// diagnostic and the caller still raises the hook failure below.
        let reportProviderStartBoundaryRejected
            (projectionSessionIdOpt: string option)
            (outObj: obj)
            (reason: string)
            : Task =
            task {
                let report () =
                    match exactProviderStartBoundaryKey projectionSessionIdOpt outObj with
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

        let confirmProviderStarted projectionSessionIdOpt outObj =
            task {
                let physical =
                    outObj
                    |> ProviderWireDecode.messagesFromTransformOutput
                    |> ProviderWireCapture.lastUserMessageId

                match projectionSessionIdOpt, physical with
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

        let freezeProviderAttemptPlan projectionSessionIdOpt outObj =
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
                        outObj
                with
                | Ok _ -> do! confirmProviderStarted projectionSessionIdOpt outObj
                | Error error ->
                    let reason = ProviderLifecycle.providerStartObservationErrorCode error

                    // managed-chat-execution-015: the refused start boundary must be
                    // reported to the settlement owner for the exact execution. The
                    // report never replaces the original refusal: a failed report is
                    // emitted as a diagnostic and the hook failure is raised below.
                    do! reportProviderStartBoundaryRejected projectionSessionIdOpt outObj reason

                    return
                        invalidOp (
                            sprintf "HOST-BOUNDARY-008: provider attempt plan freeze failed (%s): %A" reason error
                        )
            }

        // / speculative-investigation-014: the only
        // enablement condition for explicit read-only delegation is that a
        // Predictor model is configured. The read-only configuration
        // existence query is the process-shared
        // ModelRouting.sharedPredictorConfiguration, loaded once together
        // with the sole MJS model configuration during the PluginBoot Load
        // Phase (PluginBoot.create runs ModelRouting.initialize before any
        // transform or hook is constructed), and is shared by tool
        // decoration (PluginHooks) and delegation admission here, so this
        // seam consumes that one query instead of holding a second enabled
        // truth. Configured enables admission; NotConfigured authorizes
        // nothing. ConfigurationInvalid is a malformed model configuration
        // and fails closed here, the same choice as the PluginHooks
        // tool-decoration seam and ModelRouting.requireRoutingProtocol: it
        // is never silently degraded to "not configured". The transform
        // hook's registered disposition (HookPolicy MessagesTransform:
        // Workflow / TypedPolicyFailClosed, diagnostic operation
        // plugin-hook-messages-transform-failed) carries the report.
        let predictorConfigured () : bool =
            match ModelRouting.sharedPredictorConfiguration () with
            | ModelRouting.PredictorConfiguration.Configured -> true
            | ModelRouting.PredictorConfiguration.NotConfigured -> false
            | ModelRouting.PredictorConfiguration.ConfigurationInvalid reason ->
                raise (
                    InvalidOperationException(
                        sprintf "execution-model-routing: Predictor model configuration is invalid: %s" reason
                    )
                )

        // host-boundary-032 / provider-facing wire-layer
        // restore of the protocol fields. The Host persists tool-call input
        // after the before hook stripped the protocol fields, so every later
        // provider request is built from stripped history. The before hook
        // recorded the wire originals in the process-local vault; this step
        // merges them back into the request's assistant tool-call parts before
        // delegation capture (13.3) or the replica batch collector reads the
        // same history. Pure per part: business arguments verbatim, protocol
        // keys appended when missing or different, results untouched, no vault
        // entry means fail-open.
        let restoreMergedPart (part: obj) (state: obj) (hasStateInput: bool) (current: obj) (merged: obj) : obj =
            if obj.ReferenceEquals(merged, current) then
                part
            elif hasStateInput then
                let stateCopy = shallowCopyObj state
                stateCopy?input <- merged
                let partCopy = shallowCopyObj part
                partCopy?state <- stateCopy
                partCopy
            else
                let partCopy = shallowCopyObj part
                partCopy?args <- merged
                partCopy

        let tryRestoreSnapshot (part: obj) (snapshot: ProtocolArgumentVault.Snapshot) : obj =
            let state = ProviderWireDecode.readField part "state"
            let hasStateInput = not (isNull state) && not (isNull state?input)
            let hasTopLevelArgs = not (isNull part?args)

            if not hasStateInput && not hasTopLevelArgs then
                part
            else
                let current = if hasStateInput then state?input else part?args
                let merged = ProtocolArgumentVault.restoreArguments snapshot current
                restoreMergedPart part state hasStateInput current merged

        let tryRestoreWithSnapshot (part: obj) (snapshotOpt: ProtocolArgumentVault.Snapshot option) : obj =
            match snapshotOpt with
            | Some snapshot -> tryRestoreSnapshot part snapshot
            | None -> part

        let tryRestoreWithCallId
            (vault: ProtocolArgumentVault.Vault)
            (sessionId: string)
            (part: obj)
            (callId: string option)
            : obj =
            match callId with
            | None -> part
            | Some id -> tryRestoreWithSnapshot part (ProtocolArgumentVault.tryFind vault sessionId id)

        let restoreToolCallPart
            (vault: ProtocolArgumentVault.Vault)
            (sessionId: string)
            (part: obj)
            (isToolCallPart: bool)
            : obj =
            if not isToolCallPart then
                part
            else
                tryRestoreWithCallId
                    vault
                    sessionId
                    part
                    (ProviderWireDecode.firstString part [ "callID"; "callId"; "id" ])

        let restorePart (vault: ProtocolArgumentVault.Vault) (sessionId: string) (part: obj) : obj =
            if isNull part then
                part
            else
                let kind =
                    ProviderWireDecode.firstString part [ "type" ]
                    |> Option.defaultValue ""
                    |> fun value -> value.ToLowerInvariant()

                let isToolCallPart = kind = "tool" || kind = "tool-call" || kind = "tool_call"
                restoreToolCallPart vault sessionId part isToolCallPart

        let restoreRawParts (raw: obj) (parts: obj list) (restoredParts: obj list) : obj =
            if List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) parts restoredParts then
                raw
            else
                let copy = shallowCopyObj raw
                copy?parts <- box (List.toArray restoredParts)
                copy

        let restoreMessage (vault: ProtocolArgumentVault.Vault) (sessionId: string) (raw: obj) : obj =
            if isNull raw then
                raw
            else
                let parts = ProviderWireDecode.rawPartsOf raw
                let restoredParts = parts |> List.map (restorePart vault sessionId)
                restoreRawParts raw parts restoredParts

        let applyRewrittenMessages (outObj: obj) (rawMessages: obj list) (rewritten: obj list) : unit =
            if not (List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) rawMessages rewritten) then
                HostMessageProjection.replaceMessagesInPlace outObj rewritten

        let rewriteSessionMessages (vault: ProtocolArgumentVault.Vault) (outObj: obj) (sessionId: string) : unit =
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj
            let rewritten = rawMessages |> List.map (restoreMessage vault sessionId)
            applyRewrittenMessages outObj rawMessages rewritten

        let tryRestoreSessionArguments
            (vault: ProtocolArgumentVault.Vault)
            (outObj: obj)
            (sessionIdOpt: string option)
            : unit =
            match sessionIdOpt with
            | Some sessionId -> rewriteSessionMessages vault outObj sessionId
            | None -> ()

        let restoreProtocolArguments (outObj: obj) : Task<unit> =
            task {
                if not (isNull outObj) && not (isNull outObj?messages) then
                    tryRestoreSessionArguments
                        boot.ProtocolArgumentVault
                        outObj
                        (ProviderWireDecode.projectionSessionIdFromMessages outObj)
            }

        let applyReadonlyDelegation projectionSessionIdOpt outObj =
            StrengthDelegate.tryCaptureAndStart
                snapshotOpt
                journal
                strengthDurability
                boot.StrengthScope
                scope.TryAttemptPlan
                scope.SyncDelegateRuntime
                (predictorConfigured ())
                projectionSessionIdOpt
                (Some boot.Timer)
                outObj

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

        let tenureHasAssistantOrTool (rawMessages: obj list) : bool =
            rawMessages
            |> List.exists (fun raw ->
                let tm = Wanxiangshu.Mission.Planning.TenureIsolation.messageOfRaw raw
                tm.Role = "assistant" || tm.Role = "tool")

        let tryReadActivePlanState () : Wanxiangshu.Mission.Planning.PlanWorkState option =
            journal
            |> Option.bind (fun durable ->
                Wanxiangshu.Mission.Planning.PlanEventStore.tryActiveWorkState (fun key ->
                    durable.Writer.TryCurrent key))

        let buildTenureOf
            (state: Wanxiangshu.Mission.Planning.PlanWorkState)
            (fresh: bool)
            : Wanxiangshu.Mission.Planning.ActiveTenureInfo =
            let active = state.Active.Value

            let workId =
                state.WorkId
                |> Option.map Wanxiangshu.Mission.Planning.PlanWorkId.value
                |> Option.defaultValue ""

            let previousRange =
                state.LatestRetirement
                |> Option.map (fun (_, _, startC, endC) -> (XTraceCursor.sequence startC, XTraceCursor.sequence endC))

            { WorkId = workId
              IncumbencyId = Wanxiangshu.Mission.Planning.PlanIncumbencyId.value active.Id
              Stage = Wanxiangshu.Mission.Planning.PlanStage.render active.Stage
              OpeningCursor = XTraceCursor.sequence active.OpeningCursor
              PreviousRange = previousRange
              IsFreshHandover = fresh }

        let resolveTenure (outObj: obj) : Wanxiangshu.Mission.Planning.ActiveTenureInfo option =
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj
            let fresh = not (tenureHasAssistantOrTool rawMessages)

            tryReadActivePlanState ()
            |> Option.filter (fun state -> state.Active.IsSome)
            |> Option.map (fun state -> buildTenureOf state fresh)

        let tenureReanchorEpoch (durable: AgentJournal) (sessionId: SessionId) : ActivePrefixEpoch option =
            match AgentProjection.tryFind sessionId (AgentJournal.snapshot durable).AgentProjections with
            | None -> None
            | Some session -> session.PrefixEpoch

        let emitTenureReanchorSkipped (reason: string) : unit =
            Diagnostic.emit "plan-tenure-reanchor-skipped" [ "result", reason ]

        let handleReanchorTarget (requested: bool) : unit =
            if requested then
                emitTenureReanchorSkipped "no-journal-or-session"
            else
                ()

        let reportTenureReanchorAppend (appendTask: Task<Result<ProjectionSet, JournalAppendFailure>>) (workId: string) (incumbencyId: string) : Task<unit> =
            task {
                match! appendTask with
                | Ok _ -> ()
                | Error failure ->
                    Diagnostic.emit
                        "plan-tenure-reanchor-append-failed"
                        [ "work_id", workId
                          "incumbency_id", incumbencyId
                          "result", JournalAppendFailure.describe failure ]
            }

        let fireTenureReanchor
            (durable: AgentJournal)
            (sessionId: SessionId)
            (workId: string)
            (incumbencyId: string)
            : unit =
            match tenureReanchorEpoch durable sessionId with
            | None -> emitTenureReanchorSkipped "no-epoch"
            | Some epoch ->
                let fact =
                    ContextFact.TenureReanchored
                        {| SessionId = sessionId
                           PreviousEpochId = epoch.EpochId
                           NextEpochId = PrefixEpochId.next epoch.EpochId
                           WorkId = workId
                           IncumbencyId = incumbencyId |}

                let appendTask =
                    AgentJournal.appendAgent (StreamId.Session sessionId) None fact durable

                reportTenureReanchorAppend appendTask workId incumbencyId |> ignore

        let tryReadReanchorRequested (result: obj) : bool =
            try
                unbox<bool> result?reanchorRequested
            with _ ->
                false

        let isTenureReanchorRequested (result: obj) : bool =
            if isNull result then
                false
            else
                tryReadReanchorRequested result

        let assembleAndRewrite
            (sidOpt: string option)
            (outObj: obj)
            (tenure: Wanxiangshu.Mission.Planning.ActiveTenureInfo)
            : unit =
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj

            let result =
                Wanxiangshu.Mission.Planning.PlanningSurface.assembleTenureMessages
                    (box (rawMessages |> List.toArray))
                    (box tenure)
                    (fun _ -> "")

            let assembled = unbox<obj array> result?messages |> Array.toList
            HostMessageProjection.replaceMessagesInPlace outObj assembled

            let requested = isTenureReanchorRequested result

            let target =
                match journal, sidOpt, requested with
                | Some durable, Some sid, true when not (String.IsNullOrWhiteSpace sid) ->
                    Some(durable, SessionId.create sid)
                | _ -> None

            match target with
            | None -> handleReanchorTarget requested
            | Some(durable, sessionId) -> fireTenureReanchor durable sessionId tenure.WorkId tenure.IncumbencyId

        let applyResolvedTenure (sidOpt: string option) (outObj: obj) : unit =
            match resolveTenure outObj with
            | None -> ()
            | Some tenure -> assembleAndRewrite sidOpt outObj tenure

        let applyTenureIsolation (sidOpt: string option) (outObj: obj) : Task<unit> =
            task {
                match planSessionOf sidOpt with
                | None -> ()
                | Some _ -> applyResolvedTenure sidOpt outObj
            }

        { BeginPhysicalProviderAttempt =
            fun sessionId output ->
                task {
                    do! wired.EnsureVisibleInputAdmission output

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
          ApplyStrengthReplay =
            // The owner-facing replay must name the projected readonly exchange
            // with the owner's own `js-<role>` surface. Resolve that role from the
            // live authority projection for the session being transformed.
            StrengthReplay.applyBeforeXTrace journal snapshotOpt strengthDurability strengthFailFuse ownerRole
          RestoreProtocolArguments = restoreProtocolArguments
          ApplyRelayProjection =
            fun sidOpt outObj ->
                task {
                    do! ManagerWorkflow.ensureManagerRoadOpened journal workspaceDirectory sidOpt None

                    let physicalUserMessageId =
                        outObj
                        |> ProviderWireDecode.messagesFromTransformOutput
                        |> ProviderWireCapture.lastUserMessageId

                    // External human messages and internal recovery/guard continuations
                    // admitted into ChatExecutions belong to this active iteration.
                    let acceptedSuccessorRequest =
                        match journal, sidOpt, physicalUserMessageId with
                        | Some durable, Some sessionId, Some physical when not (String.IsNullOrWhiteSpace sessionId) ->
                            let key: ChatExecutionKey =
                                { SessionId = SessionId.create sessionId
                                  PhysicalUserMessageId = physical }

                            (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                            |> ChatExecutionProjection.byKey key
                            |> Option.isSome
                        | _ -> false

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

                                                ModelRouting.releasePhysicalExecution exactSessionId physical
                                                |> ignore

                                                let! interruption = sessionPort.InterruptAttempt exactSessionId

                                                return
                                                    interruption
                                                    |> Result.defaultWith (fun error ->
                                                        invalidOp (
                                                            "MANAGER-LOOP-004: retired attempt interrupt failed: "
                                                            + error
                                                        ))
                                        })
                                    sid)
                            sidOpt
                            outObj
                }
          ApplyTenureIsolation = applyTenureIsolation
          CaptureXTraceMessages =
            fun projectionSessionIdOpt outObj ->
                task {
                    let rawMessages = unbox<obj array> outObj?messages |> Array.toList

                    match projectionSessionIdOpt with
                    | None ->
                        return
                            { RawMessages = rawMessages
                              Current = None }
                    | Some sessionId ->
                        let! canonical = canonicalCaptureMessages journal (SessionId.create sessionId) rawMessages

                        let captureMessages =
                            canonical |> Result.defaultWith (raiseFailClosed strengthFailFuse)

                        let observations =
                            captureMessages
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
                                  Current = captured.Current }
                        | Error(XTraceCaptureError.Refused reason) -> return raiseFailClosed strengthFailFuse reason
                        | Error(XTraceCaptureError.StorageAppendFailed failure) ->
                            return raiseFailClosed strengthFailFuse (JournalAppendFailure.describe failure)
                        | Error(XTraceCaptureError.StorageFailed reason) ->
                            return raiseFailClosed strengthFailFuse reason
                }
          CommitStrengthTrace =
            fun projectionSessionIdOpt traceState strengthReplayPlans ->
                match projectionSessionIdOpt with
                | Some _ ->
                    task {
                        do!
                            StrengthReplay.commitTracedAfterCapture
                                journal
                                strengthDurability
                                strengthFailFuse
                                traceState
                                strengthReplayPlans
                    }
                | None -> Task.FromResult()
          RefreshCompanionXTrace =
            fun projectionSessionIdOpt traceState ->
                let sessionId = projectionSessionIdOpt |> Option.defaultValue ""
                let found, companion = scope.Sessions.Companions.TryGetValue sessionId

                if found then
                    traceState |> Option.iter companion.RefreshXTrace
          ApplyCompanion =
            let apply =
                CompanionTransform.applyCompanionForOrdinaryMaterial
                    scope.Sessions.Companions
                    scope.Sessions.CompanionGate
                    scope.CompanionLeases
                    scope.BloggerRuntimeHost
                    sessionPort
                    journal

                    (Some(fun bloggerId ->
                        // Register ownership + ActiveRun so idle→reconcile
                        // emits TerminalOutcome.Completed for this child.
                        wired.RegisterOwned(SessionId.value bloggerId)
                        wired.BindActiveRun bloggerId Role.Blogger None))

                    (host.RootWorkspace.TryRead())

            apply
          ApplyXWire =
            let isReplica =
                fun (sid: SessionId) -> boot.StrengthScope.StrengthRuntime.TryFindByReplica sid |> Option.isSome

            let attempts: AttemptPlanCapability =
                { TryAttemptPlan = scope.TryAttemptPlan
                  TryBindAttemptPlan = scope.TryBindAttemptPlan
                  ConsumeAttemptPlan = scope.ConsumeAttemptPlan
                  FreezePendingAttemptPlan = scope.Recovery.FreezePendingAttemptPlan
                  TryPendingAttemptPlan = scope.Recovery.TryPendingAttemptPlan }

            let wirePort = journal |> Option.map AgentJournalPortAdapter.forWire
            XWire.applyTransform isReplica snapshotOpt wirePort attempts
          FreezeProviderAttemptPlan = freezeProviderAttemptPlan
          ApplyEnforcerContinuation =
            fun projectionSessionIdOpt outObj ->
                task {
                    do!
                        EnforcerContinuation.applyContinuation
                            scope.BloggerRuntimeHost
                            journal
                            terminateSession
                            projectionSessionIdOpt
                            outObj
                }

          ApplyReadonlyDelegation = applyReadonlyDelegation
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
          ProjectRequirementGrounding =
            RequirementGroundingTransform.projectOrTerminate journal workspaceDirectory terminateSession
          InjectBloggerChronicle =
            fun projectionSessionIdOpt physicalUserMessageId outObj ->
                BloggerChronicleText.maybeInject
                    journal
                    projectionSessionIdOpt
                    physicalUserMessageId
                    (languageFor projectionSessionIdOpt)
                    outObj
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
            fun outObj ->
                let rawMessages = ProviderWireDecode.messagesFromTransformOutput outObj

                match StrengthReplicaTransform.tryEncodeOwnerMessages HostDigest.sha256Hex rawMessages with
                | Error error -> raiseFailClosed strengthFailFuse error
                | Ok encoded ->
                    HostMessageProjection.replaceMessagesInPlace outObj encoded
                    HostMessageProjection.sanitizeOutputMessages outObj }

    let defaultBranchCapabilities (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : TransformBranchCapabilities =
        let scope = boot.Scope
        let journal = boot.Journal
        let snapshotOpt = host.SnapshotOpt
        let wired = host.Wired

        { RegisterOwned = wired.RegisterOwned
          ReplicaRuntime =
            fun projectionSessionIdOpt ->
                match projectionSessionIdOpt, boot.StrengthScope.StrengthReplicaRuntime with
                | Some sessionId, Some runtime when runtime.IsReplica(SessionId.create sessionId) -> Some runtime
                | _ -> None
          ReplicaXWire =
            fun outObj ->
                task {
                    let isReplica =
                        fun (sid: SessionId) -> boot.StrengthScope.StrengthRuntime.TryFindByReplica sid |> Option.isSome

                    let attempts: AttemptPlanCapability =
                        { TryAttemptPlan = scope.TryAttemptPlan
                          TryBindAttemptPlan = scope.TryBindAttemptPlan
                          ConsumeAttemptPlan = scope.ConsumeAttemptPlan
                          FreezePendingAttemptPlan = scope.Recovery.FreezePendingAttemptPlan
                          TryPendingAttemptPlan = scope.Recovery.TryPendingAttemptPlan }

                    let wirePort = journal |> Option.map AgentJournalPortAdapter.forWire
                    let! _ = XWire.applyTransform isReplica snapshotOpt wirePort attempts outObj
                    return ()
                }
          ReplicaSanitize = HostMessageProjection.sanitizeOutputMessages }

    /// 普通分支请求的静态管道上下文：预读的物理消息 id 与各 stage 产出的
    /// 会话起点、Strength 重放计划、已捕获 XTrace、horizon 只经这份 frame 传递。
    type private TransformFrame =
        { SessionId: string option
          InObj: obj
          OutObj: obj
          PhysicalUserMessageId: PhysicalUserMessageId option
          SessionStartedAt: DateTimeOffset option
          ReplayPlans: StrengthReplayPlan list
          TracedXTrace: XTraceProjectionState option
          Horizon: PrefixPresentationHorizon }

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
                  { Name = "relay-projection-cut"
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
                  // 5. StrengthReplay.applyBeforeXTrace
                  { Name = "strength-replay"
                    Run =
                      fun frame ->
                          task {
                              let! plans = caps.ApplyStrengthReplay frame.SessionId frame.OutObj
                              return Some { frame with ReplayPlans = plans }
                          } }
                  // 5. host-boundary-032 / restore the protocol fields the Host
                  // persisted away into the provider-facing request BEFORE
                  // delegation capture (13.3) reads the same history; without
                  // this the capture never sees the budget the model signed.
                  { Name = "restore-protocol-arguments"
                    Run =
                      fun frame ->
                          task {
                              do! caps.RestoreProtocolArguments frame.OutObj
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
                  // 7. StrengthReplay.commitTracedAfterCapture
                  { Name = "commit-strength-trace"
                    Run =
                      fun frame ->
                          task {
                              do! caps.CommitStrengthTrace frame.SessionId frame.TracedXTrace frame.ReplayPlans

                              return Some frame
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
                  // 10. XWire.applyTransform. A selected prefix probe creates a
                  // tentative cold horizon for this physical request; downstream
                  // historical auxiliaries must not replay the old horizon into it.
                  { Name = "apply-xwire"
                    Run =
                      fun frame ->
                          task {
                              let! horizon = caps.ApplyXWire frame.OutObj
                              return Some { frame with Horizon = horizon }
                          } }
                  // 11. ProviderLifecycle.freezeProviderAttemptPlanForTransform.
                  // Freeze the exact plan, then confirm the Host's real assistant
                  // identity and durable ProviderStarted before returning its body.
                  { Name = "freeze-provider-attempt-plan"
                    Run =
                      fun frame ->
                          task {
                              do! caps.FreezeProviderAttemptPlan frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 12. EnforcerContinuation.applyContinuation
                  { Name = "apply-enforcer-continuation"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyEnforcerContinuation frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 13.1 PairProgrammingThoughtTransform.maybeInjectGuideline
                  // 13.2 RequirementGroundingTransform.projectOrTerminate
                  // 13.3 Capture and start on the final outgoing request so the
                  //      preparation owns the same mirror and provider attempt plan.
                  { Name = "current-horizon-auxiliaries"
                    Run =
                      fun frame ->
                          task {
                              if frame.Horizon = PrefixPresentationHorizon.Current then
                                  do! caps.InjectPairGuideline frame.SessionId frame.SessionStartedAt frame.OutObj
                                  do! caps.ProjectRequirementGrounding frame.SessionId frame.OutObj
                                  do! caps.ApplyReadonlyDelegation frame.SessionId frame.OutObj

                              return Some frame
                          } }
                  // 14. BloggerChronicleText.maybeInject
                  { Name = "inject-blogger-chronicle"
                    Run =
                      fun frame ->
                          caps.InjectBloggerChronicle frame.SessionId frame.PhysicalUserMessageId frame.OutObj
                          Task.FromResult(Some frame) }
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
                  ReplayPlans = []
                  TracedXTrace = None
                  Horizon = PrefixPresentationHorizon.Current }

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

                match determineTransformMode branches projectionSessionIdOpt outObj with
                | StrengthReplica runtime ->
                    projectionSessionIdOpt |> Option.iter branches.RegisterOwned
                    // STRENGTH-004/009: Replica uses exactly one request-plan
                    // writer plus its mirror/K gate. XTrace, Manager narrative,
                    // Companion, Enforcer, Pair and Review are owner-only.
                    do! branches.ReplicaXWire outObj
                    do! caps.FreezeProviderAttemptPlan projectionSessionIdOpt outObj
                    // host-boundary-032 / same restore on the
                    // Replica branch, before the runtime reads this request.
                    do! caps.RestoreProtocolArguments outObj
                    let! handled = runtime.HandleTransform outObj
                    do failIfReplicaDecisionLost handled
                    branches.ReplicaSanitize outObj
                | Ordinary ->
                    projectionSessionIdOpt |> Option.iter branches.RegisterOwned
                    do! normalTransform caps projectionSessionIdOpt inObj outObj
            }

    /// Provider-facing transform composition: order only.
    /// Relay cut → Strength replay/trace → Companion/XWire → speculation;
    /// retired raw history is removed before any downstream context owner.
    let create (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) : obj -> obj -> Task<unit> =
        let caps = defaultCapabilities boot host
        let branches = defaultBranchCapabilities boot host
        createWithCaps caps branches
