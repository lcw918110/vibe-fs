namespace Wanxiangshu.Execution.Fission

open Fable.Core
open Fable.Core.JsInterop

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Trace
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Execution.Routing
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// Same-participant physical replacement, host-neutral. Fission never calls the
/// host session fork endpoint: it creates fresh sibling sessions, starts them
/// from the owner's current session context (when there is one) + exact lane
/// input, then physically interrupts the old present without terminating the
/// logical owner. Every host capability arrives through FissionHostPort; a host
/// tool adapter only decodes the call and encodes the outcome.
module FissionLaunch =

    [<Import("FissionLifecycle", "wanxiangli/fission-group")>]
    let private lifecycleClass: obj = jsNative
    let private lifecycle: obj = createNew lifecycleClass ()

    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private fissionRuntime: obj = jsNative
    [<Emit("$0 === undefined")>]
    let private isUndefined (_v: obj) : bool = jsNative

    let private isLane (sessionId: SessionId) : bool =
        let r: obj = fissionRuntime?tryLane (SessionId.value sessionId)
        not (isNull r) && not (isUndefined r)

    type FissionLaunchRequest =
        { CallerSessionId: string
          ToolCallId: ToolCallId option
          ProviderRun: ProviderRunIdentity option
          Prompts: string list }

    type FissionLaunched =
        { GroupId: string
          LaneCount: int
          Lanes: FissionStartedLane list }

    [<RequireQualifiedAccess>]
    type FissionLaunchRejection =
        | MissingCaller
        | ParentLookupFailed of string
        | NotSubsession
        | LanePromptsInvalid
        | PromptsRejected of FissionRejectReason
        | ContextUnavailable
        | CallerIsLane
        | OwnerAlreadyFissioned
        | AuthorityUnavailable
        | RoleNotPermitted
        | ObservationUnavailable
        | AdmissionRejected of FissionRejectReason

    [<Literal>]
    let private SharedCompletionPath = "tool/fission/shared-completion"

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
                SharedCompletionPath
                (Map [ "completion_id", completionId; "payload", payload ])

        LlmFacing.instruction instruction
        |> LlmFacing.withData [ LlmFacing.Data.stringField "completion_id" completionId ]
        |> LlmFacing.render

    let private nativeResult convert (operation: Task<Result<'a, string>>) =
        task {
            let! answer = operation
            return
                match answer with
                | Ok value -> createObj [ "ok" ==> true; "value" ==> convert value ]
                | Error error -> createObj [ "ok" ==> false; "error" ==> error ]
        }

    let private appendCompletion (durable: AgentJournal) (fact: obj) =
        let owner = SessionId.create(unbox fact?owner)
        let event =
            if string fact?kind = "completionCaptured" then
                FissionFact.FissionCompletionCaptured
                    {| GroupId = unbox fact?groupId
                       OwnerSessionId = owner
                       CompletionId = unbox fact?completion
                       PayloadRef = BlobRef.create(unbox fact?reference)
                       PayloadDigest = BlobDigest.create(unbox fact?digest) |}
            else
                FissionFact.FissionCompletionDelivered
                    {| GroupId = unbox fact?groupId
                       OwnerSessionId = owner
                       CompletionId = unbox fact?completion
                       LaneIndex = unbox fact?laneIndex |}
        appendFission durable owner None event |> nativeResult box

    let private sendSharedCompletion (port: FissionHostPort) (durable: AgentJournal) (request: obj) (lane: obj) =
        task {
            let session = SessionId.create(unbox lane?session)
            let authority =
                PromptAuthorityProjectionQueries.activeProfile session (AgentJournal.snapshot durable).AgentProjections
            match authority with
            | None -> return Ok false
            | Some profile ->
                let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)
                let! sent =
                    dispatcher.SendContinuation
                        (DispatchSessionPort.ofSessionPort port.Sessions)
                        session
                        (deliveryPrompt (SessionId.create(unbox request?owner)) (unbox request?completion) (unbox request?payload))
                        PromptAuthority.ContinuationKind.FissionHandoff
                        profile
                        (port.DirectoryFor session)
                        PromptDispatcher.AwaitMode.Detached
                        None
                return sent |> Result.map (fun _ -> true)
        }
        |> nativeResult box

    let private reportBroadcast kind (request: obj) (detail: obj) =
        let owner: string = unbox request?owner
        let group: string = unbox request?id
        let completion: string = unbox request?completion
        let error: string = unbox detail?error
        let event, session, message =
            match kind with
            | "capture-write" ->
                "fission-capture-write-failed", owner,
                sprintf "fission completion capture blob write failed: group=%s completion=%s reason=%s" group completion error
            | "capture-append" ->
                "fission-capture-fact-append-failed", owner,
                sprintf "fission completion captured fact append failed: group=%s completion=%s reason=%s" group completion error
            | "delivery-append" ->
                "fission-delivery-fact-append-failed", owner,
                sprintf "fission completion delivered fact append failed: group=%s completion=%s lane=%d reason=%s"
                    group completion (unbox<int> detail?lane) error
            | "shared-send" ->
                "fission-send-failed", unbox detail?session,
                sprintf "fission shared completion send failed: group=%s completion=%s lane=%d reason=%s"
                    group completion (unbox<int> detail?lane) error
            | _ ->
                "fission-broadcast-failed", owner,
                sprintf "fission shared completion broadcast failed: group=%s completion=%s error=%s" group completion error
        Diagnostic.emit event [ "session_id", session; "result", message ]

    let private captureAndBroadcast
        (port: FissionHostPort)
        (durable: AgentJournal)
        (_profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (completionId: string)
        (payload: string)
        : Task<unit> =
        let request =
            createObj
                [ "id" ==> groupId
                  "owner" ==> SessionId.value owner
                  "completion" ==> completionId
                  "payload" ==> payload
                  "lanes" ==> (lanes |> List.map (fun lane ->
                      createObj [ "index" ==> lane.Index; "session" ==> SessionId.value lane.SessionId; "prompt" ==> lane.Prompt ]) |> List.toArray) ]
        let effects =
            createObj
                [ "history" ==> (fun () -> FissionProjection.native (AgentJournal.snapshot durable).AgentProjections.Fission)
                  "writeBlob" ==> (fun text -> durable.WriteBlob text |> nativeResult (fun receipt ->
                      createObj [ "reference" ==> BlobRef.value receipt.BlobRef; "digest" ==> BlobDigest.value receipt.BlobDigest ]))
                  "append" ==> (fun fact -> appendCompletion durable fact)
                  "sendShared" ==> (fun request lane -> sendSharedCompletion port durable request lane)
                  "canConverge" ==> port.Events.IsSome
                  "revision" ==> (fun () -> snd durable.SnapshotWithRevision)
                  "awaitChange" ==> (fun revision -> task {
                      let! _ = durable.AwaitChangeFrom revision
                      return () })
                  "converge" ==> (fun owner ->
                      FissionHost.tryConverge port.Sessions durable (fun session -> port.DirectoryFor session) (SessionId.create owner))
                  "report" ==> (fun kind request detail -> reportBroadcast kind request detail) ]
        lifecycle?broadcast (request, effects)

    let private payloadFromView view fallback =
        match view with
        | Some record when not (String.IsNullOrWhiteSpace record) -> record
        | _ -> fallback

    let private captureCompletedTerminal
        (port: FissionHostPort)
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
            let view =
                TaskDeliveryJournal.tryFindByFenceFields
                    durable
                    childId
                    terminal.ProviderRun
                    None
                    (Some terminal.AuthorityRootUserMessageId)
                    None
                    None
                    None
                |> Option.map (fun witness -> witness.View)
                |> Option.orElseWith (fun () ->
                    JsDeliveredValues.tryPeekMaterialWith (SessionId.value childId) (Some terminal.TerminalText))
                |> Option.map ReturnedMaterial.fullText

            let payload = payloadFromView view terminal.TerminalText

            do! captureAndBroadcast port durable profile groupId owner lanes completionId payload
        }

    let private dispatchTerminalOutcome
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (childId: SessionId)
        (completionId: string)
        (outcome: Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome)
        =
        match outcome with
        | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Aborted _ -> ()
        | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Failed stop ->
            captureAndBroadcast
                port
                durable
                profile
                groupId
                owner
                lanes
                completionId
                (String.concat "\n" [ "status=failed"; "error=" + stop.Reason ])
            |> ignore
        | Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome.Completed terminal ->
            captureCompletedTerminal port durable profile groupId owner lanes childId completionId terminal
            |> ignore

    let private onPreAgentTerminal
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (childId: SessionId)
        (completionId: string)
        (sessionId: SessionId)
        (outcome: Wanxiangshu.Foundation.TerminalSignal.TerminalOutcome)
        =
        if sessionId <> childId then
            ()
        else
            dispatchTerminalOutcome port durable profile groupId owner lanes childId completionId outcome

    let private ptyCompletionPayload (item: FissionPtyCompletion) =
        match item.Status with
        | FissionPtyStatus.Exited -> item.Outcome
        | FissionPtyStatus.Failed ->
            String.concat
                "\n"
                [ "status=failed"
                  "code=" + (item.Code |> Option.defaultValue "")
                  "error=" + (item.Message |> Option.defaultValue "") ]
        | FissionPtyStatus.Aborted ->
            String.concat
                "\n"
                [ "status=aborted"
                  "code=" + (item.Code |> Option.defaultValue "")
                  "error=" + (item.Message |> Option.defaultValue "") ]

    let private installAgentBroadcasts
        (eventPort: IEventObservationPort)
        (port: FissionHostPort)
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
                    onPreAgentTerminal port durable profile groupId owner lanes childId completionId
                )

            fissionRuntime?trackGroupResource (groupId, subscription)

    let private onPtyCompletion
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (wanted: Set<string>)
        (item: FissionPtyCompletion)
        =
        let ptyId = item.PtyId

        if not (Set.contains ptyId wanted) then
            ()
        else
            captureAndBroadcast
                port
                durable
                profile
                groupId
                owner
                lanes
                (FissionExternalId.pty ptyId)
                (ptyCompletionPayload item)
            |> ignore

    let private installPtyBroadcasts
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (prePtys: string list)
        (ownerRuns: FissionOwnerRuns)
        =
        if List.isEmpty prePtys then
            ()
        else
            let wanted = Set.ofList prePtys

            let subscription =
                ownerRuns.SubscribePtyCompletion(onPtyCompletion port durable profile groupId owner lanes wanted)

            fissionRuntime?trackGroupResource (groupId, subscription)

    let private installPreFissionBroadcasts
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (groupId: string)
        (owner: SessionId)
        (lanes: FissionStartedLane list)
        (preAgents: (string * SessionId) list)
        (prePtys: string list)
        (ownerRuns: FissionOwnerRuns)
        =
        match port.Events with
        | None -> ()
        | Some eventPort ->
            installAgentBroadcasts eventPort port durable profile groupId owner lanes preAgents

            installPtyBroadcasts port durable profile groupId owner lanes prePtys ownerRuns

    /// Fission is the same identity on another execution lane, so the lanes
    /// inherit the owner's current conversation context unconditionally. The
    /// source is the session context itself, not the last delivered record.
    let private ownerViewPort (port: FissionHostPort) (sessionId: SessionId) =
        task {
            match port.OwnerView with
            | Some viewOf ->
                let! view = viewOf sessionId
                return Ok view
            | None -> return Ok None
        }

    let private createLanePort
        (port: FissionHostPort)
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
                port.Sessions.CreateSiblingSession(
                    logicalOwner,
                    physicalParent,
                    { Title = Some(sprintf "Fission lane %d/%d" (lane.Index + 1) parsedCount)
                      Agent = Some participant
                      Directory = directory }
                )
            with
            | Error error -> return Error error
            | Ok laneId ->
                port.RegisterLane laneId physicalParent directory

                fissionRuntime?bindLane (groupId, SessionId.value owner, lane.Index, parsedCount, SessionId.value laneId)
                return Ok laneId
        }

    let private startLanePort
        (port: FissionHostPort)
        (durable: AgentJournal)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (directory: string option)
        (laneId: SessionId)
        (startup: string)
        =
        task {
            port.LaneStarted laneId profile.CanonicalRole directory

            match! XTraceCapture.captureOpeningWithReceipt (Some durable) laneId startup [] with
            | Error _ -> return Error "fission_trace_capture_failed"
            | Ok _ ->
                let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

                match!
                    dispatcher.SendContinuation
                        (DispatchSessionPort.ofSessionPort port.Sessions)
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

    let private abortLanePort (port: FissionHostPort) (laneId: SessionId) =
        task {
            fissionRuntime?unbindLane (SessionId.value laneId) |> ignore
            ModelRouting.cancelUnacquiredExecution laneId |> ignore
            let! aborted = port.Sessions.AbortSession laneId

            match aborted with
            | Ok() -> ()
            | Error reason ->
                Diagnostic.emit
                    "fission-lane-abort-failed"
                    [ "session_id", SessionId.value laneId
                      "result", "fission lane abort failed: reason=" + reason ]

            return ()
        }
        :> Task

    let private silentInterruptOwnerPort (port: FissionHostPort) (sessionId: SessionId) =
        task {
            fissionRuntime?markSilentInterrupt (SessionId.value sessionId)

            match! port.Sessions.InterruptAttempt sessionId with
            | Ok() -> return Ok()
            | Error error ->
                fissionRuntime?clearSilentInterrupt (SessionId.value sessionId)
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
            let! appended =
                appendFission
                    durable
                    logicalOwner
                    providerRun
                    (FissionFact.FissionFailed
                        {| GroupId = groupId
                           OwnerSessionId = logicalOwner
                           Reason = reason |})

            match appended with
            | Error failure ->
                Diagnostic.emit
                    "fission-failed-fact-append-failed"
                    [ "session_id", SessionId.value logicalOwner
                      "result", sprintf "fission failed fact append failed: group=%s reason=%s" groupId failure ]
            | Ok() -> ()

            return ()
        }
        :> Task

    let private runAdmission
        (port: FissionHostPort)
        (durable: AgentJournal)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuns: FissionOwnerRuns)
        (preAgents: (string * SessionId) list)
        (prePtys: string list)
        =
        task {
            let groupId = groupIdFor owner toolCallId

            let directory = port.DirectoryFor caller |> Option.orElse port.WorkspaceDirectory

            let preCompletionIds =
                (preAgents |> List.map (fst >> FissionExternalId.agent))
                @ (prePtys |> List.map FissionExternalId.pty)

            let deps: FissionAdmissionDependencies =
                { ParentOf = port.Sessions.TryGetParentSession
                  OwnerView = ownerViewPort port
                  CreateLane = createLanePort port groupId owner profile.SelectedAgent directory parsed.Count
                  StartLane = startLanePort port durable profile directory
                  AbortLane = abortLanePort port
                  SilentInterruptOwner = silentInterruptOwnerPort port }

            let hooks: FissionAdmissionHooks =
                { OnLanesCreated =
                    onLanesCreatedHook durable groupId toolCallId parsed.Count preCompletionIds request.ProviderRun
                  OnFailed = onFailedHook durable groupId request.ProviderRun }

            let admissionRuntime = FissionAdmission.createWithHooks deps hooks

            match! FissionAdmission.admit admissionRuntime owner parsed with
            | Error reason -> return Error(FissionLaunchRejection.AdmissionRejected reason)
            | Ok admission ->
                installPreFissionBroadcasts
                    port
                    durable
                    profile
                    groupId
                    owner
                    admission.Lanes
                    preAgents
                    prePtys
                    ownerRuns

                let launched: FissionLaunched =
                    { GroupId = groupId
                      LaneCount = parsed.Count
                      Lanes = admission.Lanes |> List.sortBy (fun lane -> lane.Index) }

                return Ok launched
        }

    let private admitWhenEventPortReady
        (port: FissionHostPort)
        (durable: AgentJournal)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (owner: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (ownerRuns: FissionOwnerRuns)
        =
        task {
            let preAgents = ownerRuns.OutstandingAgentRuns()
            let prePtys = ownerRuns.OutstandingPtyRuns()

            if
                (not (List.isEmpty preAgents) || not (List.isEmpty prePtys))
                && port.Events.IsNone
            then
                return Error FissionLaunchRejection.ObservationUnavailable
            else
                return!
                    runAdmission port durable request caller parsed toolCallId owner profile ownerRuns preAgents prePtys
        }

    let private executeWhenIdle
        (port: FissionHostPort)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        (owner: SessionId)
        =
        task {
            let activeProfile =
                PromptAuthorityProjectionQueries.activeProfile owner (AgentJournal.snapshot durable).AgentProjections

            match activeProfile, port.OwnerRuns() with
            | None, _
            | _, Error _ -> return Error FissionLaunchRejection.AuthorityUnavailable
            | Some profile, _ when
                profile.CanonicalRole <> Role.Engineer
                || not (OfficeCapability.isAllowed profile.CanonicalRole ToolPermission.Fission)
                ->
                return Error FissionLaunchRejection.RoleNotPermitted
            | Some profile, Ok ownerRuns ->
                return! admitWhenEventPortReady port durable request caller parsed toolCallId owner profile ownerRuns
        }

    let private executeWithJournal
        (port: FissionHostPort)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        (parsed: ParsedFissionPrompts)
        (toolCallId: ToolCallId)
        (durable: AgentJournal)
        =
        task {
            let owner = port.LogicalOwnerFor caller
            let fissionState = (AgentJournal.snapshot durable).AgentProjections.Fission

            if isLane caller then
                return Error FissionLaunchRejection.CallerIsLane
            elif FissionProjection.tryActiveForOwner owner fissionState |> Option.isSome then
                return Error FissionLaunchRejection.OwnerAlreadyFissioned
            else
                return! executeWhenIdle port request caller parsed toolCallId durable owner
        }

    let private executeParsed
        (port: FissionHostPort)
        (journal: AgentJournal option)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        (parsed: ParsedFissionPrompts)
        =
        task {
            match request.ToolCallId, journal with
            | None, _
            | _, None -> return Error FissionLaunchRejection.ContextUnavailable
            | Some toolCallId, Some durable -> return! executeWithJournal port request caller parsed toolCallId durable
        }

    let private executeForSubsession
        (port: FissionHostPort)
        (journal: AgentJournal option)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        =
        task {
            match FissionPrompt.parse request.Prompts with
            | Error FissionRejectReason.TooFewLanes
            | Error(FissionRejectReason.EmptyLanePrompt _) -> return Error FissionLaunchRejection.LanePromptsInvalid
            | Error reason -> return Error(FissionLaunchRejection.PromptsRejected reason)
            | Ok parsed -> return! executeParsed port journal request caller parsed
        }

    let private executeForCaller
        (port: FissionHostPort)
        (journal: AgentJournal option)
        (request: FissionLaunchRequest)
        (caller: SessionId)
        =
        task {
            match! port.Sessions.TryGetParentSession caller with
            | Error reason -> return Error(FissionLaunchRejection.ParentLookupFailed reason)
            | Ok None -> return Error FissionLaunchRejection.NotSubsession
            | Ok(Some _) -> return! executeForSubsession port journal request caller
        }

    let launch
        (port: FissionHostPort)
        (journal: AgentJournal option)
        (request: FissionLaunchRequest)
        : Task<Result<FissionLaunched, FissionLaunchRejection>> =
        task {
            if String.IsNullOrWhiteSpace request.CallerSessionId then
                return Error FissionLaunchRejection.MissingCaller
            else
                return! executeForCaller port journal request (SessionId.create request.CallerSessionId)
        }
