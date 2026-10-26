namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Context.Scope
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Advisor
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Git
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Manager
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Investigation.WarmStart

open Wanxiangshu.Host
open Wanxiangshu.Change
open Wanxiangshu.Change.Host
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission.OpenCode
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Git
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Repository.Investigation.Semble
open Wanxiangshu.Repository.Investigation.WarmStart
open Wanxiangshu.Resources

open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process
open Wanxiangshu.Context.Companion
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

open Wanxiangshu.Participant.ManagedAgents
open Wanxiangshu.Repository.Knowledge.Casebook
open Wanxiangshu.Repository.Knowledge.Casebook.OpenCode
open Wanxiangshu.OpenCode.Host
open PluginHostInterop

module PluginHooks =

    [<Import("AdvisorFlow", "wanxiangli/scope-progress")>]
    let private createAdvisorFlow: obj = jsNative

    [<Import("advisorClaim", "wanxiangli/scope-progress")>]
    let private advisorClaim: obj = jsNative

    [<Import("advisorFinish", "wanxiangli/scope-progress")>]
    let private advisorFinish: obj = jsNative

    [<Import("advisorStageBegin", "wanxiangli/scope-progress")>]
    let private advisorStageBegin: obj = jsNative

    [<Import("advisorStageConfirm", "wanxiangli/scope-progress")>]
    let private advisorStageConfirm: obj = jsNative

    [<Import("advisorPending", "wanxiangli/scope-progress")>]
    let private advisorPending: obj = jsNative

    [<Import("SqliteJournal", "wanxiangli/eventstore")>]
    let private advisorJournalConstructor: obj = jsNative

    [<Import("createRequire", "node:module")>]
    let private createRequire (url: string) : obj = jsNative

    [<Emit("import.meta.url")>]
    let private moduleUrl: string = jsNative

    [<Emit("typeof globalThis.Bun !== 'undefined'")>]
    let private runningOnBun () : bool = jsNative

    [<Emit("$0($1)")>]
    let private requireModule (_require: obj) (_name: string) : obj = jsNative

    [<Import("join", "node:path")>]
    let private joinPath (left: string) (right: string) : string = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (directory: string) (options: obj) : unit = jsNative

    [<Import("scopeProgress", "wanxiangli/scope-progress")>]
    let private scopeProgress: obj = jsNative

    [<Import("advisorProcessQueue", "wanxiangli/scope-progress")>]
    let private advisorProcessQueue: obj = jsNative

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    [<Import("ProviderCalibration", "wanxiangli/provider-calibration")>]
    let private calibration: obj = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwnProperty (target: obj) (prop: string) : bool = jsNative

    /// Host hook surface: chat / transform / config / compaction / text /
    /// tool hooks plus event + dispose, and the optional client tool module.
    let private reportOnError (report: 'e -> unit) (result: Result<unit, 'e>) : unit =
        match result with
        | Ok() -> ()
        | Error reason -> report reason

    /// The admission ledger answers whether a failed append releases the call
    /// for a real retry; only a release forgets the in-flight append.
    let private forgetWhenReleased (pending: Dictionary<'K, 'V>) (key: 'K) (outcome: string) : unit =
        match outcome with
        | "released" -> pending.Remove(key) |> ignore
        | _ -> ()

    /// LC-017: the exact (provider run, tool call) fence a replayed ask/deliver carries.
    let private replayFenceOf toolInput =
        let ctx = ToolHostCodec.decodeContext toolInput

        match ctx.ProviderRunId, ctx.ToolCallId with
        | Some run, Some call -> Some(run, call)
        | _ -> None


    let private negotiationSdk (session: obj) (methodName: string) (payload: obj) : Task<obj> =
        let action = emitJsExpr (session, methodName) "$0[$1]"

        if isNull action then
            failwith ("session." + methodName + " unavailable")
        else
            emitJsExpr (session, action, payload) "$1.call($0, $2)" |> unbox<Task<obj>>

    let private negotiationResponse (reply: obj) =
        if isNull reply then null
        elif isNull reply?data then reply
        else reply?data

    let private negotiationSessionPayload (sessionId: string) (body: (string * obj) list) =
        let fields =
            [ "sessionID", box sessionId; "path", box (createObj [ "id", box sessionId ]) ]

        createObj (fields @ [ "body", box (createObj body) ] @ body)

    let private negotiationResultText (response: obj) =
        let body = negotiationResponse response

        let parts =
            if isNull body || isNull body?parts then
                [||]
            else
                unbox<obj array> body?parts

        parts
        |> Array.filter (fun p -> not (isNull p) && emitJsExpr p "$0?.type === 'text'")
        |> Array.map (fun part -> if isNull part?text then "" else string part?text)
        |> String.concat "\n"

    /// Temporary snapshot dialogue: the harshest tool prohibition rides with
    /// every question, and a tool call earns the bash-honeypot-class denial.
    let private snapshotForbidText (sessionText: string) =
        ProviderProse.render
            (ProviderLanguageBinding.forSessionText sessionText)
            "delegation/snapshot-dialogue/forbid"
            Map.empty

    let private snapshotDenialText (sessionText: string) =
        ProviderProse.render
            (ProviderLanguageBinding.forSessionText sessionText)
            "delegation/snapshot-dialogue/denial"
            Map.empty

    let private snapshotPromptText (sessionText: string) (questions: string) =
        questions.TrimEnd() + "\n\n" + snapshotForbidText sessionText

    let private negotiationPromptText (delegator: string) (questions: string) =
        snapshotPromptText delegator questions

    let private previousScopePromptText (recipient: string) (questions: string) =
        snapshotPromptText recipient questions

    [<RequireQualifiedAccess>]
    type private TemporaryAnswer =
        | Replied of string
        | Retry
        | Failed of string

    let private classifyTemporaryAnswer (text: string) (blocked: bool) =
        if blocked then TemporaryAnswer.Retry
        elif String.IsNullOrWhiteSpace text then
            TemporaryAnswer.Failed "temporary consultation completed without assistant prose"
        else TemporaryAnswer.Replied text

    // An SDK error without an observed tool-call veto is a transport/admission
    // failure, never an incomplete reply. An independently observed tool veto
    // can still use its bounded clarification retry.
    let private classifyTemporaryFailure (ex: exn) (blocked: bool) =
        if blocked then TemporaryAnswer.Retry
        else TemporaryAnswer.Failed ex.Message

    let private temporarySdkError (result: obj) =
        let body = negotiationResponse result
        let raw =
            if not (isNull result) && not (isNull result?error) then result?error
            elif not (isNull body) && not (isNull body?error) then body?error
            elif not (isNull body) && not (isNull body?info) then body?info?error
            else null
        if isNull raw then None
        elif isNull raw?message then Some(string raw)
        else Some(string raw?message)

    let private temporaryPromptPayload (tempId: string) (agentName: string) (model: obj) (prose: string) =
        let parts = box [| createObj [ "type", box "text"; "text", box prose ] |]
        let body = [ "agent", box agentName; "parts", parts ]

        let physicalModel =
            if isNull model then
                null
            else
                createObj [ "providerID", model?providerID; "modelID", model?modelID ]

        let request =
            if isNull model then
                body
            elif isNull model?variant then
                body @ [ "model", physicalModel ]
            else
                body @ [ "model", physicalModel; "variant", model?variant ]

        negotiationSessionPayload tempId request

    let private classifySdkTemporaryAnswer (result: obj) (blocked: bool) =
        match temporarySdkError result with
        | Some reason -> TemporaryAnswer.Failed reason
        | None -> classifyTemporaryAnswer (negotiationResultText result) blocked

    let private tryTemporaryAnswer (session: obj) (tempId: string) (payload: obj) : Task<TemporaryAnswer> =
        task {
            let before = PluginTransforms.negotiationToolViolationCount tempId

            try
                let! result = negotiationSdk session "prompt" payload
                let blocked = PluginTransforms.negotiationToolViolationCount tempId > before
                return classifySdkTemporaryAnswer result blocked
            with ex ->
                let blocked = PluginTransforms.negotiationToolViolationCount tempId > before
                return classifyTemporaryFailure ex blocked
        }

    let private runTemporaryQuestion
        (session: obj)
        (tempId: string)
        (agentName: string)
        (model: obj)
        (firstPrompt: string)
        : Task<Result<string, string>> =
        // The temporary projection keeps only the latest user question, so the
        // retry must restate it behind the denial.
        let retryText = snapshotDenialText tempId + "\n\n" + firstPrompt

        let rec run attempt prose =
            if not (PluginTransforms.negotiationIsTemporary tempId) then
                Task.FromResult(Error "temporary read-only consultation was cancelled")
            elif attempt > 2 then
                Task.FromResult(
                    Error "read-only consultation exhausted two bounded retries after tool violations or empty answers"
                )
            else
                runCandidate attempt prose

        and runCandidate attempt prose =
            task {
                let payload = temporaryPromptPayload tempId agentName model prose
                let! result = tryTemporaryAnswer session tempId payload

                match result with
                | TemporaryAnswer.Replied text -> return Ok text
                | TemporaryAnswer.Retry -> return! run (attempt + 1) retryText
                | TemporaryAnswer.Failed error -> return Error error
            }

        run 0 firstPrompt

    let private temporarySessionId (created: obj) : Result<string, string> =
        let data = negotiationResponse created
        let value = if isNull data || isNull data?id then "" else string data?id

        if String.IsNullOrWhiteSpace value then
            Error "temporary session create yielded no identity"
        else
            Ok value

    let private createTemporarySession (session: obj) (recipient: string) : Task<Result<string, string>> =
        task {
            let options = createObj [ "parentID", box recipient; "title", box "negotiation" ]

            try
                let! created =
                    negotiationSdk session "create" (createObj [ "body", options; "parentID", box recipient ])

                return temporarySessionId created
            with ex ->
                return Error ex.Message
        }

    let private deleteTemporarySession (session: obj) (tempId: string) : Task<unit> =
        task {
            try
                let! _ = negotiationSdk session "delete" (negotiationSessionPayload tempId [])
                return ()
            with _ ->
                return ()
        }

    [<RequireQualifiedAccess>]
    type private ConsultationOrigin =
        | Delegator of string
        | Previous of string * obj

    let private runLinkedTemporary
        (session: obj)
        (origin: ConsultationOrigin)
        (tempId: string)
        (agentName: string)
        (promptText: string)
        : Task<Result<string, string>> =
        task {
            let linked, model =
                match origin with
                | ConsultationOrigin.Delegator delegator ->
                    PluginTransforms.negotiationStartTemporary tempId delegator,
                    PluginTransforms.negotiationModelFor delegator
                | ConsultationOrigin.Previous (source, metadata) ->
                    PluginTransforms.consultationStartPrevious tempId source metadata,
                    (if isNull metadata then null else metadata?model)

            let questionRun =
                match linked with
                | Error reason -> Task.FromResult(Error reason)
                | Ok() -> runTemporaryQuestion session tempId agentName model promptText

            try
                return! questionRun
            finally
                PluginTransforms.negotiationFinishTemporary tempId
        // Physical session cleanup is deliberately performed after the runner by createAndRunTemporary.
        }

    let private createAndRunTemporary
        (session: obj)
        (origin: ConsultationOrigin)
        (recipient: string)
        (agentName: string)
        (promptText: string)
        : Task<Result<string, string>> =
        task {
            let! created = createTemporarySession session recipient

            match created with
            | Error reason -> return Error reason
            | Ok tempId ->
                let! result = runLinkedTemporary session origin tempId agentName promptText
                do! deleteTemporarySession session tempId
                return result
        }

    /// The Host temp session contains only the question. Provider transform
    /// inserts the immutable delegator wire before the LLM request.
    let private negotiateBtw
        (client: obj)
        (roleFor: SessionId -> Role option)
        (delegator: string)
        (recipient: string)
        (questions: string)
        : Task<Result<string, string>> =
        let session = if isNull client then null else client?session

        match roleFor (SessionId.create delegator) with
        | None -> Task.FromResult(Error "delegator has no verified model agent identity")
        | Some _ when isNull session -> Task.FromResult(Error "OpenCode SDK session transport unavailable")
        | Some role ->
            createAndRunTemporary
                session
                (ConsultationOrigin.Delegator delegator)
                recipient
                (ManagedAgent.nameOf role)
                (negotiationPromptText delegator questions)

    let private consultPreviousBtw
        (client: obj)
        (roleFor: SessionId -> Role option)
        (recipient: string)
        (snapshot: obj)
        (questions: string)
        : Task<Result<string, string>> =
        let session = if isNull client then null else client?session

        match roleFor (SessionId.create recipient) with
        | None -> Task.FromResult(Error "previous Scope consultation has no verified model agent identity")
        | Some _ when isNull session -> Task.FromResult(Error "OpenCode SDK session transport unavailable")
        | Some role ->
            createAndRunTemporary
                session
                (ConsultationOrigin.Previous(recipient, snapshot))
                recipient
                (ManagedAgent.nameOf role)
                (previousScopePromptText recipient questions)

    let create
        (boot: PluginBoot.Boot)
        (host: PluginHostWiring.Host)
        (transform: obj -> obj -> Task<unit>)
        (product: Wanxiangshu.Host.Ports.ProductHooks)
        (liveConfig: LiveHostConfig)
        : Task<obj> =
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

            // DSL-MUTABLE: resource — durable progress journal factory resolved once at activation
            let mutable progressJournalFactory: obj option = None

            // The JS todowrite port is synchronous. Resolve the physical store once
            // at plugin activation, then open a fresh transactional journal per call.
            // Progress and Advisor use different streams in the same SQLite database.
            match workspaceDirectory with
            | Some workspace ->
                let! root = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                let directory = joinPath root "wanxiangshu"
                mkdirSync directory (createObj [ "recursive" ==> true ])
                let target = joinPath directory "advisor-process.sqlite"
                let makeJournal = Func<string, obj>(fun _session ->
                    let loader = createRequire moduleUrl
                    let sqlite = requireModule loader (if runningOnBun () then "bun:sqlite" else "node:sqlite")
                    let connection =
                        if runningOnBun () then createNew sqlite?Database target
                        else createNew sqlite?DatabaseSync target
                    try createNew advisorJournalConstructor connection
                    with _ ->
                        connection?close() |> ignore
                        reraise ())
                progressJournalFactory <- Some(box makeJournal)
                scopeProgress?configureJournal(makeJournal) |> ignore
            | None ->
                let unavailable = Func<string, obj>(fun _ -> failwith "ScopeProgress requires a verified durable workspace")
                scopeProgress?configureJournal(unavailable) |> ignore

            let bindProgressJournal (sessionId: string) =
                match progressJournalFactory with
                | Some factory when not (String.IsNullOrWhiteSpace sessionId) ->
                    scopeProgress?bindSessionJournal(sessionId, factory) |> ignore
                | None when not (String.IsNullOrWhiteSpace sessionId) ->
                    scopeProgress?unbindSessionJournal(sessionId) |> ignore
                | _ -> ()

            let roleFor (sessionId: SessionId) =
                journal
                |> Option.bind (fun durable ->
                    let projections = (AgentJournal.snapshot durable).AgentProjections

                    PromptAuthorityProjectionQueries.activeProfile sessionId projections
                    |> Option.orElseWith (fun () ->
                        PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId projections))
                |> Option.map (fun profile -> profile.CanonicalRole)



            // work-031: the live stage is the plan milestone. The planning/sN
            // directories are resource paths. A tenure with no active id, and
            // any other milestone, appends nothing.
            let stagePathFor (stage: string) (activeId: string option) : string option =
                match stage, activeId with
                | "事实与取舍", Some _ -> Some "planning/s1"
                | "理解与路径", Some _ -> Some "planning/s2"
                | "流程与伪代码", Some _ -> Some "planning/s3"
                | _ -> None

            let planStagePath root sessionId =
                match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected root sessionId with
                | None -> Some "planning/s1"
                | Some work -> stagePathFor work.Stage work.ActiveId

            let planStageFor (sessionId: SessionId) : Task<(string * string) option> =
                task {
                    match roleFor sessionId, workspaceDirectory with
                    | Some Role.Plan, Some workspace ->
                        let! commonDir = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace

                        return
                            planStagePath commonDir (SessionId.value sessionId)
                            |> Option.map (fun stagePath ->
                                let draftPath =
                                    Wanxiangshu.Mission.Planning.PlanPath.resolvePlanPath
                                        (joinPath commonDir "wanxiangshu")
                                        (SessionId.value sessionId)
                                    |> Result.defaultWith invalidOp

                                (stagePath, draftPath))
                    | _ -> return None
                }

            let transformOrdinarySystem =
                ProviderSystemTransform.createWith roleFor planStageFor

            let systemTransform (input: obj) (output: obj) : Task<unit> =
                task {
                    let projected = PluginTransforms.negotiationProjectSystem input output

                    if not projected then
                        do! transformOrdinarySystem input output
                        PluginTransforms.negotiationRememberSystem input output

                    let sid =
                        if isNull input?sessionID then
                            ""
                        else
                            string input?sessionID

                    if not projected && not (String.IsNullOrEmpty sid) then
                        outboundViewStore?recordSystem (sid, output?system) |> ignore
                }


            // CASE-003: typed capture at the tool boundary — shared
            // CasebookLifecycle.collector; marker flag gates the after-hook.
            // Store IO stays out of SpikePlugin (unified-store dual-write gate).
            let casebookEnabled =
                match workspaceDirectory with
                | Some ws -> CasebookFeature.isEnabled ws
                | None -> false

            // Programming-surface calls carrying a non-empty `view` argument establish

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

            let isToastAvailable () : bool =
                emitJsExpr client "typeof $0?.tui?.showToast === 'function'"

            let checkQuestionToastResult (response: obj) : Result<unit, string> =
                if not (isNull response) && not (isNull response?error) then
                    Error(string response?error)
                else
                    Ok()

            let postQuestionToastUnchecked (question: string) : Task<Result<unit, string>> =
                task {
                    let title =
                        ProviderProse.render
                            (ProviderLanguageBinding.readGlobalPreference ())
                            "tool/ask-title"
                            Map.empty

                    let! response =
                        emitJsExpr
                            (client, title, question)
                            "$0.tui.showToast({ body: { title: $1, message: $2, variant: 'info' } })"
                        |> unbox<Task<obj>>

                    return checkQuestionToastResult response
                }

            let runGuardedToast (question: string) : Task<Result<unit, string>> =
                task {
                    try
                        return! postQuestionToastUnchecked question
                    with ex ->
                        return Error ex.Message
                }

            let showQuestionToast (question: string) : Task<Result<unit, string>> =
                if not (isToastAvailable ()) then
                    Task.FromResult(Error "the Host cannot present a question to the user")
                else
                    runGuardedToast question

            let configureClient () : Task<ToolRegistration> =
                task {
                    let! toolModule = importToolModule ()

                    let onRunStarted =
                        Some(fun sessionId role directory -> wired.BindActiveRun sessionId role directory)

                    // A participant's return is the delivered material view.
                    let deliveredView =
                        Some(fun sessionId ->
                            Task.FromResult(
                                Wanxiangshu.Repository.Programming.Js.JsDeliveredValues.tryPeekMaterial sessionId
                            ))

                    let childViewForRun = fun _ _ -> Task.FromResult None

                    let! casebookToolSpecs =
                        match workspaceDirectory with
                        | Some ws ->
                            CasebookTools.buildSpecs (ToolHostCodec.factory toolModule) ws boot.CasebookSettlements
                        | None -> Task.FromResult []

                    let! toolRegistration =
                        toolHooks
                            toolModule
                            sessionPort
                            host.CausalWaitObserver
                            host.RootWorkspace
                            journal
                            workspaceDirectory
                            scope
                            wired.CurrentPhysicalUserMessage
                            onRunStarted
                            deliveredView
                            childViewForRun
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
                            userNotify

                    toolRegistration.Runtime.AttachQuestionNotifier(fun question -> showQuestionToast question)

                    toolRegistration.Runtime.AttachNegotiationTransport(
                        negotiateBtw client roleFor,
                        (fun recipient answer receipt ->
                            toolRegistration.Runtime.SendNegotiationSteer(recipient, answer, receipt))
                    )

                    toolRegistration.Runtime.AttachPreviousScopeTransport(consultPreviousBtw client roleFor)

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

            let scopeEpochForAdvisor (sessionId: string) =
                let durableEpoch =
                    journal
                    |> Option.bind (fun durable ->
                        AgentProjection.tryFind (SessionId.create sessionId) (AgentJournal.snapshot durable).AgentProjections)
                    |> Option.bind (fun session -> session.PrefixEpoch)
                    |> Option.map (fun epoch -> string (Set.count epoch.ReanchoredScopes))
                match durableEpoch with
                | Some verified -> verified
                | None ->
                    let state: obj = scopeProgress?status(sessionId)
                    if not (isNull state?epoch) then string state?epoch
                    else
                        let originalSessionIsKnown =
                            journal
                            |> Option.bind (fun durable ->
                                AgentProjection.tryFind (SessionId.create sessionId) (AgentJournal.snapshot durable).AgentProjections)
                            |> Option.isSome
                        if originalSessionIsKnown then
                            let recovered: obj = scopeProgress?recordedEpoch(sessionId, "0")
                            if isNull recovered then "" else string recovered
                        else ""

            let createManagedAdvisor (registered: ToolRegistration) : obj =
                let runtime = registered.Runtime
                let proseResult (answer: Result<string, string>) =
                    match answer with
                    | Ok prose -> createObj [ "Ok" ==> true; "Prose" ==> prose; "Reason" ==> "" ]
                    | Error reason -> createObj [ "Ok" ==> false; "Prose" ==> ""; "Reason" ==> reason ]
                let steerResult (answer: Result<unit, string>) =
                    match answer with
                    | Ok () -> createObj [ "Ok" ==> true; "Prose" ==> ""; "Reason" ==> "" ]
                    | Error reason -> createObj [ "Ok" ==> false; "Prose" ==> ""; "Reason" ==> reason ]
                let workerRequest (session: string) (question: string) =
                    negotiateBtw client roleFor session session question
                let askWorker = Func<string, string, Task<obj>>(fun session question ->
                    task {
                        let! answered = workerRequest session question
                        return proseResult answered
                    })
                let askSeat = Func<string, string, Task<obj>>(fun session prompt ->
                    let deps: AdvisorSeatDeps =
                        { Sessions = runtime.Sessions
                          Snapshot = runtime.Snapshot
                          Journal = runtime.Journal
                          Directory = runtime.WorkspaceDirectory
                          Participant = ManagedAgent.nameOf Role.Advisor
                          BindDelegation = fun _ _ -> () }
                    task {
                        let! answered = AdvisorSeat.consult deps session prompt
                        return proseResult answered
                    })
                let sendSteer = Func<string, string, string, string, Task<obj>>(fun session epoch processId text ->
                    task {
                        let isCurrent () =
                            scopeEpochForAdvisor session = epoch
                        let! sent = runtime.SendAdvisorSteer(session, text, processId, isCurrent)
                        return steerResult sent
                    })
                let currentEpoch = Func<string, string>(scopeEpochForAdvisor)
                let languageFor = Func<string, string>(fun session ->
                    ProviderLanguage.label (ProviderLanguageBinding.forSessionText session))
                let withAdvisorJournal (action: obj -> obj) : Task<obj> =
                    task {
                        match runtime.WorkspaceDirectory with
                        | None ->
                            return createObj [ "ok" ==> false; "reason" ==> "Advisor persistence needs a durable workspace" ]
                        | Some workspace ->
                            try
                                let! root = Wanxiangshu.Persistence.Journal.RuntimePath.stateRoot workspace
                                let directory = joinPath root "wanxiangshu"
                                mkdirSync directory (createObj [ "recursive" ==> true ])
                                let target = joinPath directory "advisor-process.sqlite"
                                let loader = createRequire moduleUrl
                                let sqlite = requireModule loader (if runningOnBun () then "bun:sqlite" else "node:sqlite")
                                let connection =
                                    if runningOnBun () then createNew sqlite?Database target
                                    else createNew sqlite?DatabaseSync target
                                let journal =
                                    try createNew advisorJournalConstructor connection
                                    with error ->
                                        connection?close() |> ignore
                                        reraise ()
                                try
                                    return action journal
                                finally
                                    journal?close() |> ignore
                            with error ->
                                return createObj [ "ok" ==> false; "reason" ==> ("Advisor durable operation unavailable: " + error.Message) ]
                    }
                let claim = Func<string, string, string, string, Task<obj>>(fun session epoch report issue ->
                    withAdvisorJournal (fun journal ->
                        emitJsExpr (advisorClaim, journal, session, epoch, report, issue) "$0($1,$2,$3,$4,$5)"))
                let finish = Func<string, string, string array, string, Task<obj>>(fun session epoch reports outcome ->
                    withAdvisorJournal (fun journal ->
                        emitJsExpr (advisorFinish, journal, session, epoch, reports, outcome) "$0($1,$2,$3,$4,$5)"))
                let beginStage = Func<string, string, string, int, string, string array, Task<obj>>(fun session epoch processId phase question reports ->
                    withAdvisorJournal (fun journal ->
                        emitJsExpr (advisorStageBegin, journal, session, epoch, processId, phase, question, reports) "$0($1,$2,$3,$4,$5,$6,$7)"))
                let confirmStage = Func<string, string, string, int, string, Task<obj>>(fun session epoch processId phase prose ->
                    withAdvisorJournal (fun journal ->
                        emitJsExpr (advisorStageConfirm, journal, session, epoch, processId, phase, prose) "$0($1,$2,$3,$4,$5,$6)"))
                let pending = Func<string, string, Task<obj>>(fun session epoch ->
                    withAdvisorJournal (fun journal ->
                        emitJsExpr (advisorPending, journal, session, epoch) "$0($1,$2,$3)"))
                let ports = createObj [ "AskWorker" ==> askWorker
                                        "AskSeat" ==> askSeat
                                        "SendSteer" ==> sendSteer
                                        "CurrentEpoch" ==> currentEpoch
                                        "LanguageFor" ==> languageFor
                                        "Claim" ==> claim
                                        "Finish" ==> finish
                                        "BeginStage" ==> beginStage
                                        "ConfirmStage" ==> confirmStage
                                        "Pending" ==> pending ]
                emitJsExpr (createAdvisorFlow, advisorProcessQueue, ports) "$0($1,$2)"

            let advisorFlow: obj =
                toolRegistration |> Option.map createManagedAdvisor |> Option.defaultValue null

            let toolDefinition (_toolInput: obj) (_toolOutput: obj) = ()

            let toolField (toolInput: obj) (name: string) =
                if isNull toolInput || isNull toolInput?(name) then
                    ""
                else
                    string toolInput?(name)

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

            let toolTerminalObservation expectedTool rawInput =
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
                    String.Equals(tool, expectedTool, StringComparison.OrdinalIgnoreCase)
                    && (status = "completed" || status = "error")
                    ->
                    let providerRun =
                        firstFieldText part [ "messageID"; "messageId" ]
                        |> Option.map ProviderRunIdentity.create

                    Some(SessionId.value sessionId, ToolCallId.create callId, status, providerRun)
                | _ -> None

            /// 条款 6：中断未确认是呈现面的失败，不是事实面的失败。提问已经
            /// 提交（durable pending 仍在等待事实），宿主也已接受工具结果；
            /// 中断失败只记诊断——不崩、不抹掉已提交的事实，也不把已成立的
            /// 交付呈现为失败。
            let reportUserQuestionInterruptUnconfirmed (sessionId: string) (reason: string) =
                Diagnostic.emit
                    "ask-interrupt-not-confirmed"
                    [ "session_id", sessionId
                      "provider_error", reason
                      "result", "interrupt-not-confirmed" ]

            /// 条款 6：缺精确 provider run 身份时没有 attempt 围栏，就不该
            /// 中断（不得误停新输入或后任），只记诊断，不崩。
            let reportUserQuestionMissingProviderRun (sessionId: string) =
                Diagnostic.emit "ask-observation-missing-provider-run" [ "session_id", sessionId; "result", "unbound" ]

            let settleUserQuestion rawInput =
                task {
                    // `this.ask` inside a plan program records the same durable
                    // question under the plan tool's call id, so both the
                    // native ask tool and the plan programming tool complete
                    // as the interrupt boundary.
                    let observation =
                        toolTerminalObservation "ask" rawInput
                        |> Option.orElseWith (fun () ->
                            toolTerminalObservation
                                (Wanxiangshu.Repository.Programming.Js.JsToolGenerator.toolNameFor "plan")
                                rawInput)
                        |> Option.orElseWith (fun () -> toolTerminalObservation "review" rawInput)
                        |> Option.orElseWith (fun () ->
                            toolTerminalObservation
                                (Wanxiangshu.Repository.Programming.Js.JsToolGenerator.toolNameFor "manager")
                                rawInput)

                    match observation, toolRegistration with
                    | Some(sessionId, callId, "completed", Some providerRun), Some registration ->
                        // completed 工具部件是占位回执已被 Host 接受的因果边界；不靠定时器猜。
                        let! result =
                            registration.Runtime.InterruptForUserQuestion(
                                SessionId.create sessionId,
                                providerRun,
                                callId
                            )

                        result |> reportOnError (reportUserQuestionInterruptUnconfirmed sessionId)
                    | Some(sessionId, _, "completed", None), Some registration when
                        registration.Runtime.IsWaitingForUser sessionId
                        ->
                        reportUserQuestionMissingProviderRun sessionId
                    | _ -> ()
                }

            /// 条款 4/6：事件钩子只做观测登记——宿主已接受本次调用 + 精确
            /// attempt 的交付工具结果（呈现面）。提交面事实由交付发生的那一次
            /// 登记写入（它携带交付当时的物理输入），事件钩子不再新造物理围栏，
            /// 新输入因此不会被旧交付停止。物理中断由 transform 钩子的交付
            /// 裁决执行；交付已提交不因中断未确认而呈现为失败。
            let settlePostS3Deliver rawInput =
                task {
                    match toolTerminalObservation "deliver" rawInput, toolRegistration with
                    | Some(sessionId, callId, "completed", Some providerRun), Some registration ->
                        registration.Runtime.RecordDeliveryPresented(SessionId.create sessionId, providerRun, callId)
                    | Some(sessionId, _, "completed", None), Some registration when
                        registration.Runtime.HasEstablishedDelivery sessionId
                        ->
                        Diagnostic.emit
                            "post-s3-deliver-observation-missing-provider-run"
                            [ "session_id", sessionId; "result", "unbound" ]
                    | _ -> ()
                }

            let firstFieldText = firstFieldText

            let checkManagerReviewPermissions toolName toolInput =
                task {
                    let sessionId = toolField toolInput "sessionID"

                    let blockedNative =
                        toolName = "todowrite-for-engineer" || toolName = "todowrite-for-devops"

                    if roleFor (SessionId.create sessionId) = Some Role.Manager && blockedNative then
                        invalidOp (sprintf "Manager tool '%s' is refused on the review gate" toolName)
                }

            let hideReviewArguments owner toolName args =
                if ManagerReviewTools.isReviewTool toolName then
                    ManagerReviewContract.hideForCall owner args

            let hideProtocolArguments owner toolName args =
                try
                    hideReviewArguments owner toolName args
                with error ->
                    ManagerReviewContract.restore args
                    raise error

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

            let hiddenArgumentState owner toolName (toolOutput: obj) =
                let isReview = ManagerReviewTools.isReviewTool toolName

                if not isReview || isNull toolOutput || isNull toolOutput?args then
                    HiddenProtocolArguments.NotHidden
                else
                    hiddenReviewState owner isReview toolOutput?args

            let protocolArgumentsAreHidden owner toolName toolOutput =
                match hiddenArgumentState owner toolName toolOutput with
                | HiddenProtocolArguments.SameCall -> true
                | HiddenProtocolArguments.DifferentCallOrChangedArguments ->
                    invalidOp
                        "Invalid investigation estimate arguments: hidden arguments belong to another call or were changed before restoration"
                | HiddenProtocolArguments.NotHidden -> false

            // 隐藏前再核一次：本调用尚未隐藏且 args 可读才执行 hide。
            let hideProtocolArgumentsIfShown owner toolName (toolOutput: obj) =
                if
                    not (protocolArgumentsAreHidden owner toolName toolOutput)
                    && not (isNull toolOutput)
                    && not (isNull toolOutput?args)
                then
                    hideProtocolArguments owner toolName toolOutput?args

            let prepareNewProtocolArguments owner toolName toolInput toolOutput =
                task {
                    do! observeDelegatedToolEstimate toolInput
                    hideProtocolArgumentsIfShown owner toolName toolOutput
                }

            let beforeStagePrepareProtocolArguments toolName toolInput toolOutput =
                task {
                    let owner = argumentCallOwner toolName toolInput

                    if not (protocolArgumentsAreHidden owner toolName toolOutput) then
                        do! prepareNewProtocolArguments owner toolName toolInput toolOutput
                }

            let toolBefore (toolInput: obj) (toolOutput: obj) =
                task {
                    let sessionId = toolField toolInput "sessionID"
                    let toolName = toolField toolInput "tool"

                    if PluginTransforms.negotiationIsTemporary sessionId then
                        PluginTransforms.negotiationRecordToolViolation sessionId

                        invalidOp (snapshotDenialText sessionId)

                    if
                        toolName = "negotiate"
                        && (String.IsNullOrWhiteSpace sessionId
                            || not (sessionPort.IsManagedChild(SessionId.create sessionId)))
                    then
                        invalidOp
                            "negotiate is only available in delegated child or Advisor sessions, not user-facing sessions."

                    // capability-enforcement-002/007: a Host-declared external
                    // (MCP) tool holds no office capability, so it is advertised
                    // but fails closed here, before any stage.
                    match toolRegistration with
                    | Some registration -> do! ExternalToolAdmission.before registration.Runtime sessionId toolName
                    | None -> ()

                    match toolRegistration with
                    | Some registration when
                        registration.Runtime.IsWaitingForUser sessionId
                        && not (
                            // LC-017: 同一围栏的 ask/deliver 精确重放可穿越等待门。
                            // 其余工具在等待期间仍拒绝。
                            let isReplayExemptTool =
                                toolName = "deliver"
                                || toolName.StartsWith("todowrite-for-", StringComparison.OrdinalIgnoreCase)

                            let replayFence = replayFenceOf toolInput

                            isReplayExemptTool
                            && replayFence = registration.Runtime.WaitingQuestionFenceFor sessionId
                        )
                        ->
                        invalidOp (
                            ProviderProse.render
                                (ProviderLanguageBinding.forSessionText sessionId)
                                "tool/ask-waiting"
                                Map.empty
                        )
                    | _ -> ()

                    let actorSession = toolField toolInput "sessionID"
                    if roleFor (SessionId.create actorSession) = Some Role.Advisor then
                        invalidOp "The dedicated Advisor has no tools"
                    do! checkManagerReviewPermissions toolName toolInput

                    do! beforeStagePrepareProtocolArguments toolName toolInput toolOutput
                }

            let restoreArgumentsForTarget owner (target: obj) =
                if not (isNull target) && not (isNull target?args) then
                    ManagerReviewContract.restoreForCall owner target?args

            let afterStageRestoreProtocolArguments (toolInput: obj) (toolOutput: obj) =
                let toolName = toolField toolInput "tool"
                let owner = argumentCallOwner toolName toolInput

                restoreArgumentsForTarget owner toolInput
                restoreArgumentsForTarget owner toolOutput

            // after stage 2: 可选 casebook 观察。失败只发诊断，不改关键结果
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
                    afterStageCasebookObservation toolInput toolOutput
                }

            /// User-facing Host roots must not present negotiate to the LLM.
            /// A managed child (including Advisor) keeps its protocol tool even
            /// when it has the same office role as a user-facing root. The SDK
            /// user message owns the per-request tools map; role AgentConfig is
            /// not a sufficient session identity discriminator.
            let originalChatMessage = curriedHook wired.ChatMessageHook

            let requireUserMessageTools (message: obj) : obj =
                let tools = message?tools

                if isNull tools then
                    let created = createObj []
                    message?tools <- created
                    created
                else
                    tools

            let chatMessageWithNegotiationPolicy (input: obj) (output: obj) : Task<unit> =
                task {
                    let rawSession = if isNull input then null else input?sessionID
                    let temporary =
                        not (isNull rawSession)
                        && PluginTransforms.negotiationIsTemporary (string rawSession)
                    // The SDK-created readonly temporary session is not an admitted
                    // managed work execution. Its real provider tool schema remains
                    // untouched, and tool.execute.before independently denies tools.
                    if not temporary then
                        let pending: Task<unit> =
                            emitJsExpr (originalChatMessage, input, output) "$0($1, $2)"
                        do! pending

                    let managedChild =
                        not (isNull rawSession)
                        && not (String.IsNullOrWhiteSpace(string rawSession))
                        && sessionPort.IsManagedChild(SessionId.create (string rawSession))

                    if not temporary && not managedChild && not (isNull output) && not (isNull output?message) then
                        let tools = requireUserMessageTools output?message
                        tools?negotiate <- box false
                }

            let chatMessage =
                registeredHook HookKey.ChatMessage (curriedHook (box chatMessageWithNegotiationPolicy))

            let advisorIssue (sessionId: string) (receipt: obj) =
                let question =
                    match string receipt?trigger with
                    | "ResetDue" -> "host/advisor-question/reset-due"
                    | "Equal" -> "host/advisor-question/equal"
                    | _ -> "host/advisor-question/increase"
                ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) question Map.empty

            let observeAdvisorTask (sessionId: string) (started: Task<obj>) =
                task {
                    try
                        let! result = started
                        if not (isNull result?ok) && not (unbox<bool> result?ok) then
                            let reason = if isNull result?reason then "Advisor admission failed" else string result?reason
                            Diagnostic.emit "advisor-flow-failed" [ "session_id", sessionId; "reason", reason ]
                    with error ->
                        Diagnostic.emit "advisor-flow-failed" [ "session_id", sessionId; "reason", error.Message ]
                } |> ignore

            let dispatchAdvisor (sessionId: string) (epoch: string) (item: obj) =
                let receipt = item?result
                let id = epoch + ":" + string item?providerRunId
                let question = advisorIssue sessionId receipt
                if isNull advisorFlow then
                    Diagnostic.emit "advisor-flow-unavailable" [ "session_id", sessionId; "provider_run", id ]
                else
                    let started: Task<obj> = unbox (advisorFlow?trigger(sessionId, epoch, id, question))
                    observeAdvisorTask sessionId started

            let observeSettledProgress (sessionId: string) (epoch: string) (item: obj) =
                let receipt = item?result
                if not (isNull receipt?advisor) && unbox<bool> receipt?advisor then
                    dispatchAdvisor sessionId epoch item

            let settlePriorProgress (sessionId: string) =
                let durableScope = scopeEpochForAdvisor sessionId
                if not (String.IsNullOrWhiteSpace durableScope) then
                    scopeProgress?ensureEpoch(sessionId, durableScope) |> ignore
                let active: obj = scopeProgress?status(sessionId)
                if not (isNull active?ok) && not (unbox<bool> active?ok) then
                    failwith ("Cannot settle Provider round without reliable Scope progress: " + string active?reason)
                let epoch = if isNull active?epoch then "" else string active?epoch
                let settled: obj array =
                    if String.IsNullOrWhiteSpace epoch then [||]
                    else unbox (scopeProgress?settlePending(sessionId, epoch))
                settled |> Array.iter (observeSettledProgress sessionId epoch)

            let tryRecoverAdvisor (sessionId: string) =
                if not (isNull advisorFlow) && not (String.IsNullOrWhiteSpace sessionId) then
                    let epoch = scopeEpochForAdvisor sessionId
                    if not (String.IsNullOrWhiteSpace epoch) then
                        let resumed: Task<obj> = unbox (advisorFlow?resume(sessionId, epoch))
                        observeAdvisorTask sessionId resumed

            let rememberModelParams (input: obj) (output: obj) =
                let currentSession = if isNull input?sessionID then "" else string input?sessionID
                let temporary =
                    not (String.IsNullOrWhiteSpace currentSession)
                    && PluginTransforms.negotiationIsTemporary currentSession
                if not temporary then
                    if not (String.IsNullOrWhiteSpace currentSession) then
                        bindProgressJournal currentSession
                        settlePriorProgress currentSession
                    emitJsExpr (chatParams, input, output) "$0($1, $2)" |> ignore
                    PluginTransforms.negotiationRememberModel input
                    tryRecoverAdvisor currentSession
                let modelSessionId = unbox<string> input?sessionID

                if not temporary && not (String.IsNullOrEmpty modelSessionId) then
                    outboundViewStore?recordModel (modelSessionId, input?model) |> ignore

            let chatParamsRegistration =
                registeredHook HookKey.ChatParams (curriedHook (box rememberModelParams))

            let messagesTransform =
                registeredHook HookKey.MessagesTransform (curriedHook (box ownedTransform))

            let systemTransformRegistration =
                registeredHook HookKey.SystemTransform (pairedHook (box systemTransform))

            let syncHostLanguagePreference (config: obj) =
                let lang = config?language

                if not (isNull lang) then
                    ProviderLanguageBinding.setHostConfigPreference (string lang)
                    ProviderLanguageBinding.refreshGlobalLanguage ()

            let syncHostToolLimits (config: obj) =
                let toolOutput = config?tool_output

                if not (isNull toolOutput) then
                    Wanxiangshu.Host.Contract.ToolResultBound.setHostLimits toolOutput?max_lines toolOutput?max_bytes

            let configurePluginHost (config: obj) =
                if not (isNull config) then
                    config?snapshot <- box false
                    syncHostLanguagePreference config
                    ManagerConfig.configureManager config |> ignore
                    // The host just delivered its config: the shared holder (and
                    // with it the product's `IHostConfigPort`) now reads the
                    // live object instead of a stale snapshot.
                    liveConfig.Accept config

                    let settingGap =
                        Wanxiangshu.Composition.Plugin.ConfigPolicy.establish (
                            liveConfig :> Wanxiangshu.Host.Ports.IHostConfigPort
                        )
                        |> Option.bind Wanxiangshu.Composition.Plugin.ConfigPolicy.settingOfGap

                    scope.RecordCompactionSettingGap settingGap
                    syncHostToolLimits config

            let config = registeredHook HookKey.Config (unaryHook (box configurePluginHost))

            let sessionIdOfHook (input: obj) : SessionId =
                let idStr =
                    if isNull input || isNull input?sessionID then
                        "<unknown>"
                    else
                        unbox<string> input?sessionID

                SessionId.create idStr

            let existingContextOf (output: obj) : string array =
                if isNull output || isNull output?context then
                    [||]
                else
                    unbox<string array> output?context

            let writeOutputField (output: obj) (field: string) (value: obj) : unit =
                if not (isNull output) then
                    output?(field) <- value

            let appendCompactionContext (output: obj) (lines: string list) : unit =
                let combined = Array.append (existingContextOf output) (Array.ofList lines)
                writeOutputField output "context" (box combined)

            let onSessionCompacting (input: obj) (output: obj) : Task<unit> =
                task {
                    let sid = sessionIdOfHook input

                    Diagnostic.emit
                        "compaction_started"
                        [ "session_id", SessionId.value sid; "result", "cannot-refuse" ]

                    let! contribution = product.Compaction.OnCompacting sid

                    match contribution with
                    | Wanxiangshu.Host.Ports.CompactionContribution.Nothing -> ()
                    | Wanxiangshu.Host.Ports.CompactionContribution.Context lines ->
                        appendCompactionContext output lines
                    | Wanxiangshu.Host.Ports.CompactionContribution.ReplacePrompt prompt ->
                        writeOutputField output "prompt" (box prompt)
                }

            let onCompactionAutoContinue (input: obj) (output: obj) : Task<unit> =
                task {
                    let sid = sessionIdOfHook input
                    let! verdict = product.Compaction.AutoContinue sid

                    match verdict with
                    | Wanxiangshu.Host.Ports.ContinueVerdict.Allow -> writeOutputField output "enabled" (box true)
                    | Wanxiangshu.Host.Ports.ContinueVerdict.Deny _reason ->
                        writeOutputField output "enabled" (box false)
                }

            let sessionCompacting =
                registeredHook HookKey.SessionCompacting (pairedHook (box onSessionCompacting))

            let compactionAutoContinue =
                registeredHook HookKey.CompactionAutoContinue (pairedHook (box onCompactionAutoContinue))

            let toolDefinitionRegistration =
                registeredHook HookKey.ToolDefinition (pairedHook (box toolDefinition))

            let toolBeforeRegistration =
                registeredHook HookKey.ToolBefore (pairedHook (box toolBefore))

            let toolAfterRegistration =
                registeredHook HookKey.ToolAfter (pairedHook (box toolAfter))

            /// context-compression [001]: the provider's own billing for the
            /// session's latest assistant turn, read off the host's message
            /// events. It is a datum about the model's own state, never a
            /// capacity derivation on our side.
            let tokenUse (raw: obj) : (int * int) option =
                let info = if isNull raw?properties then null else raw?properties?info
                let tokens = if isNull info then null else info?tokens
                let cached = if isNull tokens then null else tokens?cache?read
                let uncached = if isNull tokens then null else tokens?input

                if
                    emitJsExpr
                        (cached, uncached)
                        "typeof $0 === 'number' && typeof $1 === 'number' && $0 >= 0 && $1 >= 0"
                then
                    Some(int (unbox<float> cached), int (unbox<float> uncached))
                else
                    None

            let recordProviderTokenUse (rawInput: obj) =
                let raw = HostEventEnvelope.unwrap rawInput

                let observation =
                    if HostEventEnvelope.eventTypeOf raw = "message.updated" then
                        tokenUse raw
                        |> Option.bind (fun usage ->
                            HostEventEnvelope.tryMessageSessionId rawInput
                            |> Option.map (fun sessionId -> sessionId, usage))
                    else
                        None

                observation
                |> Option.iter (fun (sessionId, (cached, uncached)) ->
                    calibration?recordUsage (SessionId.value sessionId, cached, uncached) |> ignore)

            let closeScopeOnSessionDeleted (rawInput: obj) =
                let raw = HostEventEnvelope.unwrap rawInput

                if HostEventEnvelope.eventTypeOf raw = "session.deleted" then
                    HostEventEnvelope.trySessionId raw
                    |> Option.iter (fun sessionId ->
                        Surface.clearSessionState (SessionId.value sessionId)
                        scopeProgress?clear(SessionId.value sessionId) |> ignore
                        scopeProgress?unbindSessionJournal(SessionId.value sessionId) |> ignore
                        advisorProcessQueue?clear(SessionId.value sessionId) |> ignore
                        calibration?clearSession(SessionId.value sessionId) |> ignore
                        PluginTransforms.negotiationForgetSession (SessionId.value sessionId)
                        outboundViewStore?clearSession (SessionId.value sessionId) |> ignore)

            let observeEvent raw =
                task {
                    closeScopeOnSessionDeleted raw
                    recordProviderTokenUse raw
                    do! settleUserQuestion raw
                    do! settlePostS3Deliver raw
                    do! wired.ObserveEvent raw
                }

            let event = registeredHook HookKey.Event (unaryHook (box observeEvent))

            let dispose =
                let disposeAll () =
                    task {
                        do! product.Dispose()
                        do! scope.DisposeAsync()
                    }

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
