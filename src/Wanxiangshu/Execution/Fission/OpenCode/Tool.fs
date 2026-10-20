namespace Wanxiangshu.Execution.Fission.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.OpenCode
open Wanxiangshu.OpenCode.ToolHostCodec
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// Same-participant physical replacement. Fission never calls the Host session
/// fork endpoint: it creates fresh sibling sessions, starts them from the
/// owner's already-delivered view (when there is one) + exact lane input, then
/// physically interrupts the old present without terminating the logical owner.
module FissionTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let Description = "tool/fission/description"

        [<Literal>]
        let TooFew = "tool/fission/too-few"

        [<Literal>]
        let InvalidOrigin = "tool/fission/invalid-origin"

        [<Literal>]
        let Capacity = "tool/fission/capacity"

        [<Literal>]
        let AlreadyActive = "tool/fission/already-active"

        [<Literal>]
        let Unavailable = "tool/fission/unavailable"

        [<Literal>]
        let Started = "tool/fission/started"

        [<Literal>]
        let SharedCompletion = "tool/fission/shared-completion"

    type FissionReceipt =
        { GroupId: string
          LaneCount: int
          Lanes: (int * string) list }

    let private languageOf (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private consequence language path =
        tomlObjectWithInstructions (ProviderProse.instructionLines language path Map.empty) []

    let private appendFission
        (durable: AgentJournal)
        (owner: SessionId)
        (providerRun: ProviderRunIdentity option)
        (fact: AgentFact)
        =
        task {
            match! AgentJournal.appendAgent (StreamId.Session owner) providerRun fact durable with
            | Ok _ -> return Ok()
            | Error failure -> return Error(JournalAppendFailure.describe failure)
        }

    let private groupIdFor owner toolCallId =
        let identity =
            SessionId.value owner + "\u001f" + ToolCallId.value toolCallId
            |> HostDigest.sha256Hex

        "fission-" + identity.Substring(0, 24)

    let private deliveryPrompt owner completionId payload =
        let instruction =
            ProviderProse.render
                (GlobalProviderLanguage.current ())
                Path.SharedCompletion
                (Map [ "completion_id", completionId; "payload", payload ])

        LlmFacing.instruction instruction
        |> LlmFacing.withData [ LlmFacing.Data.stringField "completion_id" completionId ]
        |> LlmFacing.render

    let private capturedGroup (durable: AgentJournal) groupId =
        FissionProjection.tryGroup groupId (AgentJournal.snapshot durable).AgentProjections.Fission

    let private appendDelivery (durable: AgentJournal) owner providerRun groupId completionId laneIndex =
        appendFission
            durable
            owner
            providerRun
            (FissionFact.FissionCompletionDelivered
                {| GroupId = groupId
                   OwnerSessionId = owner
                   CompletionId = completionId
                   LaneIndex = laneIndex |})

    let private writeAndAppendCapture (durable: AgentJournal) owner groupId completionId payload =
        task {
            match! durable.WriteBlob payload with
            | Error _ -> return ()
            | Ok blob ->
                let! _ =
                    appendFission
                        durable
                        owner
                        None
                        (FissionFact.FissionCompletionCaptured
                            {| GroupId = groupId
                               OwnerSessionId = owner
                               CompletionId = completionId
                               PayloadRef = blob.BlobRef
                               PayloadDigest = blob.BlobDigest |})

                return ()
        }

    let private ensureCompletionCaptured (durable: AgentJournal) owner groupId completionId payload =
        task {
            match
                capturedGroup durable groupId
                |> Option.bind (fun group -> Map.tryFind completionId group.CapturedCompletions)
            with
            | Some _ -> return ()
            | None -> return! writeAndAppendCapture durable owner groupId completionId payload
        }

    let private deliveryState (durable: AgentJournal) groupId completionId (lane: FissionStartedLane) =
        match capturedGroup durable groupId with
        | None -> true, true
        | Some group ->
            let delivered =
                group.CompletionDeliveries
                |> Map.tryFind completionId
                |> Option.defaultValue Set.empty
                |> Set.contains lane.Index

            delivered, Map.containsKey lane.Index group.LaneWork

    let private continueLiveLane
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lane: FissionStartedLane)
        (completionId: string)
        (payload: string)
        =
        task {
            let laneProfile =
                PromptAuthorityProjectionQueries.activeProfile
                    lane.SessionId
                    (AgentJournal.snapshot durable).AgentProjections

            match laneProfile with
            | None -> return ()
            | Some activeLaneProfile ->
                let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

                match!
                    dispatcher.SendContinuation
                        (DispatchSessionPort.ofSessionPort scope.Sessions)
                        lane.SessionId
                        (deliveryPrompt owner completionId payload)
                        PromptAuthority.ContinuationKind.FissionHandoff
                        activeLaneProfile
                        (scope.DirectoryFor(SessionId.value lane.SessionId))
                        PromptDispatcher.AwaitMode.Detached
                        None
                with
                | Error _ -> return ()
                | Ok _ ->
                    let! _ = appendDelivery durable owner None groupId completionId lane.Index
                    return ()
        }

    let private deliverLaneBody
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lane: FissionStartedLane)
        (completionId: string)
        (payload: string)
        (laneClosed: bool)
        =
        task {
            if laneClosed then
                let! _ = appendDelivery durable owner None groupId completionId lane.Index
                return ()
            else
                return! continueLiveLane scope durable profile groupId owner lane completionId payload
        }

    let private deliverLaneGuarded
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lane: FissionStartedLane)
        (completionId: string)
        (payload: string)
        (laneClosed: bool)
        =
        task {
            try
                do! deliverLaneBody scope durable profile groupId owner lane completionId payload laneClosed
            finally
                FissionRuntime.endDelivery groupId completionId lane.Index
        }

    let private deliverOneLane
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (completionId: string)
        (payload: string)
        (lane: FissionStartedLane)
        =
        task {
            let alreadyDelivered, laneClosed = deliveryState durable groupId completionId lane

            if
                alreadyDelivered
                || not (FissionRuntime.tryBeginDelivery groupId completionId lane.Index)
            then
                return ()
            else
                return! deliverLaneGuarded scope durable profile groupId owner lane completionId payload laneClosed
        }

    let private readyExceptForRetirement (durable: AgentJournal) groupId =
        match capturedGroup durable groupId with
        | None -> false
        | Some group ->
            group.LaneWork.Count = group.LaneCount
            && group.PreFissionCompletionIds
               |> Set.forall (fun id ->
                   let delivered =
                       group.CompletionDeliveries |> Map.tryFind id |> Option.defaultValue Set.empty

                   Map.containsKey id group.CapturedCompletions
                   && delivered = Set.ofList [ 0 .. group.LaneCount - 1 ])

    let private convergeWithScope
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (owner: SessionId)
        (groupId: string)
        =
        task {
            let _, beforeRevision = durable.SnapshotWithRevision

            let directoryFor sessionId =
                scope.DirectoryFor(SessionId.value sessionId)

            let! converged = FissionHost.tryConverge scope.Sessions durable directoryFor owner

            if converged then
                return ()
            elif not (readyExceptForRetirement durable groupId) then
                return ()
            else
                let! _ = durable.AwaitChangeFrom beforeRevision
                let! _ = FissionHost.tryConverge scope.Sessions durable directoryFor owner
                return ()
        }

    let private convergeOwnerIfNeeded (scope: ToolRuntimeScope) durable owner groupId =
        task {
            match scope.EventPort with
            | None -> return ()
            | Some _ -> return! convergeWithScope scope durable owner groupId
        }

    let private deliverAllLaneCompletions
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (completionId: string)
        (payload: string)
        =
        task {
            for lane in lanes |> List.sortBy (fun lane -> lane.Index) do
                do! deliverOneLane scope durable profile groupId owner completionId payload lane
        }

    let private broadcastAfterBegin
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (completionId: string)
        (payload: string)
        =
        task {
            try
                do! ensureCompletionCaptured durable owner groupId completionId payload

                do! deliverAllLaneCompletions scope durable profile groupId owner lanes completionId payload

                do! convergeOwnerIfNeeded scope durable owner groupId
            finally
                FissionRuntime.endDelivery groupId completionId -1
        }

    /// Capture exactly one canonical payload for a pre-Fission completion, then
    /// account/deliver that same payload once per lane. Closed lanes are accounted
    /// through the durable group closure; live lanes also receive a same-run
    /// continuation. No per-lane view copy is created.
    let private captureAndBroadcast
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (completionId: string)
        (payload: string)
        =
        task {
            if not (FissionRuntime.tryBeginDelivery groupId completionId -1) then
                return ()
            else
                return! broadcastAfterBegin scope durable profile groupId owner lanes completionId payload
        }

    let private payloadFromView view fallback =
        match view with
        | Some record when not (String.IsNullOrWhiteSpace record) -> record
        | _ -> fallback

    let private captureCompletedTerminal
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (childId: SessionId)
        (completionId: string)
        (terminal: AgentRunResult)
        =
        task {
            let view = JsDeliveredValues.tryPeekRendered (SessionId.value childId)
            let payload = payloadFromView view terminal.TerminalText

            do! captureAndBroadcast scope durable profile groupId owner lanes completionId payload
        }

    let private dispatchTerminalOutcome
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (childId: SessionId)
        (completionId: string)
        (outcome: Wanxiangshu.OpenCode.TerminalOutcome)
        =
        match outcome with
        | Wanxiangshu.OpenCode.TerminalOutcome.Aborted _ -> ()
        | Wanxiangshu.OpenCode.TerminalOutcome.Failed stop ->
            captureAndBroadcast
                scope
                durable
                profile
                groupId
                owner
                lanes
                completionId
                (String.concat "\n" [ "status=failed"; "error=" + stop.Reason ])
            |> ignore
        | Wanxiangshu.OpenCode.TerminalOutcome.Completed terminal ->
            captureCompletedTerminal scope durable profile groupId owner lanes childId completionId terminal
            |> ignore

    let private onPreAgentTerminal
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (childId: SessionId)
        (completionId: string)
        (sessionId: SessionId)
        (outcome: Wanxiangshu.OpenCode.TerminalOutcome)
        =
        if sessionId <> childId then
            ()
        else
            dispatchTerminalOutcome scope durable profile groupId owner lanes childId completionId outcome

    let private ptyCompletionPayload (item: PtyJoinItem) =
        match item with
        | PtyJoinItem.PtyExited exit -> exit.Outcome
        | PtyJoinItem.PtyFailed failure ->
            String.concat "\n" [ "status=failed"; "code=" + failure.Code; "error=" + failure.Message ]
        | PtyJoinItem.PtyAborted aborted ->
            String.concat "\n" [ "status=aborted"; "code=" + aborted.Code; "error=" + aborted.Message ]

    let private installAgentBroadcasts
        (eventPort: IEventObservationPort)
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (preAgents: (string * SessionId) list)
        =
        for agentId, childId in preAgents do
            let completionId = FissionExternalId.agent agentId

            let subscription =
                eventPort.SubscribeTerminalListener(
                    onPreAgentTerminal scope durable profile groupId owner lanes childId completionId
                )

            FissionRuntime.trackGroupResource groupId subscription

    let private onPtyCompletion
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (wanted: Set<string>)
        (item: PtyJoinItem)
        =
        let ptyId = PtyJoinItem.ptyId item

        if not (Set.contains ptyId wanted) then
            ()
        else
            captureAndBroadcast
                scope
                durable
                profile
                groupId
                owner
                lanes
                (FissionExternalId.pty ptyId)
                (ptyCompletionPayload item)
            |> ignore

    let private installPtyBroadcasts
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (prePtys: string list)
        (ownerRuntime: HostForkRuntime)
        =
        if List.isEmpty prePtys then
            ()
        else
            let wanted = Set.ofList prePtys

            let subscription =
                ownerRuntime.SubscribePtyCompletion(onPtyCompletion scope durable profile groupId owner lanes wanted)

            FissionRuntime.trackGroupResource groupId subscription

    let private installPreFissionBroadcasts
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (preAgents: (string * SessionId) list)
        (prePtys: string list)
        (ownerRuntime: HostForkRuntime)
        =
        match scope.EventPort with
        | None -> ()
        | Some eventPort ->
            installAgentBroadcasts eventPort scope durable profile groupId owner lanes preAgents

            installPtyBroadcasts scope durable profile groupId owner lanes prePtys ownerRuntime

    let private ownerViewPort (scope: ToolRuntimeScope) sessionId =
        task {
            match! scope.ViewForSession(SessionId.value sessionId) with
            | Some view when not (String.IsNullOrWhiteSpace view) -> return Ok(Some view)
            | _ -> return Ok None
        }

    let private createLanePort
        (scope: ToolRuntimeScope)
        (groupId: string)
        (owner: SessionId)
        (participant: string)
        (directory: string option)
        (parsedCount: int)
        (logicalOwner: SessionId)
        (physicalParent: SessionId option)
        (lane: FissionLanePrompt)
        =
        task {
            match!
                scope.Sessions.CreateSiblingSession(
                    logicalOwner,
                    physicalParent,
                    { Title = Some(sprintf "Fission lane %d/%d" (lane.Index + 1) parsedCount)
                      Agent = Some participant
                      Directory = directory }
                )
            with
            | Error error -> return Error error
            | Ok laneId ->
                scope.RegisterPhysicalParent(laneId, physicalParent)

                directory
                |> Option.iter (fun path -> scope.RegisterDirectory(SessionId.value laneId, path))

                FissionRuntime.bindLane groupId owner lane.Index parsedCount laneId
                return Ok laneId
        }

    let private startLanePort
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (directory: string option)
        (laneId: SessionId)
        (startup: string)
        =
        task {
            scope.RunStarted laneId profile.CanonicalRole directory

            match! XTraceCapture.captureOpeningWithReceipt scope.Journal laneId startup [] with
            | Error _ -> return Error "fission_trace_capture_failed"
            | Ok _ ->
                let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

                match!
                    dispatcher.SendContinuation
                        (DispatchSessionPort.ofSessionPort scope.Sessions)
                        laneId
                        startup
                        PromptAuthority.ContinuationKind.FissionHandoff
                        profile
                        directory
                        PromptDispatcher.AwaitMode.Detached
                        None
                with
                | Ok _ -> return Ok()
                | Error error -> return Error error
        }

    let private abortLanePort (scope: ToolRuntimeScope) (laneId: SessionId) =
        task {
            FissionRuntime.unbindLane laneId
            ModelRouting.cancelUnacquiredExecution laneId |> ignore
            let! _ = scope.Sessions.AbortSession laneId
            return ()
        }
        :> Task

    let private silentInterruptOwnerPort (scope: ToolRuntimeScope) (sessionId: SessionId) =
        task {
            FissionRuntime.markSilentInterrupt sessionId

            match! scope.Sessions.InterruptAttempt sessionId with
            | Ok() -> return Ok()
            | Error error ->
                FissionRuntime.clearSilentInterrupt sessionId
                return Error error
        }

    let private onLanesCreatedHook
        (durable: AgentJournal)
        (groupId: string)
        (toolCallId: ToolCallId)
        (parsedCount: int)
        (preCompletionIds: string list)
        (providerRun: ProviderRunIdentity option)
        (logicalOwner: SessionId)
        (physicalParent: SessionId option)
        (ownerView: string)
        (created: FissionStartedLane list)
        =
        task {
            // The durable owner view records exactly what the lanes received: the
            // owner's delivered view, or the empty view when the owner had not
            // delivered one at admission time.
            match! durable.WriteBlob ownerView with
            | Error error -> return Error error
            | Ok blob ->
                let ordered = created |> List.sortBy (fun lane -> lane.Index)

                return!
                    appendFission
                        durable
                        logicalOwner
                        providerRun
                        (FissionFact.FissionAdmitted
                            {| GroupId = groupId
                               OwnerSessionId = logicalOwner
                               ParentSessionId = physicalParent
                               OriginToolCallId = toolCallId
                               LaneCount = parsedCount
                               LaneSessions = ordered |> List.map (fun lane -> lane.SessionId)
                               LanePrompts = ordered |> List.map (fun lane -> lane.Prompt)
                               OwnerViewRef = blob.BlobRef
                               OwnerViewDigest = blob.BlobDigest
                               PreFissionCompletionIds = preCompletionIds |})
        }

    let private onFailedHook
        (durable: AgentJournal)
        (groupId: string)
        (providerRun: ProviderRunIdentity option)
        (logicalOwner: SessionId)
        (reason: string)
        =
        task {
            let! _ =
                appendFission
                    durable
                    logicalOwner
                    providerRun
                    (FissionFact.FissionFailed
                        {| GroupId = groupId
                           OwnerSessionId = logicalOwner
                           Reason = reason |})

            return ()
        }
        :> Task

    let private classifyAdmissionOutcome =
        function
        | Error FissionRejectReason.InvalidOrigin -> Error Path.InvalidOrigin
        | Error FissionRejectReason.AlreadyFissioned -> Error Path.AlreadyActive
        | Error FissionRejectReason.CapacityExceeded -> Error Path.Capacity
        | Error _ -> Error Path.Unavailable
        | Ok admission -> Ok admission

    let private runAdmission
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuntime: HostForkRuntime)
        (preAgents: (string * SessionId) list)
        (prePtys: string list)
        =
        task {
            let groupId = groupIdFor owner toolCallId

            let directory =
                scope.DirectoryFor ctx.SessionId |> Option.orElse scope.WorkspaceDirectory

            let preCompletionIds =
                (preAgents |> List.map (fst >> FissionExternalId.agent))
                @ (prePtys |> List.map FissionExternalId.pty)

            let deps: FissionAdmissionDependencies =
                { ParentOf = scope.Sessions.TryGetParentSession
                  OwnerView = ownerViewPort scope
                  CreateLane = createLanePort scope groupId owner profile.SelectedAgent directory parsed.Count
                  StartLane = startLanePort scope durable profile directory
                  AbortLane = abortLanePort scope
                  SilentInterruptOwner = silentInterruptOwnerPort scope }

            let hooks: FissionAdmissionHooks =
                { OnLanesCreated =
                    onLanesCreatedHook durable groupId toolCallId parsed.Count preCompletionIds ctx.ProviderRunId
                  OnFailed = onFailedHook durable groupId ctx.ProviderRunId }

            let admissionRuntime = FissionAdmission.createWithHooks deps hooks

            let! admittedEffect =
                taskResult {
                    let! admissionAttempt = FissionAdmission.admit admissionRuntime owner parsed |> TaskResultCE.ofTask

                    let! admission = classifyAdmissionOutcome admissionAttempt

                    installPreFissionBroadcasts
                        scope
                        durable
                        profile
                        groupId
                        owner
                        admission.Lanes
                        preAgents
                        prePtys
                        ownerRuntime

                    return tomlObject [ "status", TString "fissioned"; "lane_count", TInt parsed.Count ]
                }

            return admittedEffect |> Result.defaultWith (consequence language)
        }

    let private admitWhenEventPortReady
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuntime: HostForkRuntime)
        =
        task {
            let preAgents = ownerRuntime.SnapshotOutstandingAgentRuns()
            let prePtys = ownerRuntime.SnapshotOutstandingPtyRuns()

            if
                (not (List.isEmpty preAgents) || not (List.isEmpty prePtys))
                && scope.EventPort.IsNone
            then
                return consequence language Path.Unavailable
            else
                return!
                    runAdmission
                        scope
                        durable
                        ctx
                        language
                        parsed
                        toolCallId
                        owner
                        profile
                        ownerRuntime
                        preAgents
                        prePtys
        }

    let private executeWhenIdle
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        (owner: SessionId)
        =
        task {
            let activeProfile =
                PromptAuthorityProjectionQueries.activeProfile owner (AgentJournal.snapshot durable).AgentProjections

            match activeProfile, scope.RuntimeFor ctx with
            | None, _
            | _, Error _ -> return consequence language Path.Unavailable
            | Some profile, _ when
                profile.CanonicalRole <> Role.Engineer
                || not (OfficeCapability.isAllowed profile.CanonicalRole ToolPermission.Fission)
                ->
                return consequence language Path.Unavailable
            | Some profile, Ok ownerRuntime ->
                return! admitWhenEventPortReady scope durable ctx language parsed toolCallId owner profile ownerRuntime
        }

    let private executeWithJournal
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        =
        task {
            let caller = SessionId.create ctx.SessionId
            let owner = scope.LogicalOwnerFor caller
            let fissionState = (AgentJournal.snapshot durable).AgentProjections.Fission

            if FissionRuntime.tryLane caller |> Option.isSome then
                return consequence language Path.AlreadyActive
            elif FissionProjection.tryActiveForOwner owner fissionState |> Option.isSome then
                return consequence language Path.AlreadyActive
            else
                return! executeWhenIdle scope ctx language parsed toolCallId durable owner
        }

    let private executeParsed
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        (parsed: ParsedFissionPrompts)
        =
        task {
            match ctx.ToolCallId, scope.Journal with
            | None, _
            | _, None -> return consequence language Path.Unavailable
            | Some _, Some _ when String.IsNullOrWhiteSpace ctx.SessionId ->
                return consequence language Path.Unavailable
            | Some toolCallId, Some durable -> return! executeWithJournal scope ctx language parsed toolCallId durable
        }

    let private executeForSubsession
        (scope: ToolRuntimeScope)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (language: ProviderLanguage)
        =
        task {
            let prompts = args.Texts "prompts"

            match FissionPrompt.parse prompts with
            | Error FissionRejectReason.TooFewLanes
            | Error(FissionRejectReason.EmptyLanePrompt _) -> return consequence language Path.TooFew
            | Error _ -> return consequence language Path.Unavailable
            | Ok parsed -> return! executeParsed scope ctx language parsed
        }

    let private executeForCaller
        (scope: ToolRuntimeScope)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        language
        (caller: SessionId)
        =
        task {
            match! scope.Sessions.TryGetParentSession caller with
            | Error _ -> return consequence language Path.Unavailable
            | Ok None -> return consequence language Path.InvalidOrigin
            | Ok(Some _) -> return! executeForSubsession scope args ctx language
        }

    let private execute (scope: ToolRuntimeScope) (args: HostToolArguments) (ctx: HostToolContext) =
        task {
            let language = languageOf ctx

            if String.IsNullOrWhiteSpace ctx.SessionId then
                return consequence language Path.Unavailable
            else
                return! executeForCaller scope args ctx language (SessionId.create ctx.SessionId)
        }

    let admission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> r = Role.Engineer && OfficeCapability.isAllowed r ToolPermission.Fission)

    let spec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        { Name = "fission"
          Description =
            ProviderProse.render (ProviderLanguageBinding.readGlobalPreference ()) Path.Description Map.empty
          Arguments = [ "prompts", ToolHostCodec.stringArraySchema factory ]
          Admission = admission
          Execute = execute scope }

    let private classifyAdmissionOutcomeReceipt =
        function
        | Error FissionRejectReason.InvalidOrigin -> Error "fission-unavailable"
        | Error FissionRejectReason.AlreadyFissioned -> Error "not-ready"
        | Error FissionRejectReason.CapacityExceeded -> Error "fission-unavailable"
        | Error(FissionRejectReason.RuntimeUnavailable msg) when
            msg.StartsWith("lane create failed:", StringComparison.Ordinal)
            || msg.StartsWith("lane start failed:", StringComparison.Ordinal)
            ->
            Error "lane-start-failed"
        | Error _ -> Error "fission-unavailable"
        | Ok admission -> Ok admission

    let private runAdmissionReceipt
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (ctx: HostToolContext)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuntime: HostForkRuntime)
        (preAgents: (string * SessionId) list)
        (prePtys: string list)
        : Task<Result<FissionReceipt, string>> =
        task {
            let groupId = groupIdFor owner toolCallId

            let directory =
                scope.DirectoryFor ctx.SessionId |> Option.orElse scope.WorkspaceDirectory

            let preCompletionIds =
                (preAgents |> List.map (fst >> FissionExternalId.agent))
                @ (prePtys |> List.map FissionExternalId.pty)

            let deps: FissionAdmissionDependencies =
                { ParentOf = scope.Sessions.TryGetParentSession
                  OwnerView = ownerViewPort scope
                  CreateLane = createLanePort scope groupId owner profile.SelectedAgent directory parsed.Count
                  StartLane = startLanePort scope durable profile directory
                  AbortLane = abortLanePort scope
                  SilentInterruptOwner = silentInterruptOwnerPort scope }

            let hooks: FissionAdmissionHooks =
                { OnLanesCreated =
                    onLanesCreatedHook durable groupId toolCallId parsed.Count preCompletionIds ctx.ProviderRunId
                  OnFailed = onFailedHook durable groupId ctx.ProviderRunId }

            let admissionRuntime = FissionAdmission.createWithHooks deps hooks

            let! admissionAttempt = FissionAdmission.admit admissionRuntime owner parsed

            match classifyAdmissionOutcomeReceipt admissionAttempt with
            | Error code -> return Error code
            | Ok admission ->
                installPreFissionBroadcasts
                    scope
                    durable
                    profile
                    groupId
                    owner
                    admission.Lanes
                    preAgents
                    prePtys
                    ownerRuntime

                let lanes =
                    admission.Lanes
                    |> List.sortBy (fun lane -> lane.Index)
                    |> List.map (fun lane -> lane.Index, SessionId.value lane.SessionId)

                return
                    Ok
                        { GroupId = groupId
                          LaneCount = parsed.Count
                          Lanes = lanes }
        }

    let private admitWhenEventPortReadyReceipt
        (scope: ToolRuntimeScope)
        (durable: AgentJournal)
        (ctx: HostToolContext)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuntime: HostForkRuntime)
        : Task<Result<FissionReceipt, string>> =
        task {
            let preAgents = ownerRuntime.SnapshotOutstandingAgentRuns()
            let prePtys = ownerRuntime.SnapshotOutstandingPtyRuns()

            if
                (not (List.isEmpty preAgents) || not (List.isEmpty prePtys))
                && scope.EventPort.IsNone
            then
                return Error "not-ready"
            else
                return!
                    runAdmissionReceipt scope durable ctx parsed toolCallId owner profile ownerRuntime preAgents prePtys
        }

    let private executeWhenIdleReceipt
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        (owner: SessionId)
        : Task<Result<FissionReceipt, string>> =
        task {
            let activeProfile =
                PromptAuthorityProjectionQueries.activeProfile owner (AgentJournal.snapshot durable).AgentProjections

            match activeProfile, scope.RuntimeFor ctx with
            | None, _
            | _, Error _ -> return Error "context-unavailable"
            | Some profile, _ when
                profile.CanonicalRole <> Role.Engineer
                || not (OfficeCapability.isAllowed profile.CanonicalRole ToolPermission.Fission)
                ->
                return Error "fission-unavailable"
            | Some profile, Ok ownerRuntime ->
                return! admitWhenEventPortReadyReceipt scope durable ctx parsed toolCallId owner profile ownerRuntime
        }

    let private executeWithJournalReceipt
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        : Task<Result<FissionReceipt, string>> =
        task {
            let caller = SessionId.create ctx.SessionId
            let owner = scope.LogicalOwnerFor caller
            let fissionState = (AgentJournal.snapshot durable).AgentProjections.Fission

            if FissionRuntime.tryLane caller |> Option.isSome then
                return Error "not-ready"
            elif FissionProjection.tryActiveForOwner owner fissionState |> Option.isSome then
                return Error "not-ready"
            else
                return! executeWhenIdleReceipt scope ctx parsed toolCallId durable owner
        }

    let private executeParsedReceipt
        (scope: ToolRuntimeScope)
        (ctx: HostToolContext)
        (parsed: ParsedFissionPrompts)
        : Task<Result<FissionReceipt, string>> =
        task {
            match ctx.ToolCallId, scope.Journal with
            | None, _
            | _, None -> return Error "context-unavailable"
            | Some _, Some _ when String.IsNullOrWhiteSpace ctx.SessionId -> return Error "context-unavailable"
            | Some toolCallId, Some durable -> return! executeWithJournalReceipt scope ctx parsed toolCallId durable
        }

    let private decodeLanesCharge (args: HostToolArguments) (charge: string) : string list =
        match args.OptionalNonNegativeInteger "lanes" with
        | Ok(Some laneCount) when laneCount >= 2 && not (String.IsNullOrWhiteSpace charge) ->
            List.replicate laneCount charge
        | _ -> []

    let private decodePromptsForReceipt (args: HostToolArguments) : string list =
        let explicit = args.Texts "prompts"

        if not (List.isEmpty explicit) then
            explicit
        else
            decodeLanesCharge args (args.Text "charge")

    let private executeForSubsessionReceipt
        (scope: ToolRuntimeScope)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<Result<FissionReceipt, string>> =
        task {
            let prompts = decodePromptsForReceipt args

            match FissionPrompt.parse prompts with
            | Error FissionRejectReason.TooFewLanes
            | Error(FissionRejectReason.EmptyLanePrompt _) -> return Error "invalid-prompt"
            | Error _ -> return Error "fission-unavailable"
            | Ok parsed -> return! executeParsedReceipt scope ctx parsed
        }

    let private executeForCallerReceipt
        (scope: ToolRuntimeScope)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (caller: SessionId)
        : Task<Result<FissionReceipt, string>> =
        task {
            match! scope.Sessions.TryGetParentSession caller with
            | Error _ -> return Error "fission-unavailable"
            | Ok None -> return Error "fission-unavailable"
            | Ok(Some _) -> return! executeForSubsessionReceipt scope args ctx
        }

    let executeReceipt
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (args: HostToolArguments)
        : Task<Result<FissionReceipt, string>> =
        task {
            if String.IsNullOrWhiteSpace context.SessionId then
                return Error "context-unavailable"
            else
                return! executeForCallerReceipt scope args context (SessionId.create context.SessionId)
        }
