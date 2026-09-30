namespace Wanxiangshu.OpenCode

#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Enforcer.Cycle
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Manager
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Mission.Relay.OpenCode
open Wanxiangshu.Mission.WorkRecord
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Investigation.WarmStart
open Wanxiangshu.Strength
open Wanxiangshu.Strength.Projection
open Wanxiangshu.Strength.Replica
open Wanxiangshu.Host
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
open Wanxiangshu.Repository.Investigation.WarmStart
open Wanxiangshu.Resources
open Wanxiangshu.Strength.OpenCode
open Wanxiangshu.Strength.Persistence
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process
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
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Repository.Knowledge.Casebook.OpenCode
open Wanxiangshu.OpenCode.Host
open PluginHostInterop

module PluginHooks =

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwnProperty (target: obj) (prop: string) : bool = jsNative

    /// Host hook surface: chat / transform / config / compaction / text /
    /// tool hooks plus event + dispose, and the optional client tool module.
    let create (boot: PluginBoot.Boot) (host: PluginHostWiring.Host) (transform: obj -> obj -> Task<unit>) : Task<obj> =
        task {
            let scope = boot.Scope
            let journal = boot.Journal
            let wired = host.Wired
            let workspaceDirectory = boot.WorkspaceDirectory
            let input = boot.Input
            let sessionPort = host.SessionPort
            let snapshotOpt = host.SnapshotOpt
            let eventPort = host.EventPort
            let chatParams = ChatParamsHook.createWith journal

            let roleFor (sessionId: SessionId) =
                journal
                |> Option.bind (fun durable ->
                    let projections = (AgentJournal.snapshot durable).AgentProjections

                    PromptAuthorityProjectionQueries.activeProfile sessionId projections
                    |> Option.orElseWith (fun () ->
                        PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId projections))
                |> Option.map (fun profile -> profile.CanonicalRole)

            let systemTransform = ProviderSystemTransform.createWith roleFor

            // CASE-003: typed capture at the tool boundary — shared
            // CasebookLifecycle.collector; marker flag gates the after-hook.
            // Store IO stays out of SpikePlugin (unified-store dual-write gate).
            let casebookEnabled =
                match workspaceDirectory with
                | Some ws -> CasebookFeature.isEnabled ws
                | None -> false

            // The Host-native todowrite remains the physical executor. The plugin
            // owns only its provider-visible compression field and the durable
            // checkpoint recorded after physical success.
            let todoCheckpointDepths = Dictionary<string, int>()

            let collectCasebookObservation (toolInput: obj) (toolOutput: obj) =
                let toolName = if isNull toolInput then "" else string (toolInput?tool)

                let sessionId =
                    if isNull toolInput then
                        ""
                    else
                        string (toolInput?sessionID)

                let rendered = if isNull toolOutput then "" else string (toolOutput?output)

                if not (System.String.IsNullOrWhiteSpace sessionId) then
                    CasebookLifecycle.collector.Collect(sessionId, toolName, toolInput?args, rendered)

            let ownedTransform (inObj: obj) (outObj: obj) : Task =
                scope.RunOwnedWork(fun () -> transform inObj outObj)

            let client = if isNull input then null else input?client

            let configureClient () : Task<ToolRegistration> =
                task {
                    let! toolModule = importToolModule ()

                    let onRunStarted =
                        Some(fun sessionId role directory -> wired.BindActiveRun sessionId role directory)

                    // EXEC-006/008: LWR; parent→child Opening on, join off.
                    let workRecord includeOpening =
                        Some(fun sessionId ->
                            LifecycleWorkRecordProjection.lifecycleWorkRecord
                                journal
                                (SessionId.create sessionId)
                                includeOpening)

                    let parentWorkRecordFor, childWorkRecordFor = workRecord true, workRecord false

                    let childRecordForRun =
                        fun sid range run ->
                            LifecycleWorkRecordProjection.lifecycleWorkRecordBoundedForRun journal sid range run

                    let workRecordCapability: Wanxiangshu.Execution.Delegation.DelegationWorkRecordCapability =
                        { ParentWorkRecord =
                            fun sid -> LifecycleWorkRecordProjection.lifecycleWorkRecord journal sid true
                          ParentWorkRecordBounded =
                            fun sid range -> LifecycleWorkRecordProjection.lifecycleWorkRecordBounded journal sid range }

                    let casebookToolSpecs: ToolSpec list =
                        match workspaceDirectory with
                        | Some ws -> CasebookTools.buildSpecs (ToolHostCodec.factory toolModule) ws
                        | None -> []

                    let toolRegistration =
                        toolHooks
                            toolModule
                            sessionPort
                            host.CausalWaitObserver
                            host.RootWorkspace
                            journal
                            (workspaceDirectory)
                            (Some boot.StrengthScope)
                            scope
                            wired.CurrentPhysicalUserMessage
                            onRunStarted
                            parentWorkRecordFor
                            childWorkRecordFor
                            childRecordForRun
                            workRecordCapability
                            snapshotOpt
                            (Some wired.CancelSignals)
                            (Some eventPort)
                            casebookToolSpecs
                            (fun (managerSessionId: SessionId) (managerWorkspace: string) ->
                                task {
                                    try
                                        do!
                                            ManagerWorkflow.maybeDeliverLoop
                                                sessionPort
                                                host.RootWorkspace
                                                journal
                                                (Some managerWorkspace)
                                                (Some(SessionId.value managerSessionId))

                                        return Ok()
                                    with ex ->
                                        return Error ex.Message
                                })
                            (fun (worktreePath: WorktreePath) ->
                                try
                                    let git: WorkspaceSnapshotGitCapability =
                                        { TryRevParseHeadTree = GitSubject.tryRevParseHeadTree
                                          DiffHeadBinary = GitSubject.diffHeadBinary
                                          LsFilesUntrackedZ = GitSubject.lsFilesUntrackedZ
                                          HashObjectNoFilters = GitSubject.hashObjectNoFilters
                                          StatusPorcelainV2Z = GitSubject.statusPorcelainV2Z
                                          LsFilesStageZ = GitSubject.lsFilesStageZ }

                                    Ok(WorkspaceSnapshot.capture git (WorktreePath.value worktreePath))
                                with error ->
                                    Error error.Message)

                    scope.AttachToolRuntime(toolRegistration.Runtime :> ISessionRuntimeOwner)

                    return toolRegistration
                }

            let guardedClientConfiguration () : Task<ToolRegistration> =
                task {
                    try
                        return! configureClient ()
                    with ex ->
                        return
                            raise (
                                InvalidOperationException(sprintf "Failed to load OpenCode tool module: %s" ex.Message)
                            )
                }

            let! toolRegistration =
                if isNull client then
                    Task.FromResult None
                else
                    task {
                        let! registration = guardedClientConfiguration ()
                        return Some registration
                    }

            let getManagerCapabilityFacts (sessionId: string) =
                match toolRegistration with
                | Some registration -> registration.Runtime.ManagerCapabilityFactsFor sessionId
                | None -> ToolRuntimeScope.emptyManagerFacts

            // DELEGATE.md 9.1/9.2: the only enablement condition for explicit
            // read-only delegation is that a Predictor model is configured.
            // The read-only configuration existence query is owned by
            // ModelRouting (ModelRouting.sharedPredictorConfiguration, loaded
            // once together with the sole MJS model configuration during the
            // PluginBoot Load Phase, before any tool definition is registered
            // or presented) and is shared by tool decoration and delegation
            // admission, so this hook consumes the query instead of holding a
            // second enabled truth. Configured decorates the schema and
            // appends the collaboration prose; NotConfigured decorates nothing.
            // ConfigurationInvalid is a malformed model configuration: it fails
            // closed here rather than silently degrading to "not configured",
            // the same choice as ModelRouting.requireRoutingProtocol; the
            // hook's registered disposition (HookPolicy ToolDefinition:
            // Invariant / TypedPolicyFailClosed, diagnostic operation
            // plugin-hook-tool-definition-failed) already carries the report.
            let readonlyDelegationPredictorConfigured () : bool =
                match ModelRouting.sharedPredictorConfiguration () with
                | ModelRouting.PredictorConfiguration.Configured -> true
                | ModelRouting.PredictorConfiguration.NotConfigured -> false
                | ModelRouting.PredictorConfiguration.ConfigurationInvalid reason ->
                    raise (
                        InvalidOperationException(
                            sprintf "execution-model-routing: Predictor model configuration is invalid: %s" reason
                        )
                    )

            let toolDefinition (toolInput: obj) (toolOutput: obj) =
                TodoWriteCompressionContract.decorateDefinition toolInput toolOutput
                ManagerReviewContract.decorateDefinition toolInput toolOutput

                if readonlyDelegationPredictorConfigured () then
                    ReadonlyDelegationContract.decorateDefinition toolInput toolOutput

            let isReviewPermitted toolName facts =
                match ManagerReviewTools.requiredPermissions toolName with
                | Some required ->
                    let allowed = OfficeCapability.permissionsForManagerFacts facts
                    Set.isSubset required allowed && not (Set.isEmpty allowed)
                | None -> false

            let assertReviewPermitted toolName sessionId =
                let facts = getManagerCapabilityFacts sessionId

                if not (isReviewPermitted toolName facts) then
                    invalidOp (
                        sprintf
                            "Manager review tool '%s' is not permitted under current manager capability facts"
                            toolName
                    )

            let toolField (toolInput: obj) (name: string) =
                if isNull toolInput || isNull toolInput?(name) then
                    ""
                else
                    string toolInput?(name)

            let todoCheckpointKey sessionId callId =
                sessionId + "" + ToolCallId.value callId

            let isTodoWrite toolInput =
                String.Equals(toolField toolInput "tool", "todowrite", StringComparison.OrdinalIgnoreCase)

            let requireTodoWriteAdmission toolInput =
                let sessionText = toolField toolInput "sessionID"

                if String.IsNullOrWhiteSpace sessionText then
                    invalidOp "todowrite requires a session identity"

                match roleFor (SessionId.create sessionText) with
                | Some role when StaticTools.cognitiveUtilityRoleAllowed role -> ()
                | Some role -> invalidOp (sprintf "todowrite is not permitted for role %A" role)
                | None -> invalidOp "todowrite requires an established office role"

            let rememberTodoCheckpointAtKey key retainCheckpoints =
                match todoCheckpointDepths.TryGetValue key with
                | true, existing when existing <> retainCheckpoints ->
                    invalidOp "todowrite call changed retainCheckpoints before completion"
                | _ -> todoCheckpointDepths[key] <- retainCheckpoints

            let rememberTodoCheckpoint toolInput retainCheckpoints =
                let sessionText = toolField toolInput "sessionID"

                match ToolHostCodec.hookCallId toolInput with
                | None -> invalidOp "todowrite requires an exact tool call identity"
                | Some callId ->
                    let key = todoCheckpointKey sessionText callId
                    rememberTodoCheckpointAtKey key retainCheckpoints

            let captureTodoWrite toolInput toolOutput =
                requireTodoWriteAdmission toolInput

                if Option.isNone journal then
                    invalidOp "todowrite compression checkpoint requires a durable journal"

                if isNull toolOutput || isNull toolOutput?args then
                    invalidOp "todowrite before hook requires tool arguments"

                match TodoWriteCompressionContract.captureAndHide toolOutput?args with
                | Ok retainCheckpoints -> rememberTodoCheckpoint toolInput retainCheckpoints
                | Error reason -> invalidOp reason

            let captureTodoCheckpoint toolInput toolOutput =
                if isTodoWrite toolInput then
                    captureTodoWrite toolInput toolOutput

            let appendTodoCheckpoint durable sessionText callId retainCheckpoints =
                task {
                    let fact =
                        ContextFact.TodoCheckpointCommitted
                            {| SessionId = SessionId.create sessionText
                               ToolCallId = callId
                               RetainCheckpoints = retainCheckpoints |}

                    match!
                        AgentJournal.appendAgent (StreamId.Session(SessionId.create sessionText)) None fact durable
                    with
                    | Ok _ -> ()
                    | Error failure -> raise (JournalAppendException failure)
                }

            let eventText (value: obj) =
                if isNull value then
                    None
                else
                    let text = string value
                    if String.IsNullOrWhiteSpace text then None else Some text

            let firstFieldText (carrier: obj) names =
                if isNull carrier then
                    None
                else
                    names |> List.tryPick (fun name -> eventText carrier?(name))

            let todoTerminalObservation rawInput =
                let raw = HostEventEnvelope.unwrap rawInput
                let properties = if isNull raw then null else raw?properties
                let part = if isNull properties then null else properties?part
                let state = if isNull part then null else part?state

                let sessionId =
                    HostEventEnvelope.trySessionId raw
                    |> Option.orElseWith (fun () ->
                        firstFieldText part [ "sessionID"; "sessionId" ] |> Option.map SessionId.create)

                let tool = firstFieldText part [ "tool"; "name" ]
                let callId = firstFieldText part [ "callID"; "callId"; "toolCallId" ]

                let status =
                    firstFieldText state [ "status" ]
                    |> Option.map (fun value -> value.ToLowerInvariant())

                match HostEventEnvelope.eventTypeOf raw, sessionId, tool, callId, status with
                | "message.part.updated", Some sessionId, Some tool, Some callId, Some status when
                    String.Equals(tool, "todowrite", StringComparison.OrdinalIgnoreCase)
                    && (status = "completed" || status = "error")
                    ->
                    Some(SessionId.value sessionId, ToolCallId.create callId, status)
                | _ -> None

            let takeTodoCheckpoint sessionText callId status =
                let key = todoCheckpointKey sessionText callId

                match todoCheckpointDepths.TryGetValue key with
                | true, retainCheckpoints ->
                    todoCheckpointDepths.Remove key |> ignore
                    Some(sessionText, callId, status, retainCheckpoints)
                | false, _ -> None

            let requiredTodoJournal () =
                journal
                |> Option.defaultWith (fun () ->
                    invalidOp "todowrite compression checkpoint requires a durable journal")

            let settleTodoTerminal (sessionText, callId, status, retainCheckpoints) =
                if status = "completed" then
                    appendTodoCheckpoint (requiredTodoJournal ()) sessionText callId retainCheckpoints
                else
                    Task.FromResult(())

            let settleTodoEvent rawInput =
                todoTerminalObservation rawInput
                |> Option.bind (fun (sessionText, callId, status) -> takeTodoCheckpoint sessionText callId status)
                |> Option.map settleTodoTerminal
                |> Option.defaultValue (Task.FromResult(()))

            let checkManagerReviewPermissions toolName toolInput =
                if ManagerReviewTools.isReviewTool toolName then
                    assertReviewPermitted toolName (toolField toolInput "sessionID")

            // host-boundary-032 / DELEGATE_REVISE.md: snapshot the protocol
            // fields the model actually produced, before either contract family
            // hides them. The Host persists the stripped arguments, so the
            // provider transform restores the fields into the request from this
            // vault. contract only for review tools (the only place review hide
            // runs); delegation fields only for participating tools (classifyTool = EstimateAfterCall).
            //
            // The call id is read from the hook input itself: WHAT[009]'s
            // both-halves pairing lives in decodeContext, and this hook input
            // carries no messageID, so decodeContext would always answer None.
            let sanitizeSnapshot (toolName: string) (snapshot: ProtocolArgumentVault.Snapshot) =
                let isReview = ManagerReviewTools.isReviewTool toolName

                let isDelegationActive =
                    readonlyDelegationPredictorConfigured ()
                    && InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall

                { ProtocolArgumentVault.Snapshot.Contract = (if isReview then snapshot.Contract else None)
                  ProtocolArgumentVault.Snapshot.ReadonlyRounds =
                    (if isDelegationActive then snapshot.ReadonlyRounds else None)
                  ProtocolArgumentVault.Snapshot.SelfNote = (if isDelegationActive then snapshot.SelfNote else None) }

            let commitRecordedSnapshot
                (vault: ProtocolArgumentVault.Vault)
                (sessionId: string)
                (toolCallId: ToolCallId)
                (recorded: ProtocolArgumentVault.Snapshot)
                =
                if
                    recorded.Contract.IsNone
                    && recorded.ReadonlyRounds.IsNone
                    && recorded.SelfNote.IsNone
                then
                    ()
                else
                    ProtocolArgumentVault.record vault sessionId (ToolCallId.value toolCallId) recorded

            let recordSnapshotIfPresent
                (vault: ProtocolArgumentVault.Vault)
                (sessionId: string)
                (toolCallId: ToolCallId)
                (toolName: string)
                (args: obj)
                =
                match ProtocolArgumentVault.snapshotOfArguments args with
                | Some snapshot ->
                    commitRecordedSnapshot vault sessionId toolCallId (sanitizeSnapshot toolName snapshot)
                | None -> ()

            let tryRecordVaultEntry
                (vault: ProtocolArgumentVault.Vault)
                (toolInput: obj)
                (toolOutput: obj)
                (toolCallId: ToolCallId)
                =
                let context = ToolHostCodec.decodeContext toolInput

                if not (String.IsNullOrWhiteSpace context.SessionId) then
                    let toolName = toolField toolInput "tool"
                    recordSnapshotIfPresent vault context.SessionId toolCallId toolName toolOutput?args

            let tryRecordCall
                (vault: ProtocolArgumentVault.Vault)
                (toolInput: obj)
                (toolOutput: obj)
                (callIdOpt: ToolCallId option)
                =
                match callIdOpt with
                | Some toolCallId -> tryRecordVaultEntry vault toolInput toolOutput toolCallId
                | None -> ()

            let recordProtocolArgumentVault (toolInput: obj) (toolOutput: obj) =
                if not (isNull toolOutput) && not (isNull toolOutput?args) then
                    tryRecordCall boot.ProtocolArgumentVault toolInput toolOutput (ToolHostCodec.hookCallId toolInput)

            let toolBefore (toolInput: obj) (toolOutput: obj) =
                task {
                    do!
                        Wanxiangshu.OpenCode.Host.RequirementGrounding.RequirementGroundingGate.before
                            journal
                            workspaceDirectory
                            toolInput
                            toolOutput

                    let toolName = toolField toolInput "tool"

                    checkManagerReviewPermissions toolName toolInput

                    let isParticipatingTool =
                        InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall

                    let isDelegationActive =
                        readonlyDelegationPredictorConfigured () && isParticipatingTool

                    if isDelegationActive && not (isNull toolOutput) && not (isNull toolOutput?args) then
                        let args = toolOutput?args

                        match InvestigationEstimateContract.parseParticipatingArguments args with
                        | Ok _ -> ()
                        | Error err ->
                            let sessionText =
                                if not (isNull toolInput) && not (isNull toolInput?sessionID) then
                                    string toolInput?sessionID
                                else
                                    let ctx = ToolHostCodec.decodeContext toolInput
                                    ctx.SessionId

                            let language = ProviderLanguageBinding.forSessionText sessionText
                            let explanation = InvestigationEstimateContract.formatArgumentError language err
                            invalidOp (sprintf "Invalid investigation estimate arguments: %s" explanation)

                    recordProtocolArgumentVault toolInput toolOutput

                    captureTodoCheckpoint toolInput toolOutput

                    let context = ToolHostCodec.decodeContext toolInput

                    // Same hook-shaped call id as the vault above:
                    // the decodeContext ToolCallId is None on this hook input.
                    match journal, ToolHostCodec.hookCallId toolInput with
                    | Some durable, Some toolCallId when not (String.IsNullOrWhiteSpace context.SessionId) ->
                        let port = AgentJournalPortAdapter.forDelegatedToolEstimate durable
                        do! DelegatedToolEstimateLedger.observe port (SessionId.create context.SessionId) toolCallId
                    | _ -> ()

                    if
                        ManagerReviewTools.isReviewTool toolName
                        && not (isNull toolOutput)
                        && not (isNull toolOutput?args)
                    then
                        ManagerReviewContract.hide toolOutput?args

                    // host-boundary-032 / DELEGATE_REVISE.md: narrow hide to participating tools only when predictor is configured.
                    // Non-participating and unreviewed tools are untouched, leaving their own business
                    // arguments intact. Unconfigured predictor leaves all tools untouched.
                    if isDelegationActive && not (isNull toolOutput) && not (isNull toolOutput?args) then
                        ReadonlyDelegationContract.hide toolOutput?args
                }

            let toolAfter (toolInput: obj) (toolOutput: obj) =
                task {
                    let toolName = toolField toolInput "tool"

                    let isParticipatingTool =
                        InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall

                    if not (isNull toolInput) && not (isNull toolInput?args) then
                        // host-boundary-032: same-source restore for both
                        // contract families on exception, repeat and concurrent
                        // paths. Restore is idempotent and a no-op when the
                        // after hook receives a different object than before.
                        if isParticipatingTool then
                            ReadonlyDelegationContract.restore toolInput?args

                        ManagerReviewContract.restore toolInput?args
                        TodoWriteCompressionContract.restore toolInput?args

                    do!
                        Wanxiangshu.OpenCode.Host.RequirementGrounding.RequirementGroundingGate.after
                            journal
                            workspaceDirectory
                            toolInput
                            toolOutput

                    if casebookEnabled then
                        HookPolicy.observeOptional Diagnostic.emit OptionalHookEffect.CasebookObservation (fun () ->
                            collectCasebookObservation toolInput toolOutput)
                        |> ignore
                }

            let chatMessage =
                registeredHook HookKey.ChatMessage (curriedHook wired.ChatMessageHook)

            let chatParamsRegistration =
                registeredHook HookKey.ChatParams (curriedHook chatParams)

            let messagesTransform =
                registeredHook HookKey.MessagesTransform (curriedHook (box ownedTransform))

            let systemTransformRegistration =
                registeredHook HookKey.SystemTransform (pairedHook (box systemTransform))

            let syncHostLanguagePreference (config: obj) =
                let lang = config?language

                if not (isNull lang) then
                    ProviderLanguageBinding.setHostConfigPreference (string lang)

            let configurePluginHost (config: obj) =
                if not (isNull config) then
                    config?snapshot <- box false
                    syncHostLanguagePreference config
                    ManagerConfig.configureManager config |> ignore
                    scope.RecordCompactionSettingGap(HostCompactionGate.enforceSettings config)

            let config = registeredHook HookKey.Config (unaryHook (box configurePluginHost))

            let sessionCompacting =
                registeredHook HookKey.SessionCompacting (pairedHook (box HostCompactionGate.onSessionCompacting))

            let compactionAutoContinue =
                registeredHook
                    HookKey.CompactionAutoContinue
                    (pairedHook (box HostCompactionGate.onCompactionAutoContinue))

            let toolDefinitionRegistration =
                registeredHook HookKey.ToolDefinition (pairedHook (box toolDefinition))

            let toolBeforeRegistration =
                registeredHook HookKey.ToolBefore (pairedHook (box toolBefore))

            let toolAfterRegistration =
                registeredHook HookKey.ToolAfter (pairedHook (box toolAfter))

            let observeEvent raw =
                task {
                    do! settleTodoEvent raw
                    do! wired.ObserveEvent raw
                }

            let event = registeredHook HookKey.Event (unaryHook (box observeEvent))

            let dispose =
                let disposeAll () = scope.DisposeAsync()

                registeredHook HookKey.Dispose (nullaryHook (box disposeAll))

            let hooks =
                createObj (
                    [ chatMessage
                      chatParamsRegistration
                      messagesTransform
                      systemTransformRegistration
                      config
                      sessionCompacting
                      compactionAutoContinue
                      toolDefinitionRegistration
                      toolBeforeRegistration
                      toolAfterRegistration
                      event
                      dispose ]
                    @ (toolRegistration
                       |> Option.map (fun registration -> [ "tool", registration.Tools ])
                       |> Option.defaultValue [])
                )

            return box hooks
        }
