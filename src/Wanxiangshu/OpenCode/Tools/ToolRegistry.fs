namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Mission.Relay.OpenCode
open Wanxiangshu.Mission.Relay
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Repository.Programming.Js.OpenCode
open Wanxiangshu.Requirement.Grounding
open Wanxiangshu.Ablation
open Wanxiangshu.Process

/// Assembly-only registry: tool behavior lives in one vertical verb module;
/// per-session resources live in ToolRuntimeScope.
type ToolRegistration =
    { Tools: obj
      Runtime: ToolRuntimeScope }

module ToolRegistry =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        let DeniedRole = "tool/registry/denied-role"

        /// Fission is Engineer-only; its denial names the sole entitled role.
        [<Literal>]
        let DeniedFission = "tool/registry/denied-fission"

        [<Literal>]
        let DeniedAblation = "tool/registry/denied-ablation"

        [<Literal>]
        let DeniedStrength = "tool/registry/denied-strength"

        [<Literal>]
        let DeniedUnestablished = "tool/registry/denied-unestablished"

        [<Literal>]
        let DeniedTaskState = "tool/registry/denied-task-state"

    let private lang (ctx: HostToolContext) =
        let sessionText = ctx.SessionId

        ProviderLanguageBinding.forSessionText sessionText

    /// The refusal prose follows the provider language, so it cannot serve as a
    /// machine-readable reason. `code` carries the resource key when a caller
    /// needs one; omitting it leaves the rendered bytes exactly as before, which
    /// is why tools without a code contract keep prose only.
    let private denied
        (ctx: HostToolContext)
        (specName: string)
        (path: string)
        (subs: Map<string, string>)
        (code: string option)
        =
        let fields =
            match code with
            | Some key -> [ "code", ToolHostCodec.TString key ]
            | None -> []

        ToolHostCodec.tomlObjectWithInstructions [ ProviderProse.render (lang ctx) path subs ] fields

    /// mv / rm expose their refusals by code; the other tools keep the
    /// prose-only shape their existing consumers already match on.
    let private carriesRefusalCode (specName: string) =
        match specName with
        | "mv"
        | "rm" -> true
        | _ -> false

    let private refusalCodeFor (specName: string) (path: string) =
        if carriesRefusalCode specName then Some path else None

    let private denyRole (ctx: HostToolContext) (specName: string) (role: Role) =
        let path =
            if specName = "fission" then
                Path.DeniedFission
            else
                Path.DeniedRole

        denied ctx specName path (Map [ "tool", specName; "role", sprintf "%A" role ]) (refusalCodeFor specName path)

    /// The current capability decides; the denial stays action-focused and never
    /// echoes internal loop state.
    let private denyTaskState (ctx: HostToolContext) (specName: string) =
        denied ctx specName Path.DeniedTaskState (Map [ "tool", specName ]) None

    /// execution-model-routing-010: the tool context carries this call's own
    /// exact ProviderRunIdentity. The physical message it answers is the one the
    /// Host stated for that run; a run this process never observed has no step to
    /// end, and must never be resolved from the session's current binding.
    let private endObservedStep (providerRun: ProviderRunIdentity) =
        ModelRouting.tryProviderStepIdentity providerRun
        |> Option.iter (fun (sessionId, physicalUserMessageId) ->
            ModelRouting.endProviderStep sessionId physicalUserMessageId providerRun)

    let private providerToolBoundary (ctx: HostToolContext) =
        if String.IsNullOrWhiteSpace ctx.SessionId then
            Ok()
        else
            ctx.ProviderRunId |> Option.iter endObservedStep
            Ok()

    /// capability-enforcement-005/010/025: the exact session facts the execute
    /// gate reads. `create` fills every field from the ToolRuntimeScope or its
    /// own callbacks; a stage never receives the whole scope.
    type private ToolGateFacts =
        { RoleFor: HostToolContext -> Role option
          EnsureRoleFor: HostToolContext -> Task<Role option>
          IsStrengthReplica: HostToolContext -> bool
          ManagerFactsFor: string -> ManagerCapabilityFacts
          IsRetirementFrozen: string -> bool
          BeginToolExecution: string -> unit
          EndToolExecution: string -> unit }

    let private accountingStageWithSession
        (gate: ToolGateFacts)
        (ctx: HostToolContext)
        (run: unit -> Task<string>)
        : Task<string> =
        task {
            gate.BeginToolExecution ctx.SessionId

            try
                return! run ()
            finally
                gate.EndToolExecution ctx.SessionId
        }

    /// Tool-execution accounting around one physical gate pass.
    let private accountingStage
        (gate: ToolGateFacts)
        (ctx: HostToolContext)
        (run: unit -> Task<string>)
        : Task<string> =
        if String.IsNullOrWhiteSpace ctx.SessionId then
            run ()
        else
            accountingStageWithSession gate ctx run

    /// feature-ablation-002: an ablated node never reaches the tool body.
    let private ablationStage (spec: ToolSpec) (ctx: HostToolContext) : Task<string option> =
        Task.FromResult(
            if AblationGate.toolDenied (AblationGate.registry ()) spec.Name then
                Some(denied ctx spec.Name Path.DeniedAblation (Map [ "tool", spec.Name ]) None)
            else
                None
        )

    /// capability-enforcement-005 / STRENGTH-004: Host-native read/glob/grep are
    /// the entire replica surface. js-predictor is the single plugin tool a live
    /// replica may execute; every other plugin tool stays denied.
    let private replicaStage (gate: ToolGateFacts) (spec: ToolSpec) (ctx: HostToolContext) : Task<string option> =
        Task.FromResult(
            if not (gate.IsStrengthReplica ctx) then
                None
            elif spec.Name = "js-predictor" then
                None
            else
                Some(denied ctx spec.Name Path.DeniedStrength Map.empty None)
        )

    /// capability-enforcement-025: current Manager facts close the office gate.
    /// A frozen retirement keeps only Join and Finality; the final incumbent
    /// keeps the read/cleanup/close-out surface and loses every new-work
    /// capability.
    let private managerFactsStage
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let managerFacts = gate.ManagerFactsFor ctx.SessionId
            let frozen = gate.IsRetirementFrozen ctx.SessionId

            match managerPermission with
            | Some permission when
                frozen
                && permission <> ToolPermission.Join
                && permission <> ToolPermission.Finality
                ->
                return denyTaskState ctx spec.Name
            | Some permission when not (OfficeCapability.isAllowedForManagerFacts managerFacts permission) ->
                return denyTaskState ctx spec.Name
            | _ -> return! spec.Execute args ctx
        }

    let private executeAdmittedRole
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        (role: Role)
        : Task<string> =
        if role = Role.Manager then
            managerFactsStage gate spec managerPermission args ctx
        else
            spec.Execute args ctx

    /// capability-enforcement-002: office Role then admission then Manager facts.
    let private officeStage
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (officeAdmission: HostToolContext -> Role -> bool)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let! resolvedRole =
                match gate.RoleFor ctx with
                | Some role -> Task.FromResult(Some role)
                | None -> gate.EnsureRoleFor ctx

            match resolvedRole with
            | Some role when officeAdmission ctx role ->
                return! executeAdmittedRole gate spec managerPermission args ctx role
            | Some role -> return denyRole ctx spec.Name role
            | None ->
                return
                    denied
                        ctx
                        spec.Name
                        Path.DeniedUnestablished
                        Map.empty
                        (refusalCodeFor spec.Name Path.DeniedUnestablished)
        }

    /// capability-enforcement-006: the attachment IS the authority. Resolving a
    /// public office Role here would deny the Bookkeeper its own exact tool,
    /// because a HostInternal prompt deliberately installs no public authority
    /// profile.
    let private attachmentStage
        (spec: ToolSpec)
        (attachmentAdmission: HostToolContext -> bool)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            if attachmentAdmission ctx then
                return! spec.Execute args ctx
            else
                return denied ctx spec.Name Path.DeniedUnestablished Map.empty None
        }

    let private admissionStage
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        match spec.Admission with
        | ToolAdmission.OfficeRole officeAdmission -> officeStage gate spec managerPermission officeAdmission args ctx
        | ToolAdmission.PrivateAttachment attachmentAdmission -> attachmentStage spec attachmentAdmission args ctx

    let private runAfterAblation
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            match! replicaStage gate spec ctx with
            | Some refusal -> return refusal
            | None -> return! admissionStage gate spec managerPermission args ctx
        }

    let private runBoundaryStages
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            match! ablationStage spec ctx with
            | Some refusal -> return refusal
            | None -> return! runAfterAblation gate spec managerPermission args ctx
        }

    /// The one composition point. Six named stages, fixed ordinary calls in the
    /// order written here. No middleware list, no fold, no registry.
    let private executeGate
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            match providerToolBoundary ctx with
            | Error error -> return raise (InvalidOperationException error)
            | Ok() ->
                return! accountingStage gate ctx (fun () -> runBoundaryStages gate spec managerPermission args ctx)
        }

    let private staticAdmissions (bloggerHost: IBloggerRuntimeHost option) : (string * ToolAdmission) list =
        [ "fork", ForkTool.managerAdmission
          "resume", ForkTool.managerAdmission
          "commission", ForkTool.orchestratorAdmission
          "open-terminal", PtyTool.admission
          "send-terminal", PtyTool.admission
          "read-terminal", PtyTool.admission
          "signal-terminal", PtyTool.admission
          "join", JoinTool.admission
          "horizon", HorizonTool.admission
          "fission", FissionTool.admission
          "review", ReviewShell.admission
          "suicide", SuicideTool.admission
          "run", ExecutorTool.runAdmission
          "mv", FileMutationTools.mvAdmission
          "rm", FileMutationTools.rmAdmission
          "bash-honeypot", BashHoneypotTool.admission
          "assume", AssumeTool.admission
          "defer", AttentionTools.admission
          "publish", ConcernTools.admission
          "chronicle", ChronicleTool.admission bloggerHost
          "fetch", ToolAdmission.OfficeRole(fun _ r -> OfficeCapability.isAllowed r ToolPermission.Fetch)
          "js-bookkeeper", JsBookkeeperTool.admission ]

    let private tryAdmissionFor (specName: string) (bloggerHost: IBloggerRuntimeHost option) : ToolAdmission option =
        match staticAdmissions bloggerHost |> List.tryFind (fun (name, _) -> name = specName) with
        | Some(_, admission) -> Some admission
        | None when specName = "js-predictor" -> Some(ToolAdmission.PrivateAttachment(fun _ -> false))
        | None when specName.StartsWith "js-" && specName <> "js-bookkeeper" ->
            Some(JsToolSpec.admissionFor (specName.Substring 3))
        | None -> None

    let private probeContext (sessionId: string) : HostToolContext =
        { SessionId = sessionId
          Agent = None
          ToolCallId = None
          ProviderRunId = None
          PromptText = None
          AttachAbort = fun _ -> id }

    /// capability-enforcement-006: the authority the execute gate resolves for a tool, so a
    /// consumer can tell an office tool from an internal leaf without guessing
    /// from the tool name.
    let tryAdmission (specName: string) (bloggerHost: IBloggerRuntimeHost option) : ToolAdmission option =
        tryAdmissionFor specName bloggerHost

    /// capability-enforcement-006: the internal-leaf decision for a session that holds no public
    /// office profile at all. An office tool is never admitted this way.
    let privateAttachmentAdmits
        (specName: string)
        (bloggerHost: IBloggerRuntimeHost option)
        (sessionId: string)
        : bool =
        match tryAdmissionFor specName bloggerHost with
        | Some(ToolAdmission.PrivateAttachment predicate) -> predicate (probeContext sessionId)
        | Some(ToolAdmission.OfficeRole _)
        | None -> false

    /// AGENT-007 role gate, delegates to owner-defined tool admissions.
    /// sessionId is the tool call's Host session; bloggerHost is optional for tests.
    let rolePredicate (specName: string) (bloggerHost: IBloggerRuntimeHost option) (sessionId: string) : Role -> bool =
        // capability-enforcement-006: an internal leaf tool is admitted by attachment, never by a
        // public office, so no public Role may ever see it on this surface.
        match tryAdmissionFor specName bloggerHost with
        | Some(ToolAdmission.OfficeRole predicate) -> predicate (probeContext sessionId)
        | Some(ToolAdmission.PrivateAttachment _)
        | None -> fun _ -> false

    let private createPlanSandboxApi (currentContent: string) (stagedMutation: string option ref) =
        createObj
            [ "js"
              ==> createObj
                      [ "read" ==> fun (_: obj) -> currentContent
                        "edit"
                        ==> fun (_path: string) (newText: string) ->
                            stagedMutation.Value <- Some newText
                            createObj [ "ok" ==> true ] ] ]

    let private writePlanFile root sessionId newText value =
        match PlanPath.rewritePlanAtomic root sessionId newText with
        | Ok(_path, _digest) -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Succeeded(value, [ "plan.md" ], []))
        | Error err ->
            JsToolsResult.render (
                JsToolWorkflow.JsToolOutcome.Failed(
                    JsFailure.ProgramFailed(sprintf "Failed to write plan artifact: %s" err)
                )
            )

    let private commitPlanMutation root sessionId hasReviewed newTextOpt value =
        match newTextOpt with
        | None -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Succeeded(value, [], []))
        | Some _ when not hasReviewed ->
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed JsFailure.PlanReviewRequired)
        | Some newText -> writePlanFile root sessionId newText value

    let private renderPlanParseOutcome root sessionId hasReviewed stagedMutation json =
        match JsToolsData.parse json with
        | Error failure -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed failure)
        | Ok value -> commitPlanMutation root sessionId hasReviewed stagedMutation value

    let private renderPlanSandboxOutcome root sessionId hasReviewed stagedMutation runOutcome =
        match runOutcome with
        | Error failure -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed failure)
        | Ok json -> renderPlanParseOutcome root sessionId hasReviewed stagedMutation json

    let private executePlanSandbox prose root sessionId hasReviewed programSource =
        task {
            let currentContent =
                match PlanPath.readPlan root sessionId with
                | Ok c -> c
                | Error _ -> ""

            // DSL-MUTABLE: algorithm-scratch — staged mutation accumulator for Plan sandbox
            let stagedMutation = ref None
            let api = createPlanSandboxApi currentContent stagedMutation
            let baseClassSource = JsCanonicalDescription.runtimePlanBaseClass prose
            let deadlineMs = 10000
            let deadlineEpochMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000L
            let outputBoundBytes = 1 <<< 20

            let! runOutcome =
                JsSandbox.runSurface baseClassSource programSource api deadlineMs deadlineEpochMs outputBoundBytes

            return renderPlanSandboxOutcome root sessionId hasReviewed stagedMutation.Value runOutcome
        }

    let private resolveActiveTenure store sessionId =
        let pid = PlanWorkId.create sessionId

        let state =
            PlanEventStore.workState store pid
            |> Option.defaultValue PlanFold.initialWorkState

        match state.Active with
        | None -> Error "No active incumbency found for this work"
        | Some active ->
            let hasReviewed =
                state.Reviewed |> Option.exists (fun (reviewedId, _) -> reviewedId = active.Id)

            Ok hasReviewed

    let private executeTenureSandbox prose root store sessionId programSource =
        match resolveActiveTenure store sessionId with
        | Error reason ->
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(JsFailure.ProgramFailed reason))
            |> Task.FromResult
        | Ok hasReviewed -> executePlanSandbox prose root sessionId hasReviewed programSource

    let private runPlanProgramCore
        (prose: JsCanonicalDescription.Prose)
        storeOpt
        (rootOpt: string option)
        (sessionId: string)
        (programSourceOpt: string option)
        : Task<string> =
        if Option.isNone programSourceOpt then
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed JsFailure.InvalidProgram)
            |> Task.FromResult
        elif Option.isNone rootOpt || Option.isNone storeOpt then
            JsToolsResult.render (
                JsToolWorkflow.JsToolOutcome.Failed(JsFailure.ProgramFailed "The plan workspace is unavailable")
            )
            |> Task.FromResult
        else
            executeTenureSandbox prose rootOpt.Value storeOpt.Value sessionId programSourceOpt.Value

    let runPlanProgram prose store root sessionId program =
        runPlanProgramCore prose (Some store) (Some root) sessionId (Some program)

    let create
        (toolModule: obj)
        (sessionPort: ISessionHostPort)
        (waitObserver: IWaitObserver)
        (rootWorkspace: IRootWorkspaceReader)
        (journal: AgentJournal option)
        (workspaceDirectory: string option)
        (sessionParents: Dictionary<string, string>)
        (currentPhysicalUserMessage: string -> string option)
        (sessionDirectories: Dictionary<string, string>)
        (onRunStarted: (SessionId -> Role -> string option -> unit) option)
        (parentWorkRecordFor: (string -> Task<string option>) option)
        (childWorkRecordFor: (string -> Task<string option>) option)
        (snapshot: ISessionSnapshotPort option)
        (cancelSignals: (SessionId seq -> unit) option)
        (beginToolExecution: string -> unit)
        (endToolExecution: string -> unit)
        (eventPort: IEventObservationPort option)
        (bloggerHost: IBloggerRuntimeHost option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (isReplicaSession: (SessionId -> bool) option)
        (casebookToolSpecs: ToolSpec list)
        (jsTransactionPersistence: IJsTransactionPersistence option)
        (continueManagerLoop: SessionId -> string -> Task<Result<unit, string>>)
        (captureWorktreeSnapshot: WorktreePath -> Result<WorkspaceSnapshotId, string>)
        (childWorkRecordForRun:
            (SessionId -> Wanxiangshu.Context.Trace.XTraceRange -> ProviderRunIdentity -> Task<string option>) option)
        (workRecordCapability: Wanxiangshu.Execution.Delegation.DelegationWorkRecordCapability option)
        (userNotify: (string -> string -> unit) option)
        =
        let factory = ToolHostCodec.factory toolModule
        let providerLanguage = ProviderLanguageBinding.readGlobalPreference ()

        let jsProse: JsCanonicalDescription.Prose =
            JsDescriptionAssets.load providerLanguage

        let groundingObservation (ctx: HostToolContext) (reads: JsExplicitFileRead list) effectPaths =
            match workspaceDirectory with
            | None -> Task.FromResult(())
            | Some _ when System.String.IsNullOrWhiteSpace ctx.SessionId -> Task.FromResult(())
            | Some root ->
                let observed =
                    reads
                    |> List.map (fun read ->
                        { Path = read.Path
                          ResultBytes = read.ResultBytes
                          Coverage = GroundingReadCoverage.CompleteFile })

                RequirementGroundingGate.programObservation journal root ctx.SessionId observed effectPaths

        let runtime =
            new ToolRuntimeScope(
                sessionPort,
                waitObserver,
                rootWorkspace,
                journal,
                workspaceDirectory,
                sessionParents,
                currentPhysicalUserMessage,
                sessionDirectories,
                onRunStarted,
                parentWorkRecordFor,
                childWorkRecordFor,
                snapshot,
                cancelSignals,
                ?childWorkRecordForRun = childWorkRecordForRun,
                ?workRecordCapability = workRecordCapability,
                continueManagerLoop = continueManagerLoop,
                captureWorktreeSnapshot = captureWorktreeSnapshot,
                ?eventPort = eventPort
            )

        let generatedJsSpecs () =
            [ for role in Roles.all do
                  match JsToolGenerator.generate (string role) (OfficeCapability.permissions role) jsProse with
                  | Some surface ->
                      yield
                          JsToolSpec.create
                              factory
                              surface
                              (defaultArg workspaceDirectory "")
                              jsTransactionPersistence
                              (Some groundingObservation)
                  | None -> () ]

        let predictorJsSpec () =
            let admission =
                ToolAdmission.PrivateAttachment(fun ctx ->
                    match isReplicaSession with
                    | Some replicaPred when not (System.String.IsNullOrWhiteSpace ctx.SessionId) ->
                        replicaPred (SessionId.create ctx.SessionId)
                    | _ -> false)

            JsToolSpec.createWithAdmission
                factory
                (JsToolGenerator.generatePredictor jsProse)
                admission
                (defaultArg workspaceDirectory "")
                jsTransactionPersistence
                (Some groundingObservation)

        // planning-016: the plan store and plan root resolve from the
        // workspace the same way the js-transaction persistence does; the
        // plan store is shared by the planning tools and the review shell.
        let planStore =
            workspaceDirectory
            |> Option.bind (fun workspace -> WorkspaceEventStore.tryCurrent (RuntimePath.gitCommonDir workspace))

        let planRoot =
            workspaceDirectory
            |> Option.map (fun workspace -> RuntimePath.gitCommonDir workspace + "/wanxiangshu")

        let planningAdmission: ToolAdmission =
            ToolAdmission.OfficeRole(fun _ role -> role = Role.Plan)

        let executeJsPlanTool (args: HostToolArguments) (ctx: HostToolContext) : Task<string> =
            runPlanProgramCore jsProse planStore planRoot ctx.SessionId (args.OptionalText "program")

        // planning-010: the real delegation capability — the SyncDelegate
        // runtime runs the bound fixed DevOps for this work synchronously and
        // hands back its complete work record. A missing runtime fails closed.
        let devopsDelegateOf (syncRuntime: SyncDelegateRuntime option) (ctx: HostToolContext) : string -> Task<string> =
            let invokeRuntime (runtime: SyncDelegateRuntime) (intent: string) : Task<string> =
                task {
                    let! outcome = runtime.Invoke(ctx.SessionId, SyncDelegateRole.DevOps, intent)

                    return
                        match outcome with
                        | Ok record -> record
                        | Error reason ->
                            raise (InvalidOperationException(sprintf "DevOps delegation failed: %s" reason))
                }

            fun (intent: string) ->
                task {
                    match syncRuntime with
                    | None -> return raise (InvalidOperationException "sync delegate runtime unavailable")
                    | Some runtime -> return! invokeRuntime runtime intent
                }

        let renderAskOutcome (outcome: Result<AskResult, string>) : string =
            match outcome with
            | Ok res -> res.WorkRecord
            | Error err -> sprintf "ask failed: %s" err

        // The plan store flows through these helpers by inference only: an
        // explicit IEventStore annotation would make this module carry both
        // the EventStore and the Journal surface token (unified-store
        // dual-write gate). The store itself is resolved once below.
        let executeAskTool
            storeOpt
            (syncRuntime: SyncDelegateRuntime option)
            (args: HostToolArguments)
            (ctx: HostToolContext)
            : Task<string> =
            task {
                match storeOpt with
                | None -> return "ask failed: the plan workspace is unavailable"
                | Some store ->
                    let delegateTo = devopsDelegateOf syncRuntime ctx
                    let! outcome = PlanTools.executeAsk store ctx.SessionId (args.Text "intent") delegateTo
                    return renderAskOutcome outcome
            }

        let activeIncumbencyOf store (sessionId: string) : ActivePlanIncumbency option =
            PlanEventStore.activeWorkState store (PlanWorkId.create sessionId)
            |> Option.bind (fun state -> state.Active)

        // planning-008: the receipt names the artifact path and the fact that
        // an execution session started; the spawned session id stays out of
        // the provider-visible text.
        let renderDeliverReceipt (res: DeliverResult) : string =
            if res.Relayed then
                res.Message
            else
                sprintf "%s The plan artifact is at %s; an execution session has been started." res.Message res.Path

        // planning-007/008: the retirement cursor is the exact consumed-through
        // XTrace frontier of the physical exchange that owns this deliver call.
        // The snapshot may already contain a newer user/provider message appended
        // while this deliver tool runs; retirement is fenced to the exact
        // assistant/tool exchange in ctx (its ProviderRunId message and the
        // ToolCallId it answers), never the indiscriminate session head. The
        // whole physical call/result exchange is kept atomic: the frontier lands
        // after the owning assistant message, so the deliver call's own result
        // (appended after retirement) belongs to the successor tenure, and no
        // prior call/result pair is split. A missing snapshot, journal, or
        // owning-exchange identity fails closed with no Plan writes or spawn.
        let deliverPorts (runtime: ToolRuntimeScope) =
            match runtime.Snapshot, runtime.Journal with
            | Some snapshotPort, Some journal -> Ok(snapshotPort, journal)
            | None, _ -> Error "deliver failed: session snapshot is unavailable"
            | _, None -> Error "deliver failed: journal is unavailable"

        let deliverOwnership (ctx: HostToolContext) =
            match ctx.ProviderRunId, ctx.ToolCallId with
            | Some providerRun, Some toolCall -> Ok(ProviderRunIdentity.value providerRun, ToolCallId.value toolCall)
            | None, _ -> Error "deliver failed: owning provider message identity is unavailable"
            | _, None -> Error "deliver failed: owning tool call identity is unavailable"

        let messageOwnsDeliver (message: SessionMessage) (owningCallId: string) =
            message.ToolParts
            |> Array.exists (fun part -> ToolCallId.value part.ToolCallId = owningCallId && part.ToolName = "deliver")

        // Fence to the owning exchange: keep every message up to and including
        // the assistant message that owns this deliver call. The snapshot port
        // pins `order = "asc"` (HOST 1.18.9 session.messages), so the list is
        // already physical append order; it is preserved verbatim. Any newer
        // user/provider message appended while deliver runs stays out of range.
        let tryOwningExchange (messages: SessionMessage list) (owningMessageId: string) (owningCallId: string) =
            let owningIndex =
                messages
                |> List.tryFindIndex (fun (message: SessionMessage) ->
                    message.Id = owningMessageId && message.Role = "assistant")

            match owningIndex with
            | None -> Error "deliver failed: the owning provider message is absent from the session snapshot"
            | Some index when not (messageOwnsDeliver messages.[index] owningCallId) ->
                Error "deliver failed: the owning tool call does not match this deliver invocation"
            | Some index -> Ok(messages.[0..index])

        let readOwningExchange
            (snapshotPort: ISessionSnapshotPort)
            (sessionId: SessionId)
            (owningMessageId: string)
            (owningCallId: string)
            =
            task {
                match! snapshotPort.GetMessages sessionId with
                | Error reason -> return Error(sprintf "deliver failed: cannot read the session snapshot: %s" reason)
                | Ok messages -> return tryOwningExchange messages owningMessageId owningCallId
            }

        let captureFencedFrontier journal sessionId fenced =
            task {
                match!
                    Wanxiangshu.Context.Trace.XTraceCapture.captureSessionMessagesWithReceipt
                        (Some journal)
                        sessionId
                        fenced
                with
                | Ok receipt -> return Ok(Wanxiangshu.Context.Trace.XTraceCursor.sequence receipt.CurrentHead)
                | Error(Wanxiangshu.Context.Trace.XTraceCaptureError.Refused reason) ->
                    return Error(sprintf "deliver failed: XTrace capture refused: %s" reason)
                | Error(Wanxiangshu.Context.Trace.XTraceCaptureError.StorageAppendFailed failure) ->
                    return
                        Error(
                            sprintf
                                "deliver failed: XTrace capture append failed: %s"
                                (JournalOutcome.JournalAppendFailure.describe failure)
                        )
                | Error(Wanxiangshu.Context.Trace.XTraceCaptureError.StorageFailed reason) ->
                    return Error(sprintf "deliver failed: XTrace capture failed: %s" reason)
            }

        let captureOrPropagate journal sessionId =
            function
            | Error err -> Task.FromResult(Error err)
            | Ok fenced -> captureFencedFrontier journal sessionId fenced

        let fenceAndCapture journal sessionId snapshotPort owningMessageId owningCallId =
            task {
                let! exchange = readOwningExchange snapshotPort sessionId owningMessageId owningCallId
                return! captureOrPropagate journal sessionId exchange
            }

        let resolveDeliverRetirementCursor
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            : Task<Result<int64, string>> =
            task {
                match deliverPorts runtime, deliverOwnership ctx with
                | Ok(snapshotPort, journal), Ok(owningMessageId, owningCallId) ->
                    return!
                        fenceAndCapture
                            journal
                            (SessionId.create ctx.SessionId)
                            snapshotPort
                            owningMessageId
                            owningCallId
                | Error err, _ -> return Error err
                | _, Error err -> return Error err
            }

        // The execution session spawn capability handed to PlanTools: the
        // provider-visible receipt never carries the spawned session id.
        let deliverSpawn (runtime: ToolRuntimeScope) (agent: string) (prompt: string) =
            task {
                let! spawned = runtime.SpawnExecutionSession(agent, prompt)

                return
                    match spawned with
                    | Ok sessionId -> sessionId
                    | Error reason ->
                        raise (InvalidOperationException(sprintf "Execution session spawn failed: %s" reason))
            }

        let renderDeliverOutcome outcome =
            match outcome with
            | Ok res -> renderDeliverReceipt res
            | Error err -> sprintf "deliver failed: %s" err

        let deliverActiveIncumbency
            store
            (root: string)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            (active: ActivePlanIncumbency)
            : Task<string> =
            task {
                let hasBlocker = not (List.isEmpty (runtime.RetirementBlockersFor ctx.SessionId))

                match! resolveDeliverRetirementCursor runtime ctx with
                | Error err -> return sprintf "deliver failed: %s" err
                | Ok retirementCursor ->
                    let! outcome =
                        PlanTools.executeDeliver
                            store
                            root
                            ctx.SessionId
                            active.Id
                            hasBlocker
                            (deliverSpawn runtime)
                            retirementCursor

                    return renderDeliverOutcome outcome
            }

        let runDeliver store (root: string) (runtime: ToolRuntimeScope) (ctx: HostToolContext) : Task<string> =
            task {
                match activeIncumbencyOf store ctx.SessionId with
                | None -> return "deliver failed: no active incumbency found for this work"
                | Some active -> return! deliverActiveIncumbency store root runtime ctx active
            }

        let executeDeliverTool
            storeOpt
            (rootOpt: string option)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            : Task<string> =
            task {
                match storeOpt, rootOpt with
                | Some store, Some root -> return! runDeliver store root runtime ctx
                | _ -> return "deliver failed: the plan workspace is unavailable"
            }

        let planningSpecs () =
            [ { Name = "js-plan"
                Description =
                  JsCanonicalDescription.renderPlan
                      jsProse
                      (ProviderProse.render providerLanguage "tool/js-plan" Map.empty)
                Arguments =
                  [ "program",
                    ToolHostCodec.stringSchemaDescribed
                        (JsDescriptionAssets.argProgram (ProviderLanguageBinding.readGlobalPreference ()))
                        factory ]
                Admission = planningAdmission
                Execute = fun args ctx -> executeJsPlanTool args ctx }
              { Name = "ask"
                Description = ProviderProse.render providerLanguage "tool/ask" Map.empty
                Arguments =
                  [ "intent",
                    ToolHostCodec.stringSchemaDescribed "Investigation intent handed to the bound fixed DevOps" factory ]
                Admission = planningAdmission
                Execute = fun args ctx -> executeAskTool planStore syncDelegateRuntime args ctx }
              { Name = "deliver"
                Description = ProviderProse.render providerLanguage "tool/deliver" Map.empty
                Arguments = [ "note", ToolHostCodec.optionalStringSchema factory ]
                Admission = planningAdmission
                Execute = fun _ ctx -> executeDeliverTool planStore planRoot runtime ctx } ]

        let baseSpecs =
            [ yield ForkTool.managerSpec factory runtime
              yield ForkTool.resumeSpec factory runtime
              let ptyContext: PtyTool.PtyRuntimeContext =
                  { IsDevOps = fun ctx -> runtime.IsRole(ctx, Role.DevOps)
                    ManagedAgentFor = runtime.ManagedAgentFor
                    PtyCapabilityFor = runtime.PtyCapabilityFor
                    DirectoryFor = runtime.DirectoryFor
                    WorkspaceDirectory = runtime.WorkspaceDirectory }

              yield! PtyTool.specs factory ptyContext
              yield ForkTool.orchestratorSpec factory runtime
              yield JoinTool.spec runtime

              let horizonContext: HorizonTool.HorizonRuntimeContext =
                  { RuntimeFor = runtime.RuntimeFor
                    LogicalOwnerFor = runtime.LogicalOwnerFor
                    Journal = runtime.Journal
                    EnsureRoadDevOpsBound = runtime.EnsureRoadDevOpsBound }

              yield HorizonTool.spec horizonContext
              yield FissionTool.spec factory runtime
              // relay-assessment-011 / planning-022: the shared review shell.
              // The plan store only activates the Plan backend; every other
              // admitted caller keeps the Manager backend's byte behavior.
              yield ReviewShell.spec factory runtime planStore
              yield SuicideTool.spec factory runtime
              yield ExecutorTool.runSpec factory runtime
              yield FileMutationTools.mvSpec factory
              yield FileMutationTools.rmSpec factory
              yield BashHoneypotTool.spec
              yield AssumeTool.spec factory
              yield! AttentionTools.specs factory (journal |> Option.map AgentJournalPortAdapter.forAttention)
              yield! ConcernTools.specs factory (journal |> Option.map AgentJournalPortAdapter.forConcern) userNotify

              yield
                  ChronicleTool.spec
                      factory
                      (fun (sessionId, reason) -> runtime.TerminateSession(sessionId, reason))
                      bloggerHost

              yield! casebookToolSpecs
              yield predictorJsSpec ()
              yield! generatedJsSpecs ()
              yield! planningSpecs () ]

        let isStrengthReplica (ctx: HostToolContext) =
            match isReplicaSession with
            | Some replicaPred when not (String.IsNullOrWhiteSpace ctx.SessionId) ->
                replicaPred (SessionId.create ctx.SessionId)
            | _ -> false

        let gateFacts: ToolGateFacts =
            { RoleFor = (fun ctx -> runtime.RoleFor ctx)
              EnsureRoleFor = (fun ctx -> runtime.EnsureRoleFor ctx)
              IsStrengthReplica = isStrengthReplica
              ManagerFactsFor = (fun sessionId -> runtime.ManagerCapabilityFactsFor sessionId)
              IsRetirementFrozen = (fun sessionId -> runtime.IsRetirementFrozen sessionId)
              BeginToolExecution = beginToolExecution
              EndToolExecution = endToolExecution }

        // Generic execute gate: every tool declares the authority it is admitted
        // under, and the registry never invents one the session does not hold.
        // capability-enforcement-012: Manager tool permissions come from the one
        // StaticTools reverse lookup the schema projection already reads.
        let gateExecute (spec: ToolSpec) =
            let managerPermission =
                match ManagerReviewTools.requiredPermissions spec.Name with
                | Some perms -> perms |> Seq.tryHead
                | None -> StaticTools.permissionOfToolName spec.Name

            fun args (ctx: HostToolContext) -> executeGate gateFacts spec managerPermission args ctx

        let specs =
            baseSpecs |> List.map (fun spec -> { spec with Execute = gateExecute spec })

        { Tools = ToolHostCodec.registry factory specs
          Runtime = runtime }
