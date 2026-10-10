namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Change
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Investigation.WarmStart

/// Manager fork & resume / Orchestrator commission. One typed request backs
/// every public tool; each tool exposes its own schema; PTY is absent.
module ForkTool =

    [<RequireQualifiedAccess>]
    module Path =
        [<RequireQualifiedAccess>]
        module Fork =
            [<Literal>]
            let Description = "tool/fork/description"

            [<Literal>]
            let ArgCalling = "tool/fork/arg-calling"

            [<Literal>]
            let ArgName = "tool/fork/arg-name"

            [<Literal>]
            let ArgCharge = "tool/fork/arg-charge"

            [<Literal>]
            let ArgKeywords = "tool/fork/arg-keywords"

            [<Literal>]
            let ArgAttach = "delegation/fork-attach-argument"

            [<Literal>]
            let AttachUnknown = "delegation/fork-attach-unknown"

            [<Literal>]
            let AttachSelf = "delegation/fork-attach-self"

            [<Literal>]
            let NameRequired = "tool/fork/name-required"

            [<Literal>]
            let ChargeRequired = "tool/fork/charge-required"

            [<Literal>]
            let CallingConflict = "tool/fork/calling-conflict"

            [<Literal>]
            let UnknownCalling = "tool/fork/unknown-calling"

            [<Literal>]
            let ChargeContextUnavailable = "tool/fork/charge-context-unavailable"

            [<Literal>]
            let NameAlreadyBelongs = "tool/fork/name-already-belongs"

            [<Literal>]
            let WarmStartUnavailable = "tool/fork/warm-start-unavailable"

            [<Literal>]
            let ChargeCarried = "tool/fork/charge-carried"

            [<Literal>]
            let ChargeNotPlaced = "tool/fork/charge-not-placed"

            [<Literal>]
            let ChargePlacementUncertain = "tool/fork/charge-placement-uncertain"

            [<Literal>]
            let PersonUnknown = "tool/fork/person-unknown"

            [<Literal>]
            let PersonUnavailable = "tool/fork/person-unavailable"

            [<Literal>]
            let PersonCannotTakeCharge = "tool/fork/person-cannot-take-charge"

            [<Literal>]
            let HandoffJournalRequired = "tool/fork/handoff-journal-required"

            [<Literal>]
            let PersonSessionUnknown = "tool/fork/person-session-unknown"

            [<Literal>]
            let HandoffAppendFailed = "tool/fork/handoff-append-failed"

        [<RequireQualifiedAccess>]
        module Commission =
            [<Literal>]
            let Description = "tool/commission/description"

            [<Literal>]
            let ArgCalling = "tool/commission/arg-calling"

            [<Literal>]
            let ArgName = "tool/commission/arg-name"

            [<Literal>]
            let ArgCharge = "tool/commission/arg-charge"

            [<Literal>]
            let AuthorityRequired = "tool/commission/authority-required"

            [<Literal>]
            let NameRequired = "tool/commission/name-required"

            [<Literal>]
            let ChargeRequired = "tool/commission/charge-required"

            [<Literal>]
            let UnknownCalling = "tool/commission/unknown-calling"

            [<Literal>]
            let NameAlreadyBelongs = "tool/commission/name-already-belongs"

            [<Literal>]
            let ChargeTaken = "tool/commission/charge-taken"

            [<Literal>]
            let RoadNotOpened = "tool/commission/road-not-opened"

            [<Literal>]
            let RoadCannotTakeCharge = "tool/commission/road-cannot-take-charge"

        [<RequireQualifiedAccess>]
        module Resume =
            [<Literal>]
            let Description = "tool/resume/description"

            [<Literal>]
            let CallingNotAllowed = "tool/resume/calling-not-allowed"

            [<Literal>]
            let GuidanceSent = "tool/resume/guidance-sent"

            [<Literal>]
            let AssessmentPendingForDevOps = "tool/resume/assessment-pending-for-devops"

    let private lang (ctx: HostToolContext) =
        ProviderLanguageBinding.forSessionText ctx.SessionId

    let private prose language path =
        ProviderProse.render language path Map.empty

    let private namedProse language path byname =
        ProviderProse.render language path (Map [ "name", byname ])

    let private forkInstructions (sessionId: SessionId) : ForkChildInstructions =
        let lang = SessionProviderLanguage.languageOf sessionId

        { Base = ProviderProse.instructionLines lang ForkChildPayload.BasePath Map.empty
          CommissionerRecord = ProviderProse.render lang ForkChildPayload.CommissionerRecordPath Map.empty
          Attachment = ProviderProse.render lang ForkChildPayload.AttachmentPath Map.empty
          Requirements = ProviderProse.render lang ForkChildPayload.RequirementsPath Map.empty }

    type Request =
        { Calling: string
          Name: string
          Charge: string
          Keywords: string
          Attach: string option
          ExpectedToolCalls: int option }

    let private decode language (args: HostToolArguments) =
        match DelegatedToolEstimate.decode args with
        | Error _ -> Error(DelegatedToolEstimate.invalid language)
        | Ok expectedToolCalls ->
            Ok
                { Calling = args.Text "calling"
                  Name = args.Text "name"
                  Charge = args.Text "charge"
                  Keywords = args.Text "keywords"
                  Attach = args.OptionalText "attach" |> Option.map (fun value -> value.Trim())
                  ExpectedToolCalls = expectedToolCalls }

    let private consequence (message: string) =
        ToolHostCodec.tomlObjectWithInstructions [ message ] []

    let private successInstruction (text: string) =
        ToolHostCodec.tomlObjectWithInstructions [ text ] []

    let private personaBinding (role: Role) =
        PersonaCatalog.persona role, ManagedAgent.make role

    let private managerCallingBindings =
        [ for role in ManagedAgentCatalog.managerForkableRoles do
              yield personaBinding role ]

    let private orchestratorCallingBindings = [ personaBinding Role.Manager ]

    let private callingNames bindings =
        bindings |> List.map (fst >> ManagedAgentCatalog.personaCallingName)

    let private tryCalling bindings (raw: string) =
        if String.IsNullOrWhiteSpace raw then
            None
        else
            raw.Trim()
            |> ManagedAgentCatalog.tryParsePersonaCallingName
            |> Option.bind (fun wanted ->
                bindings
                |> List.tryPick (fun (identity, managed) -> if identity = wanted then Some managed else None))

    /// delegation-003: a blank fork calling derives from the name. `devops` is
    /// the fixed execution operator and cannot be forked, so it derives to a
    /// value the fork bindings reject.
    let private derivedForkCalling (byname: string) =
        if String.Equals(byname.Trim(), "devops", StringComparison.OrdinalIgnoreCase) then
            "devops"
        else
            "engineer"

    let private hasKeywords (request: Request) =
        not (String.IsNullOrWhiteSpace request.Keywords)

    let private warmStartAllowed (role: Role) =
        match role with
        | Role.Engineer
        | Role.DevOps -> true
        | _ -> false

    let private prepareForkPromptWithRecord
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (role: Role)
        (request: Request)
        (commissionerRecord: string option)
        (attachment: string option)
        : Task<string> =
        let baseDocument =
            ForkChildPayload.relayDocument
                (forkInstructions runtime.ParentId)
                request.Charge
                commissionerRecord
                attachment
                []
                None

        if hasKeywords request then
            RepositoryWarmStart.appendToBaseDocument
                runtime.ParentId
                role
                scope.WorkspaceDirectory
                request.Keywords
                baseDocument
            |> TaskValue.map (Result.defaultWith invalidOp >> LlmFacing.render)
        else
            Task.FromResult(LlmFacing.render baseDocument)

    let private isSelfAttachment (request: Request) =
        request.Attach
        |> Option.exists (fun attach ->
            System.String.Equals(attach, request.Name.Trim(), System.StringComparison.OrdinalIgnoreCase))

    let private resolveAttachment
        (scope: ToolRuntimeScope)
        (handles: AgentLinkageProjection option)
        (request: Request)
        : Task<Result<string option, string>> =
        match request.Attach with
        | None -> Task.FromResult(Ok None)
        | Some attach ->
            taskResult {
                let! record =
                    handles
                    |> Option.bind (HandleProjection.tryFindByByname attach)
                    |> Result.requireSome Path.Fork.AttachUnknown

                let! workRecord =
                    scope.ParentWorkRecordFor(SessionId.value record.ChildSessionId)
                    |> TaskResultCE.ofTask

                return workRecord
            }

    let private appendFissionAffinity durable (context: HostToolContext) (lane: FissionLaneBinding) handleId =
        taskResult {
            let! _ =
                task {
                    let! result =
                        AgentJournal.appendAgent
                            (StreamId.Session lane.OwnerSessionId)
                            context.ProviderRunId
                            (FissionFact.FissionExternalAffinityBound
                                {| GroupId = lane.GroupId
                                   OwnerSessionId = lane.OwnerSessionId
                                   ExternalId = FissionExternalId.agent handleId
                                   LaneIndex = lane.LaneIndex |})
                            durable

                    return Result.mapError JournalAppendFailure.describe result
                }

            return ()
        }

    let private bindFissionAffinity (scope: ToolRuntimeScope) (context: HostToolContext) handleId =
        match FissionRuntime.tryLane (SessionId.create context.SessionId), scope.Journal with
        | Some lane, Some durable -> appendFissionAffinity durable context lane handleId
        | _ -> Task.FromResult(Ok())

    let private recordFissionAffinity (scope: ToolRuntimeScope) (context: HostToolContext) handleId =
        if String.IsNullOrWhiteSpace context.SessionId then
            Task.FromResult(Ok())
        else
            bindFissionAffinity scope context handleId

    let private agentHandles (scope: ToolRuntimeScope) (context: HostToolContext) =
        match scope.Journal with
        | Some journal when not (String.IsNullOrWhiteSpace context.SessionId) ->
            let owner = scope.LogicalOwnerFor(SessionId.create context.SessionId)
            Some(AgentJournal.handleProjection journal owner)
        | _ -> None

    let private announceChild (runtime: HostForkRuntime) (context: HostToolContext) agentKey =
        runtime.TryFindAgentOrAdopt agentKey
        |> Option.map (fun (childId, _, _) -> childId)
        |> Option.iter (fun childId ->
            FissionRuntime.notifyChildCreated (SessionId.create context.SessionId) agentKey childId)

    let private runManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        role
        (request: Request)
        language
        attachment
        handleId
        (managed: ManagedAgent)
        =
        taskResult {
            let route = DelegationHandoffRoute.forkByname request.Name
            let! handoff = runtime.PrepareHandoff route

            let! rendered =
                prepareForkPromptWithRecord scope runtime role request handoff.ParentRecord attachment
                |> TaskResultCE.ofTask

            let! result =
                runtime.Fork(
                    handleId,
                    role,
                    managed.Name,
                    request.Charge,
                    None,
                    renderedPrompt = rendered,
                    byname = request.Name,
                    ?expectedToolCalls = request.ExpectedToolCalls,
                    preparedHandoff = handoff
                )

            return result
        }

    let private runManagerReuse
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        role
        (request: Request)
        language
        attachment
        agentId
        =
        taskResult {
            let route = DelegationHandoffRoute.forkByname request.Name
            let! handoff = runtime.PrepareHandoff route

            let! rendered =
                prepareForkPromptWithRecord scope runtime role request handoff.ParentRecord attachment
                |> TaskResultCE.ofTask

            return!
                runtime.Reuse(
                    agentId,
                    request.Charge,
                    renderedPrompt = rendered,
                    ?expectedToolCalls = request.ExpectedToolCalls,
                    preparedHandoff = handoff
                )
        }

    let private commitNewManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        (managed: ManagedAgent)
        role
        attachment
        =
        task {
            let handleId = ToolHostCodec.newHandleId ()

            let! placement =
                taskResult {
                    do! recordFissionAffinity scope context handleId
                    let! result = runManagerFork scope runtime role request language attachment handleId managed
                    announceChild runtime context handleId

                    return
                        match result with
                        | ForkResult.DispatchUncertain _ ->
                            consequence (namedProse language Path.Fork.ChargePlacementUncertain (request.Name.Trim()))
                        | _ -> successInstruction (namedProse language Path.Fork.ChargeCarried (request.Name.Trim()))
                }

            match placement with
            | Ok wire -> return wire
            | Error _ -> return consequence (prose language Path.Fork.ChargeNotPlaced)
        }

    let private finishNewManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        role
        =
        task {
            match! resolveAttachment scope handles request with
            | Error path -> return consequence (prose language path)
            | Ok attachment ->
                return! commitNewManagerFork scope runtime context request language managed role attachment
        }

    let private placeNewManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        =
        let role = managed.Role

        if hasKeywords request && not (warmStartAllowed role) then
            Task.FromResult(consequence (prose language Path.Fork.WarmStartUnavailable))
        else
            finishNewManagerFork scope runtime context request language handles managed role

    let private appendBusyGuidance (runtime: HostForkRuntime) (request: Request) language agentId profile rendered =
        task {
            match! runtime.AppendGuidance(agentId, profile, rendered) with
            | Ok _ -> return successInstruction (namedProse language Path.Resume.GuidanceSent (request.Name.Trim()))
            | Error _ -> return consequence (prose language Path.Fork.ChargeNotPlaced)
        }

    let private reuseWhileActive
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        agentId
        =
        let prepareAndSend (profile: PromptAuthority.AuthorityExecutionProfile) =
            task {
                match! resolveAttachment scope handles request with
                | Error path -> return consequence (prose language path)
                | Ok attachment ->
                    let! rendered =
                        prepareForkPromptWithRecord scope runtime profile.CanonicalRole request None attachment

                    return! appendBusyGuidance runtime request language agentId profile rendered
            }

        match runtime.ActiveGuidanceProfile agentId with
        | Error _ -> Task.FromResult(consequence (prose language Path.Fork.ChargeNotPlaced))
        | Ok profile -> prepareAndSend profile

    let private commitIdleReuse
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        (role: Role)
        agentId
        attachment
        =
        task {
            let! placement =
                taskResult {
                    do! recordFissionAffinity scope context agentId
                    let! result = runManagerReuse scope runtime role request language attachment agentId
                    announceChild runtime context agentId

                    return
                        match result with
                        | ForkResult.DispatchUncertain _ ->
                            consequence (namedProse language Path.Fork.ChargePlacementUncertain (request.Name.Trim()))
                        | _ -> successInstruction (namedProse language Path.Fork.ChargeCarried (request.Name.Trim()))
                }

            match placement with
            | Ok wire -> return wire
            | Error err ->
                Diagnostic.emit "fork-reuse-commit-failed" [ "result", err ]
                return consequence (prose language Path.Fork.PersonCannotTakeCharge)
        }

    let private reuseWhileIdle
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        (role: Role)
        agentId
        =
        task {
            match! resolveAttachment scope handles request with
            | Error path -> return consequence (prose language path)
            | Ok attachment -> return! commitIdleReuse scope runtime context request language role agentId attachment
        }

    let private validateWarmStart (request: Request) (role: Role) language =
        if hasKeywords request && not (warmStartAllowed role) then
            Error(consequence (prose language Path.Fork.WarmStartUnavailable))
        else
            Ok()

    let private reuseWhileAllowed
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        (role: Role)
        agentId
        =
        match validateWarmStart request role language with
        | Error err -> Task.FromResult err
        | Ok() -> reuseWhileIdle scope runtime context request language handles role agentId

    let private reuseResolvedAgent
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        (handle: HandleRecord)
        agentId
        =
        let activeRun =
            lock runtime.Gate (fun () -> runtime.PendingRuns.ContainsKey agentId)

        match activeRun, runtime.TryFindAgentOrAdopt agentId, handle.CanonicalRole with
        | true, _, _ -> reuseWhileActive scope runtime request language handles agentId
        | false, Some(_, role, _), _ -> reuseWhileAllowed scope runtime context request language handles role agentId
        | false, None, Role.DevOps ->
            reuseWhileAllowed scope runtime context request language handles Role.DevOps agentId
        | false, None, _ ->
            // crash-reconciliation-020: a restarted process may not have the child
            // in its runtime registry yet; the durable handle is the evidence that
            // this person exists, so reuse goes through the normal path instead of
            // answering "person-unavailable".
            reuseWhileAllowed scope runtime context request language handles handle.CanonicalRole agentId

    let private executeManagerReusePerson
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        handle
        =
        match HandleId.tryAgent handle.Handle with
        | None -> Task.FromResult(consequence (prose language Path.Fork.PersonUnknown))
        | Some handleId ->
            reuseResolvedAgent scope runtime context request language handles handle (AgentHandleId.value handleId)

    let private callingConflictsWithDerived (request: Request) derived =
        not (String.IsNullOrWhiteSpace request.Calling)
        && not (String.Equals(request.Calling.Trim(), derived, StringComparison.OrdinalIgnoreCase))

    let private placeDerivedManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        derived
        =
        match tryCalling managerCallingBindings derived with
        | None -> Task.FromResult(consequence (prose language Path.Fork.UnknownCalling))
        | Some managed -> placeNewManagerFork scope runtime context request language handles managed

    let private createNewManagerFork
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        =
        let derived = derivedForkCalling request.Name

        if callingConflictsWithDerived request derived then
            Task.FromResult(consequence (prose language Path.Fork.CallingConflict))
        else
            placeDerivedManagerFork scope runtime context request language handles derived

    let private executeManagerNewCalling
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        existingByname
        =
        match existingByname with
        | Some _ -> Task.FromResult(consequence (prose language Path.Fork.NameAlreadyBelongs))
        | None -> createNewManagerFork scope runtime context request language handles

    let private executeManagerExistingPerson
        (scope: ToolRuntimeScope)
        (runtime: HostForkRuntime)
        (context: HostToolContext)
        (request: Request)
        language
        handles
        existingByname
        =
        match existingByname with
        | None -> Task.FromResult(consequence (prose language Path.Fork.PersonUnknown))
        | Some handle -> executeManagerReusePerson scope runtime context request language handles handle

    let private executeManagerAfterGuards
        (scope: ToolRuntimeScope)
        (request: Request)
        (context: HostToolContext)
        language
        =
        match scope.RuntimeFor context with
        | Error _ -> Task.FromResult(consequence (prose language Path.Fork.ChargeContextUnavailable))
        | Ok runtime ->
            let handles = agentHandles scope context

            let existingByname =
                handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

            executeManagerNewCalling scope runtime context request language handles existingByname

    let private resumeOnRuntime (scope: ToolRuntimeScope) (request: Request) (context: HostToolContext) language =
        match scope.RuntimeFor context with
        | Error _ -> task { return consequence (prose language Path.Fork.ChargeContextUnavailable) }
        | Ok runtime ->
            let handles = agentHandles scope context

            let existingByname =
                handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

            executeManagerExistingPerson scope runtime context request language handles existingByname

    let private resumeDevOpsAssessmentPending (scope: ToolRuntimeScope) parentSessionId isDevOps =
        if isDevOps then
            let roadSessionId = SessionId.value parentSessionId
            let facts = scope.ManagerCapabilityFactsFor roadSessionId
            not facts.HasAssessment
        else
            false

    let private bindRoadDevOpsIfNeeded (scope: ToolRuntimeScope) parentSessionId isDevOps =
        task {
            if isDevOps then
                do! scope.EnsureRoadDevOpsBound parentSessionId
        }

    let private executeManagerResumeAfterGuards
        (scope: ToolRuntimeScope)
        (request: Request)
        (context: HostToolContext)
        language
        =
        task {
            let parentSessionId =
                if String.IsNullOrWhiteSpace context.SessionId then
                    SessionId.create ""
                else
                    scope.LogicalOwnerFor(SessionId.create context.SessionId)

            let isDevOps =
                String.Equals(request.Name.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

            if resumeDevOpsAssessmentPending scope parentSessionId isDevOps then
                return consequence (prose language Path.Resume.AssessmentPendingForDevOps)
            else
                do! bindRoadDevOpsIfNeeded scope parentSessionId isDevOps
                return! resumeOnRuntime scope request context language
        }

    let private executeManagerResume (scope: ToolRuntimeScope) (request: Request) (context: HostToolContext) =
        task {
            let language = lang context

            if String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Fork.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Fork.ChargeRequired)
            elif isSelfAttachment request then
                return consequence (prose language Path.Fork.AttachSelf)
            elif not (String.IsNullOrWhiteSpace request.Calling) then
                return consequence (prose language Path.Resume.CallingNotAllowed)
            else
                return! executeManagerResumeAfterGuards scope request context language
        }

    let private executeManager (scope: ToolRuntimeScope) (request: Request) (context: HostToolContext) =
        task {
            let language = lang context

            if String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Fork.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Fork.ChargeRequired)
            elif isSelfAttachment request then
                return consequence (prose language Path.Fork.AttachSelf)
            else
                return! executeManagerAfterGuards scope request context language
        }

    let private orchestratorExistingByname (scope: ToolRuntimeScope) (request: Request) =
        scope.Journal
        |> Option.bind (fun journal ->
            (AgentJournal.snapshot journal).AgentProjections.Orchestrator
            |> OrchestratorProjection.tryFindByByname request.Name)

    let private finishCommissionNew
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (request: Request)
        language
        (managed: ManagedAgent)
        =
        task {
            let managerId = ManagerJobId.create (ToolHostCodec.newHandleId ())

            let host = scope.OrchestratorHostFor context.SessionId

            match!
                host.ForkManagerJob(
                    managerId,
                    managed.Name,
                    request.Charge,
                    byname = request.Name,
                    ?expectedToolCalls = request.ExpectedToolCalls
                )
            with
            | Ok _ -> return successInstruction (namedProse language Path.Commission.ChargeTaken (request.Name.Trim()))
            | Error _ -> return consequence (prose language Path.Commission.RoadNotOpened)
        }

    /// delegation-004: Orchestrator only commissions a Manager, so a blank
    /// commission calling derives to the single Manager persona.
    let private derivedCommissionCalling = "lead"

    let private commissionNewCalling (scope: ToolRuntimeScope) (context: HostToolContext) (request: Request) language =
        match tryCalling orchestratorCallingBindings derivedCommissionCalling with
        | None -> Task.FromResult(consequence (prose language Path.Commission.UnknownCalling))
        | Some managed -> finishCommissionNew scope context request language managed

    let private continueExistingCommission
        (scope: ToolRuntimeScope)
        (context: HostToolContext)
        (request: Request)
        language
        job
        =
        task {
            let host = scope.OrchestratorHostFor context.SessionId

            match context.ProviderRunId, context.ToolCallId with
            | Some providerRun, Some toolCallId ->
                match!
                    host.ContinueManagerJob(
                        job.ManagerJobId,
                        request.Charge,
                        providerRun,
                        toolCallId,
                        ?expectedToolCalls = request.ExpectedToolCalls
                    )
                with
                | Ok _ ->
                    return successInstruction (namedProse language Path.Commission.ChargeTaken (request.Name.Trim()))
                | Error _ -> return consequence (prose language Path.Commission.RoadCannotTakeCharge)
            | _ -> return consequence (prose language Path.Commission.RoadCannotTakeCharge)
        }

    let private executeOrchestratorAfterGuards
        (scope: ToolRuntimeScope)
        (request: Request)
        (context: HostToolContext)
        language
        =
        let existingByname = orchestratorExistingByname scope request
        let hasCalling = not (String.IsNullOrWhiteSpace request.Calling)

        if
            hasCalling
            && not (String.Equals(request.Calling.Trim(), derivedCommissionCalling, StringComparison.OrdinalIgnoreCase))
        then
            Task.FromResult(consequence (prose language Path.Commission.UnknownCalling))
        else
            match existingByname with
            | Some _ when hasCalling -> Task.FromResult(consequence (prose language Path.Commission.NameAlreadyBelongs))
            | Some job -> continueExistingCommission scope context request language job
            | None -> commissionNewCalling scope context request language

    let private executeOrchestrator (scope: ToolRuntimeScope) (request: Request) (context: HostToolContext) =
        task {
            let language = lang context

            if String.IsNullOrWhiteSpace context.SessionId then
                return consequence (prose language Path.Commission.AuthorityRequired)
            elif String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Commission.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Commission.ChargeRequired)
            else
                return! executeOrchestratorAfterGuards scope request context language
        }

    let managerAdmission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> r = Role.Manager)

    let resumeAdmission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> r = Role.Manager || r = Role.Plan)

    let orchestratorAdmission: ToolAdmission =
        ToolAdmission.OfficeRole(fun _ r -> r = Role.Orchestrator)

    let managerSpec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let language = ProviderLanguageBinding.readGlobalPreference ()

        { Name = "fork"
          Description = prose language Path.Fork.Description
          Arguments =
            [ "calling",
              ToolHostCodec.optionalEnumSchemaDescribed
                  (callingNames managerCallingBindings)
                  (prose language Path.Fork.ArgCalling)
                  factory
              "name", ToolHostCodec.stringSchemaDescribed (prose language Path.Fork.ArgName) factory
              "charge", ToolHostCodec.stringSchemaDescribed (prose language Path.Fork.ArgCharge) factory
              "keywords", ToolHostCodec.optionalStringSchemaDescribed (prose language Path.Fork.ArgKeywords) factory
              "attach", ToolHostCodec.optionalStringSchemaDescribed (prose language Path.Fork.ArgAttach) factory
              "expected_tool_calls", DelegatedToolEstimate.schema language factory ]
          Admission = managerAdmission
          Execute =
            fun args context ->
                task {
                    let language = lang context

                    match decode language args with
                    | Error message -> return consequence message
                    | Ok request -> return! executeManager scope request context
                } }

    let resumeSpec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let language = ProviderLanguageBinding.readGlobalPreference ()

        { Name = "resume"
          Description = prose language Path.Resume.Description
          Arguments =
            [ "name", ToolHostCodec.stringSchemaDescribed (prose language Path.Fork.ArgName) factory
              "charge", ToolHostCodec.stringSchemaDescribed (prose language Path.Fork.ArgCharge) factory
              "keywords", ToolHostCodec.optionalStringSchemaDescribed (prose language Path.Fork.ArgKeywords) factory
              "attach", ToolHostCodec.optionalStringSchemaDescribed (prose language Path.Fork.ArgAttach) factory
              "expected_tool_calls", DelegatedToolEstimate.schema language factory ]
          Admission = resumeAdmission
          Execute =
            fun args context ->
                task {
                    let language = lang context

                    match decode language args with
                    | Error message -> return consequence message
                    | Ok request -> return! executeManagerResume scope request context
                } }

    let orchestratorSpec (factory: HostToolFactory) (scope: ToolRuntimeScope) : ToolSpec =
        let language = ProviderLanguageBinding.readGlobalPreference ()

        { Name = "commission"
          Description = prose language Path.Commission.Description
          Arguments =
            [ "calling",
              ToolHostCodec.optionalEnumSchemaDescribed
                  (callingNames orchestratorCallingBindings)
                  (prose language Path.Commission.ArgCalling)
                  factory
              "name", ToolHostCodec.stringSchemaDescribed (prose language Path.Commission.ArgName) factory
              "charge", ToolHostCodec.stringSchemaDescribed (prose language Path.Commission.ArgCharge) factory
              "expected_tool_calls", DelegatedToolEstimate.schema language factory ]
          Admission = orchestratorAdmission
          Execute =
            fun args context ->
                task {
                    let language = lang context

                    match decode language args with
                    | Error message -> return consequence message
                    | Ok request -> return! executeOrchestrator scope request context
                } }
