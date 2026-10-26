namespace Wanxiangshu.Execution.Tool

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Ablation
open Wanxiangshu.Execution.Routing
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.AsyncSupport
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Host.Ports
open Wanxiangshu.Participant.Provider

/// The part of a product tool the execute gate reads: its product name and its
/// body. The admission is not carried here; the gate resolves it from the
/// product admission table by tool name. The argument carrier stays opaque so
/// any host adapter's decoded arguments pass through untouched.
type GatedTool<'args> =
    { Name: string
      Execute: 'args -> HostToolContext -> Task<string> }

/// capability-enforcement-005/010/025: the exact session facts the execute
/// gate reads. The host registry fills every field from its runtime scope or its
/// own callbacks; a stage never receives the whole scope.
type ToolGateFacts =
    {
        RoleFor: HostToolContext -> Role option
        EnsureRoleFor: HostToolContext -> Task<Role option>
        ManagerFactsFor: string -> Task<ManagerCapabilityFacts>

        IsRetirementFrozen: string -> Task<bool>
        IsWaitingForUser: string -> bool
        /// LC-017: the pending question's exact fence (provider run, tool call)
        /// for a session, when a question is waiting for the user. Ask replays
        /// carrying the same fence pass the waiting gate; everything else
        /// stays refused.
        WaitingQuestionFenceFor: string -> (ProviderRunIdentity * ToolCallId) option
        BeginToolExecution: string -> unit
        EndToolExecution: string -> unit
        /// Algorithm E1.1: the work already recorded a retirement for this
        /// call's exact fence, so a retried deliver replays its state instead
        /// of being refused for the tenure that retirement removed.
        RecordedDeliverReplay: HostToolContext -> Task<bool>
    }

/// capability-enforcement: the product's tool execute gate. A host adapter
/// wraps every registered tool body in `execute`; the admission decision, the
/// refusal prose and the stage order live here, never in the adapter.
module ToolExecuteGate =

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
            | Some key -> [ "code", ToolResultText.TString key ]
            | None -> []

        ToolResultText.tomlObjectWithInstructions [ ProviderProse.render (lang ctx) path subs ] fields

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
    let private ablationStage (spec: GatedTool<'args>) (ctx: HostToolContext) : Task<string option> =
        Task.FromResult(
            if AblationGate.toolDenied (AblationGate.registry ()) spec.Name then
                Some(denied ctx spec.Name Path.DeniedAblation (Map [ "tool", spec.Name ]) None)
            else
                None
        )

    let private executeToolBody (spec: GatedTool<'args>) (args: 'args) (ctx: HostToolContext) : Task<string> =
        spec.Execute args ctx

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
        (spec: GatedTool<'args>)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let! replayed = gate.RecordedDeliverReplay ctx

            if replayed then
                return! executeToolBody spec args ctx
            else
                return denyTaskState ctx spec.Name
        }

    let private refusedOrReplayed
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        if spec.Name = "deliver" then
            executeDeliverReplay gate spec args ctx
        else
            Task.FromResult(denyTaskState ctx spec.Name)

    let private managerFactsStage
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
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
            | _ -> return! executeToolBody spec args ctx
        }

    let private executeAdmittedRole
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
        (ctx: HostToolContext)
        (role: Role)
        : Task<string> =
        let isExactQuestionReplay =
            match ctx.ProviderRunId, ctx.ToolCallId with
            | Some run, Some call -> gate.WaitingQuestionFenceFor ctx.SessionId = Some(run, call)
            | _ -> false

        let isWaitingReplayExempt =
            (spec.Name = "deliver" || spec.Name.StartsWith "todowrite-for-")
            && isExactQuestionReplay

        if gate.IsWaitingForUser ctx.SessionId && not isWaitingReplayExempt then
            Task.FromResult(
                ProviderProse.render (ProviderLanguageBinding.forSessionText ctx.SessionId) "tool/ask-waiting" Map.empty
            )
        elif
            role = Role.Manager
            && spec.Name <> "todowrite-for-manager"
            && spec.Name <> "review"
            && spec.Name <> "resetScope"
            && spec.Name <> "negotiate"
        then
            managerFactsStage gate spec managerPermission args ctx
        else
            executeToolBody spec args ctx

    /// capability-enforcement-002: office Role then admission then Manager facts.
    let private officeStage
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (officeAdmission: AgentRole -> bool)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            let! resolvedRole =
                match gate.RoleFor ctx with
                | Some role -> Task.FromResult(Some role)
                | None -> gate.EnsureRoleFor ctx

            match resolvedRole with
            | Some role when officeAdmission (AgentRole(Roles.roleLabel role)) ->
                return! executeAdmittedRole gate spec managerPermission args ctx role
            | Some role -> return denyRole ctx spec.Name role
            | None -> return denied ctx spec.Name Path.DeniedUnestablished Map.empty None
        }

    /// capability-enforcement-006: the attachment IS the authority. Resolving a
    /// public office Role here would deny the Bookkeeper its own exact tool,
    /// because a HostInternal prompt deliberately installs no public authority
    /// profile.
    let private attachmentStage (spec: GatedTool<'args>) (args: 'args) (ctx: HostToolContext) : Task<string> =
        task {
            if ToolAdmissionTable.privateAttachmentAdmitsForSession spec.Name ctx.SessionId then
                return! executeToolBody spec args ctx
            else
                return denied ctx spec.Name Path.DeniedUnestablished Map.empty None
        }

    let private admissionStage
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        match ToolAdmissionTable.tryAdmission spec.Name with
        | Some(ToolAdmission.OfficeRole officeAdmission) ->
            officeStage gate spec managerPermission officeAdmission args ctx
        | Some(ToolAdmission.PrivateAttachment _) -> attachmentStage spec args ctx
        | None ->
            // An unknown tool name is refused exactly like an office-role
            // denial: same prose, same substitutions, no authority.
            officeStage gate spec managerPermission (fun _ -> false) args ctx

    let private runAfterAblation
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        admissionStage gate spec managerPermission args ctx


    let private runBoundaryStages
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            match! ablationStage spec ctx with
            | Some refusal -> return refusal
            | None -> return! runAfterAblation gate spec managerPermission args ctx
        }

    /// The one composition point. Six named stages, fixed ordinary calls in the
    /// order written here. No middleware list, no fold, no registry.
    let execute
        (gate: ToolGateFacts)
        (spec: GatedTool<'args>)
        (managerPermission: ToolPermission option)
        (args: 'args)
        (ctx: HostToolContext)
        : Task<string> =
        task {
            match providerToolBoundary ctx with
            | Error error -> return raise (InvalidOperationException error)
            | Ok() ->
                return! accountingStage gate ctx (fun () -> runBoundaryStages gate spec managerPermission args ctx)
        }
