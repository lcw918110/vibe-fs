namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
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

            let isReplica (sessionId: SessionId) =
                boot.StrengthScope.StrengthRuntime.TryFindByReplica sessionId |> Option.isSome

            let systemTransform = ProviderSystemTransform.createWith roleFor isReplica

            // CASE-003: typed capture at the tool boundary — shared
            // CasebookLifecycle.collector; marker flag gates the after-hook.
            // Store IO stays out of SpikePlugin (unified-store dual-write gate).
            let casebookEnabled =
                match workspaceDirectory with
                | Some ws -> CasebookFeature.isEnabled ws
                | None -> false

            // The Host-native todowrite remains the physical executor, schema and
            // description included. The plugin records one durable compression
            // checkpoint per successfully completed call, keyed by exact call id.
            let settledTodoCheckpointCalls = HashSet<SessionId * ToolCallId>()
            // obligation-ledger-005: the in-flight append for each identity, so a
            // concurrent duplicate terminal waits for the same append's final
            // outcome instead of reporting an early success.
            let pendingTodoCheckpointAppends = Dictionary<SessionId * ToolCallId, Task<unit>>()

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

            // concern-routing-003: the reserved `user` address renders as a
            // user-visible TUI notification. The server SDK exposes
            // `client.tui.showToast`; a missing client leaves the notifier
            // absent and publish still copies to `root`.
            let userNotify =
                if isNull client then
                    None
                else
                    Some(fun (title: string) (message: string) ->
                        Fable.Core.JsInterop.emitJsExpr
                            (client, title, message)
                            "$0?.tui?.showToast?.({ body: { title: $1, message: $2, variant: 'info' } })"
                        |> ignore)

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
                        | Some ws ->
                            CasebookTools.buildSpecs (ToolHostCodec.factory toolModule) ws boot.CasebookSettlements
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
                            userNotify

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

            // the only enablement condition for explicit
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

            let appendTodoCheckpoint durable sessionText callId =
                task {
                    let fact =
                        ContextFact.TodoCheckpointCommitted
                            {| SessionId = SessionId.create sessionText
                               ToolCallId = callId |}

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

            let requiredTodoJournal () =
                journal
                |> Option.defaultWith (fun () ->
                    invalidOp "todowrite compression checkpoint requires a durable journal")

            let removePendingCheckpoint key =
                lock pendingTodoCheckpointAppends (fun () -> pendingTodoCheckpointAppends.Remove(key) |> ignore)

            let releaseUnattemptedCheckpoint key failure =
                match failure with
                | JournalAppendFailure.WriterUnavailable _ ->
                    removePendingCheckpoint key
                    settledTodoCheckpointCalls.Remove(key) |> ignore
                | JournalAppendFailure.WriteUnknown _
                | JournalAppendFailure.NoNewWriteReleaseFailed _
                | JournalAppendFailure.FactRejected _ -> ()

            let appendTerminalCheckpoint key sessionText callId =
                task {
                    try
                        do! appendTodoCheckpoint (requiredTodoJournal ()) sessionText callId
                        removePendingCheckpoint key
                    with :? JournalAppendException as appendFailure ->
                        releaseUnattemptedCheckpoint key appendFailure.Failure
                        return raise appendFailure
                }

            let startTerminalCheckpoint key sessionText callId =
                lock pendingTodoCheckpointAppends (fun () ->
                    match pendingTodoCheckpointAppends.TryGetValue(key) with
                    | true, pending -> pending
                    | false, _ ->
                        let append = appendTerminalCheckpoint key sessionText callId
                        pendingTodoCheckpointAppends[key] <- append
                        append)

            let settleNewTodoTerminal key sessionText callId status =
                if not (settledTodoCheckpointCalls.Add(key)) then
                    Task.FromResult(())
                elif status = "completed" then
                    startTerminalCheckpoint key sessionText callId
                else
                    Task.FromResult(())

            /// One durable checkpoint per exact terminal call. A repeated Host
            /// part update for the same call id is a replay, not a second fact.
            /// obligation-ledger-005: concurrent duplicates of the same terminal
            /// share the one in-flight append and observe its final outcome.
            /// A failed append propagates its error to every waiter. The settled
            /// mark is only kept when the outcome is final: WriteUnknown and
            /// FactRejected stay settled (the event may already be durable or is
            /// semantically refused — repeating it is unsafe), while
            /// WriterUnavailable (NotAttempted) releases the mark so the Host's
            /// retry gets a real second attempt.
            let settleTodoTerminal (sessionText, callId, status) =
                let key = SessionId.create sessionText, callId

                // A concurrent duplicate first joins the in-flight append (it
                // must observe the same final outcome); only a terminal with no
                // pending append consults the settled mark.
                let joinPending () =
                    lock pendingTodoCheckpointAppends (fun () ->
                        match pendingTodoCheckpointAppends.TryGetValue(key) with
                        | true, pending -> Some(pending)
                        | false, _ -> None)

                match joinPending () with
                | Some pending -> pending
                | None -> settleNewTodoTerminal key sessionText callId status

            let settleTodoEvent rawInput =
                todoTerminalObservation rawInput
                |> Option.map settleTodoTerminal
                |> Option.defaultValue (Task.FromResult(()))

            let checkManagerReviewPermissions toolName toolInput =
                if ManagerReviewTools.isReviewTool toolName then
                    assertReviewPermitted toolName (toolField toolInput "sessionID")

            // host-boundary-032: snapshot the protocol
            // fields the model actually produced, before either contract family
            // hides them. The Host persists the stripped arguments, so the
            // provider transform restores the fields into the request from this
            // vault. contract only for review tools (the only place review hide
            // runs); delegation fields only for participating tools (classifyTool = EstimateAfterCall).
            //
            // The call id is read from the hook input itself: WHAT[009]'s
            // both-halves pairing lives in decodeContext, and this hook input
            // carries no messageID, so decodeContext would always answer None.
            let protocolFieldOwnership (toolName: string) : ProtocolArgumentVault.FieldOwnership =
                { ReviewContract = ManagerReviewTools.isReviewTool toolName
                  InvestigationEstimate =
                    readonlyDelegationPredictorConfigured ()
                    && InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall }

            let recordSnapshotIfPresent
                (vault: ProtocolArgumentVault.Vault)
                (sessionId: string)
                (toolCallId: ToolCallId)
                (toolName: string)
                (args: obj)
                =
                match ProtocolArgumentVault.snapshotOfArguments (protocolFieldOwnership toolName) args with
                | Some snapshot -> ProtocolArgumentVault.record vault sessionId (ToolCallId.value toolCallId) snapshot
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

            let estimateSessionText (toolInput: obj) =
                if not (isNull toolInput) && not (isNull toolInput?sessionID) then
                    string toolInput?sessionID
                else
                    (ToolHostCodec.decodeContext toolInput).SessionId

            let rejectInvalidEstimateArguments (toolInput: obj) (args: obj) =
                match InvestigationEstimateContract.parseParticipatingArguments args with
                | Ok _ -> ()
                | Error err ->
                    let language =
                        ProviderLanguageBinding.forSessionText (estimateSessionText toolInput)

                    let explanation = InvestigationEstimateContract.formatArgumentError language err
                    invalidOp (sprintf "Invalid investigation estimate arguments: %s" explanation)

            let restoreParticipatingArguments (isParticipatingTool: bool) (args: obj) =
                if isParticipatingTool then
                    ReadonlyDelegationContract.restore args

                ManagerReviewContract.restore args

            let hideReviewArguments owner toolName args =
                if ManagerReviewTools.isReviewTool toolName then
                    ManagerReviewContract.hideForCall owner args

            let hideDelegationArguments owner isDelegationActive args =
                if isDelegationActive then
                    ReadonlyDelegationContract.hideForCall owner args

            let hideProtocolArguments owner toolName isDelegationActive args =
                try
                    hideReviewArguments owner toolName args
                    hideDelegationArguments owner isDelegationActive args
                with error ->
                    restoreParticipatingArguments isDelegationActive args
                    raise error

            let requireDelegationEstimateArguments (toolInput: obj) (toolOutput: obj) =
                if not (isNull toolOutput) && not (isNull toolOutput?args) then
                    rejectInvalidEstimateArguments toolInput toolOutput?args

            let argumentCallOwner toolName toolInput : ProtocolArgumentCall option =
                let context = ToolHostCodec.decodeContext toolInput

                match ToolHostCodec.hookCallId toolInput with
                | Some callId when not (String.IsNullOrWhiteSpace context.SessionId) ->
                    Some
                        { SessionId = SessionId.create context.SessionId
                          ToolCallId = callId
                          Tool = toolName }
                | _ -> None

            let observeDelegatedToolEstimate toolInput =
                task {
                    let context = ToolHostCodec.decodeContext toolInput

                    match journal, ToolHostCodec.hookCallId toolInput with
                    | Some durable, Some toolCallId when not (String.IsNullOrWhiteSpace context.SessionId) ->
                        let port = AgentJournalPortAdapter.forDelegatedToolEstimate durable
                        do! DelegatedToolEstimateLedger.observe port (SessionId.create context.SessionId) toolCallId
                    | _ -> ()
                }

            let hiddenReviewState owner isReview args =
                if isReview then
                    ManagerReviewContract.classifyHiddenArguments owner args
                else
                    HiddenProtocolArguments.SameCall

            let hiddenDelegationState owner isDelegationActive args =
                if isDelegationActive then
                    ReadonlyDelegationContract.classifyHiddenArguments owner args
                else
                    HiddenProtocolArguments.SameCall

            // 隐藏状态合并规则是合同：任一 Different → Different；都 Same → Same；
            // 否则 NotHidden。并发与重放下同一参数对象只有一个隐藏归属
            //（host-boundary-032）。
            let combineHiddenStates review delegation =
                match review, delegation with
                | HiddenProtocolArguments.DifferentCallOrChangedArguments, _
                | _, HiddenProtocolArguments.DifferentCallOrChangedArguments ->
                    HiddenProtocolArguments.DifferentCallOrChangedArguments
                | HiddenProtocolArguments.SameCall, HiddenProtocolArguments.SameCall -> HiddenProtocolArguments.SameCall
                | _ -> HiddenProtocolArguments.NotHidden

            let hiddenArgumentState owner toolName isDelegationActive (toolOutput: obj) =
                let isReview = ManagerReviewTools.isReviewTool toolName

                if
                    (not isReview && not isDelegationActive)
                    || isNull toolOutput
                    || isNull toolOutput?args
                then
                    HiddenProtocolArguments.NotHidden
                else
                    combineHiddenStates
                        (hiddenReviewState owner isReview toolOutput?args)
                        (hiddenDelegationState owner isDelegationActive toolOutput?args)

            let protocolArgumentsAreHidden owner toolName isDelegationActive toolOutput =
                match hiddenArgumentState owner toolName isDelegationActive toolOutput with
                | HiddenProtocolArguments.SameCall -> true
                | HiddenProtocolArguments.DifferentCallOrChangedArguments ->
                    invalidOp
                        "Invalid investigation estimate arguments: hidden arguments belong to another call or were changed before restoration"
                | HiddenProtocolArguments.NotHidden -> false

            // 隐藏前再核一次：本调用尚未隐藏且 args 可读才执行 hide。
            // hideProtocolArguments 半途失败时补偿性全量 restore 再重抛，是 before
            // 失败的唯一回滚点（宿主不会在 before 失败后再调 after）。
            let hideProtocolArgumentsIfShown owner toolName isDelegationActive (toolOutput: obj) =
                if
                    not (protocolArgumentsAreHidden owner toolName isDelegationActive toolOutput)
                    && not (isNull toolOutput)
                    && not (isNull toolOutput?args)
                then
                    hideProtocolArguments owner toolName isDelegationActive toolOutput?args

            // 新参数准备链的具名步骤，按序：非法估计拒绝 → wire vault 记录 →
            // 委托估计观察 → 隐藏协议字段。
            let prepareNewProtocolArguments owner toolName isDelegationActive toolInput toolOutput =
                task {
                    if isDelegationActive then
                        requireDelegationEstimateArguments toolInput toolOutput

                    recordProtocolArgumentVault toolInput toolOutput
                    do! observeDelegatedToolEstimate toolInput
                    hideProtocolArgumentsIfShown owner toolName isDelegationActive toolOutput
                }

            // 已隐藏同调用 → 短路：不重复记录、不重复隐藏。不同调用或参数已变
            // 由 protocolArgumentsAreHidden 报错。
            let beforeStagePrepareProtocolArguments toolName isDelegationActive toolInput toolOutput =
                task {
                    let owner = argumentCallOwner toolName toolInput

                    if not (protocolArgumentsAreHidden owner toolName isDelegationActive toolOutput) then
                        do! prepareNewProtocolArguments owner toolName isDelegationActive toolInput toolOutput
                }

            // tool.execute.before 的具名 stage 序列。次序即合同（host-boundary-019），
            // 不得重排。第 1 步无条件最先；第 2 步只对评审工具生效；第 3/4 步
            // 服务参与工具的估计协议。任何一步抛错即中断本次 before。
            let beforeStageRequirementGrounding (toolInput: obj) (toolOutput: obj) =
                Wanxiangshu.OpenCode.Host.RequirementGrounding.RequirementGroundingGate.before
                    journal
                    workspaceDirectory
                    toolInput
                    toolOutput

            let beforeStageReviewPermission (toolName: string) (toolInput: obj) =
                checkManagerReviewPermissions toolName toolInput

            let beforeStageDelegationActive toolName =
                let isParticipatingTool =
                    InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall

                readonlyDelegationPredictorConfigured () && isParticipatingTool

            let toolBefore (toolInput: obj) (toolOutput: obj) =
                task {
                    do! beforeStageRequirementGrounding toolInput toolOutput
                    let toolName = toolField toolInput "tool"
                    beforeStageReviewPermission toolName toolInput

                    do!
                        beforeStagePrepareProtocolArguments
                            toolName
                            (beforeStageDelegationActive toolName)
                            toolInput
                            toolOutput
                }

            // 先 Delegation（仅参与工具）后 Manager，两个字段族互不覆盖。
            let restoreOwnedArguments owner isParticipatingTool args =
                if isParticipatingTool then
                    ReadonlyDelegationContract.restoreForCall owner args

                ManagerReviewContract.restoreForCall owner args

            let restoreArgumentsForTarget owner isParticipatingTool (target: obj) =
                if not (isNull target) && not (isNull target?args) then
                    restoreOwnedArguments owner isParticipatingTool target?args

            // after stage 1: 按 exact 调用身份重算 classify/owner，对 toolInput 与
            // toolOutput 各恢复一次协议字段。restore 抛错则后续 stage 不执行（现状）。
            let afterStageRestoreProtocolArguments (toolInput: obj) (toolOutput: obj) =
                let toolName = toolField toolInput "tool"
                let owner = argumentCallOwner toolName toolInput

                let isParticipatingTool =
                    InvestigationEstimateContract.classifyTool toolName = InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall

                restoreArgumentsForTarget owner isParticipatingTool toolInput
                restoreArgumentsForTarget owner isParticipatingTool toolOutput

            // after stage 2: requirement grounding 读结果补规范。
            let afterStageRequirementGrounding (toolInput: obj) (toolOutput: obj) =
                Wanxiangshu.OpenCode.Host.RequirementGrounding.RequirementGroundingGate.after
                    journal
                    workspaceDirectory
                    toolInput
                    toolOutput

            // after stage 3: 可选 casebook 观察。失败只发诊断，不改关键结果
            //（host-boundary-024）。
            let afterStageCasebookObservation (toolInput: obj) (toolOutput: obj) =
                if casebookEnabled then
                    HookPolicy.observeOptional Diagnostic.emit OptionalHookEffect.CasebookObservation (fun () ->
                        collectCasebookObservation toolInput toolOutput)
                    |> ignore

            // tool.execute.after 的具名 stage 序列。次序即合同。
            let toolAfter (toolInput: obj) (toolOutput: obj) =
                task {
                    afterStageRestoreProtocolArguments toolInput toolOutput
                    do! afterStageRequirementGrounding toolInput toolOutput
                    afterStageCasebookObservation toolInput toolOutput
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
                    ProviderLanguageBinding.refreshGlobalLanguage ()

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
