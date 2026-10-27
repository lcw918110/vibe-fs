namespace Wanxiangshu.Execution.Delegation.Fork

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Change
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports
open Wanxiangshu.Host.Domain
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.ManagedAgents
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal

/// Manager fork & resume / Orchestrator commission: the product rules behind
/// the three delegation commands, host-neutral. A host reaches them through
/// `DelegationEnvironment` and renders the `DelegationReply` itself.
module ForkCommand =

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
            let PersonSessionUnknown = "tool/fork/person-session-unknown"

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
            let WorkNotOpened = "tool/commission/work-not-opened"

            [<Literal>]
            let WorkCannotTakeCharge = "tool/commission/work-cannot-take-charge"

        [<RequireQualifiedAccess>]
        module ExpectedToolCalls =
            [<Literal>]
            let Argument = "delegation/expected-tool-calls-argument"

            [<Literal>]
            let Invalid = "delegation/expected-tool-calls-invalid"

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

            [<Literal>]
            let DispatchClosed = "tool/resume/dispatch-closed"


    [<RequireQualifiedAccess>]
    type Command =
        | Fork
        | Resume
        | Commission

    type Request =
        { Calling: string
          Name: string
          Charge: string
          Attach: string option
          ExpectedToolCalls: int option }

    /// The calling session's id text; empty when the host could not name it.
    let private callerText (host: DelegationEnvironment) =
        match host.Caller.Session with
        | Some session -> SessionId.value session
        | None -> ""

    let private lang (host: DelegationEnvironment) =
        ProviderLanguageBinding.forSessionText (callerText host)

    let private prose language path =
        ProviderProse.render language path Map.empty

    let private namedProse language path byname =
        ProviderProse.render language path (Map [ "name", byname ])

    /// A refusal prose path and the stable kebab code it stands for. The prose
    /// paths render a refusal; the structured fork and resume paths answer
    /// with the code, so both share this mapping.
    let private refusalCode (path: string) =
        if path = Path.Fork.NameRequired then
            "name-required"
        elif path = Path.Fork.ChargeRequired then
            "charge-required"
        elif path = Path.Fork.AttachSelf then
            "attach-self"
        elif path = Path.Fork.AttachUnknown then
            "attach-unknown"
        elif path = Path.Fork.PersonUnknown then
            "person-unknown"
        elif path = Path.Fork.WarmStartUnavailable then
            "warm-start-unavailable"
        elif path = Path.Fork.ChargeContextUnavailable then
            "context-unavailable"
        elif path = Path.Fork.CallingConflict then
            "calling-conflict"
        elif path = Path.Fork.UnknownCalling then
            "unknown-calling"
        elif path = Path.Fork.NameAlreadyBelongs then
            "name-already-belongs"
        elif path = Path.Fork.ChargePlacementUncertain then
            "dispatch-uncertain"
        elif path = Path.Resume.CallingNotAllowed then
            "calling-not-allowed"
        elif path = Path.Resume.AssessmentPendingForDevOps then
            "assessment-pending-for-devops"
        elif path = Path.Resume.DispatchClosed then
            "dispatch-closed"
        else
            "placement-failed"

    let private forkInstructions (sessionId: SessionId) : ForkChildInstructions =
        Wanxiangshu.Execution.Delegation.SyncDelegate.HostForkProse.forkInstructions sessionId

    let private decode language (args: ICommandArguments) =
        match args.OptionalCount "expected_tool_calls" with
        | Error _ -> Error(prose language Path.ExpectedToolCalls.Invalid)
        | Ok expectedToolCalls ->
            Ok
                { Calling = args.Text "calling"
                  Name = args.Text "name"
                  Charge = args.Text "charge"
                  Attach = args.OptionalText "attach" |> Option.map (fun value -> value.Trim())
                  ExpectedToolCalls = expectedToolCalls }

    let private consequence (message: string) = DelegationReply.Refused message

    let private successInstruction (text: string) = DelegationReply.Placed text

    let private commissionCharge (request: Request) =
        LlmFacing.instruction request.Charge |> LlmFacing.render

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

    let private prepareForkPromptWithRecord
        (runtime: HostForkRuntime)
        (request: Request)
        : Task<Result<string, string>> =
        let prompt =
            ForkChildPayload.relayDocument (forkInstructions runtime.ParentId) request.Charge []
            |> LlmFacing.render

        Task.FromResult(Ok prompt)

    let private isSelfAttachment (request: Request) =
        request.Attach
        |> Option.exists (fun attach ->
            System.String.Equals(attach, request.Name.Trim(), System.StringComparison.OrdinalIgnoreCase))

    let private resolveAttachmentRecord (handles: AgentLinkageProjection option) attach =
        handles
        |> Option.bind (HandleProjection.tryFindByByname attach)
        |> Result.requireSome Path.Fork.AttachUnknown

    /// The attach argument may bind a known handle, but MUST NOT copy its
    /// previous view into the new assignment. Clarifications use negotiate.
    let private resolveAttachment
        (_host: DelegationEnvironment)
        (handles: AgentLinkageProjection option)
        (request: Request)
        : Task<Result<string option, string>> =
        let outcome =
            match request.Attach with
            | None -> Ok None
            | Some name -> resolveAttachmentRecord handles name |> Result.map (fun _ -> None)

        Task.FromResult outcome

    let private appendFissionAffinity durable (host: DelegationEnvironment) (lane: FissionLaneBinding) handleId =
        taskResult {
            let! _ =
                task {
                    let! result =
                        AgentJournal.appendAgent
                            (StreamId.Session lane.OwnerSessionId)
                            host.Caller.ProviderRunId
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

    let private bindFissionAffinity (host: DelegationEnvironment) handleId =
        match FissionRuntime.tryLane (SessionId.create (callerText host)), host.Journal with
        | Some lane, Some durable -> appendFissionAffinity durable host lane handleId
        | _ -> Task.FromResult(Ok())

    let private recordFissionAffinity (host: DelegationEnvironment) handleId =
        if String.IsNullOrWhiteSpace(callerText host) then
            Task.FromResult(Ok())
        else
            bindFissionAffinity host handleId

    let private agentHandles (host: DelegationEnvironment) =
        match host.Journal with
        | Some journal when not (String.IsNullOrWhiteSpace(callerText host)) ->
            let owner = host.LogicalOwnerFor(SessionId.create (callerText host))
            Some(AgentJournal.handleProjection journal owner)
        | _ -> None

    let private runManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        role
        (request: Request)
        language
        attachment
        handleId
        (managed: ManagedAgent)
        =
        taskResult {
            let! rendered = prepareForkPromptWithRecord runtime request

            let! result =
                runtime.Fork(
                    handleId,
                    role,
                    managed.Name,
                    request.Charge,
                    None,
                    renderedPrompt = rendered,
                    byname = request.Name,
                    ?expectedToolCalls = request.ExpectedToolCalls
                )

            return result
        }

    let private runManagerReuse
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        role
        (request: Request)
        language
        attachment
        agentId
        =
        taskResult {
            let! rendered = prepareForkPromptWithRecord runtime request

            return!
                runtime.Reuse(
                    agentId,
                    request.Charge,
                    renderedPrompt = rendered,
                    ?expectedToolCalls = request.ExpectedToolCalls
                )
        }

    let private placementWire (language) (request: Request) (result: ForkResult) =
        match result with
        | ForkResult.DispatchUncertain _ ->
            consequence (namedProse language Path.Fork.ChargePlacementUncertain (request.Name.Trim()))
        | _ -> successInstruction (namedProse language Path.Fork.ChargeCarried (request.Name.Trim()))

    let private resolveNewManagerPlacement
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        (managed: ManagedAgent)
        role
        attachment
        =
        match host.NewHandleId with
        | None -> Task.FromResult(Ok(consequence (prose language Path.Fork.ChargeContextUnavailable)))
        | Some newHandleId ->
            let handleId = newHandleId ()

            taskResult {
                do! recordFissionAffinity host handleId
                let! result = runManagerFork host runtime role request language attachment handleId managed
                return placementWire language request result
            }

    let private commitNewManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        (managed: ManagedAgent)
        role
        attachment
        =
        task {
            let! placement = resolveNewManagerPlacement host runtime request language managed role attachment

            match placement with
            | Ok wire -> return wire
            | Error _ -> return consequence (prose language Path.Fork.ChargeNotPlaced)
        }

    let private finishNewManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        role
        =
        task {
            match! resolveAttachment host handles request with
            | Error path -> return consequence (prose language path)
            | Ok attachment -> return! commitNewManagerFork host runtime request language managed role attachment
        }

    let private placeNewManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        =
        finishNewManagerFork host runtime request language handles managed managed.Role

    let private appendBusyGuidance (runtime: HostForkRuntime) (request: Request) language agentId profile rendered =
        task {
            match! runtime.AppendGuidance(agentId, profile, rendered) with
            | Ok _ -> return successInstruction (namedProse language Path.Resume.GuidanceSent (request.Name.Trim()))
            | Error _ -> return consequence (prose language Path.Fork.ChargeNotPlaced)
        }

    let private reuseWhileActive
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        agentId
        =
        let prepareAndSend (profile: PromptAuthority.AuthorityExecutionProfile) =
            task {
                let outcome =
                    taskResult {
                        let! attachment =
                            resolveAttachment host handles request
                            |> TaskResult.mapError (fun path -> consequence (prose language path))

                        let! rendered =
                            prepareForkPromptWithRecord runtime request
                            |> TaskResult.mapError (fun _ ->
                                consequence (prose language Path.Fork.ChargeContextUnavailable))

                        return rendered
                    }

                match! outcome with
                | Error early -> return early
                | Ok rendered -> return! appendBusyGuidance runtime request language agentId profile rendered
            }

        match runtime.ActiveGuidanceProfile agentId with
        | Error _ -> Task.FromResult(consequence (prose language Path.Fork.ChargeNotPlaced))
        | Ok profile -> prepareAndSend profile

    let private commitIdleReuse
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        (role: Role)
        agentId
        attachment
        =
        task {
            let! placement =
                taskResult {
                    do! recordFissionAffinity host agentId
                    let! result = runManagerReuse host runtime role request language attachment agentId

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
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (role: Role)
        agentId
        =
        task {
            match! resolveAttachment host handles request with
            | Error path -> return consequence (prose language path)
            | Ok attachment -> return! commitIdleReuse host runtime request language role agentId attachment
        }

    let private reuseWhileAllowed
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (role: Role)
        agentId
        =
        reuseWhileIdle host runtime request language handles role agentId

    let private reuseResolvedAgent
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (handle: HandleRecord)
        agentId
        =
        let activeRun =
            lock runtime.Gate (fun () -> runtime.PendingRuns.ContainsKey agentId)

        match activeRun, runtime.TryFindAgentOrAdopt agentId, handle.CanonicalRole with
        | true, _, _ -> reuseWhileActive host runtime request language handles agentId
        | false, Some(_, role, _), _ -> reuseWhileAllowed host runtime request language handles role agentId
        | false, None, Role.DevOps -> reuseWhileAllowed host runtime request language handles Role.DevOps agentId
        | false, None, _ ->
            // crash-reconciliation-020: a restarted process may not have the child
            // in its runtime registry yet; the durable handle is the evidence that
            // this person exists, so reuse goes through the normal path instead of
            // answering "person-unavailable".
            reuseWhileAllowed host runtime request language handles handle.CanonicalRole agentId

    let private executeManagerReusePerson
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        handle
        =
        match HandleId.tryAgent handle.Handle with
        | None -> Task.FromResult(consequence (prose language Path.Fork.PersonUnknown))
        | Some handleId ->
            reuseResolvedAgent host runtime request language handles handle (AgentHandleId.value handleId)

    let private callingConflictsWithDerived (request: Request) derived =
        not (String.IsNullOrWhiteSpace request.Calling)
        && not (String.Equals(request.Calling.Trim(), derived, StringComparison.OrdinalIgnoreCase))

    let private placeDerivedManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        derived
        =
        match tryCalling managerCallingBindings derived with
        | None -> Task.FromResult(consequence (prose language Path.Fork.UnknownCalling))
        | Some managed -> placeNewManagerFork host runtime request language handles managed

    let private createNewManagerFork
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        =
        let derived = derivedForkCalling request.Name

        if callingConflictsWithDerived request derived then
            Task.FromResult(consequence (prose language Path.Fork.CallingConflict))
        else
            placeDerivedManagerFork host runtime request language handles derived

    let private executeManagerNewCalling
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        existingByname
        =
        match existingByname with
        | Some _ -> Task.FromResult(consequence (prose language Path.Fork.NameAlreadyBelongs))
        | None -> createNewManagerFork host runtime request language handles

    let private executeManagerExistingPerson
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        existingByname
        =
        match existingByname with
        | None -> Task.FromResult(consequence (prose language Path.Fork.PersonUnknown))
        | Some handle -> executeManagerReusePerson host runtime request language handles handle

    let private executeManagerAfterGuards (host: DelegationEnvironment) (request: Request) language =
        match host.ForkRuntime() with
        | Error _ -> Task.FromResult(consequence (prose language Path.Fork.ChargeContextUnavailable))
        | Ok runtime ->
            let handles = agentHandles host

            let existingByname =
                handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

            executeManagerNewCalling host runtime request language handles existingByname

    let private resumeOnRuntime (host: DelegationEnvironment) (request: Request) language =
        match host.ForkRuntime() with
        | Error _ -> task { return consequence (prose language Path.Fork.ChargeContextUnavailable) }
        | Ok runtime ->
            let handles = agentHandles host

            let existingByname =
                handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

            executeManagerExistingPerson host runtime request language handles existingByname

    /// A bound DevOps task waits until the active tenure has a substantive review.
    let private devopsDispatchRefusal (facts: ManagerCapabilityFacts) : string option =
        if not facts.HasAssessment then
            Some Path.Resume.AssessmentPendingForDevOps
        else
            None

    let private boundDevOpsDispatchRefusal
        (host: DelegationEnvironment)
        parentSessionId
        isDevOps
        : Task<string option> =
        let readRefusal () =
            match host.ManagerCapabilityFactsFor with
            | None -> Task.FromResult None
            | Some factsFor ->
                task {
                    let! facts = factsFor parentSessionId
                    return devopsDispatchRefusal facts
                }

        if isDevOps then readRefusal () else Task.FromResult None

    let private bindWorkDevOpsIfNeeded (host: DelegationEnvironment) parentSessionId isDevOps =
        task {
            if isDevOps then
                do! host.EnsureWorkDevOpsBound parentSessionId
        }

    let private executeManagerResumeAfterGuards (host: DelegationEnvironment) (request: Request) language =
        task {
            let parentSessionId =
                if String.IsNullOrWhiteSpace(callerText host) then
                    SessionId.create ""
                else
                    host.LogicalOwnerFor(SessionId.create (callerText host))

            let isDevOps =
                String.Equals(request.Name.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

            match! boundDevOpsDispatchRefusal host parentSessionId isDevOps with
            | Some refusal -> return consequence (prose language refusal)
            | None ->
                do! bindWorkDevOpsIfNeeded host parentSessionId isDevOps
                return! resumeOnRuntime host request language
        }

    let private executeManagerResume (host: DelegationEnvironment) (request: Request) =
        task {
            let language = lang host

            if String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Fork.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Fork.ChargeRequired)
            elif isSelfAttachment request then
                return consequence (prose language Path.Fork.AttachSelf)
            elif not (String.IsNullOrWhiteSpace request.Calling) then
                return consequence (prose language Path.Resume.CallingNotAllowed)
            else
                return! executeManagerResumeAfterGuards host request language
        }

    let private executeManagerAfterGate (host: DelegationEnvironment) (request: Request) language =
        task {
            let parentSessionId =
                if String.IsNullOrWhiteSpace(callerText host) then
                    SessionId.create ""
                else
                    host.LogicalOwnerFor(SessionId.create (callerText host))

            let isDevOps =
                String.Equals(request.Name.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

            match! boundDevOpsDispatchRefusal host parentSessionId isDevOps with
            | Some refusal -> return consequence (prose language refusal)
            | None -> return! executeManagerAfterGuards host request language
        }

    let private executeManager (host: DelegationEnvironment) (request: Request) =
        task {
            let language = lang host

            if String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Fork.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Fork.ChargeRequired)
            elif isSelfAttachment request then
                return consequence (prose language Path.Fork.AttachSelf)
            else
                return! executeManagerAfterGate host request language
        }

    let private orchestratorExistingByname (host: DelegationEnvironment) (request: Request) =
        host.Journal
        |> Option.bind (fun journal ->
            (AgentJournal.snapshot journal).AgentProjections.Orchestrator
            |> OrchestratorProjection.tryFindByByname request.Name)

    let private finishCommissionNew (host: DelegationEnvironment) (request: Request) language (managed: ManagedAgent) =
        task {
            match host.NewHandleId, host.OpenManagerJob with
            | Some newHandleId, Some openManagerJob ->
                let managerId = ManagerJobId.create (newHandleId ())

                match!
                    openManagerJob (
                        managerId,
                        managed.Name,
                        commissionCharge request,
                        request.Name,
                        request.ExpectedToolCalls
                    )
                with
                | Ok _ ->
                    return successInstruction (namedProse language Path.Commission.ChargeTaken (request.Name.Trim()))
                | Error _ -> return consequence (prose language Path.Commission.WorkNotOpened)
            | _ -> return consequence (prose language Path.Commission.WorkNotOpened)
        }

    /// delegation-004: Orchestrator only commissions a Manager, so a blank
    /// commission calling derives to the single Manager persona.
    let private derivedCommissionCalling = "lead"

    let private commissionNewCalling (host: DelegationEnvironment) (request: Request) language =
        match tryCalling orchestratorCallingBindings derivedCommissionCalling with
        | None -> Task.FromResult(consequence (prose language Path.Commission.UnknownCalling))
        | Some managed -> finishCommissionNew host request language managed

    let private continueExistingCommission (host: DelegationEnvironment) (request: Request) language job =
        task {
            match host.Caller.ProviderRunId, host.Caller.ToolCallId, host.ContinueManagerJob with
            | Some providerRun, Some toolCallId, Some continueManagerJob ->
                match!
                    continueManagerJob (
                        job.ManagerJobId,
                        request.Charge,
                        providerRun,
                        toolCallId,
                        request.ExpectedToolCalls
                    )
                with
                | Ok _ ->
                    return successInstruction (namedProse language Path.Commission.ChargeTaken (request.Name.Trim()))
                | Error _ -> return consequence (prose language Path.Commission.WorkCannotTakeCharge)
            | _ -> return consequence (prose language Path.Commission.WorkCannotTakeCharge)
        }

    let private executeOrchestratorAfterGuards (host: DelegationEnvironment) (request: Request) language =
        let existingByname = orchestratorExistingByname host request
        let hasCalling = not (String.IsNullOrWhiteSpace request.Calling)

        if
            hasCalling
            && not (String.Equals(request.Calling.Trim(), derivedCommissionCalling, StringComparison.OrdinalIgnoreCase))
        then
            Task.FromResult(consequence (prose language Path.Commission.UnknownCalling))
        else
            match existingByname with
            | Some _ when hasCalling -> Task.FromResult(consequence (prose language Path.Commission.NameAlreadyBelongs))
            | Some job -> continueExistingCommission host request language job
            | None -> commissionNewCalling host request language

    let private executeOrchestrator (host: DelegationEnvironment) (request: Request) =
        task {
            let language = lang host

            if String.IsNullOrWhiteSpace(callerText host) then
                return consequence (prose language Path.Commission.AuthorityRequired)
            elif String.IsNullOrWhiteSpace request.Name then
                return consequence (prose language Path.Commission.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return consequence (prose language Path.Commission.ChargeRequired)
            else
                return! executeOrchestratorAfterGuards host request language
        }

    let admits (command: Command) (role: Role) =
        match command with
        | Command.Fork -> role = Role.Manager
        | Command.Resume -> role = Role.Manager || role = Role.Plan
        | Command.Commission -> role = Role.Orchestrator

    let private argument name shape description : CommandArgument =
        { Name = name
          Shape = shape
          Description = description }

    let private expectedToolCallsArgument language =
        argument "expected_tool_calls" ArgumentShape.OptionalCount (prose language Path.ExpectedToolCalls.Argument)

    let declaration (command: Command) (language: ProviderLanguage) : CommandDeclaration =
        match command with
        | Command.Fork ->
            { Name = "fork"
              Description = prose language Path.Fork.Description
              Arguments =
                [ argument
                      "calling"
                      (ArgumentShape.OptionalChoice(callingNames managerCallingBindings))
                      (prose language Path.Fork.ArgCalling)
                  argument "name" ArgumentShape.Text (prose language Path.Fork.ArgName)
                  argument "charge" ArgumentShape.Text (prose language Path.Fork.ArgCharge)
                  argument "attach" ArgumentShape.OptionalText (prose language Path.Fork.ArgAttach)
                  expectedToolCallsArgument language ] }
        | Command.Resume ->
            { Name = "resume"
              Description = prose language Path.Resume.Description
              Arguments =
                [ argument "name" ArgumentShape.Text (prose language Path.Fork.ArgName)
                  argument "charge" ArgumentShape.Text (prose language Path.Fork.ArgCharge)
                  argument "attach" ArgumentShape.OptionalText (prose language Path.Fork.ArgAttach)
                  expectedToolCallsArgument language ] }
        | Command.Commission ->
            { Name = "commission"
              Description = prose language Path.Commission.Description
              Arguments =
                [ argument
                      "calling"
                      (ArgumentShape.OptionalChoice(callingNames orchestratorCallingBindings))
                      (prose language Path.Commission.ArgCalling)
                  argument "name" ArgumentShape.Text (prose language Path.Commission.ArgName)
                  argument "charge" ArgumentShape.Text (prose language Path.Commission.ArgCharge)
                  expectedToolCallsArgument language ] }

    let private executeCommand (command: Command) (host: DelegationEnvironment) (request: Request) =
        match command with
        | Command.Fork -> executeManager host request
        | Command.Resume -> executeManagerResume host request
        | Command.Commission -> executeOrchestrator host request

    let run (command: Command) (host: DelegationEnvironment) (args: ICommandArguments) : Task<DelegationReply> =
        match decode (lang host) args with
        | Error message -> Task.FromResult(consequence message)
        | Ok request -> executeCommand command host request

    let private commitNewManagerForkReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        (managed: ManagedAgent)
        role
        attachment
        : Task<Result<ForkReceipt, string>> =
        task {
            let! placement =
                match host.NewHandleId with
                | None -> Task.FromResult(Ok(ForkResult.NotFound ""))
                | Some newHandleId ->
                    let handleId = newHandleId ()

                    taskResult {
                        do! recordFissionAffinity host handleId
                        let! result = runManagerFork host runtime role request language attachment handleId managed
                        return result
                    }

            return
                match placement with
                | Error _ -> Error "placement-failed"
                | Ok(ForkResult.DispatchUncertain _) -> Error(refusalCode Path.Fork.ChargePlacementUncertain)
                | Ok(ForkResult.NotFound _) -> Error "placement-failed"
                | Ok(ForkResult.Created agentId) ->
                    Ok
                        { Name = request.Name.Trim()
                          AgentId = agentId
                          Outcome = "created" }
                | Ok(ForkResult.Nudged agentId) ->
                    Ok
                        { Name = request.Name.Trim()
                          AgentId = agentId
                          Outcome = "nudged" }
        }

    let private finishNewManagerForkReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        role
        : Task<Result<ForkReceipt, string>> =
        task {
            match! resolveAttachment host handles request with
            | Error path -> return Error(refusalCode path)
            | Ok attachment -> return! commitNewManagerForkReceipt host runtime request language managed role attachment
        }

    let private placeNewManagerForkReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        (managed: ManagedAgent)
        : Task<Result<ForkReceipt, string>> =
        finishNewManagerForkReceipt host runtime request language handles managed managed.Role

    let private resolveDerivedManagedCalling bindings derived =
        match tryCalling bindings derived with
        | None -> Error "unknown-calling"
        | Some managed -> Ok managed

    let private resolveManagerCalling request derived =
        if callingConflictsWithDerived request derived then
            Error "calling-conflict"
        else
            resolveDerivedManagedCalling managerCallingBindings derived

    let private createNewManagerForkReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        : Task<Result<ForkReceipt, string>> =
        let derived = derivedForkCalling request.Name

        match resolveManagerCalling request derived with
        | Error err -> Task.FromResult(Error err)
        | Ok managed -> placeNewManagerForkReceipt host runtime request language handles managed

    let private checkExistingByname (existing: HandleRecord option) =
        match existing with
        | Some _ -> Error "name-already-belongs"
        | None -> Ok()

    let private createManagerForkIfNameFree
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        language
        handles
        =
        let existingByname =
            handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

        match checkExistingByname existingByname with
        | Error err -> Task.FromResult(Error err)
        | Ok() -> createNewManagerForkReceipt host runtime request language handles

    let private executeManagerReceiptAfterGuards
        (host: DelegationEnvironment)
        (request: Request)
        language
        : Task<Result<ForkReceipt, string>> =
        match host.ForkRuntime() with
        | Error _ -> Task.FromResult(Error(refusalCode Path.Fork.ChargeContextUnavailable))
        | Ok runtime ->
            let handles = agentHandles host
            createManagerForkIfNameFree host runtime request language handles

    let private resolveParentSessionId (host: DelegationEnvironment) (sessionId: string) =
        if String.IsNullOrWhiteSpace sessionId then
            SessionId.create ""
        else
            host.LogicalOwnerFor(SessionId.create sessionId)

    let private validateManagerRequestSyntax (request: Request) =
        if String.IsNullOrWhiteSpace request.Name then
            Error(refusalCode Path.Fork.NameRequired)
        elif String.IsNullOrWhiteSpace request.Charge then
            Error(refusalCode Path.Fork.ChargeRequired)
        elif isSelfAttachment request then
            Error(refusalCode Path.Fork.AttachSelf)
        else
            Ok()

    let private validateManagerRequestPrerequisites
        (host: DelegationEnvironment)
        (request: Request)
        (sessionId: string)
        : Task<Result<unit, string>> =
        taskResult {
            do! validateManagerRequestSyntax request
            let parentSessionId = resolveParentSessionId host sessionId

            let isDevOps =
                String.Equals(request.Name.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

            let! refusal = boundDevOpsDispatchRefusal host parentSessionId isDevOps |> TaskResultCE.ofTask

            return!
                match refusal with
                | Some path -> Error(refusalCode path)
                | None -> Ok()
        }

    let private executeManagerReceipt
        (host: DelegationEnvironment)
        (request: Request)
        : Task<Result<ForkReceipt, string>> =
        task {
            match! validateManagerRequestPrerequisites host request (callerText host) with
            | Error err -> return Error err
            | Ok() ->
                let language = lang host
                return! executeManagerReceiptAfterGuards host request language
        }

    let private finishCommissionNewReceipt
        (host: DelegationEnvironment)
        (request: Request)
        (managed: ManagedAgent)
        : Task<Result<ForkReceipt, string>> =
        task {
            match host.NewHandleId, host.OpenManagerJob with
            | Some newHandleId, Some openManagerJob ->
                let managerId = ManagerJobId.create (newHandleId ())

                match!
                    openManagerJob (
                        managerId,
                        managed.Name,
                        commissionCharge request,
                        request.Name,
                        request.ExpectedToolCalls
                    )
                with
                | Ok _ ->
                    return
                        Ok
                            { Name = request.Name.Trim()
                              AgentId = ""
                              Outcome = "commissioned" }
                | Error _ -> return Error "placement-failed"
            | _ -> return Error "placement-failed"
        }

    let private validateOrchestratorCalling (request: Request) =
        let hasCalling = not (String.IsNullOrWhiteSpace request.Calling)

        if
            hasCalling
            && not (String.Equals(request.Calling.Trim(), derivedCommissionCalling, StringComparison.OrdinalIgnoreCase))
        then
            Error "unknown-calling"
        else
            Ok()

    let private validateOrchestratorRequest (host: DelegationEnvironment) (request: Request) =
        let existingByname = orchestratorExistingByname host request

        if String.IsNullOrWhiteSpace(callerText host) then
            Error "context-unavailable"
        elif String.IsNullOrWhiteSpace request.Name then
            Error "name-required"
        elif String.IsNullOrWhiteSpace request.Charge then
            Error "charge-required"
        elif (validateOrchestratorCalling request).IsError then
            Error "unknown-calling"
        elif existingByname.IsSome then
            Error "name-already-belongs"
        else
            resolveDerivedManagedCalling orchestratorCallingBindings derivedCommissionCalling

    let private executeOrchestratorReceipt
        (host: DelegationEnvironment)
        (request: Request)
        : Task<Result<ForkReceipt, string>> =
        match validateOrchestratorRequest host request with
        | Error err -> Task.FromResult(Error err)
        | Ok managed -> finishCommissionNewReceipt host request managed

    let private dispatchRoleReceipt
        (host: DelegationEnvironment)
        (role: Role)
        (decoded: Result<Request, string>)
        : Task<Result<ForkReceipt, string>> =
        match decoded, role with
        | Ok request, Role.Manager -> executeManagerReceipt host request
        | Ok request, Role.Orchestrator -> executeOrchestratorReceipt host request
        | Error _, _ -> Task.FromResult(Error "placement-failed")
        | _ -> Task.FromResult(Error "not-a-delegation-role")

    let private executeReceiptForRole (host: DelegationEnvironment) (args: ICommandArguments) (roleOpt: Role option) =
        match roleOpt with
        | None -> Task.FromResult(Error "not-a-delegation-role")
        | Some role ->
            let language = lang host
            let decoded = decode language args
            dispatchRoleReceipt host role decoded

    let receipt (host: DelegationEnvironment) (args: ICommandArguments) : Task<Result<ForkReceipt, string>> =
        task {
            match host.CallerRole with
            | None -> return Error "not-a-delegation-role"
            | Some callerRole ->
                let! roleOpt = callerRole ()
                return! executeReceiptForRole host args roleOpt
        }

    let private resumeResultReceipt (request: Request) (result: ForkResult) : Result<ForkReceipt, string> =
        match result with
        | ForkResult.Created created ->
            Ok
                { Name = request.Name.Trim()
                  AgentId = created
                  Outcome = "created" }
        | ForkResult.Nudged nudged ->
            Ok
                { Name = request.Name.Trim()
                  AgentId = nudged
                  Outcome = "nudged" }
        | ForkResult.DispatchUncertain _ -> Error(refusalCode Path.Fork.ChargePlacementUncertain)
        | ForkResult.NotFound _ -> Error "placement-failed"

    /// A placement either resumes the child (its fork result is the receipt) or
    /// refuses as placement-failed.
    let private placedResumeReceipt
        (request: Request)
        (placement: Result<ForkResult, string>)
        : Result<ForkReceipt, string> =
        match placement with
        | Ok forkResult -> resumeResultReceipt request forkResult
        | Error _ -> Error "placement-failed"



    let private resumeBusyGuidanceReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        agentId
        (profile: PromptAuthority.AuthorityExecutionProfile)
        : Task<Result<ForkReceipt, string>> =
        taskResult {
            let! attachment = resolveAttachment host handles request |> TaskResult.mapError refusalCode
            let! rendered = prepareForkPromptWithRecord runtime request
            let! result = runtime.AppendGuidance(agentId, profile, rendered) |> TaskResultCE.ofTask
            return! placedResumeReceipt request result
        }

    /// Busy child: the charge becomes guidance appended to the running turn.
    let private resumeBusyReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        agentId
        : Task<Result<ForkReceipt, string>> =
        match runtime.ActiveGuidanceProfile agentId with
        | Error _ -> Task.FromResult(Error "placement-failed")
        | Ok profile -> resumeBusyGuidanceReceipt host runtime request handles agentId profile

    let private resumeIdleAttachedReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        (role: Role)
        agentId
        : Task<Result<ForkReceipt, string>> =
        task {
            let! attachment = resolveAttachment host handles request

            match attachment with
            | Error path -> return Error(refusalCode path)
            | Ok attachment ->
                let! placement =
                    taskResult {
                        do! recordFissionAffinity host agentId
                        let! reused = runManagerReuse host runtime role request (lang host) attachment agentId

                        return reused
                    }

                return placedResumeReceipt request placement
        }

    /// Idle child: the charge reopens the existing child session.
    let private resumeIdleReceipt
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        (role: Role)
        agentId
        : Task<Result<ForkReceipt, string>> =
        resumeIdleAttachedReceipt host runtime request handles role agentId

    /// The agent id behind a handle, or the person-unknown refusal.
    let private handleAgentId (handle: HandleRecord) : Result<AgentHandleId, string> =
        match HandleId.tryAgent handle.Handle with
        | None -> Error(refusalCode Path.Fork.PersonUnknown)
        | Some handleId -> Ok handleId

    /// A busy child takes the charge as guidance; an idle one is reopened under
    /// its live (or adopted) role, else its durable canonical role.
    let private resumeKnownAgent
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        (handle: HandleRecord)
        (handleId: AgentHandleId)
        : Task<Result<ForkReceipt, string>> =
        let agentId = AgentHandleId.value handleId

        let activeRun =
            lock runtime.Gate (fun () -> runtime.PendingRuns.ContainsKey agentId)

        match activeRun, runtime.TryFindAgentOrAdopt agentId, handle.CanonicalRole with
        | true, _, _ -> resumeBusyReceipt host runtime request handles agentId
        | false, Some(_, role, _), _ -> resumeIdleReceipt host runtime request handles role agentId
        | false, None, Role.DevOps -> resumeIdleReceipt host runtime request handles Role.DevOps agentId
        | false, None, _ -> resumeIdleReceipt host runtime request handles handle.CanonicalRole agentId

    /// The resume of a known person: the handle's agent id decides whether the
    /// child is busy or idle.
    let private resumeKnownHandle
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        (handle: HandleRecord)
        : Task<Result<ForkReceipt, string>> =
        match handleAgentId handle with
        | Error path -> Task.FromResult(Error path)
        | Ok handleId -> resumeKnownAgent host runtime request handles handle handleId

    let private resumeReceiptExistingPerson
        (host: DelegationEnvironment)
        (runtime: HostForkRuntime)
        (request: Request)
        (handles: AgentLinkageProjection option)
        : Task<Result<ForkReceipt, string>> =
        let existingByname =
            handles |> Option.bind (HandleProjection.tryFindByByname request.Name)

        match existingByname with
        | None -> Task.FromResult(Error(refusalCode Path.Fork.PersonUnknown))
        | Some handle -> resumeKnownHandle host runtime request handles handle

    /// The manager resume against an available fork runtime.
    let private resumeWithForkRuntime
        (host: DelegationEnvironment)
        (request: Request)
        (runtime: HostForkRuntime)
        : Task<Result<ForkReceipt, string>> =
        let handles = agentHandles host
        resumeReceiptExistingPerson host runtime request handles

    /// The manager resume against the host's fork runtime, or the
    /// charge-context-unavailable refusal.
    let private resumeWithHostRuntime
        (host: DelegationEnvironment)
        (request: Request)
        : Task<Result<ForkReceipt, string>> =
        match host.ForkRuntime() with
        | Error _ -> Task.FromResult(Error(refusalCode Path.Fork.ChargeContextUnavailable))
        | Ok runtime -> resumeWithForkRuntime host request runtime


    /// The manager resume once the devops guards have passed.
    let private executeManagerResumeReceiptAfterGuards
        (host: DelegationEnvironment)
        (request: Request)
        : Task<Result<ForkReceipt, string>> =
        task {
            let parentSessionId = resolveParentSessionId host (callerText host)

            let isDevOps =
                String.Equals(request.Name.Trim(), "devops", StringComparison.OrdinalIgnoreCase)

            let! refusal = boundDevOpsDispatchRefusal host parentSessionId isDevOps

            match refusal with
            | Some path -> return Error(refusalCode path)
            | None ->
                do! bindWorkDevOpsIfNeeded host parentSessionId isDevOps

                return! resumeWithHostRuntime host request
        }

    let private executeManagerResumeReceipt
        (host: DelegationEnvironment)
        (request: Request)
        : Task<Result<ForkReceipt, string>> =
        task {
            if String.IsNullOrWhiteSpace request.Name then
                return Error(refusalCode Path.Fork.NameRequired)
            elif String.IsNullOrWhiteSpace request.Charge then
                return Error(refusalCode Path.Fork.ChargeRequired)
            elif isSelfAttachment request then
                return Error(refusalCode Path.Fork.AttachSelf)
            elif not (String.IsNullOrWhiteSpace request.Calling) then
                return Error(refusalCode Path.Resume.CallingNotAllowed)
            else
                return! executeManagerResumeReceiptAfterGuards host request
        }

    /// Structured resume entry point: same guard chain as the prose `resume`
    /// tool, but it answers with data instead of rendered prose.
    let resumeReceipt (host: DelegationEnvironment) (args: ICommandArguments) : Task<Result<ForkReceipt, string>> =
        task {
            let language = lang host

            match decode language args with
            | Error _ -> return Error "placement-failed"
            | Ok request -> return! executeManagerResumeReceipt host request
        }
