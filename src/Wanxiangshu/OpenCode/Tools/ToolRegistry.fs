namespace Wanxiangshu.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.Context.Scope
open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Participant.ManagedAgents

open Wanxiangshu.Foundation.TerminalSignal

open System
open FsToolkit.ErrorHandling
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
open Wanxiangshu.Execution.Tool
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Mission.Work
open Wanxiangshu.Execution.Routing
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Programming.Js
open Wanxiangshu.Repository.Programming.Js.OpenCode
open Wanxiangshu.Process

/// Assembly-only registry: tool behavior lives in one vertical verb module;
/// per-session resources live in ToolRuntimeScope.
type ToolRegistration =
    { Tools: obj
      Runtime: ToolRuntimeScope }

module ToolRegistry =

    [<Import("join", "node:path")>]
    let private joinPath (left: string) (right: string) : string = jsNative

    [<Import("deliveryAttemptLedger", "wanxiangli/dispatch")>]
    let private deliveryAttemptLedger: obj = jsNative

    /// An unanswered question refuses source writes. The ledger being empty does not admit them.
    let planSourceWriteAllowed (waitingForUser: bool) = not waitingForUser

    /// The same unanswered question refuses a dispatch to the fixed DevOps member.
    let devopsDispatchAllowed (waitingForUser: bool) = not waitingForUser

    [<RequireQualifiedAccess>]
    module Path =
        /// Algorithm G1: deliver is blocked while any parked site is unresolved.
        [<Literal>]
        let DeliverBlockedByParked = "tool/js-program/parked-deliver-blocked"

    let private lang (ctx: HostToolContext) =
        let sessionText = ctx.SessionId

        ProviderLanguageBinding.forSessionText sessionText

    [<Emit("typeof $0 === 'string'")>]
    let private isStringValue (value: obj) : bool = jsNative

    [<Emit("$0 === null || $0 === undefined")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("(() => { const out = {}; for (const k of Object.keys($0)) out[k] = $0[k]; out[$1] = $2; return out; })()")>]
    let private withField (source: obj) (key: string) (value: obj) : obj = jsNative

    /// A delegate payload is built inside the JS sandbox realm, so its
    /// prototype is that realm's `Object.prototype` — never the host's. The
    /// host's plain-record check compares realms, so the payload is copied
    /// here into a host-realm plain record: own enumerable data properties
    /// only (accessors and non-enumerable properties are skipped). The copy
    /// is a snapshot; the program may still mutate its own object afterwards.
    [<Emit("(() => { const out = {}; const src = $0; if (src === null || src === undefined || typeof src !== 'object') return out; for (const key of Object.keys(src)) { const descriptor = Object.getOwnPropertyDescriptor(src, key); if (descriptor && Object.prototype.hasOwnProperty.call(descriptor, 'value')) { out[key] = descriptor.value; } } return out; })()")>]
    let private plainRecordCopy (source: obj) : obj = jsNative

    let private devopsIntentText (payload: obj) : string =
        if isStringValue payload then
            unbox<string> payload
        elif isNullish payload then
            ""
        else
            let prop = payload?intent
            if isNullish prop then "" else string prop

    let private reviewRefusal (code: string) (reason: string) : obj =
        createObj [ "ok" ==> false; "code" ==> code; "reason" ==> reason ]

    let private savedReviewDraft root sessionId office =
        match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected root sessionId with
        | Some work when office = "Plan" && work.Stage = "待执行" ->
            Wanxiangshu.Mission.Planning.PlanTools.readAndValidatePlan (joinPath root "wanxiangshu") sessionId
            |> Result.map (fun (content, _, _) -> content)
            |> Result.defaultWith invalidOp
        | _ -> ""

    let private reviewDraft root sessionId office (note: string) =
        if String.IsNullOrEmpty note then savedReviewDraft root sessionId office else note

    /// The optional `note` of `this.review(findings, note)`. Absent or empty
    /// leaves the draft to the canonical plan file.
    let private reviewNote (note: obj) : Result<string, string> =
        if isNullish note then Ok ""
        elif isStringValue note then Ok(unbox<string> note)
        else Error "review note must be a string"

    /// The `this.review(findings, note)` member. Plan and Manager send one Review command.
    let private reviewMember
        (runtime: ToolRuntimeScope)
        (ctx: HostToolContext)
        (findings: obj)
        (note: obj)
        : Task<obj> =
        task {
            match runtime.WorkspaceDirectory, ctx.ProviderRunId, ctx.ToolCallId, reviewNote note with
            | _, _, _, Error reason -> return reviewRefusal "INVALID_ARGUMENT" reason
            | Some directory, Some providerRun, Some toolCallId, Ok digest when
                runtime.IsRole(ctx, Role.Plan) || runtime.IsRole(ctx, Role.Manager)
                ->
                let! gitCommonDir = RuntimePath.stateRoot directory
                let office = if runtime.IsRole(ctx, Role.Plan) then "Plan" else "Manager"
                let runId = ProviderRunIdentity.value providerRun
                let callId = ToolCallId.value toolCallId
                let draft = reviewDraft gitCommonDir ctx.SessionId office digest

                let selfName = JsPrograms.callerName ctx.SessionId (runId + ":" + callId)

                let parked =
                    JsPrograms.deliveryBlockersWithChoicesForSession ctx.SessionId
                    |> List.map fst
                    |> List.filter (fun name -> selfName <> Some name)

                let blockers =
                    parked @ runtime.RetirementBlockersFor ctx.SessionId
                    |> List.distinct
                    |> List.toArray
                    |> box

                let produced =
                    Wanxiangshu.Mission.Lifecycle.DurableWorkHost.submitReview
                        gitCommonDir
                        ctx.SessionId
                        (runId + ":" + callId)
                        office
                        runId
                        callId
                        findings
                        blockers
                        draft
                        (runtime.ReviewPorts ctx)

                let! reply =
                    if
                        produced <> null
                        && emitJsExpr produced "typeof $0.then === 'function'"
                    then
                        unbox<Task<obj>> produced
                    else
                        Task.FromResult produced

                let status = string reply?status

                if status = "Committed" || status = "Replayed" || status = "Observed" then
                    return
                        createObj
                            [ "ok", box true
                              "kind", reply?kind
                              "milestone", reply?milestone
                              "wait", reply?wait
                              "incumbencyId", reply?incumbencyId
                              "blockers", reply?blockers ]
                else
                    return createObj [ "ok", box false; "reason", reply?reason ]
            | _ ->
                return
                    reviewRefusal "review-unavailable" "review requires an owning provider call and a Plan or Manager role"
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
            JsToolPolicy.rootForSession sessionDirectories (defaultArg workspaceDirectory "") sessionId

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

        // DSL-MUTABLE: resource — workspace state-root memo for this tool registry
        let mutable cachedGitCommonDir: string option = None

        let populateStateRoot (ws: string) : Task<unit> =
            task {
                let! dir = RuntimePath.stateRoot ws
                cachedGitCommonDir <- Some dir
            }

        let ensureStateRoot (workspace: string option) : Task<unit> =
            match cachedGitCommonDir, workspace with
            | None, Some ws -> populateStateRoot ws
            | _ -> Task.FromResult()

        let askGitCommonDir (directory: string option) : Task<string> =
            task {
                match directory with
                | Some workspace ->
                    let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                    return commonDir
                | None -> return ""
            }

        /// The assistant message that owns this call: the provider run IS that
        /// message id. The call's prose and the physical message it answers
        /// are both read from this one message.
        let owningAssistantMessage
            (messages: SessionMessage list)
            (providerRun: ProviderRunIdentity)
            : SessionMessage option =
            let providerRunId = ProviderRunIdentity.value providerRun

            messages
            |> List.tryFind (fun message -> message.Id = providerRunId && message.Role = "assistant")

        /// The formal prose of the assistant message that owns the call: the
        /// text parts of that one message, joined by newlines. A call whose
        /// assistant message carries no prose has no formal text, and a
        /// witness without prose is no witness.
        let formalTextOf (message: SessionMessage) : string option =
            let text =
                message.Parts
                |> Array.choose (function
                    | MessagePart.Text text -> Some text
                    | _ -> None)
                |> String.concat "\n"

            if String.IsNullOrWhiteSpace text then None else Some text

        /// The physical user message this call answers.
        ///
        /// The parent of the assistant message is the run's physical input
        /// only when that parent is a user message: inside one run the next
        /// assistant message hangs under the tool result, not under the user.
        /// Otherwise the nearest user message before this call answers: a
        /// delivery always belongs to the turn that asked for it, and relying
        /// on the routing's current physical alone loses that turn as soon as
        /// the lease moves. The routing value is the last fallback, not the
        /// first source.
        let physicalUserMessageOf
            (messages: SessionMessage list)
            (message: SessionMessage)
            (currentPhysical: string option)
            : PhysicalUserMessageId option =
            let parentIsUserMessage =
                match message.ParentId with
                | Some parent when not (String.IsNullOrWhiteSpace parent) ->
                    messages
                    |> List.exists (fun candidate -> candidate.Id = parent && candidate.Role = "user")
                | _ -> false

            if parentIsUserMessage then
                message.ParentId |> Option.map PhysicalUserMessageId.create
            else
                let nearestUser =
                    messages
                    |> List.takeWhile (fun candidate -> candidate.Id <> message.Id)
                    |> List.rev
                    |> List.tryPick (fun candidate -> if candidate.Role = "user" then Some candidate.Id else None)

                (nearestUser |> Option.orElse currentPhysical)
                |> Option.map PhysicalUserMessageId.create

        /// The task a delivery serves.
        ///
        /// A lifecycle work names the tenure the delivery closes: the
        /// retirement that carries this very call's fence is the durable fact
        /// of that tenure, so the tenure and the retirement id come from one
        /// fact. A delegated child names its own handle — the agent handle the
        /// delegation bound to that child session. Everything else is this very
        /// invocation, named by the tool call that carries it.
        let deliveryTaskOf
            (durable: AgentJournal)
            (sessionId: SessionId)
            (providerRun: ProviderRunIdentity)
            (toolCallId: ToolCallId)
            (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
            : DeliveryTaskReference * string option =
            let providerRunId = ProviderRunIdentity.value providerRun
            let toolCallIdText = ToolCallId.value toolCallId

            let lifecycleTask =
                work
                |> Option.bind (fun work ->
                    work.Retirements
                    |> List.tryFind (fun retirement ->
                        retirement.Fence.ProviderRunId = providerRunId
                        && retirement.Fence.ToolCallId = toolCallIdText)
                    |> Option.map (fun retirement ->
                        DeliveryTaskReference.Lifecycle(SessionId.value sessionId, retirement.IncumbencyId),
                        Some retirement.RetirementId))

            let childHandle =
                (AgentJournal.snapshot durable).AgentProjections.HandleByChildSession
                |> Map.tryFind sessionId
                |> Option.bind (fun record ->
                    let agentHandle =
                        HandleId.tryAgent record.Handle
                        |> Option.map AgentHandleId.value
                        |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

                    agentHandle
                    |> Option.orElse (
                        Some record.TargetAgent
                        |> Option.filter (fun target -> not (String.IsNullOrWhiteSpace target))
                    ))

            match lifecycleTask, childHandle with
            | Some task, _ -> task
            | None, Some handle -> DeliveryTaskReference.ChildWork handle, None
            | None, None -> DeliveryTaskReference.Invocation toolCallIdText, None

        let witnessSettlement
            (durable: AgentJournal)
            (sessionId: SessionId)
            (providerRun: ProviderRunIdentity)
            (toolCallId: ToolCallId)
            logicalRunId
            authorityRoot
            physical
            (formalText: string)
            work
            : DeliverySettlement =
            let deliveryTask, retirementId =
                deliveryTaskOf durable sessionId providerRun toolCallId work

            DurableDelivery(
                { LogicalRunId = logicalRunId
                  AuthorityRoot = authorityRoot
                  PhysicalUserMessageId = physical
                  Task = deliveryTask
                  RetirementId = retirementId
                  FormalText = formalText },
                DeliveryWitnessWriter.create durable (fun witness ->
                    // The durable witness is committed by now: the
                    // registration only records the committed attempt, so the
                    // transform hook's delivery adjudication can stop it.
                    deliveryAttemptLedger?recordEstablished(
                        createObj [
                            "sessionId" ==> SessionId.value witness.SessionId
                            "providerRun" ==> ProviderRunIdentity.value witness.ProviderRun
                            "callId" ==> ToolCallId.value witness.ToolCallId
                        ],
                        box (PhysicalUserMessageId.value witness.PhysicalUserMessageId)) |> ignore)
            )

        /// The formal prose and the physical input of the assistant message
        /// that made the call; either missing leaves the call unattributed.
        ///
        /// The prose may be the immediately preceding assistant text of the
        /// same turn: the delivery contract tells the model to state the
        /// result in prose and then call `deliver`, and providers commonly
        /// split those into two assistant messages. Only text after this
        /// call's physical input is accepted, so an older turn's prose can
        /// never attribute a new delivery.
        let formalTextOfTurn (messages: SessionMessage list) (owner: SessionMessage) (physical: PhysicalUserMessageId) =
            let beforeOwner = messages |> List.takeWhile (fun message -> message.Id <> owner.Id)

            let boundary =
                beforeOwner
                |> List.tryFindIndex (fun message -> message.Id = PhysicalUserMessageId.value physical)

            match boundary with
            | None -> None
            | Some index ->
                beforeOwner
                |> List.skip (index + 1)
                |> List.rev
                |> List.tryFind (fun candidate -> candidate.Role = "assistant")
                |> Option.bind formalTextOf

        let messageAttribution
            (ctx: HostToolContext)
            (providerRun: ProviderRunIdentity)
            (messages: SessionMessage list)
            : (string * PhysicalUserMessageId) option =
            owningAssistantMessage messages providerRun
            |> Option.bind (fun message ->
                physicalUserMessageOf messages message (currentPhysicalUserMessage ctx.SessionId)
                |> Option.map (fun physical ->
                    let formalText =
                        formalTextOf message
                        |> Option.orElseWith (fun () -> formalTextOfTurn messages message physical)
                        |> Option.defaultValue ""

                    formalText, physical))

        let attributedSettlement
            (ctx: HostToolContext)
            (durable: AgentJournal)
            (sessionId: SessionId)
            (providerRun: ProviderRunIdentity)
            (toolCallId: ToolCallId)
            logicalRunId
            authorityRoot
            (messages: SessionMessage list)
            : Task<DeliverySettlement option> =
            match messageAttribution ctx providerRun messages with
            | None -> Task.FromResult None
            | Some(formalText, physical) ->
                task {
                    // The lifecycle work is read only for a
                    // call that already has its attribution:
                    // the work journal is a durable read and
                    // must not be paid for calls that never
                    // get a witness.
                    let! work = runtime.LifecycleWorkFor ctx.SessionId

                    return
                        Some(
                            witnessSettlement
                                durable
                                sessionId
                                providerRun
                                toolCallId
                                logicalRunId
                                authorityRoot
                                physical
                                formalText
                                work
                        )
                }

        let loadedSettlement
            (ctx: HostToolContext)
            (durable: AgentJournal)
            (sessionId: SessionId)
            (providerRun: ProviderRunIdentity)
            (toolCallId: ToolCallId)
            (loadMessages: unit -> Task<Result<SessionMessage list, 'error>>)
            logicalRunId
            authorityRoot
            : Task<DeliverySettlement option> =
            task {
                let! loaded = loadMessages ()

                match loaded with
                | Error err -> return Some(DeliverySettlement.ContextUnavailable(sprintf "%A" err))
                | Ok messages ->
                    return!
                        attributedSettlement
                            ctx
                            durable
                            sessionId
                            providerRun
                            toolCallId
                            logicalRunId
                            authorityRoot
                            messages
            }

        /// The delivery settlement of one js-* call: the host's attribution of
        /// the call and the durable place its witness goes.
        ///
        /// Every field is read from the source the read side reads. A durable
        /// host whose call cannot be attributed refuses the delivery: no
        /// witness is invented, and a process-live cell alone must never
        /// present a return the caller's join cannot receive. Only a host
        /// without a journal is the harness case and keeps the cell-only
        /// behaviour.
        let unwrapAttributed (attributed: DeliverySettlement option) : DeliverySettlement option =
            match attributed with
            | Some settlement -> Some settlement
            | None ->
                Some(
                    UnattributedDelivery
                        "the delivering assistant message carries no prose or physical input to own the witness"
                )

        let settlementFromSnapshot
            (ctx: HostToolContext)
            (durable: AgentJournal)
            (snapshotPort: ISessionSnapshotPort)
            (providerRun: ProviderRunIdentity)
            (toolCallId: ToolCallId)
            : Task<DeliverySettlement option> =
            let sessionId = SessionId.create ctx.SessionId

            Wanxiangshu.Interaction.Authority.PromptAuthorityProjectionQueries.activeProfile
                sessionId
                (AgentJournal.snapshot durable).AgentProjections
            |> Option.map (fun profile -> profile.LogicalRunId, profile.AuthorityRootUserMessageId)
            |> Option.filter (fun (logicalRunId, authorityRoot) ->
                not (
                    String.IsNullOrWhiteSpace(LogicalRunId.value logicalRunId)
                    || String.IsNullOrWhiteSpace(AuthorityRootUserMessageId.value authorityRoot)
                ))
            |> Option.map (fun (logicalRunId, authorityRoot) ->
                task {
                    let! attributed =
                        loadedSettlement
                            ctx
                            durable
                            sessionId
                            providerRun
                            toolCallId
                            (fun () -> snapshotPort.GetMessages sessionId)
                            logicalRunId
                            authorityRoot

                    return unwrapAttributed attributed
                })
            |> Option.defaultValue (
                Task.FromResult(
                    Some(UnattributedDelivery "the delivery call has no active logical run or authority root")
                )
            )

        let settlementOfDurable (ctx: HostToolContext) (durable: AgentJournal) : Task<DeliverySettlement option> =
            match runtime.Snapshot, ctx.ProviderRunId, ctx.ToolCallId with
            | Some snapshotPort, Some providerRun, Some toolCallId ->
                settlementFromSnapshot ctx durable snapshotPort providerRun toolCallId
            | _ ->
                Task.FromResult(
                    Some(
                        UnattributedDelivery
                            "the delivery call has no provider run, tool call or host snapshot to be attributed"
                    )
                )

        let settlementOf (ctx: HostToolContext) : Task<DeliverySettlement option> =
            match journal with
            | None -> Task.FromResult None
            | Some durable -> settlementOfDurable ctx durable

        let scopeProgressEpoch (ctx: HostToolContext) =
            journal
            |> Option.bind (fun durable ->
                AgentProjection.tryFind (SessionId.create ctx.SessionId) (AgentJournal.snapshot durable).AgentProjections)
            |> Option.bind (fun session -> session.PrefixEpoch)
            |> Option.map (fun epoch -> string (Set.count epoch.ReanchoredScopes))
            |> Option.defaultValue "0"

        let generatedJsSpecFor (role: Role) =
            JsToolGenerator.generate (string role) (OfficeCapability.permissions role) jsProse
            |> Option.map (fun surface ->
                let reviewAllowed (ctx: HostToolContext) : Task<bool> =
                    task {
                        let! work = runtime.LifecycleWorkFor ctx.SessionId
                        let! frozen = runtime.IsRetirementFrozen ctx.SessionId

                        return
                            (work
                             |> Option.forall (fun current ->
                                 current.ActiveId.IsSome
                                 && not current.Reviewed
                                 && current.BlockerDigest.IsNone))
                            && not frozen
                    }

                // 每次写入重新读取当前任期，不让已保存的成员越过接力边界。
                let planWriteCheck (ctx: HostToolContext) : bool =
                    planSourceWriteAllowed (runtime.IsWaitingForUser ctx.SessionId)
                    && (cachedGitCommonDir
                        |> Option.bind (fun root ->
                            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected root ctx.SessionId)
                        |> Option.exists (fun work -> work.ActiveId.IsSome && work.Reviewed))

                let spec =
                    let createSpec =
                        if role = Role.Manager then
                            JsToolSpec.createWithReviewCheck reviewAllowed
                        elif role = Role.Plan then
                            JsToolSpec.createWithPlanWriteCheck planWriteCheck
                        else
                            JsToolSpec.create

                    createSpec
                        factory
                        surface
                        (defaultArg workspaceDirectory "")
                        jsTransactionPersistence
                        jsLifecyclePersistence
                        None
                        jsRootOf
                        settlementOf
                        scopeProgressEpoch

                { spec with
                    Execute =
                        fun args ctx ->
                            let envelopeOk (fields: (string * obj) list) : obj = createObj (("ok" ==> true) :: fields)

                            let refusal (code: string) : obj =
                                createObj [ "ok" ==> false; "code" ==> code ]

                            let refusalWithReason (code: string) (reason: string) : obj =
                                createObj [ "ok" ==> false; "code" ==> code; "reason" ==> reason ]

                            let envelopeOrRefusal (fields: Result<(string * obj) list, string>) : obj =
                                match fields with
                                | Ok fields -> envelopeOk fields
                                | Error code -> refusal code

                            let placedFields (placed: ForkTool.PlacementReceipt) : (string * obj) list =
                                [ "name" ==> placed.Name
                                  "agentId" ==> placed.AgentId
                                  "outcome" ==> placed.Outcome ]

                            let placedDelegate
                                (execute: _ -> Task<Result<ForkTool.PlacementReceipt, string>>)
                                (payload: obj)
                                : Task<obj> =
                                task {
                                    let! receipt = execute (ToolHostCodec.argumentsOfRaw payload)
                                    return receipt |> Result.map placedFields |> envelopeOrRefusal
                                }

                            let devopsAnswer (outcome: Result<AskResult, string>) : obj =
                                match outcome with
                                | Ok answer ->
                                    envelopeOk
                                        [ "kind" ==> answer.Kind
                                          "status" ==> answer.Status
                                          "body" ==> (ReturnedMaterial.body answer.View |> String.concat "\n")
                                          "returned" ==> ReturnedMaterial.entries answer.View ]
                                | Error reason ->
                                    refusalWithReason "devops-failed" (sprintf "devops failed: %s" reason)

                            let askDevops (payload: obj) (gitCommonDir: string) : Task<obj> =
                                task {
                                    let intentText = devopsIntentText payload
                                    let intent = LlmFacing.instruction intentText |> LlmFacing.render

                                    let delegateTo = devopsDelegateOf syncDelegateRuntime ctx

                                    let! outcome =
                                        Wanxiangshu.Mission.Planning.PlanTools.executeAsk
                                            gitCommonDir
                                            ctx.SessionId
                                            intent
                                            delegateTo

                                    return devopsAnswer outcome
                                }

                            let devopsDelegate (payload: obj) : Task<obj> =
                                task {
                                    if not (devopsDispatchAllowed (runtime.IsWaitingForUser ctx.SessionId)) then
                                        return
                                            refusalWithReason
                                                "devops-refused"
                                                "a question is waiting for the user"
                                    else
                                        let! gitCommonDir = askGitCommonDir workspaceDirectory

                                        if String.IsNullOrWhiteSpace gitCommonDir then
                                            return
                                                refusalWithReason
                                                    "devops-failed"
                                                    "devops failed: git common directory is missing"
                                        else
                                            return! askDevops payload gitCommonDir
                                }

                            // The commit-side fact of a delivery: the live cell
                            // carries this call's exact fence, so THIS call
                            // established the delivery and the ledger may
                            // record it. A cell of another fence, or no cell
                            // at all, is no delivery of this call.
                            let registerOwnDelivery () =
                                match ctx.ProviderRunId, ctx.ToolCallId with
                                | Some providerRun, Some toolCallId when
                                    JsDeliveredValues.peekFence ctx.SessionId
                                    |> Option.exists (fun fence ->
                                        fence.ProviderRun = providerRun && fence.ToolCallId = toolCallId)
                                    ->
                                    runtime.RegisterEstablishedDelivery(
                                        SessionId.create ctx.SessionId,
                                        providerRun,
                                        toolCallId
                                    )
                                | _ -> ()

                            let host: JsDelegationHost =
                                { Delegate =
                                    fun name payload ->
                                        task {
                                            match name with
                                            | "fork"
                                            | "commission" ->
                                                return! placedDelegate (ForkTool.executeReceipt runtime ctx) payload
                                            | "resume" ->
                                                return!
                                                    placedDelegate (ForkTool.executeResumeReceipt runtime ctx) payload
                                            | "devops" -> return! devopsDelegate payload
                                            | _ -> return refusal "unknown-delegate"
                                        }
                                  Fission =
                                    fun payload ->
                                        task {
                                            // The fission payload is built inside the JS sandbox
                                            // realm, so its prototype is that realm's
                                            // `Object.prototype` — the host's plain-record check
                                            // compares realms and would drop every field. The
                                            // payload is copied into a host-realm plain record
                                            // first, so `charge`, `lanes` and `prompts` survive.
                                            // Fission is not a delegation: it carries no report
                                            // material, so nothing is appended to the copy.
                                            let raw = plainRecordCopy payload

                                            let! receipt =
                                                FissionTool.executeReceipt
                                                    runtime
                                                    ctx
                                                    (ToolHostCodec.argumentsOfRaw raw)

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
                                  Review = fun findings note -> reviewMember runtime ctx findings note }

                            JsDelegationBridge.install ctx.SessionId host

                            JsDelegationBridge.installAsk ctx.SessionId (fun question ->
                                runtime.AskUser(ctx, question))

                            task {
                                do! ensureStateRoot workspaceDirectory

                                try
                                    let! outcome = spec.Execute args ctx

                                    registerOwnDelivery ()

                                    return outcome
                                finally
                                    JsDelegationBridge.clear ctx.SessionId
                            } })

        let generatedJsSpecs () =
            [ for role in Roles.all do
                  yield! generatedJsSpecFor role |> Option.toList ]

        // Join, horizon and fission stay registered. Lifecycle deliver does not.
        let lifecycleWorkOf
            (runtime: ToolRuntimeScope)
            (sessionId: string)
            : Task<Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option> =
            runtime.LifecycleWorkFor sessionId


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

        let negotiateSpec: ToolSpec =
            { Name = "negotiate"
              Description =
                "Ask your delegator clarifying questions. The first call is required before resetScope initialization; once it is accepted, negotiation is complete and resetScope is available immediately. Later calls are optional and can happen at any time. Pass all questions together in questions (plural). Acceptance completes the negotiation and the timeline continues; do not wait for an answer. If the delegator's answer is obtained, it may arrive later as a user steer; it never blocks resetScope or ongoing work."
              Arguments =
                [ "questions",
                  ToolHostCodec.stringSchemaDescribed
                      "Ask all clarifying questions in this single argument, verbosely enough to avoid ambiguity. Several related questions are welcome."
                      factory ]
              Execute = fun args ctx -> Task.FromResult(runtime.Negotiate(ctx.SessionId, args.Text "questions")) }

        let previousScopeSpec: ToolSpec =
            { Name = "askPreviousScope"
              Description =
                "Ask the immediately preceding Scope concrete clarifying questions using the single questions argument. Accepted questions run asynchronously; the answer arrives as an agent-attributed steer. Follow-up questions are supported."
              Arguments =
                [ "questions",
                  ToolHostCodec.stringSchemaDescribed
                      "Ask your previous Scope questions based on its handoff and the current objective. Group related questions in one call."
                      factory ]
              Execute = fun args ctx -> runtime.AskPreviousScope(ctx, args.Text "questions") }

        let baseSpecs =
            [ yield negotiateSpec
              yield previousScopeSpec
              yield Wanxiangshu.Mission.Work.OpenCode.ReviewShell.spec factory runtime
              yield JoinTool.spec runtime

              let horizonContext: HorizonTool.HorizonRuntimeContext =
                  { RuntimeFor = runtime.RuntimeFor
                    LogicalOwnerFor = runtime.LogicalOwnerFor
                    Journal = runtime.Journal
                    EnsureWorkDevOpsBound = runtime.EnsureWorkDevOpsBound }

              yield HorizonTool.spec horizonContext
              yield FissionTool.spec factory runtime
              yield BashHoneypotTool.spec
              yield AssumeTool.spec factory
              yield ResetScopeTool.spec factory runtime.Journal runtime.Snapshot

              yield! casebookToolSpecs
              yield! generatedJsSpecs () ]

        let gateFacts: ToolGateFacts =
            { RoleFor = (fun (ctx: HostToolContext) -> runtime.RoleFor ctx)
              EnsureRoleFor = (fun (ctx: HostToolContext) -> runtime.EnsureRoleFor ctx)
              ManagerFactsFor = (fun sessionId -> runtime.ManagerCapabilityFactsFor sessionId)
              IsRetirementFrozen = (fun sessionId -> runtime.IsRetirementFrozen sessionId)
              IsWaitingForUser = runtime.IsWaitingForUser
              WaitingQuestionFenceFor = runtime.WaitingQuestionFenceFor
              BeginToolExecution = beginToolExecution
              EndToolExecution = endToolExecution
              RecordedDeliverReplay = recordedDeliverReplay }

        let gateExecute (spec: Wanxiangshu.OpenCode.ToolSpec) =
            let managerPermission =
                match ManagerReviewTools.requiredPermissions spec.Name with
                | Some perms -> perms |> Seq.tryHead
                | None -> StaticTools.permissionOfToolName Role.Manager spec.Name

            let gated: GatedTool<HostToolArguments> =
                { Name = spec.Name
                  Execute = spec.Execute }

            fun args (ctx: HostToolContext) -> ToolExecuteGate.execute gateFacts gated managerPermission args ctx

        let specs =
            baseSpecs |> List.map (fun spec -> { spec with Execute = gateExecute spec })

        { Tools = ToolHostCodec.registry factory specs
          Runtime = runtime }
