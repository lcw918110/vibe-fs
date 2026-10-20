namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation.Advisor
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Mission.Work
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Repository.Programming.Js.Anchor
open Wanxiangshu.Repository.Programming.Js.OpenCode
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

        /// Algorithm G1: deliver is blocked while any parked site is unresolved.
        [<Literal>]
        let DeliverBlockedByParked = "tool/js-program/parked-deliver-blocked"

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

    let private denyRole (ctx: HostToolContext) (specName: string) (role: Role) =
        let path =
            if specName = "fission" then
                Path.DeniedFission
            else
                Path.DeniedRole

        denied ctx specName path (Map [ "tool", specName; "role", sprintf "%A" role ]) None

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
        {
            RoleFor: HostToolContext -> Role option
            EnsureRoleFor: HostToolContext -> Task<Role option>
            ManagerFactsFor: string -> Task<ManagerCapabilityFacts>

            IsRetirementFrozen: string -> Task<bool>
            IsWaitingForUser: string -> bool
            BeginToolExecution: string -> unit
            EndToolExecution: string -> unit
            /// Algorithm E1.1: the work already recorded a retirement for this
            /// call's exact fence, so a retried deliver replays its state instead
            /// of being refused for the tenure that retirement removed.
            RecordedDeliverReplay: HostToolContext -> Task<bool>
        }

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



    /// capability-enforcement-025: current Manager facts close the office gate.
    /// A frozen retirement keeps Join, Finality and the explicit human Ask; the final incumbent
    /// keeps the read/cleanup/close-out surface and loses every new-work
    /// capability.
    /// Algorithm E1.1: a retried deliver whose fence the work already settled is
    /// not a new capability — it replays the state that call recorded, so the
    /// missing tenure is not a reason to refuse it. Every other denied call
    /// keeps the action-focused refusal.
    let private executeDeliverReplay
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let! replayed = gate.RecordedDeliverReplay ctx

            if replayed then
                return! spec.Execute args ctx
            else
                return denyTaskState ctx spec.Name
        }

    let private refusedOrReplayed
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        if spec.Name = "deliver" then
            executeDeliverReplay gate spec args ctx
        else
            Task.FromResult(denyTaskState ctx spec.Name)

    let private managerFactsStage
        (gate: ToolGateFacts)
        (spec: ToolSpec)
        (managerPermission: ToolPermission option)
        (args: HostToolArguments)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let! managerFacts = gate.ManagerFactsFor ctx.SessionId
            let! frozen = gate.IsRetirementFrozen ctx.SessionId

            match managerPermission with
            | Some permission when
                frozen
                && permission <> ToolPermission.Join
                && permission <> ToolPermission.Finality
                ->
                return denyTaskState ctx spec.Name
            | Some permission when not (OfficeCapability.isAllowedForManagerFacts managerFacts permission) ->
                return! refusedOrReplayed gate spec args ctx
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
        if gate.IsWaitingForUser ctx.SessionId && spec.Name <> "ask" then
            Task.FromResult(
                ProviderProse.render (ProviderLanguageBinding.forSessionText ctx.SessionId) "tool/ask-waiting" Map.empty
            )
        elif role = Role.Manager then
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
            | None -> return denied ctx spec.Name Path.DeniedUnestablished Map.empty None
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
        admissionStage gate spec managerPermission args ctx


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

    let private staticAdmissions () : (string * ToolAdmission) list =
        [ "fork", ForkTool.managerAdmission
          "resume", ForkTool.managerAdmission
          "commission", ForkTool.orchestratorAdmission

          "join", JoinTool.admission
          "ask", ToolAdmission.OfficeRole(fun _ role -> OfficeCapability.isAllowed role ToolPermission.Ask)
          "devops",
          ToolAdmission.OfficeRole(fun _ role -> OfficeCapability.isAllowed role ToolPermission.DevOpsConsultation)
          "horizon", HorizonTool.admission
          "fission", FissionTool.admission
          "review", ReviewShell.admission
          "deliver",
          ToolAdmission.OfficeRole(fun _ role ->
              OfficeCapability.isAllowed role ToolPermission.Finality
              || OfficeCapability.isAllowed role ToolPermission.Deliver)
          "bash-honeypot", BashHoneypotTool.admission
          "assume", AssumeTool.admission
          "fetch", ToolAdmission.OfficeRole(fun _ r -> OfficeCapability.isAllowed r ToolPermission.Fetch)
          "todowrite-for-bookkeeper", JsBookkeeperTool.admission ]

    let private tryAdmissionFor (specName: string) : ToolAdmission option =
        match staticAdmissions () |> List.tryFind (fun (name, _) -> name = specName) with
        | Some(_, admission) -> Some admission

        | None when specName.StartsWith "todowrite-for-" && specName <> "todowrite-for-bookkeeper" ->
            Some(JsToolSpec.admissionFor (specName.Substring 14))
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
    let tryAdmission (specName: string) : ToolAdmission option = tryAdmissionFor specName

    /// capability-enforcement-006: the internal-leaf decision for a session that holds no public
    /// office profile at all. An office tool is never admitted this way.
    let privateAttachmentAdmits (specName: string) (sessionId: string) : bool =
        match tryAdmissionFor specName with
        | Some(ToolAdmission.PrivateAttachment predicate) -> predicate (probeContext sessionId)
        | Some(ToolAdmission.OfficeRole _)
        | None -> false

    /// AGENT-007 role gate, delegates to owner-defined tool admissions.
    /// sessionId is the tool call's Host session.
    let rolePredicate (specName: string) (sessionId: string) : Role -> bool =
        // capability-enforcement-006: an internal leaf tool is admitted by attachment, never by a
        // public office, so no public Role may ever see it on this surface.
        match tryAdmissionFor specName with
        | Some(ToolAdmission.OfficeRole predicate) -> predicate (probeContext sessionId)
        | Some(ToolAdmission.PrivateAttachment _)
        | None -> fun _ -> false

    /// A refused `js.ask` must reject the program's await with the stable
    /// failure sentinel instead of resolving to a data object.
    [<Emit("Promise.reject(Object.assign(new Error($1), { __jsFailure: { code: $0, reason: $1 } }))")>]
    let private rejectedJsFailure (code: string) (reason: string) : obj = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isStringValue (value: obj) : bool = jsNative

    let private recordParkStack (state: Wanxiangshu.Process.JsSandbox.ParkState) (stack: obj) =
        if not (isNull stack) then
            state.ParkStack <- string stack

    let private finishPlanAskOutcome
        (state: Wanxiangshu.Process.JsSandbox.ParkState)
        (text: string)
        (stack: obj)
        (asked: Result<unit, string>)
        =
        match asked with
        | Ok() ->
            recordParkStack state stack
            Wanxiangshu.Process.JsSandbox.suspendProgram state (createObj [ "question" ==> text ])
        | Error reason ->
            let failure = JsFailure.AskRefused reason
            rejectedJsFailure (JsFailure.code failure) (JsFailure.reason failure)

    let private performPlanAsk
        (state: Wanxiangshu.Process.JsSandbox.ParkState)
        (invoke: string -> Task<Result<unit, string>>)
        (text: string)
        (stack: obj)
        : Task<obj> =
        task {
            if System.String.IsNullOrWhiteSpace text then
                let failure = JsFailure.InvalidArgument "ask requires a question"
                return rejectedJsFailure (JsFailure.code failure) (JsFailure.reason failure)
            else
                let! asked = invoke text
                return finishPlanAskOutcome state text stack asked
        }

    let private handlePlanAsk
        (state: Wanxiangshu.Process.JsSandbox.ParkState)
        (invoke: string -> Task<Result<unit, string>>)
        (question: obj)
        (stack: obj)
        : obj =
        if state.AbandonRequested then
            rejectedJsFailure
                (JsFailure.code JsFailure.CannotParkWhileAbandoning)
                (JsFailure.reason JsFailure.CannotParkWhileAbandoning)
        else
            performPlanAsk state invoke (string question) stack |> box

    let private createPlanSandboxApi
        (currentContent: string)
        (stagedMutation: string option ref)
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (askInvoke: (string -> Task<Result<unit, string>>) option)
        =
        let members =
            [ "read" ==> fun (_: obj) -> currentContent
              "edit"
              ==> fun (_path: string) (newText: string) ->
                  stagedMutation.Value <- Some newText
                  createObj [ "ok" ==> true ] ]

        let askMembers =
            match parkState, askInvoke with
            | Some state, Some invoke ->
                [ "ask"
                  ==> fun (question: obj) (stack: obj) -> handlePlanAsk state invoke question stack ]
            | _ -> []

        createObj [ "js" ==> createObj (members @ askMembers) ]

    let private writePlanFile root sessionId newText value =
        match PlanPath.rewritePlanAtomic root sessionId newText with
        | Ok(_path, _digest) ->
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Succeeded(value, [ "plan.md" ], [], [], []))
        | Error err ->
            JsToolsResult.render (
                JsToolWorkflow.JsToolOutcome.Failed(
                    JsFailure.ProgramFailed(sprintf "Failed to write plan artifact: %s" err),
                    [],
                    [],
                    [],
                    []
                )
            )

    let private commitPlanMutation root sessionId hasReviewed newTextOpt value =
        match newTextOpt with
        | None -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Succeeded(value, [], [], [], []))
        | Some _ when not hasReviewed ->
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(JsFailure.PlanReviewRequired, [], [], [], []))
        | Some newText -> writePlanFile root sessionId newText value

    let private renderPlanParseOutcome root sessionId hasReviewed stagedMutation json =
        match JsToolsData.parse json with
        | Error failure -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(failure, [], [], [], []))
        | Ok value -> commitPlanMutation root sessionId hasReviewed stagedMutation value

    let private renderPlanSandboxOutcome root sessionId hasReviewed stagedMutation runOutcome =
        match runOutcome with
        | Error failure -> JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(failure, [], [], [], []))
        | Ok json -> renderPlanParseOutcome root sessionId hasReviewed stagedMutation json

    let private executePlanSandbox prose root sessionId hasReviewed programSource =
        task {
            let currentContent =
                match PlanPath.readPlan root sessionId with
                | Ok c -> c
                | Error _ -> ""

            // DSL-MUTABLE: algorithm-scratch — staged mutation accumulator for Plan sandbox
            let stagedMutation = ref None
            let api = createPlanSandboxApi currentContent stagedMutation None None
            let baseClassSource = JsCanonicalDescription.runtimePlanBaseClass prose
            let deadlineMs = 10000
            let deadlineEpochMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000L
            let outputBoundBytes = 1 <<< 20

            let! handle = JsSandbox.runSurface baseClassSource programSource api deadlineMs deadlineEpochMs

            let! runOutcome = JsSandbox.awaitResult handle outputBoundBytes

            return renderPlanSandboxOutcome root sessionId hasReviewed stagedMutation.Value runOutcome
        }

    let private activeReviewed (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork) =
        match work.ActiveId with
        | None -> Error "No active incumbency found for this work"
        | Some _ -> Ok work.Reviewed

    let private resolveActiveTenure (gitCommonDir: string) (sessionId: string) =
        match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir sessionId with
        | Some work -> activeReviewed work
        | None -> Error "No active incumbency found for this work"

    let private executeTenureSandbox prose root gitCommonDir sessionId programSource =
        match resolveActiveTenure gitCommonDir sessionId with
        | Error reason ->
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(JsFailure.ProgramFailed reason, [], [], [], []))
            |> Task.FromResult
        | Ok hasReviewed -> executePlanSandbox prose root sessionId hasReviewed programSource

    let private runPlanProgramCore
        (prose: JsCanonicalDescription.Prose)
        gitCommonDirOpt
        (rootOpt: string option)
        (sessionId: string)
        (programSourceOpt: string option)
        : Task<string> =
        if Option.isNone programSourceOpt then
            JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(JsFailure.InvalidProgram, [], [], [], []))
            |> Task.FromResult
        elif Option.isNone rootOpt || Option.isNone gitCommonDirOpt then
            JsToolsResult.render (
                JsToolWorkflow.JsToolOutcome.Failed(
                    JsFailure.ProgramFailed "The plan workspace is unavailable",
                    [],
                    [],
                    [],
                    []
                )
            )
            |> Task.FromResult
        else
            executeTenureSandbox prose rootOpt.Value gitCommonDirOpt.Value sessionId programSourceOpt.Value

    let runPlanProgram prose gitCommonDir root sessionId program =
        runPlanProgramCore prose (Some gitCommonDir) (Some root) sessionId (Some program)

    [<Emit("$0 === undefined || $0 === null")>]
    let private isNullishValue (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isNumberValue (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'object'")>]
    let private isObjectValue (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArrayValue (value: obj) : bool = jsNative

    [<Emit("$0[$1]")>]
    let private propertyOf (value: obj) (name: string) : obj = jsNative

    [<Emit("Object.keys($0)")>]
    let private objectKeys (value: obj) : string[] = jsNative

    let private advisorInvalid (detail: string) : Result<'a, JsFailure> =
        Error(JsFailure.ViewDeclarationInvalid detail)

    let private advisorAnchorOfKey (key: string) : int option =
        match System.Int32.TryParse key with
        | true, anchor when string anchor = key -> Some anchor
        | _ -> None

    let private advisorPrefixUntilOf (raw: obj) : Result<int, JsFailure> =
        if isNumberValue raw then
            Ok(int (unbox<float> raw))
        else
            advisorInvalid
                "prefixUntil must be an anchor number; pass one above every anchor you have seen to keep everything"

    /// `thenRemove` is an anchor→summary map: the summary is what stands in for
    /// that anchor from here on, so a missing or blank summary is a declaration
    /// that would silently drop knowledge.
    let private advisorReplacePair (source: obj) (key: string) : Result<int * string, JsFailure> =
        let summary = propertyOf source key

        let isSummary =
            isStringValue summary && not (String.IsNullOrWhiteSpace(unbox<string> summary))

        match advisorAnchorOfKey key, isSummary with
        | None, _ -> advisorInvalid (sprintf "thenRemove keys must be anchor numbers; \"%s\" is not one" key)
        | Some _, false ->
            advisorInvalid (
                sprintf
                    "thenRemove[%s] must be a non-blank summary string (the summary replaces that anchor's segments, not deletes them)"
                    key
            )
        | Some anchor, true -> Ok(anchor, string summary)

    let private advisorAppendPair
        (accumulated: Result<(int * string) list, JsFailure>)
        (pair: Result<int * string, JsFailure>)
        : Result<(int * string) list, JsFailure> =
        match accumulated, pair with
        | Ok pairs, Ok entry -> Ok(pairs @ [ entry ])
        | Error failure, _ -> Error failure
        | _, Error failure -> Error failure

    /// `prefixUntil` is a required anchor number (never "all"); `thenRemove` is
    /// an optional anchor→summary map naming what stands in for which anchor.
    /// The parse is the same one `AnchorSurface.setView` applies to a tool-level
    /// declaration, but a consult never writes it back into the ANCHOR ledger:
    /// the ledger's pending declaration belongs to the caller's own next
    /// request, and consulting the advisor must not rewrite it (advisor-[005]).
    let private advisorParseDeclaration (view: obj) : Result<AdvisorViewDeclaration, JsFailure> =
        let thenRemoveOf (raw: obj) : Result<(int * string) list, JsFailure> =
            if isNullishValue raw then
                Ok []
            elif isArrayValue raw || not (isObjectValue raw) then
                advisorInvalid
                    "thenRemove must be an object mapping anchor numbers to summaries, e.g. { \"3\": \"what segment 3 held\" }"
            else
                objectKeys raw
                |> Array.toList
                |> List.map (advisorReplacePair raw)
                |> List.fold advisorAppendPair (Ok [])
                |> Result.map (List.sortBy fst)

        let prefixUntil = advisorPrefixUntilOf (propertyOf view "prefixUntil")
        let thenRemove = thenRemoveOf (propertyOf view "thenRemove")

        match prefixUntil, thenRemove with
        | Ok prefixUntil, Ok thenRemove ->
            Ok
                { PrefixUntil = prefixUntil
                  ThenRemove = thenRemove }
        | Error failure, _
        | _, Error failure -> Error failure

    /// A missing declaration hands over no material (`PrefixUntil = 0`), the
    /// same default the transform applies when a request carries no view.
    let private advisorDeclarationOf (view: obj) : Result<AdvisorViewDeclaration, JsFailure> =
        if isNullishValue view then
            Ok { PrefixUntil = 0; ThenRemove = [] }
        else
            advisorParseDeclaration view

    /// The transform's own wire text over a decoded snapshot part: the advisor
    /// reads exactly what the caller's request carried, tool calls included.
    let private advisorPartText (part: MessagePart) : string =
        match part with
        | MessagePart.Text text
        | MessagePart.Reasoning text -> text
        | MessagePart.ToolCall(_, name, args) -> name + " " + args
        | MessagePart.ToolResult(_, result) -> result
        | MessagePart.Activity _ -> ""

    let private advisorMessageText (parts: MessagePart array) : string =
        if isNull parts then
            ""
        else
            parts |> Array.map advisorPartText |> String.concat "\n"

    /// Numbering each snapshot message is what keeps the anchors in a consult
    /// identical to the ones the caller's own request used.
    let private advisorViewMessage (sessionId: string) (message: SessionMessage) : AdvisorViewMessage =
        { Anchor = AnchorLedger.numberMessage sessionId message.Id
          Text = advisorMessageText message.Parts }

    let private advisorIntentText (intent: obj) : string =
        if isStringValue intent then unbox<string> intent else ""

    /// advisor-[006]: the member's refusals are data — `{ ok = false, code,
    /// reason }` — never a throw the program would have to catch.
    let private advisorRefusal (code: string) (reason: string) : obj =
        createObj [ "ok" ==> false; "code" ==> code; "reason" ==> reason ]

    /// advisor-[002]/[003]: one consult — the caller's own delta rendered as the
    /// seat's first prompt, the intent appended after it, and the seat's answer
    /// handed back as data. A successful consult advances that caller session's
    /// delivered-anchor cursor; a refusal changes nothing.
    let private advisorSeatAnswer
        (runtime: ToolRuntimeScope)
        (ctx: HostToolContext)
        (messages: SessionMessage list)
        (declaration: AdvisorViewDeclaration)
        (intent: obj)
        : Task<obj> =
        task {
            let sessionId = ctx.SessionId

            let request: AdvisorViewRequest =
                { Messages = messages |> List.map (advisorViewMessage sessionId)
                  Declaration = declaration }

            let plan =
                AdvisorView.plan (ProviderLanguage.label (lang ctx)) (Cursor.delivered sessionId) request

            let deps: AdvisorSeatDeps =
                { Sessions = runtime.Sessions
                  Snapshot = runtime.Snapshot
                  Journal = runtime.Journal
                  Directory = runtime.WorkspaceDirectory
                  Participant = ManagedAgent.nameOf Role.Advisor }

            let! answered = AdvisorSeat.consult deps sessionId (advisorIntentText intent) plan.ViewPrompt

            match answered with
            | Ok advice ->
                Cursor.record sessionId plan.DeliveredAnchors
                return createObj [ "ok" ==> true; "advice" ==> advice ]
            | Error reason -> return advisorRefusal "advisor-unavailable" reason
        }

    /// advisor-[006]: the `js.advisor` host closure. The caller's context comes
    /// from its own session snapshot; an unreadable snapshot or an invalid view
    /// declaration is a refusal, never a silently empty view. `HasNewMaterial`
    /// does not gate the consult: the intent alone is new material.
    let private advisorSnapshotMessages
        (runtime: ToolRuntimeScope)
        (ctx: HostToolContext)
        : Task<Result<SessionMessage list, string>> =
        match runtime.Snapshot with
        | None -> Task.FromResult(Error "the session snapshot is unavailable")
        | Some snapshotPort -> snapshotPort.GetMessages(SessionId.create ctx.SessionId)

    let private advisorConsult
        (runtime: ToolRuntimeScope)
        (ctx: HostToolContext)
        (intent: obj)
        (view: obj)
        : Task<obj> =
        task {
            let! loaded = advisorSnapshotMessages runtime ctx

            match loaded, advisorDeclarationOf view with
            | Error reason, _ -> return advisorRefusal "advisor-unavailable" reason
            | _, Error failure -> return advisorRefusal (JsFailure.code failure) (JsFailure.reason failure)
            | Ok messages, Ok declaration -> return! advisorSeatAnswer runtime ctx messages declaration intent
        }

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
        (viewForSession: (string -> Task<string option>) option)
        (snapshot: ISessionSnapshotPort option)
        (cancelSignals: (SessionId seq -> unit) option)
        (beginToolExecution: string -> unit)
        (endToolExecution: string -> unit)
        (eventPort: IEventObservationPort option)
        (syncDelegateRuntime: SyncDelegateRuntime option)
        (casebookToolSpecs: ToolSpec list)

        (jsTransactionPersistence: IJsTransactionPersistence option)
        (jsLifecyclePersistence: IJsLifecyclePersistence option)
        (continueManagerLoop: SessionId -> string -> Task<Result<unit, string>>)
        (childViewForRun: (SessionId -> ProviderRunIdentity -> Task<string option>) option)
        (userNotify: (string -> string -> unit) option)
        =
        let factory = ToolHostCodec.factory toolModule
        let providerLanguage = ProviderLanguageBinding.readGlobalPreference ()

        let jsProse: JsCanonicalDescription.Prose =
            JsDescriptionAssets.load providerLanguage

        /// Where the programming surface of one session executes. A Manager, child
        /// or DevOps session is created rooted at the directory its owner
        /// registered (a Change road's worktree), so the surface follows the
        /// session instead of the plugin's project directory.
        let jsRootOf (sessionId: string) =
            JsToolSpec.rootForSession sessionDirectories (defaultArg workspaceDirectory "") sessionId

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
                viewForSession,
                snapshot,
                cancelSignals,
                ?childViewForRun = childViewForRun,
                continueManagerLoop = continueManagerLoop,
                ?eventPort = eventPort
            )

        let generatedJsSpecFor (role: Role) =
            JsToolGenerator.generate (string role) (OfficeCapability.permissions role) jsProse
            |> Option.map (fun surface ->
                let spec =
                    JsToolSpec.create
                        factory
                        surface
                        (defaultArg workspaceDirectory "")
                        jsTransactionPersistence
                        jsLifecyclePersistence
                        None
                        jsRootOf

                // Delegation seam: the programming surface's delegation members
                // return data envelopes built from the delegation shard's
                // structured entries. The host tool prose (fork/join/fission)
                // is untouched; the host is process-live for one programming
                // call and capability gating stays on the surface.
                { spec with
                    Execute =
                        fun args ctx ->
                            let envelopeOk (fields: (string * obj) list) : obj = createObj (("ok" ==> true) :: fields)

                            let refusal (code: string) : obj =
                                createObj [ "ok" ==> false; "code" ==> code ]

                            let host: JsDelegationHost =
                                { Fork =
                                    fun payload ->
                                        task {
                                            let raw =
                                                if isStringValue payload then
                                                    createObj [ "charge" ==> payload ]
                                                else
                                                    payload

                                            let! receipt =
                                                ForkTool.executeReceipt runtime ctx (ToolHostCodec.argumentsOfRaw raw)

                                            return
                                                match receipt with
                                                | Ok placed ->
                                                    envelopeOk
                                                        [ "name" ==> placed.Name
                                                          "agentId" ==> placed.AgentId
                                                          "outcome" ==> placed.Outcome ]
                                                | Error code -> refusal code
                                        }
                                  Join =
                                    fun () ->
                                        task {
                                            let! receipt = JoinTool.executeReceipt runtime ctx

                                            return
                                                match receipt with
                                                | Ok records ->
                                                    let toObj (record: JoinTool.JoinRecord) =
                                                        createObj
                                                            [ "name" ==> record.Name
                                                              "agentId" ==> record.AgentId
                                                              "role" ==> record.Role
                                                              "status" ==> record.Status
                                                              "view" ==> record.View ]

                                                    envelopeOk
                                                        [ "records" ==> (records |> List.map toObj |> Array.ofList)
                                                          "byName"
                                                          ==> createObj
                                                                  [ for record in records ->
                                                                        record.Name ==> box (toObj record) ] ]
                                                | Error code -> refusal code
                                        }
                                  Fission =
                                    fun payload ->
                                        task {
                                            let! receipt =
                                                FissionTool.executeReceipt
                                                    runtime
                                                    ctx
                                                    (ToolHostCodec.argumentsOfRaw payload)

                                            return
                                                match receipt with
                                                | Ok group ->
                                                    envelopeOk
                                                        [ "groupId" ==> group.GroupId
                                                          "lanes"
                                                          ==> (group.Lanes
                                                               |> List.map (fun (index, sessionId) ->
                                                                   createObj
                                                                       [ "index" ==> index
                                                                         "sessionId" ==> sessionId ])
                                                               |> Array.ofList) ]
                                                | Error code -> refusal code
                                        }
                                  // The horizon roster envelope comes from the
                                  // horizon owner; the test-facing fork receipt
                                  // entry takes the JS harness, so the
                                  // generated-surface host builds the context
                                  // from its own production runtime scope.
                                  Horizon =
                                    fun () ->
                                        let horizonContext: HorizonTool.HorizonRuntimeContext =
                                            { RuntimeFor = runtime.RuntimeFor
                                              LogicalOwnerFor = runtime.LogicalOwnerFor
                                              Journal = runtime.Journal
                                              EnsureWorkDevOpsBound = runtime.EnsureWorkDevOpsBound }

                                        HorizonTool.rosterReceipt
                                            (ProviderLanguage.externalName (
                                                ProviderLanguageBinding.forSessionText ctx.SessionId
                                            ))
                                            horizonContext
                                            ctx
                                  Advisor = fun intent view -> advisorConsult runtime ctx intent view }

                            JsDelegationBridge.install ctx.SessionId host

                            task {
                                try
                                    return! spec.Execute args ctx
                                finally
                                    JsDelegationBridge.clear ctx.SessionId
                            } })

        let generatedJsSpecs () =
            [ for role in Roles.all do
                  yield! generatedJsSpecFor role |> Option.toList ]



        // Lifecycle uses its own wanxiangli journal. The common directory is
        // data passed to that owner, not a borrowed workspace EventStore.
        let resolvePlanGitCommonDir () : Task<string option> =
            task {
                match workspaceDirectory with
                | Some ws ->
                    let! root = RuntimePath.stateRoot ws
                    return Some root
                | None -> return None
            }

        let resolvePlanRoot () : Task<string option> =
            task {
                match workspaceDirectory with
                | Some ws ->
                    let! root = RuntimePath.stateRoot ws
                    return Some(root + "/wanxiangshu")
                | None -> return None
            }

        let planningAdmission: ToolAdmission =
            ToolAdmission.OfficeRole(fun _ role -> role = Role.Plan)

        // 只有 S1 阶段的 Plan 可以提问；阶段门禁由 AskUser 与 durable work 共同兜底。
        let planDeadlineMs = 10000
        let planOutputBoundBytes = 1 <<< 20

        let planBaseClassSource () =
            JsCanonicalDescription.runtimePlanBaseClass jsProse

        let resolveHasReviewed (planGitCommonDir: string option) (sessionId: string) : bool =
            planGitCommonDir
            |> Option.bind (fun gitCommonDir -> resolveActiveTenure gitCommonDir sessionId |> Result.toOption)
            |> Option.defaultValue false

        let commitOrRenderPlanOutcome root sessionId hasReviewed stagedMutation value outcome =
            match stagedMutation with
            | None -> JsToolsResult.render outcome
            | Some _ -> commitPlanMutation root sessionId hasReviewed stagedMutation value

        let finishPlanOutcome root sessionId hasReviewed stagedMutation outcome =
            match outcome with
            | JsToolWorkflow.JsToolOutcome.Succeeded(value, _, _, _, _) ->
                commitOrRenderPlanOutcome root sessionId hasReviewed stagedMutation value outcome
            | _ -> JsToolsResult.render outcome

        let executePlanProgramWithPark (root: string) (ctx: HostToolContext) (programSource: string) : Task<string> =
            task {
                let currentContent =
                    match PlanPath.readPlan root ctx.SessionId with
                    | Ok c -> c
                    | Error _ -> ""

                // DSL-MUTABLE: algorithm-scratch — staged plan mutation
                let stagedMutation = ref None

                let makeApi (parkState: Wanxiangshu.Process.JsSandbox.ParkState) =
                    createPlanSandboxApi
                        currentContent
                        stagedMutation
                        (Some parkState)
                        (Some(fun question -> runtime.AskUser(ctx, question)))

                let now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

                let! outcome =
                    JsToolWorkflow.runWithParkApi
                        ctx.SessionId
                        (planBaseClassSource ())
                        programSource
                        makeApi
                        planDeadlineMs
                        (now + int64 planDeadlineMs)
                        planOutputBoundBytes

                let! commonDirOpt = resolvePlanGitCommonDir ()
                let hasReviewed = resolveHasReviewed commonDirOpt ctx.SessionId
                return finishPlanOutcome root ctx.SessionId hasReviewed stagedMutation.Value outcome
            }

        let executePlanProgramRoute (root: string option) (ctx: HostToolContext) (source: string) : Task<string> =
            match root with
            | Some rootDir -> executePlanProgramWithPark rootDir ctx source
            | None ->
                Task.FromResult(
                    JsToolsResult.render (
                        JsToolWorkflow.JsToolOutcome.Failed(
                            JsFailure.ProgramFailed "The plan workspace is unavailable",
                            [],
                            [],
                            [],
                            []
                        )
                    )
                )

        let executeJsPlanTool (args: HostToolArguments) (ctx: HostToolContext) : Task<string> =
            task {
                let viewFailure =
                    match
                        Wanxiangshu.Repository.Programming.Js.Anchor.AnchorSurface.setView ctx.SessionId (args.Raw?view)
                    with
                    | Ok() -> None
                    | Error failure -> Some failure

                let program = args.OptionalText "program"

                let unpark = if isNull (args.Raw?unpark) then null else args.Raw?unpark

                let abandon = if isNull (args.Raw?abandon) then null else args.Raw?abandon

                match viewFailure, program, unpark, abandon with
                | Some failure, _, _, _ ->
                    return JsToolsResult.render (JsToolWorkflow.JsToolOutcome.Failed(failure, [], [], [], []))
                | None, Some source, _, _ ->
                    let! root = resolvePlanRoot ()
                    return! executePlanProgramRoute root ctx source
                | None, None, unparkValue, _ when not (isNull unparkValue) ->
                    let! outcome =
                        JsToolWorkflow.resumeParked
                            (string (unparkValue?handle))
                            (unparkValue?value)
                            (planBaseClassSource ())
                            planDeadlineMs
                            planOutputBoundBytes

                    return JsToolsResult.render outcome
                | None, None, _, abandonValue when not (isNull abandonValue) ->
                    let! outcome =
                        JsToolWorkflow.abandonParked (string (abandonValue?handle)) planDeadlineMs planOutputBoundBytes

                    return JsToolsResult.render outcome
                | _ ->
                    return
                        JsToolsResult.render (
                            JsToolWorkflow.JsToolOutcome.Failed(
                                JsFailure.InvalidArgument "no program, unpark, or abandon input",
                                [],
                                [],
                                [],
                                []
                            )
                        )
            }

        // work-028: the real delegation capability — the SyncDelegate
        // runtime runs the bound fixed DevOps for this work synchronously and
        // hands back its complete delivered view. A missing runtime fails closed.
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
            | Ok res -> res.View
            | Error err -> sprintf "devops failed: %s" err

        let askGitCommonDir (directory: string option) : Task<string> =
            task {
                match directory with
                | Some workspace ->
                    let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                    return commonDir
                | None -> return ""
            }

        let askWithLifecycle
            (syncRuntime: SyncDelegateRuntime option)
            (args: HostToolArguments)
            (ctx: HostToolContext)
            : Task<string> =
            task {
                let! gitCommonDir = askGitCommonDir workspaceDirectory

                if String.IsNullOrWhiteSpace gitCommonDir then
                    return "devops failed: git common directory is missing"
                else
                    let delegateTo = devopsDelegateOf syncRuntime ctx

                    let! outcome =
                        Wanxiangshu.Mission.Planning.PlanTools.executeAsk
                            gitCommonDir
                            ctx.SessionId
                            (args.Text "intent")
                            delegateTo

                    return renderAskOutcome outcome
            }

        let executeAskTool
            (syncRuntime: SyncDelegateRuntime option)
            (args: HostToolArguments)
            (ctx: HostToolContext)
            : Task<string> =
            askWithLifecycle syncRuntime args ctx

        let lifecycleWorkOf
            (runtime: ToolRuntimeScope)
            (sessionId: string)
            : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
            runtime.LifecycleWorkFor sessionId

        let lifecycleActiveIncumbencyOf (runtime: ToolRuntimeScope) (sessionId: string) : Task<string option> =
            task {
                let! work = lifecycleWorkOf runtime sessionId
                return work |> Option.bind (fun work -> work.ActiveId)
            }

        // work-026: the receipt names the artifact path and the fact that
        // an execution session started; the spawned session id stays out of
        // the provider-visible text.
        let renderDeliverReceipt (res: DeliverResult) : string =
            if res.Relayed then
                res.Message
            elif res.RetirementOutcome = "PostS3" then
                sprintf
                    "%s The plan artifact is ready at %s; nudges are permanently disabled and this session is now in post-S3 state."
                    res.Message
                    res.Path
            elif res.Delivered then
                sprintf "%s The plan artifact is at %s; an execution session has been started." res.Message res.Path
            else
                res.Message

        // work-025/026: the retirement cursor is the exact consumed-through
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

        let deliverCreate (runtime: ToolRuntimeScope) (operationId: string) (agent: string) =
            runtime.CreateExecutionSession(operationId, agent)

        let deliverPrompt (runtime: ToolRuntimeScope) (execSessionId: string) (agent: string) (prompt: string) =
            runtime.PromptExecutionSession(execSessionId, agent, prompt)

        let deliverDiscard (runtime: ToolRuntimeScope) (execSessionId: string) =
            runtime.DiscardExecutionSession execSessionId

        let renderDeliverOutcome outcome =
            match outcome with
            | Ok res -> renderDeliverReceipt res
            | Error err -> sprintf "deliver failed: %s" err

        let registerPostS3IfEntered
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            (outcome: Result<DeliverResult, string>)
            =
            match outcome, ctx.ProviderRunId, ctx.ToolCallId with
            | Ok res, Some runId, Some callId when res.RetirementOutcome = "PostS3" ->
                runtime.RegisterPostS3Interrupt(SessionId.create ctx.SessionId, runId, callId)
            | _ -> ()

        let deliverCursor
            (root: string)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            (incumbencyId: string)
            (gitCommonDir: string)
            : Task<string> =
            task {
                let providerRunId =
                    ctx.ProviderRunId
                    |> Option.map ProviderRunIdentity.value
                    |> Option.defaultValue ""

                let toolCallId =
                    ctx.ToolCallId |> Option.map ToolCallId.value |> Option.defaultValue ""

                let retirementBlockers () =
                    runtime.RetirementBlockersFor ctx.SessionId |> List.toArray

                match! resolveDeliverRetirementCursor runtime ctx with
                | Error err -> return sprintf "deliver failed: %s" err
                | Ok retirementCursor ->
                    let! outcome =
                        PlanTools.enterPostS3OrRelay
                            gitCommonDir
                            root
                            ctx.SessionId
                            incumbencyId
                            providerRunId
                            toolCallId
                            retirementBlockers
                            retirementCursor

                    registerPostS3IfEntered runtime ctx outcome

                    return renderDeliverOutcome outcome
            }

        let deliverPostS3
            (root: string)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            (gitCommonDir: string)
            : Task<string> =
            task {
                let providerRunId =
                    ctx.ProviderRunId
                    |> Option.map ProviderRunIdentity.value
                    |> Option.defaultValue ""

                let toolCallId =
                    ctx.ToolCallId |> Option.map ToolCallId.value |> Option.defaultValue ""

                let retirementBlockers () =
                    runtime.RetirementBlockersFor ctx.SessionId |> List.toArray

                match! resolveDeliverRetirementCursor runtime ctx with
                | Error err -> return sprintf "deliver failed: %s" err
                | Ok retirementCursor ->
                    let! outcome =
                        PlanTools.startPostS3Execution
                            gitCommonDir
                            root
                            ctx.SessionId
                            providerRunId
                            toolCallId
                            retirementBlockers
                            (deliverCreate runtime)
                            (deliverPrompt runtime)
                            (deliverDiscard runtime)
                            retirementCursor

                    return renderDeliverOutcome outcome
            }

        let deliverForIncumbency
            (root: string)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            (incumbencyId: string)
            : Task<string> =
            task {
                let! gitCommonDir =
                    task {
                        match runtime.WorkspaceDirectory with
                        | Some ws -> return! Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot ws
                        | None -> return ""
                    }

                if String.IsNullOrWhiteSpace gitCommonDir then
                    return "deliver failed: git common directory is missing"
                else
                    return! deliverCursor root runtime ctx incumbencyId gitCommonDir
            }

        let runDeliver (root: string) (runtime: ToolRuntimeScope) (ctx: HostToolContext) : Task<string> =
            task {
                let! gitCommonDir =
                    task {
                        match runtime.WorkspaceDirectory with
                        | Some ws -> return! Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot ws
                        | None -> return ""
                    }

                match! lifecycleWorkOf runtime ctx.SessionId with
                | Some work when work.ActiveId.IsSome ->
                    return! deliverForIncumbency root runtime ctx work.ActiveId.Value
                | Some work when
                    work.Delivered
                    && work.Sequence = [ "S1"; "S2"; "S3" ]
                    && String.IsNullOrWhiteSpace gitCommonDir
                    ->
                    return "deliver failed: git common directory is missing"
                | Some work when work.Delivered && work.Sequence = [ "S1"; "S2"; "S3" ] ->
                    return! deliverPostS3 root runtime ctx gitCommonDir
                | _ -> return "deliver failed: no active incumbency found for this work"
            }

        let executeDeliverTool
            (rootOpt: string option)
            (runtime: ToolRuntimeScope)
            (ctx: HostToolContext)
            : Task<string> =
            task {
                match rootOpt with
                | Some root -> return! runDeliver root runtime ctx
                | None -> return "deliver failed: the plan workspace is unavailable"
            }

        let planningSpecs () =
            let planUnparkSchema =
                ToolHostCodec.objectSchemaOf
                    [ "handle", ToolHostCodec.stringSchema factory
                      "value", ToolHostCodec.anySchema factory ]
                    factory

            let planAbandonSchema =
                ToolHostCodec.objectSchemaOf [ "handle", ToolHostCodec.stringSchema factory ] factory

            // ANCHOR handover view (context-compression [031]): a retiring
            // tenure declares what the successor request must keep. Same
            // contract as the generated surfaces: prefixUntil protects the
            // cache prefix, thenRemove replaces anchors with the tenure's own
            // summaries for good.
            let planViewSchema =
                ToolHostCodec.objectSchemaOf
                    [ "prefixUntil", ToolHostCodec.numberSchema factory
                      "thenRemove",
                      ToolHostCodec.recordSchemaOf
                          (ToolHostCodec.stringSchema factory)
                          (ToolHostCodec.stringSchema factory)
                          (JsDescriptionAssets.viewDeclarationExtra (ProviderLanguageBinding.readGlobalPreference ()))
                          factory ]
                    factory

            [ { Name = "todowrite-for-plan"
                Description =
                  JsCanonicalDescription.renderPlan
                      jsProse
                      (ProviderProse.render providerLanguage "tool/js-plan" Map.empty)
                Arguments =
                  [ "program",
                    ToolHostCodec.stringSchemaDescribed
                        (JsDescriptionAssets.argProgram (ProviderLanguageBinding.readGlobalPreference ()))
                        factory
                    "unpark", planUnparkSchema
                    "abandon", planAbandonSchema
                    "view", planViewSchema ]
                Admission = planningAdmission
                Execute = fun args ctx -> executeJsPlanTool args ctx }
              { Name = "devops"
                Description = ProviderProse.render providerLanguage "tool/devops" Map.empty
                Arguments =
                  [ "intent",
                    ToolHostCodec.stringSchemaDescribed "Investigation intent handed to the bound fixed DevOps" factory ]
                Admission = planningAdmission
                Execute = fun args ctx -> executeAskTool syncDelegateRuntime args ctx } ]

        let askSpec: ToolSpec =
            { Name = "ask"
              Description = ProviderProse.render providerLanguage "tool/ask" Map.empty
              Arguments =
                [ "question",
                  ToolHostCodec.stringSchemaDescribed
                      (ProviderProse.render providerLanguage "tool/ask-question" Map.empty)
                      factory ]
              Admission = ToolAdmission.OfficeRole(fun _ role -> OfficeCapability.isAllowed role ToolPermission.Ask)
              Execute =
                fun args ctx ->
                    task {
                        let! asked = runtime.AskUser(ctx, args.Text "question")
                        let language = ProviderLanguageBinding.forSessionText ctx.SessionId

                        return
                            match asked with
                            | Ok() -> ProviderProse.render language "tool/ask-sent" Map.empty
                            | Error reason -> ProviderProse.render language "tool/ask-failed" (Map [ "reason", reason ])
                    } }

        /// Algorithm D/E: one `deliver` tool. The work's sequence decides the
        /// path — S4 is the two-stage Manager deliver, S1–S3 the one-shot Plan
        /// deliver. The name, the registration and the resource are single.
        let deliverSpec () =
            let managerSpec = DeliverTool.spec factory runtime

            let planSpec =
                { Name = "deliver"
                  Description = ProviderProse.render providerLanguage "tool/deliver" Map.empty
                  Arguments = [ "note", ToolHostCodec.optionalStringSchema factory ]
                  Admission = planningAdmission
                  Execute =
                    fun _ ctx ->
                        task {
                            let! root = resolvePlanRoot ()
                            return! executeDeliverTool root runtime ctx
                        } }

            let sequenceOf (ctx: HostToolContext) : Task<string list option> =
                task {
                    let! work = lifecycleWorkOf runtime ctx.SessionId
                    return work |> Option.map (fun work -> work.Sequence)
                }

            let executeDeliverRoute (ctx: HostToolContext) (args: HostToolArguments) : Task<string> =
                task {
                    match! sequenceOf ctx with
                    | Some [ "S1"; "S2"; "S3" ] -> return! planSpec.Execute args ctx
                    | _ -> return! managerSpec.Execute args ctx
                }

            { managerSpec with
                Admission =
                    ToolAdmission.OfficeRole(fun _ role ->
                        OfficeCapability.isAllowed role ToolPermission.Finality
                        || OfficeCapability.isAllowed role ToolPermission.Deliver)
                Execute =
                    fun args ctx ->
                        task {
                            // Algorithm G1: the deliver hard gate. While any
                            // parked site is unresolved (state=Parked and no
                            // unpark/abandon has been taken), delivery is
                            // refused and every handle is listed. The parked
                            // site table and its read-only query are owned by
                            // lane-a-core (JsParkedSites.parkedHandles).
                            let parkedHandles = JsParkedSites.parkedHandles ()

                            if not (List.isEmpty parkedHandles) then
                                let handles = String.Join(", ", parkedHandles)

                                return
                                    ToolHostCodec.tomlObjectWithInstructions
                                        [ ProviderProse.render
                                              (lang ctx)
                                              Path.DeliverBlockedByParked
                                              (Map [ "handles", handles ]) ]
                                        [ "code", ToolHostCodec.TString Path.DeliverBlockedByParked
                                          "parked_handles", ToolHostCodec.TString handles ]
                            else
                                return! executeDeliverRoute ctx args
                        } }

        let baseSpecs =
            [ yield ForkTool.managerSpec factory runtime
              yield ForkTool.resumeSpec factory runtime

              yield ForkTool.orchestratorSpec factory runtime
              yield JoinTool.spec runtime
              yield askSpec

              let horizonContext: HorizonTool.HorizonRuntimeContext =
                  { RuntimeFor = runtime.RuntimeFor
                    LogicalOwnerFor = runtime.LogicalOwnerFor
                    Journal = runtime.Journal
                    EnsureWorkDevOpsBound = runtime.EnsureWorkDevOpsBound }

              yield HorizonTool.spec horizonContext
              yield FissionTool.spec factory runtime
              // work-038: the shared review shell.
              // Plan lifecycle reads its own durable owner directly.
              yield ReviewShell.spec factory runtime
              yield deliverSpec ()
              yield BashHoneypotTool.spec
              yield AssumeTool.spec factory

              yield! casebookToolSpecs
              yield! generatedJsSpecs ()

              yield! planningSpecs () ]

        let recordedDeliverReplay (ctx: HostToolContext) : Task<bool> =
            task {
                match ctx.ProviderRunId, ctx.ToolCallId with
                | Some run, Some call ->
                    let settled work =
                        DeliverTool.recordedFenceReplay work (ProviderRunIdentity.value run) (ToolCallId.value call)
                        |> Option.isSome

                    let! work = lifecycleWorkOf runtime ctx.SessionId
                    return work |> Option.exists settled
                | _ -> return false
            }

        let gateFacts: ToolGateFacts =
            { RoleFor = (fun ctx -> runtime.RoleFor ctx)
              EnsureRoleFor = (fun ctx -> runtime.EnsureRoleFor ctx)
              ManagerFactsFor = (fun sessionId -> runtime.ManagerCapabilityFactsFor sessionId)

              IsRetirementFrozen = (fun sessionId -> runtime.IsRetirementFrozen sessionId)
              IsWaitingForUser = runtime.IsWaitingForUser
              BeginToolExecution = beginToolExecution
              EndToolExecution = endToolExecution
              RecordedDeliverReplay = recordedDeliverReplay }

        // Generic execute gate: every tool declares the authority it is admitted
        // under, and the registry never invents one the session does not hold.
        // capability-enforcement-012: Manager tool permissions come from the one
        // StaticTools reverse lookup the schema projection already reads.
        let gateExecute (spec: ToolSpec) =
            let managerPermission =
                match ManagerReviewTools.requiredPermissions spec.Name with
                | Some perms -> perms |> Seq.tryHead
                | None -> StaticTools.permissionOfToolName Role.Manager spec.Name

            fun args (ctx: HostToolContext) -> executeGate gateFacts spec managerPermission args ctx

        let specs =
            baseSpecs |> List.map (fun spec -> { spec with Execute = gateExecute spec })

        { Tools = ToolHostCodec.registry factory specs
          Runtime = runtime }
