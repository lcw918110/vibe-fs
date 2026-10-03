namespace Wanxiangshu.OpenCode

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

    type TraceTransformCapture =
        { RawMessages: obj list
          Current: XTraceProjectionState option }

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
          BindSessionStartedAt: string option -> Task<DateTimeOffset option>
          ApplyStrengthReplay: string option -> obj -> Task<StrengthReplayPlan list>
          RestoreProtocolArguments: obj -> Task<unit>
          ApplyRelayProjection: string option -> obj -> Task<RelayProjectionDisposition>
          CaptureXTraceMessages: string option -> obj -> Task<TraceTransformCapture>
          CommitStrengthTrace: string option -> XTraceProjectionState option -> StrengthReplayPlan list -> Task<unit>
          RefreshCompanionXTrace: string option -> XTraceProjectionState option -> unit
          ApplyCompanion: RelayProjectionDisposition -> string option -> obj -> obj -> Task<unit>
          ApplyXWire: RelayProjectionDisposition -> obj -> Task<PrefixPresentationHorizon>
          FreezeProviderAttemptPlan: string option -> obj -> Task<unit>
          ApplyEnforcerContinuation: string option -> obj -> Task<unit>
          CaptureReadonlyDelegation: string option -> obj -> Task<unit>
          ApplyReadonlyDelegation: string option -> obj -> Task<unit>
          InjectPairGuideline: string option -> DateTimeOffset option -> obj -> Task<unit>
          ProjectRequirementGrounding: string option -> obj -> Task<unit>
          InjectBloggerChronicle: string option -> obj -> unit
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
        let strengthFailFuse = boot.StrengthFailClosed

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
                | Ok _ -> return ()
                | Error error ->
                    return
                        invalidOp (
                            sprintf
                                "HOST-BOUNDARY-008: provider attempt plan freeze failed (%s): %A"
                                (ProviderLifecycle.providerStartObservationErrorCode error)
                                error
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
        // delegation capture (4.5) or the replica batch collector reads the
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

        let captureReadonlyDelegation projectionSessionIdOpt outObj =
            task {
                let! outcome =
                    StrengthDelegate.tryCapture
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

                match outcome with
                | StrengthDelegate.CaptureOutcome.Captured request ->
                    Diagnostic.emit
                        "strength-delegation-requested"
                        [ "session_id", SessionId.value request.OwnerSessionId
                          "result", "capture-phase:" + string (ReadonlyRoundBudget.value request.RequestedRounds) ]
                | StrengthDelegate.CaptureOutcome.Skipped reason ->
                    let sessionId =
                        projectionSessionIdOpt
                        |> Option.orElseWith (fun () -> ProviderWireDecode.projectionSessionIdFromMessages outObj)
                        |> Option.defaultValue ""

                    Diagnostic.emit
                        "strength-delegation-skip"
                        [ "session_id", sessionId; "result", "capture-phase:" + reason ]

                return ()
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

        { BeginPhysicalProviderAttempt =
            SessionExecutionBinding.beginPhysicalProviderAttemptForTransform
                journal
                scope.Sessions.Quiescence.BeginProviderAttempt
          BindSessionStartedAt =
            let port = journal |> Option.map AgentJournalPortAdapter.forSessionStartedAt
            SessionStartedAtLedger.bindSessionStartedAt port clock terminateSession Diagnostic.emit
          ApplyStrengthReplay =
            // The owner-facing replay must name the projected readonly exchange
            // with the owner's own `js-<role>` surface. Resolve that role from the
            // live authority projection for the session being transformed.
            let ownerRole (sessionId: string) =
                journal
                |> Option.bind (fun durable ->
                    let projections = (AgentJournal.snapshot durable).AgentProjections
                    let sid = SessionId.create sessionId

                    PromptAuthorityProjectionQueries.activeProfile sid projections
                    |> Option.orElseWith (fun () ->
                        PromptAuthorityProjectionQueries.lastAuthorityProfile sid projections))
                |> Option.map (fun profile -> profile.CanonicalRole)

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

                    // External HumanMessage acceptance lives in ChatExecutions, not
                    // the claimed-prompt continuation map used by manager-loop gates.
                    let acceptedHuman =
                        match journal, sidOpt, physicalUserMessageId with
                        | Some durable, Some sessionId, Some physical when not (String.IsNullOrWhiteSpace sessionId) ->
                            let key: ChatExecutionKey =
                                { SessionId = SessionId.create sessionId
                                  PhysicalUserMessageId = physical }

                            (AgentJournal.snapshot durable).AgentProjections.ChatExecutions
                            |> ChatExecutionProjection.byKey key
                            |> Option.exists (fun execution ->
                                execution.origin = PromptAuthority.PromptOrigin.Continuation
                                    PromptAuthority.ContinuationKind.HumanMessage)
                        | _ -> false

                    return!
                        RelayNarrativeTransform.apply
                            journal
                            acceptedHuman
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
                        let observations =
                            rawMessages
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
                                (raiseFailClosed strengthFailFuse)
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

            fun relayProjection projectionSessionIdOpt inObj outObj ->
                match relayProjection with
                | RelayProjectionDisposition.CurrentIteration -> Task.FromResult()
                | _ -> apply projectionSessionIdOpt inObj outObj
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
            let apply = XWire.applyTransform isReplica snapshotOpt wirePort attempts

            fun relayProjection outObj ->
                match relayProjection with
                | RelayProjectionDisposition.CurrentIteration -> Task.FromResult PrefixPresentationHorizon.TentativeCold
                | _ -> apply outObj
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

          CaptureReadonlyDelegation = captureReadonlyDelegation
          ApplyReadonlyDelegation = applyReadonlyDelegation
          InjectPairGuideline =
            fun projectionSessionIdOpt sessionStartedAt outObj ->
                task {
                    do!
                        PairProgrammingThoughtTransform.maybeInjectGuideline
                            journal
                            projectionSessionIdOpt
                            sessionStartedAt
                            clock
                            terminateSession
                            (languageFor projectionSessionIdOpt)
                            outObj
                }
          ProjectRequirementGrounding =
            RequirementGroundingTransform.projectOrTerminate journal workspaceDirectory terminateSession
          InjectBloggerChronicle =
            fun projectionSessionIdOpt outObj ->
                BloggerChronicleText.maybeInject
                    journal
                    projectionSessionIdOpt
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

    let normalTransform
        (caps: NormalTransformCapabilities)
        (projectionSessionIdOpt: string option)
        (inObj: obj)
        (outObj: obj)
        : Task<unit> =
        task {
            // 1. SessionExecutionBinding.beginPhysicalProviderAttemptForTransform (durable-evidence gate)
            do! caps.BeginPhysicalProviderAttempt projectionSessionIdOpt outObj

            // 2. SessionStartedAtLedger.tryBindOrAbort
            let! sessionStartedAt = caps.BindSessionStartedAt projectionSessionIdOpt

            // 3. Relay projection cut + manager-loop opening. This MUST run
            // before every trace/compaction owner so retired raw history cannot
            // be reintroduced later in the composition.
            do! caps.SettleAndReplaceDeferredInspections projectionSessionIdOpt outObj
            let! relayProjection = caps.ApplyRelayProjection projectionSessionIdOpt outObj

            if relayProjection = RelayProjectionDisposition.RetiredAttemptStopped then
                return ()

            // 4. StrengthReplay.applyBeforeXTrace
            let! strengthReplayPlans = caps.ApplyStrengthReplay projectionSessionIdOpt outObj

            // 4.4 host-boundary-032 / restore the protocol
            // fields the Host persisted away into the provider-facing request
            // BEFORE delegation capture (4.5) reads the same history; without
            // this the capture never sees the budget the model signed.
            do! caps.RestoreProtocolArguments outObj

            // 4.5 StrengthDelegate.tryCapture — freeze the explicit authorization
            // from the real completed owner batch and persist DelegationRequested
            // here, before any compaction or message replacement downstream can
            // lose batch metadata.
            do! caps.CaptureReadonlyDelegation projectionSessionIdOpt outObj

            // 5. XTraceCapture.captureObservedMessagesWithReceipt
            let! traceCapture = caps.CaptureXTraceMessages projectionSessionIdOpt outObj

            // 6. StrengthReplay.commitTracedAfterCapture
            do! caps.CommitStrengthTrace projectionSessionIdOpt traceCapture.Current strengthReplayPlans

            // 7. CompanionHost.RefreshXTrace
            caps.RefreshCompanionXTrace projectionSessionIdOpt traceCapture.Current

            // 8. applyCompanionForOrdinaryMaterial
            do! caps.ApplyCompanion relayProjection projectionSessionIdOpt inObj outObj

            // 9. XWire.applyTransform. A selected prefix probe creates a
            // tentative cold horizon for this physical request; downstream
            // historical auxiliaries must not replay the old horizon into it.
            let! prefixHorizon = caps.ApplyXWire relayProjection outObj

            // 10. ProviderLifecycle.freezeProviderAttemptPlanForTransform
            // The transform sees the accepted user message only. Freeze the
            // exact request plan; a later public assistant observation owns
            // ProviderRunIdentity binding and ProviderStarted persistence.
            do! caps.FreezeProviderAttemptPlan projectionSessionIdOpt outObj

            // 11. EnforcerContinuation.applyContinuation
            do! caps.ApplyEnforcerContinuation projectionSessionIdOpt outObj

            if prefixHorizon = PrefixPresentationHorizon.Current then
                // 12. PairProgrammingThoughtTransform.maybeInjectGuideline
                do! caps.InjectPairGuideline projectionSessionIdOpt sessionStartedAt outObj

                // 13. RequirementGroundingTransform.projectOrTerminate
                do! caps.ProjectRequirementGrounding projectionSessionIdOpt outObj

                // 14. StrengthDelegate.tryCaptureAndStart — only on the live
                // Current horizon so manager-loop / prefix-probe sealed views
                // are not rewritten by a no-op start path's surface apply.
                do! caps.ApplyReadonlyDelegation projectionSessionIdOpt outObj

            // 15. BloggerChronicleText.maybeInject
            caps.InjectBloggerChronicle projectionSessionIdOpt outObj

            // 15.1 Re-apply replaced inspection results after any intermediate insertions
            do! caps.SettleAndReplaceDeferredInspections projectionSessionIdOpt outObj

            // 16. HostMessageProjection.sanitizeMessages
            caps.SanitizeMessages outObj

            ()
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
