namespace Wanxiangshu.Execution.Delegation.Fork.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Mission.WorkRecord
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Dispatch.OpenCode
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session.OpenCode
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Mission.Relay
open Wanxiangshu.Foundation.Identity

/// Opaque JS-native harness for the real Manager fork tool path.
/// Production semantics stay in ForkTool/HostForkRuntime; this surface only
/// supplies a physical Host boundary for executable requirement proofs.
module ForkToolSurface =

    type private TerminalSubscription =
        { Listener: TerminalCompletionListener }

    type private RecordedPrompt =
        { SessionId: SessionId
          Text: string
          Options: SessionPromptOptions }

    type private ForkSessionPort(abortSession: SessionId -> Task<Result<unit, string>>, existingChildIds: Set<string>) =
        let children = ResizeArray<OpenCodeChildInfo>()
        // DSL-MUTABLE: algorithm-scratch — latest prompted session in the harness
        let mutable latestPromptedSession: SessionId option = None
        // DSL-MUTABLE: algorithm-scratch — pre-accepted prompt count in the harness
        let mutable preAcceptedPrompts = 0
        let listeners = Dictionary<string, ResizeArray<TerminalSubscription>>()
        let prompts = Dictionary<string, ResizeArray<string>>()
        let recordedPrompts = Dictionary<string, ResizeArray<RecordedPrompt>>()

        let promptWaiters =
            Dictionary<string, ResizeArray<int * TaskCompletionSource<unit>>>()

        let emittedWaiters = ResizeArray<int * TaskCompletionSource<unit>>()

        let pendingAcceptances =
            Dictionary<string, ResizeArray<TaskCompletionSource<SendOutcome>>>()

        let physicalRoots = Dictionary<string, ResizeArray<string>>()
        // DSL-MUTABLE: algorithm-scratch — exactly one next Host send outcome in the harness
        let mutable nextSendOutcome: SendOutcome option = None
        // DSL-MUTABLE: algorithm-scratch — Host AbortSession call count in the harness
        let mutable abortCount = 0
        // DSL-MUTABLE: algorithm-scratch — synthetic physical message id counter for the harness
        let physicalSequence = ref 0
        let physicalNamespace = Guid.NewGuid().ToString("N")

        let historyOf (source: Dictionary<string, ResizeArray<string>>) key =
            match source.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<string>()
                source[key] <- values
                values

        let acceptancesOf key =
            match pendingAcceptances.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<TaskCompletionSource<SendOutcome>>()
                pendingAcceptances[key] <- values
                values

        let waitersOf key =
            match promptWaiters.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<int * TaskCompletionSource<unit>>()
                promptWaiters[key] <- values
                values

        let promptCountForKey key =
            match prompts.TryGetValue key with
            | true, values -> values.Count
            | false, _ -> 0

        let retainPromptWaiters key pending =
            match pending with
            | [] -> promptWaiters.Remove key |> ignore
            | values -> promptWaiters[key] <- ResizeArray(values)

        let releasePromptWaiters key =
            match promptWaiters.TryGetValue key with
            | false, _ -> ()
            | true, waiters ->
                let admitted = promptCountForKey key

                let ready, pending =
                    waiters |> Seq.toList |> List.partition (fun (target, _) -> admitted >= target)

                retainPromptWaiters key pending

                ready
                |> List.iter (fun (_, waiter) -> AsyncSupport.trySetResult waiter () |> ignore)

        let latestChild () =
            children
            |> Seq.tryLast
            |> Option.map (fun child -> child.SessionId)
            |> Option.orElse latestPromptedSession

        let releaseEmittedWaiters () =
            let admitted =
                match latestChild () with
                | Some childId -> promptCountForKey (SessionId.value childId)
                | None -> 0

            let ready =
                emittedWaiters
                |> Seq.filter (fun (target, _) -> admitted >= target)
                |> Seq.toList

            for registration in ready do
                emittedWaiters.Remove registration |> ignore

                registration
                |> snd
                |> fun waiter -> AsyncSupport.trySetResult waiter () |> ignore

        let subscribe sessionId listener =
            let key = SessionId.value sessionId

            let registrations =
                match listeners.TryGetValue key with
                | true, values -> values
                | false, _ ->
                    let values = ResizeArray<TerminalSubscription>()
                    listeners[key] <- values
                    values

            let registration = { Listener = listener }
            registrations.Add registration

            { new IDisposable with
                member _.Dispose() =
                    let index =
                        registrations.FindIndex(fun candidate -> obj.ReferenceEquals(candidate, registration))

                    if index >= 0 then
                        registrations.RemoveAt index }

        member _.LatestChild = latestChild ()

        member _.ChildCount = children.Count
        member _.TerminalListenerCount = listeners.Values |> Seq.sumBy _.Count

        member _.PromptCount(sessionId: SessionId) =
            promptCountForKey (SessionId.value sessionId)

        member _.WaitForPromptCount(sessionId: SessionId, count: int) : Task =
            let key = SessionId.value sessionId

            if promptCountForKey key >= count then
                Task.FromResult(()) :> Task
            else
                let waiter =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                waitersOf key |> fun values -> values.Add(count, waiter)
                waiter.Task :> Task

        member _.WaitForEmittedPromptCount(count: int) : Task =
            let admitted =
                match latestChild () with
                | Some childId -> promptCountForKey (SessionId.value childId)
                | None -> 0

            if admitted >= count then
                Task.FromResult(()) :> Task
            else
                let waiter =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                emittedWaiters.Add(count, waiter)
                waiter.Task :> Task

        member _.AcceptNextPrompt() =
            preAcceptedPrompts <- preAcceptedPrompts + 1

        member _.AcceptPrompt(sessionId: SessionId, index: int) =
            let key = SessionId.value sessionId

            match pendingAcceptances.TryGetValue key with
            | true, values when index >= 0 && index < values.Count ->
                physicalSequence.Value <- physicalSequence.Value + 1

                let physical =
                    sprintf "fork-physical-%s-%d" physicalNamespace physicalSequence.Value

                if
                    AsyncSupport.trySetResult
                        values[index]
                        (SendOutcome.AdmittedWithPhysicalMessage(PhysicalUserMessageId.create physical))
                then
                    historyOf physicalRoots key |> fun roots -> roots.Add physical
                    true
                else
                    false
            | true, values when values.Count > 0 ->
                let lastIndex = values.Count - 1
                physicalSequence.Value <- physicalSequence.Value + 1

                let physical =
                    sprintf "fork-physical-%s-%d" physicalNamespace physicalSequence.Value

                if
                    AsyncSupport.trySetResult
                        values[lastIndex]
                        (SendOutcome.AdmittedWithPhysicalMessage(PhysicalUserMessageId.create physical))
                then
                    historyOf physicalRoots key |> fun roots -> roots.Add physical
                    true
                else
                    false
            | _ ->
                preAcceptedPrompts <- preAcceptedPrompts + 1
                true

        member _.Prompt(sessionId: SessionId, index: int) =
            match prompts.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count -> Some values[index]
            | _ -> None

        member _.RecordedPrompt(sessionId: SessionId, index: int) =
            match recordedPrompts.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count -> Some values[index]
            | _ -> None

        member _.LatestAuthorityRoot(sessionId: SessionId) =
            match physicalRoots.TryGetValue(SessionId.value sessionId) with
            | true, values when values.Count > 0 -> Some values[values.Count - 1]
            | _ -> None

        member _.SetNextSendOutcome(outcome: SendOutcome) = nextSendOutcome <- Some outcome
        member _.AbortCount = abortCount

        member _.Notify(sessionId: SessionId, outcome: TerminalOutcome) =
            match listeners.TryGetValue(SessionId.value sessionId) with
            | true, registrations ->
                for registration in registrations |> Seq.toList do
                    registration.Listener sessionId outcome
            | false, _ -> ()

        interface ISessionHostPort with
            member _.SubscribeTerminal(sessionId, listener) = subscribe sessionId listener
            member _.SubscribeFutureTerminal(sessionId, listener) = subscribe sessionId listener

            member _.SendPrompt(sessionId, text, options) =
                latestPromptedSession <- Some sessionId
                let key = SessionId.value sessionId
                historyOf prompts key |> fun values -> values.Add text

                let recorded =
                    match recordedPrompts.TryGetValue key with
                    | true, values -> values
                    | false, _ ->
                        let values = ResizeArray<RecordedPrompt>()
                        recordedPrompts[key] <- values
                        values

                recorded.Add
                    { SessionId = sessionId
                      Text = text
                      Options = options }

                releasePromptWaiters key
                releaseEmittedWaiters ()

                match nextSendOutcome with
                | Some outcome ->
                    nextSendOutcome <- None
                    Task.FromResult outcome
                | None when preAcceptedPrompts > 0 ->
                    preAcceptedPrompts <- preAcceptedPrompts - 1
                    physicalSequence.Value <- physicalSequence.Value + 1

                    let physical =
                        sprintf "fork-physical-%s-%d" physicalNamespace physicalSequence.Value

                    historyOf physicalRoots key |> fun roots -> roots.Add physical
                    Task.FromResult(SendOutcome.AdmittedWithPhysicalMessage(PhysicalUserMessageId.create physical))
                | None ->
                    let acceptance =
                        TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                    acceptancesOf key |> fun values -> values.Add acceptance
                    acceptance.Task

            member _.AbortSession sessionId =
                abortCount <- abortCount + 1
                abortSession sessionId

            member _.InterruptAttempt _ = Task.FromResult(Ok())
            member _.IsManagedChild _ = true
            member _.AbortChildren _ = Task.FromResult()

            member _.CreateSiblingSession(_, _, _) =
                Task.FromResult(Error "fork surface does not create fission siblings")

            member _.TryGetParentSession sessionId =
                children
                |> Seq.tryFind (fun child -> child.SessionId = sessionId)
                |> Option.bind (fun child -> child.ParentSessionId)
                |> Ok
                |> Task.FromResult

            member _.CreateChildSession(parent, options) =
                let prefix = sprintf "%s-fork-child-" (SessionId.value parent)

                let isTaken (candidate: SessionId) =
                    (children |> Seq.exists (fun child -> child.SessionId = candidate))
                    || Set.contains (SessionId.value candidate) existingChildIds

                let rec pick index =
                    let candidate = SessionId.create (sprintf "%s%d" prefix index)

                    if isTaken candidate then pick (index + 1) else candidate

                let childId = pick (children.Count + 1)

                children.Add
                    { SessionId = childId
                      ParentSessionId = Some parent
                      Agent = options.Agent
                      Title = options.Title }

                Task.FromResult(Ok childId)

            member _.ListChildren parent =
                children
                |> Seq.filter (fun child -> child.ParentSessionId = Some parent)
                |> Seq.toList
                |> Ok
                |> Task.FromResult

            member _.FamilyRootOf sessionId =
                children
                |> Seq.tryFind (fun child -> child.SessionId = sessionId)
                |> Option.bind (fun child -> child.ParentSessionId)
                |> Option.defaultValue sessionId

    type private ForkHarness
        (
            journal: AgentJournal,
            scope: ToolRuntimeScope,
            sessions: ForkSessionPort,
            ownerAgents: Dictionary<string, string>
        ) =
        member _.Journal = journal
        member _.Scope = scope
        member _.Sessions = sessions
        member _.OwnerSession(owner: string) = SessionId.create owner
        member _.OwnerAgent(owner: string) = ownerAgents[owner]

        member _.Dispose() =
            (scope :> IDisposable).Dispose()
            (journal :> IDisposable).Dispose()

    type private CapturedChildPromptSender =
        { Runtime: HostForkRuntime
          AgentId: string
          ChildId: SessionId
          Role: Role
          IdentitySeed: PromptAuthority.IdentitySeed
          OnAccepted: string -> unit }

    let private createJournal (directory: string) : Task<AgentJournal> =
        task {
            let store =
                EventStore.createLocal
                    directory
                    (Guid.NewGuid().ToString("N"))
                    (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

            match!
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create (sprintf "fork-surface-%s" (ToolHostCodec.digest directory)),
                    1,
                    DateTimeOffset.UtcNow,
                    store
                )
            with
            | Ok(writer, _, projection) ->
                match AgentJournal.createFromProjection writer projection with
                | Ok journal -> return journal
                | Error rejection -> return failwithf "%s: %s" rejection.Fact rejection.Reason
            | Error rejection -> return failwithf "%s: %s" rejection.Fact rejection.Reason
        }

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private requiredOwnerString (fieldName: string) (value: obj) : Result<string, string> =
        let isString: bool = emitJsExpr value "typeof $0 === 'string'"

        if not isString || String.IsNullOrWhiteSpace(unbox<string> value) then
            Error(sprintf "invalid fork owner descriptor: %s must be a non-empty string" fieldName)
        else
            Ok(unbox<string> value)

    let private ownerAdmission (descriptor: obj) =
        let isPlainObject: bool =
            not (isNullish descriptor)
            && emitJsExpr
                descriptor
                "typeof $0 === 'object' && !Array.isArray($0) && (Object.getPrototypeOf($0) === Object.prototype || Object.getPrototypeOf($0) === null)"

        if not isPlainObject then
            Error "invalid fork owner descriptor: descriptor must be a plain object"
        else
            match requiredOwnerString "sessionId" descriptor?sessionId with
            | Error error -> Error error
            | Ok sessionId ->
                match requiredOwnerString "agent" descriptor?agent with
                | Error error -> Error error
                | Ok agent ->
                    ParticipantIdentity.resolveAtRoot agent
                    |> Result.mapError (sprintf "invalid fork owner descriptor agent: %A")
                    |> Result.map (fun identity ->
                        SessionId.create sessionId,
                        PhysicalUserMessageId.create (sprintf "fork-owner-root:%s" sessionId),
                        PromptAuthority.IdentitySeed.RootSelection identity,
                        agent)

    let private ownerAdmissions (owners: obj) =
        let isArray: bool = emitJsExpr owners "Array.isArray($0)"

        if not isArray || (unbox<obj array> owners).Length = 0 then
            Error "invalid fork owner descriptors: expected a non-empty array"
        else
            let rec collect seen admissions remaining =
                match remaining with
                | [] -> Ok(List.rev admissions)
                | descriptor :: tail ->
                    match ownerAdmission descriptor with
                    | Error error -> Error error
                    | Ok((sessionId, _, _, _) as admission) ->
                        let session = SessionId.value sessionId

                        if Set.contains session seen then
                            Error(sprintf "invalid fork owner descriptors: duplicate sessionId '%s'" session)
                        else
                            collect (Set.add session seen) (admission :: admissions) tail

            unbox<obj array> owners |> Array.toList |> collect Set.empty []

    let rec private acceptOwnerRoots (dispatcher: PromptDispatcher.Runtime) admissions : Task<Result<unit, string>> =
        task {
            match admissions with
            | [] -> return Ok()
            | (sessionId, physicalMessageId, identitySeed, _) :: tail ->
                match! dispatcher.AcceptHumanRoot sessionId physicalMessageId (Some identitySeed) with
                | Ok _ -> return! acceptOwnerRoots dispatcher tail
                | Error error ->
                    return
                        Error(
                            sprintf
                                "fork owner '%s' root admission rejected: %s"
                                (SessionId.value sessionId)
                                (PromptDispatcher.describeHumanRootAcceptanceFailure error)
                        )
        }

    let private createRuntimeUsingAbort
        directory
        owners
        abortSession
        cancelSignals
        (beforeWorkRecord: (string -> Task) option)
        : Task<obj> =
        emitJsExpr () "process.env.WANXIANGSHU_ADMISSION_TIMEOUT_MS = '100'" |> ignore

        task {
            let admissions =
                match ownerAdmissions owners with
                | Ok admissions -> admissions
                | Error error -> raise (ArgumentException error)

            let! journal = createJournal directory
            let dispatcher = PromptDispatcher.Runtime(PromptJournalAdapter.create journal)

            match! acceptOwnerRoots dispatcher admissions with
            | Error error ->
                (journal :> IDisposable).Dispose()
                raise (InvalidOperationException error)
            | Ok() -> ()

            let ownerAgents = Dictionary<string, string>()

            for (sessionId, _, _, agent) in admissions do
                ownerAgents.Add(SessionId.value sessionId, agent)

            let existingChildIds =
                (AgentJournal.snapshot journal).AgentProjections.HandleByChildSession
                |> Map.toSeq
                |> Seq.map (fun (childId, _) -> SessionId.value childId)
                |> Set.ofSeq

            let sessionPort = ForkSessionPort(abortSession, existingChildIds)
            let sessions = sessionPort :> ISessionHostPort

            let childWorkRecordForRun sessionId range providerRun =
                LifecycleWorkRecordProjection.lifecycleWorkRecordBoundedForRun
                    (Some journal)
                    sessionId
                    range
                    providerRun

            let workRecordCapability: DelegationWorkRecordCapability =
                { ParentWorkRecord =
                    fun sessionId -> LifecycleWorkRecordProjection.lifecycleWorkRecord (Some journal) sessionId true
                  ParentWorkRecordBounded =
                    fun sessionId range ->
                        LifecycleWorkRecordProjection.lifecycleWorkRecordBounded (Some journal) sessionId range }

            let parentWorkRecord sessionId =
                task {
                    match beforeWorkRecord with
                    | Some observe -> do! observe sessionId
                    | None -> ()

                    return!
                        LifecycleWorkRecordProjection.lifecycleWorkRecord
                            (Some journal)
                            (SessionId.create sessionId)
                            true
                }

            let scope =
                new ToolRuntimeScope(
                    sessions,
                    CausalWaitRuntime().Observer,
                    { new IRootWorkspaceReader with
                        member _.TryRead() = Some directory },
                    Some journal,
                    Some directory,
                    Dictionary<string, string>(),
                    (fun _ -> None),
                    Dictionary<string, string>(),
                    None,
                    Some parentWorkRecord,
                    None,
                    None,
                    cancelSignals,
                    childWorkRecordForRun = childWorkRecordForRun,
                    workRecordCapability = workRecordCapability
                )

            scope.AttachCurrentProcessJoinMode "ready"

            return box (ForkHarness(journal, scope, sessionPort, ownerAgents))
        }

    let createRuntime (directory: string) (owners: obj) : Task<obj> =
        createRuntimeUsingAbort directory owners (fun _ -> Task.FromResult(Ok())) None None

    let createRuntimeWithWorkRecordRead (directory: string) (owners: obj) (beforeRead: string -> Task) : Task<obj> =
        createRuntimeUsingAbort directory owners (fun _ -> Task.FromResult(Ok())) None (Some beforeRead)

    let createRuntimeWithCancelSignals
        (directory: string)
        (owners: obj)
        (cancelSignals: string array -> unit)
        : Task<obj> =
        createRuntimeUsingAbort
            directory
            owners
            (fun _ -> Task.FromResult(Ok()))
            (Some(fun sessionIds -> sessionIds |> Seq.map SessionId.value |> Seq.toArray |> cancelSignals))
            None

    let createRuntimeWithAbort (directory: string) (owners: obj) (abortSession: string -> Task<obj>) : Task<obj> =
        let abort sessionId =
            task {
                let! result = abortSession (SessionId.value sessionId)

                return
                    if unbox<bool> result?ok then
                        Ok()
                    else
                        Error(string result?error)
            }

        createRuntimeUsingAbort directory owners abort None None

    let private managerContext (harness: ForkHarness) owner =
        { SessionId = SessionId.value (harness.OwnerSession owner)
          Agent = Some(harness.OwnerAgent owner)
          ToolCallId = None
          ProviderRunId = None
          PromptText = None
          AttachAbort = fun _ -> fun () -> () }

    let captureChildPromptSender (value: obj) (owner: string) (byname: string) (onAccepted: string -> unit) : obj =
        let harness = unbox<ForkHarness> value

        let runtime =
            harness.Scope.RuntimeFor(managerContext harness owner)
            |> Result.defaultWith invalidOp

        let binding =
            AgentJournal.handleProjection harness.Journal (harness.OwnerSession owner)
            |> HandleProjection.tryFindByByname byname
            |> Option.defaultWith (fun () -> invalidOp "Captured Fork sender requires an existing named child")

        let agentId =
            HandleId.tryAgent binding.Handle
            |> Option.map AgentHandleId.value
            |> Option.defaultWith (fun () -> invalidOp "Captured Fork sender requires an agent handle")

        let seed =
            HostForkRunLifecycle.issueCurrentOwnerIdentitySeed
                (Some harness.Journal)
                (harness.OwnerSession owner)
                binding.TargetAgent
            |> Result.defaultWith invalidOp

        box
            { Runtime = runtime
              AgentId = agentId
              ChildId = binding.ChildSessionId
              Role = binding.CanonicalRole
              IdentitySeed = seed
              OnAccepted = onAccepted }

    let sendCapturedChildPrompt (captured: obj) (text: string) : Task<obj> =
        task {
            let sender = unbox<CapturedChildPromptSender> captured

            let! outcome =
                sender.Runtime.SendChildPrompt
                    sender.AgentId
                    sender.ChildId
                    sender.Role
                    sender.IdentitySeed
                    text
                    (PhysicalUserMessageId.value >> sender.OnAccepted)

            return
                match outcome with
                | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Accepted(physical, root) ->
                    box
                        {| kind = "Accepted"
                           physicalUserMessageId = PhysicalUserMessageId.value physical
                           authorityRoot = AuthorityRootUserMessageId.value root
                           reason = null |}
                | HostForkRunLifecycle.AgentOwnerDispatchOutcome.AcceptanceUncertain reason ->
                    box
                        {| kind = "AcceptanceUncertain"
                           physicalUserMessageId = null
                           authorityRoot = null
                           reason = reason |}
                | HostForkRunLifecycle.AgentOwnerDispatchOutcome.Rejected reason ->
                    box
                        {| kind = "Rejected"
                           physicalUserMessageId = null
                           authorityRoot = null
                           reason = reason |}
        }

    let executeManagerFork
        (value: obj)
        (toolModule: obj)
        (owner: string)
        (calling: string)
        (byname: string)
        (charge: string)
        : Task<string> =
        task {
            let harness = unbox<ForkHarness> value
            let spec = ForkTool.managerSpec (ToolHostCodec.factory toolModule) harness.Scope

            let args =
                HostToolArguments(
                    box
                        {| calling = if String.IsNullOrWhiteSpace calling then null else calling
                           name = byname
                           charge = charge
                           keywords = null
                           attach = null
                           expected_tool_calls = null |}
                )

            return! spec.Execute args (managerContext harness owner)
        }

    let executeManagerResume
        (value: obj)
        (toolModule: obj)
        (owner: string)
        (calling: string)
        (byname: string)
        (charge: string)
        : Task<string> =
        task {
            let harness = unbox<ForkHarness> value
            let spec = ForkTool.resumeSpec (ToolHostCodec.factory toolModule) harness.Scope

            let args =
                HostToolArguments(
                    box
                        {| calling = if String.IsNullOrWhiteSpace calling then null else calling
                           name = byname
                           charge = charge
                           keywords = null
                           attach = null
                           expected_tool_calls = null |}
                )

            return! spec.Execute args (managerContext harness owner)
        }

    let executeManagerResumeWithAttachment
        (value: obj)
        (toolModule: obj)
        (owner: string)
        (byname: string)
        (charge: string)
        (attach: string)
        : Task<string> =
        let harness = unbox<ForkHarness> value
        let spec = ForkTool.resumeSpec (ToolHostCodec.factory toolModule) harness.Scope

        let args =
            HostToolArguments(
                box
                    {| calling = null
                       name = byname
                       charge = charge
                       keywords = null
                       attach = attach
                       expected_tool_calls = null |}
            )

        spec.Execute args (managerContext harness owner)

    let executeCommission
        (value: obj)
        (toolModule: obj)
        (owner: string)
        (calling: string)
        (byname: string)
        (charge: string)
        : Task<string> =
        task {
            let harness = unbox<ForkHarness> value

            let spec =
                ForkTool.orchestratorSpec (ToolHostCodec.factory toolModule) harness.Scope

            let args =
                HostToolArguments(
                    box
                        {| calling = if String.IsNullOrWhiteSpace calling then null else calling
                           name = byname
                           charge = charge
                           expected_tool_calls = null |}
                )

            let orchestratorContext = managerContext harness owner

            return! spec.Execute args orchestratorContext
        }

    let captureOwnerOpening (value: obj) (owner: string) (text: string) : Task =
        task {
            let harness = unbox<ForkHarness> value

            match!
                XTraceCapture.captureOpeningWithReceipt (Some harness.Journal) (harness.OwnerSession owner) text []
            with
            | Ok _ -> ()
            | Error error -> return raise (InvalidOperationException(sprintf "%A" error))
        }
        :> Task

    let private traceMessage (messageId: string) (text: string) : SessionMessage =
        { Id = messageId
          Role = "assistant"
          Agent = Some "coder"
          Finish = Some "stop"
          ErrorName = None
          Model = None
          ParentId = None
          CreatedAt = None
          Completed = true
          IsCompaction = false
          PromptKey = None
          Parts = [| MessagePart.Text text |]
          PartIds = [| None |]
          ToolParts = [||] }

    let private captureTraceText journal sessionId messageId text : Task =
        task {
            match!
                XTraceCapture.captureSessionMessagesWithReceipt (Some journal) sessionId [ traceMessage messageId text ]
            with
            | Ok _ -> ()
            | Error error -> return raise (InvalidOperationException(sprintf "%A" error))
        }
        :> Task

    let captureOwnerDeltaPart (value: obj) (owner: string) (text: string) (providerRun: string) : Task =
        let harness = unbox<ForkHarness> value
        captureTraceText harness.Journal (harness.OwnerSession owner) providerRun text

    let childCount (value: obj) =
        (unbox<ForkHarness> value).Sessions.ChildCount

    let abortCount (value: obj) =
        (unbox<ForkHarness> value).Sessions.AbortCount

    let child (value: obj) : obj =
        (unbox<ForkHarness> value).Sessions.LatestChild
        |> Option.map (SessionId.value >> box)
        |> Option.defaultValue null

    let promptCount (value: obj) =
        let harness = unbox<ForkHarness> value

        harness.Sessions.LatestChild
        |> Option.map harness.Sessions.PromptCount
        |> Option.defaultValue 0

    let awaitPromptCount (value: obj) (count: int) : Task =
        let harness = unbox<ForkHarness> value
        harness.Sessions.WaitForEmittedPromptCount count

    let acceptPrompt (value: obj) (index: int) : bool =
        let harness = unbox<ForkHarness> value

        match harness.Sessions.LatestChild with
        | Some childId -> harness.Sessions.AcceptPrompt(childId, index)
        | None -> harness.Sessions.AcceptPrompt(SessionId.create "", index)

    let acceptNextPrompt (value: obj) =
        (unbox<ForkHarness> value).Sessions.AcceptNextPrompt()

    let terminalListenerCount (value: obj) =
        (unbox<ForkHarness> value).Sessions.TerminalListenerCount

    let prompt (value: obj) (index: int) : obj =
        let harness = unbox<ForkHarness> value

        harness.Sessions.LatestChild
        |> Option.bind (fun childId -> harness.Sessions.Prompt(childId, index))
        |> Option.map box
        |> Option.defaultValue null

    let private recordedPrompt (harness: ForkHarness) index =
        harness.Sessions.LatestChild
        |> Option.bind (fun childId -> harness.Sessions.RecordedPrompt(childId, index))
        |> Option.defaultWith (fun () -> invalidArg "index" "No actual Host prompt exists at this index")

    let private recordedPromptKey (recorded: RecordedPrompt) =
        let metadata =
            recorded.Options.Metadata
            |> Option.defaultWith (fun () -> invalidOp "Actual managed Host prompt has no correlation metadata")

        let value: obj = metadata?(PromptMetadataCodec.PromptKeyField)
        let isString: bool = emitJsExpr value "typeof $0 === 'string'"

        if not isString || String.IsNullOrWhiteSpace(unbox<string> value) then
            invalidOp "Actual managed Host prompt has no valid PromptKey"

        PromptKey.create (unbox<string> value)

    let promptEvidence (value: obj) (index: int) : obj =
        let recorded = recordedPrompt (unbox<ForkHarness> value) index

        box
            {| sessionId = SessionId.value recorded.SessionId
               text = recorded.Text
               promptKey = PromptKey.value (recordedPromptKey recorded)
               agent = recorded.Options.Agent |> Option.toObj |}

    let confirmPromptPhysical (value: obj) (index: int) (physicalMessageId: string) : Task<obj> =
        task {
            let harness = unbox<ForkHarness> value
            let recorded = recordedPrompt harness index

            let decision =
                PromptIngress.resolveDecision
                    (Some harness.Journal)
                    { SessionId = Some recorded.SessionId
                      PhysicalUserMessageId = Some(PhysicalUserMessageId.create physicalMessageId)
                      InvalidIdentityCarrier = None
                      ExplicitAgent = recorded.Options.Agent
                      PromptKey = Some(recordedPromptKey recorded)
                      IsHostCompaction = false
                      IsHostSynthetic = false
                      Text = None }

            let! accepted =
                (PromptDispatcher.forPrompts (PromptJournalAdapter.create harness.Journal))
                    .AcceptManagedChatIntent
                    decision

            return
                match accepted with
                | Ok witness ->
                    let evidence = ManagedChatAcceptanceWitness.evidence witness

                    box
                        {| ok = true
                           error = null
                           sessionId = SessionId.value evidence.SessionId
                           physicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId |}
                | Error error ->
                    box
                        {| ok = false
                           error = sprintf "%A" error
                           sessionId = null
                           physicalUserMessageId = null |}
        }

    let physicalAcceptanceObservation (value: obj) (index: int) (physicalMessageId: string) : obj =
        let harness = unbox<ForkHarness> value
        let recorded = recordedPrompt harness index
        let snapshot = (AgentJournal.snapshot harness.Journal).AgentProjections
        let physical = PhysicalUserMessageId.create physicalMessageId
        let promptKey = recordedPromptKey recorded

        let pending =
            (PromptDispatcher.forPrompts (PromptJournalAdapter.create harness.Journal))
                .PendingClaim(recorded.SessionId, promptKey)
                .IsSome

        let landing =
            PromptAuthorityProjectionQueries.physicalLanding recorded.SessionId physical snapshot
            |> Option.filter (fun accepted -> accepted.PromptKey = promptKey)

        let managed =
            snapshot.ChatExecutions
            |> ChatExecutionProjection.byKey
                { SessionId = recorded.SessionId
                  PhysicalUserMessageId = physical }

        box
            {| pending = pending
               landedPhysical =
                landing
                |> Option.map (fun accepted -> PhysicalUserMessageId.value accepted.PhysicalUserMessageId)
                |> Option.toObj
               managedAccepted = managed.IsSome |}

    let nextPromptAcceptanceUnknown (value: obj) (reason: string) =
        let harness = unbox<ForkHarness> value
        harness.Sessions.SetNextSendOutcome(SendOutcome.AcceptanceUnknown reason)

    let nextPromptAdmittedWithReceipt (value: obj) (receipt: string) =
        let harness = unbox<ForkHarness> value
        harness.Sessions.SetNextSendOutcome(SendOutcome.AdmittedWithReceipt(TransportReceipt.create receipt))

    /// Parent cancellation runs the production cancel chain: Scope.CancelSessionChildren
    /// -> HostForkRuntime.CancelAndDrain -> HostForkChildDispatch.cancelParent. That
    /// chain commits durable HandleAbandoned (filtered by its process-owned active-work
    /// rule) before its teardown abort, so the harness must not repeat the durable
    /// abandon itself: an early abandon would leave the chain nothing to cancel and the
    /// physical AbortSession would never run.
    let cancelOwnerChildren (value: obj) (owner: string) : Task =
        let harness = unbox<ForkHarness> value
        harness.Scope.CancelSessionChildren(SessionId.value (harness.OwnerSession owner))

    let detachToolRuntime (value: obj) : Task =
        let harness = unbox<ForkHarness> value
        harness.Scope.DisposeAsync()

    let durableLifecycleByname (value: obj) (owner: string) (byname: string) : obj =
        let harness = unbox<ForkHarness> value

        AgentJournal.handleProjection harness.Journal (harness.OwnerSession owner)
        |> HandleProjection.tryFindByByname byname
        |> Option.map (fun record ->
            match record.Lifecycle with
            | HandleLifecycle.Active -> "Active"
            | HandleLifecycle.CompletedAwaitingJoin _ -> "CompletedAwaitingJoin"
            | HandleLifecycle.Abandoned _ -> "Abandoned"
            | HandleLifecycle.Retired -> "Retired")
        |> Option.map box
        |> Option.defaultValue null

    let executeHorizon (value: obj) (owner: string) : Task<string> =
        let harness = unbox<ForkHarness> value

        let horizonContext: HorizonTool.HorizonRuntimeContext =
            { RuntimeFor = harness.Scope.RuntimeFor
              LogicalOwnerFor = harness.Scope.LogicalOwnerFor
              Journal = harness.Scope.Journal
              EnsureRoadDevOpsBound = harness.Scope.EnsureRoadDevOpsBound }

        let spec = HorizonTool.spec horizonContext
        spec.Execute (HostToolArguments(box {| |})) (managerContext harness owner)

    let executeJoin (value: obj) (owner: string) : Task<string> =
        let harness = unbox<ForkHarness> value
        let spec = JoinTool.spec harness.Scope
        spec.Execute (HostToolArguments(box {| |})) (managerContext harness owner)

    let settle (value: obj) (owner: string) (answer: string) (providerRun: string) : Task<bool> =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Sessions.LatestChild with
            | None -> return false
            | Some childId ->
                match harness.Sessions.LatestAuthorityRoot childId with
                | None -> return false
                | Some root ->
                    do! captureTraceText harness.Journal childId providerRun answer

                    match harness.Scope.RuntimeFor(managerContext harness owner) with
                    | Error _ -> return false
                    | Ok runtime ->
                        let agentRole =
                            match
                                runtime.List()
                                |> fst
                                |> List.tryFind (fun a ->
                                    match runtime.TryChildSession a.AgentId with
                                    | Some sid -> sid = childId
                                    | None -> false)
                            with
                            | Some a -> a.Role
                            | None ->
                                if (SessionId.value childId).Contains("devops") then
                                    Role.DevOps
                                else
                                    Role.Engineer

                        harness.Sessions.Notify(
                            childId,
                            TerminalOutcome.Completed
                                { SessionId = childId
                                  AuthorityRootUserMessageId = AuthorityRootUserMessageId.create root
                                  ProviderRun = ProviderRunIdentity.create providerRun
                                  Role = agentRole
                                  Directory = None
                                  TerminalText = answer
                                  TurnFormalText = answer }
                        )

                        match runtime.List() |> fst |> List.tryHead with
                        | None -> return false
                        | Some agent ->
                            match! runtime.AwaitCurrentWorkRecord agent.AgentId with
                            | Ok _ -> return true
                            | Error _ -> return false
        }

    let prepareTerminalDelivery (value: obj) (owner: string) (answer: string) (providerRun: string) =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Sessions.LatestChild, harness.Scope.RuntimeFor(managerContext harness owner) with
            | Some childId, Ok runtime ->
                let projection =
                    AgentJournal.handleProjection harness.Journal (harness.OwnerSession owner)

                match
                    HandleProjection.tryFindByChildSession childId projection,
                    harness.Sessions.LatestAuthorityRoot childId
                with
                | Some binding, Some root ->
                    do! captureTraceText harness.Journal childId providerRun answer

                    return
                        fun () ->
                            task {
                                harness.Sessions.Notify(
                                    childId,
                                    TerminalOutcome.Completed
                                        { SessionId = childId
                                          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create root
                                          ProviderRun = ProviderRunIdentity.create providerRun
                                          Role = binding.CanonicalRole
                                          Directory = None
                                          TerminalText = answer
                                          TurnFormalText = answer }
                                )

                                do! runtime.AwaitObservedWork()
                            }
                            :> Task
                | _ -> return invalidOp "no accepted child work for terminal delivery"
            | _ -> return invalidOp "no owned runtime for terminal delivery"
        }

    let private workView (record: HandleRecord) : obj =
        let root =
            record.Work
            |> Option.map (fun work -> AuthorityRootUserMessageId.value work.AuthorityRoot)

        let lifecycle =
            match record.Lifecycle with
            | Active -> "Active"
            | CompletedAwaitingJoin _ -> "CompletedAwaitingJoin"
            | Abandoned _ -> "Abandoned"
            | Retired -> "Retired"

        box
            {| root = root
               handle = HandleId.describe record.Handle
               child = SessionId.value record.ChildSessionId
               targetAgent = record.TargetAgent
               byname = record.Byname
               role = Roles.roleLabel record.CanonicalRole
               lifecycle = lifecycle
               completionRef = record.LastCompletion |> Option.bind _.CompletionRef |> Option.map BlobRef.value
               completionDigest =
                record.LastCompletion
                |> Option.bind _.CompletionDigest
                |> Option.map BlobDigest.value |}

    let workSnapshot (value: obj) (owner: string) : obj array =
        let harness = unbox<ForkHarness> value

        AgentJournal.handleProjection harness.Journal (harness.OwnerSession owner)
        |> HandleProjection.workRecords
        |> List.map workView
        |> List.toArray

    let handoffSnapshot (value: obj) : obj array =
        (AgentJournal.snapshot (unbox<ForkHarness> value).Journal)
            .AgentProjections.DelegationCompletedHandoffs
        |> Map.toArray
        |> Array.map (fun (key, sequence) ->
            box
                {| key = key
                   sequence = string sequence |})

    let coldWorkSnapshot (directory: string) (owner: string) : Task<obj array> =
        task {
            let! journal = createJournal directory

            try
                return
                    AgentJournal.handleProjection journal (SessionId.create owner)
                    |> HandleProjection.workRecords
                    |> List.map workView
                    |> List.toArray
            finally
                (journal :> IDisposable).Dispose()
        }

    let replayBinding (value: obj) (owner: string) (byname: string) : Task<obj> =
        task {
            let harness = unbox<ForkHarness> value
            let parent = harness.OwnerSession owner
            let projection = AgentJournal.handleProjection harness.Journal parent

            match HandleProjection.tryFindByByname byname projection with
            | None ->
                return
                    box
                        {| ok = false
                           error = "binding not found" |}
            | Some record ->
                match HandleId.tryAgent record.Handle with
                | None ->
                    return
                        box
                            {| ok = false
                               error = "not an agent binding" |}
                | Some id ->
                    let! result =
                        Wanxiangshu.Execution.Delegation.Handle.HandleController.linkNamed
                            (Some(AgentJournalPortAdapter.fromAgentJournal harness.Journal))
                            parent
                            (AgentHandleId.value id)
                            record.ChildSessionId
                            record.TargetAgent
                            record.Byname
                            record.CanonicalRole
                            record.Ownership

                    let error =
                        match result with
                        | Ok() -> None
                        | Error reason -> Some reason

                    return
                        box
                            {| ok = Result.isOk result
                               error = error |}
        }

    let emitTerminalForRoot (value: obj) (owner: string) (root: string) (answer: string) (providerRun: string) : Task =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Sessions.LatestChild, harness.Scope.RuntimeFor(managerContext harness owner) with
            | Some childId, Ok runtime ->
                harness.Sessions.Notify(
                    childId,
                    TerminalOutcome.Completed
                        { SessionId = childId
                          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create root
                          ProviderRun = ProviderRunIdentity.create providerRun
                          Role = Role.Engineer
                          Directory = None
                          TerminalText = answer
                          TurnFormalText = answer }
                )

                do! runtime.AwaitObservedWork()
            | _ -> invalidOp "no owned child work"
        }
        :> Task

    let startUnprepared (value: obj) (owner: string) (charge: string) : Task<obj> =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Scope.RuntimeFor(managerContext harness owner) with
            | Error error ->
                return
                    box
                        {| ok = false
                           error = sprintf "%A" error |}
            | Ok runtime ->
                let! result = runtime.Fork("plain-child", Role.Engineer, "engineer", charge, None, byname = "Plain")
                return box {| ok = Result.isOk result |}
        }

    let emitStopForRoot (value: obj) (owner: string) (root: string) (kind: string) : Task =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Sessions.LatestChild, harness.Scope.RuntimeFor(managerContext harness owner) with
            | Some childId, Ok runtime ->
                let stop =
                    TerminalStop.forAuthority (AuthorityRootUserMessageId.create root) "old work stop"

                let outcome =
                    match kind with
                    | "Failed" -> TerminalOutcome.Failed stop
                    | "Aborted" -> TerminalOutcome.Aborted stop
                    | _ -> invalidArg "kind" "unknown stop"

                harness.Sessions.Notify(childId, outcome)
                do! runtime.AwaitObservedWork()
            | _ -> invalidOp "no owned child work"
        }
        :> Task

    /// managed-session-lifecycle-018: inject a stop carrying an exact reason
    /// and an optional authority root. A non-empty root with a
    /// MISSING_FINAL_REPORT reason reproduces the provider-retry observation;
    /// an empty root reproduces the session-scoped stop shape a Fission
    /// external-abort group failure delivers to the owner's run lifecycle.
    let emitStopWithReason (value: obj) (owner: string) (root: string) (kind: string) (reason: string) : Task =
        task {
            let harness = unbox<ForkHarness> value

            match harness.Sessions.LatestChild, harness.Scope.RuntimeFor(managerContext harness owner) with
            | Some childId, Ok runtime ->
                let stop =
                    if String.IsNullOrWhiteSpace root then
                        TerminalStop.session reason
                    else
                        TerminalStop.forAuthority (AuthorityRootUserMessageId.create root) reason

                let outcome =
                    match kind with
                    | "Failed" -> TerminalOutcome.Failed stop
                    | "Aborted" -> TerminalOutcome.Aborted stop
                    | _ -> invalidArg "kind" "unknown stop"

                harness.Sessions.Notify(childId, outcome)
                do! runtime.AwaitObservedWork()
            | _ -> invalidOp "no owned child work"
        }
        :> Task

    let replayWorkCompletion (value: obj) (owner: string) (root: string) : Task<obj> =
        task {
            let harness = unbox<ForkHarness> value
            let parent = harness.OwnerSession owner
            let handles = AgentJournal.handleProjection harness.Journal parent

            let candidate =
                HandleProjection.workRecords handles
                |> List.tryFind (fun record ->
                    record.Work
                    |> Option.exists (fun work -> AuthorityRootUserMessageId.value work.AuthorityRoot = root))

            match
                candidate
                |> Option.bind (fun record -> record.LastCompletion |> Option.map (fun cell -> record, cell))
            with
            | None -> return box {| ok = false |}
            | Some(record, cell) ->
                let! result =
                    (AgentJournalPortAdapter.fromAgentJournal harness.Journal).AppendExecutionFact
                        parent
                        (ExecutionFactCases.HandleWorkCompleted
                            {| ParentSessionId = parent
                               Work = record.Work |> Option.get
                               Kind = HandleCompletionKind.SendFailure
                               CompletionRef = cell.CompletionRef
                               CompletionDigest = cell.CompletionDigest |})

                return box {| ok = Result.isOk result |}
        }

    let private consumeWorkViaJournal journal parent (root: string) (commitment: string) : Task<obj> =
        task {
            let capability =
                match commitment with
                | "confirmed" -> journal
                | "before" ->
                    { journal with
                        AppendExecutionFact = fun _ _ -> Task.FromResult(Error "known-not-committed") }
                | "after" ->
                    { journal with
                        AppendExecutionFact =
                            fun session fact ->
                                task {
                                    let! outcome = journal.AppendExecutionFact session fact
                                    return outcome |> Result.bind (fun () -> Error "commit-unknown-after-append")
                                } }
                | _ -> invalidArg "commitment" "unknown commitment scenario"

            let candidate =
                HandleProjection.workRecords (journal.HandleProjection parent)
                |> List.tryFind (fun record ->
                    record.Work
                    |> Option.exists (fun work -> AuthorityRootUserMessageId.value work.AuthorityRoot = root))

            match candidate with
            | None ->
                return
                    box
                        {| ok = false
                           error = "WorkNotAdmitted" |}
            | Some record ->
                let! payload =
                    Wanxiangshu.Execution.Delegation.Handle.HandleCompletionCodec.tryRead
                        journal
                        record
                        (HandleId.tryAgent record.Handle
                         |> Option.map AgentHandleId.value
                         |> Option.defaultValue "")
                        DateTimeOffset.MinValue

                let consumption =
                    match payload with
                    | Error reason ->
                        Task.FromResult(Error(Wanxiangshu.Execution.Delegation.Handle.AppendFailed reason))
                    | Ok _ ->
                        Wanxiangshu.Execution.Delegation.Handle.HandleController.consumeWork capability parent record

                match! consumption with
                | Error reason ->
                    return
                        box
                            {| ok = false
                               error = sprintf "%A" reason |}
                | Ok consumed ->
                    match payload with
                    | Ok(Some { Outcome = AgentCompleted completion }) ->
                        return
                            box
                                {| ok = true
                                   root = root
                                   workRecord = completion.WorkRecord |}
                    | _ ->
                        return
                            box
                                {| ok = false
                                   error = "materialization failed" |}
        }

    let consumeWorkWithOutcome (value: obj) (owner: string) (root: string) (commitment: string) : Task<obj> =
        task {
            let harness = unbox<ForkHarness> value

            return!
                consumeWorkViaJournal
                    (AgentJournalPortAdapter.fromAgentJournal harness.Journal)
                    (harness.OwnerSession owner)
                    root
                    commitment
        }

    /// Cold-recovered consume for crash-cut proofs: reopens the journal from
    /// disk with a fresh writer and a full replay (the shape a restarted
    /// process takes), then runs the same consume settlement chain against the
    /// durable facts alone.
    let coldConsumeWorkWithOutcome (directory: string) (owner: string) (root: string) (commitment: string) : Task<obj> =
        task {
            let! journal = createJournal directory

            try
                return!
                    consumeWorkViaJournal
                        (AgentJournalPortAdapter.fromAgentJournal journal)
                        (SessionId.create owner)
                        root
                        commitment
            finally
                (journal :> IDisposable).Dispose()
        }

    let injectAcceptedAssessment (value: obj) (owner: string) : Task =
        task {
            let harness = unbox<ForkHarness> value
            let sessionId = harness.OwnerSession owner
            let sessionStr = SessionId.value sessionId

            let existingRoad =
                AgentProjection.tryFind sessionId (AgentJournal.snapshot harness.Journal).AgentProjections
                |> Option.bind (fun session -> session.Relay)
                |> Option.bind (fun relay -> Wanxiangshu.Mission.Relay.Fold.view relay (RoadId.create sessionStr))

            let hasAssessment =
                existingRoad
                |> Option.bind (fun road -> road.AcceptedAssessmentTransport)
                |> Option.isSome

            if not hasAssessment then
                let roadId = RoadId.create sessionStr
                let incId = IncumbencyId.create (sprintf "incumbency:%s" sessionStr)
                let snapId = WorkspaceSnapshotId.create (sprintf "snapshot:%s" sessionStr)
                let authRev = AuthorityRevision.create (sprintf "rev:%s" sessionStr)

                let physUser =
                    Wanxiangshu.Mission.Relay.PhysicalUserMessageId.create (sprintf "phys:%s" sessionStr)

                let assessId = AssessmentId.create (sprintf "assess:%s" sessionStr)

                let binding: AssessmentBinding =
                    { PhysicalUserMessageId = sprintf "phys:%s" sessionStr
                      ProviderRunId = sprintf "run:%s" sessionStr
                      ToolCallId = sprintf "tool:%s" sessionStr
                      NarrativeDigest = sprintf "narrative:%s" sessionStr
                      PayloadDigest = sprintf "payload:%s" sessionStr
                      RootRequestDigest = sprintf "root:%s" sessionStr
                      RequirementSetDigest = sprintf "req:%s" sessionStr
                      EvidenceFrontierDigest = sprintf "evidence:%s" sessionStr }

                let findings =
                    AssessmentFindings.tryCreate
                        [ { AcceptanceCriteria = "the delivery reaches the requested target state"
                            WorkPlan = "close the remaining gap before the next review" } ]
                    |> Result.defaultWith (fun _ ->
                        failwith "injectAcceptedAssessment: failed to construct assessment findings")

                let events =
                    match existingRoad with
                    | None ->
                        [ RelayEvent.RoadOpened(roadId, authRev, physUser)
                          RelayEvent.IncumbencyOpened(incId, snapId)
                          RelayEvent.AssessmentCommitted(assessId, incId, binding, snapId, authRev, findings) ]
                    | Some road ->
                        match road.ActiveIncumbency, road.ActiveSnapshotId, road.ActiveAuthorityRevision with
                        | Some activeInc, Some activeSnap, Some activeRev ->
                            [ RelayEvent.AssessmentCommitted(
                                  assessId,
                                  activeInc,
                                  binding,
                                  activeSnap,
                                  activeRev,
                                  findings
                              ) ]
                        | _ ->
                            let currentRev =
                                if road.AuthorityRevisions.IsEmpty then
                                    authRev
                                else
                                    road.AuthorityRevision

                            let roadOpened =
                                if road.AuthorityRevisions.IsEmpty then
                                    [ RelayEvent.RoadOpened(roadId, currentRev, physUser) ]
                                else
                                    []

                            roadOpened
                            @ [ RelayEvent.IncumbencyOpened(incId, snapId)
                                RelayEvent.AssessmentCommitted(assessId, incId, binding, snapId, currentRev, findings) ]

                match RelayTransaction.create events with
                | Error error ->
                    return raise (InvalidOperationException(sprintf "Failed to create relay transaction: %s" error))
                | Ok tx ->
                    let fact =
                        Fact.AgentFact.Relay(
                            RelayFactCases.TransactionCommitted {| RoadId = roadId; Transaction = tx |}
                        )

                    match! AgentJournal.appendAgent (StreamId.Session sessionId) None fact harness.Journal with
                    | Ok _ -> ()
                    | Error error ->
                        return
                            raise (
                                InvalidOperationException(sprintf "Failed to append accepted assessment fact: %A" error)
                            )
        }
        :> Task

    let injectAuditPendingIncumbency (value: obj) (owner: string) : Task =
        task {
            let harness = unbox<ForkHarness> value
            let sessionId = harness.OwnerSession owner
            let sessionStr = SessionId.value sessionId

            let existingRoad =
                AgentProjection.tryFind sessionId (AgentJournal.snapshot harness.Journal).AgentProjections
                |> Option.bind (fun session -> session.Relay)
                |> Option.bind (fun relay -> Wanxiangshu.Mission.Relay.Fold.view relay (RoadId.create sessionStr))

            let hasActiveIncumbency =
                existingRoad |> Option.bind (fun road -> road.ActiveIncumbency) |> Option.isSome

            if not hasActiveIncumbency then
                let roadId = RoadId.create sessionStr
                let incId = IncumbencyId.create (sprintf "incumbency:%s" sessionStr)
                let snapId = WorkspaceSnapshotId.create (sprintf "snapshot:%s" sessionStr)
                let authRev = AuthorityRevision.create (sprintf "rev:%s" sessionStr)

                let physUser =
                    Wanxiangshu.Mission.Relay.PhysicalUserMessageId.create (sprintf "phys:%s" sessionStr)

                let events =
                    match existingRoad with
                    | None ->
                        [ RelayEvent.RoadOpened(roadId, authRev, physUser)
                          RelayEvent.IncumbencyOpened(incId, snapId) ]
                    | Some road ->
                        let currentRev =
                            if road.AuthorityRevisions.IsEmpty then
                                authRev
                            else
                                road.AuthorityRevision

                        let roadOpened =
                            if road.AuthorityRevisions.IsEmpty then
                                [ RelayEvent.RoadOpened(roadId, currentRev, physUser) ]
                            else
                                []

                        roadOpened @ [ RelayEvent.IncumbencyOpened(incId, snapId) ]

                match RelayTransaction.create events with
                | Error error ->
                    return raise (InvalidOperationException(sprintf "Failed to create relay transaction: %s" error))
                | Ok tx ->
                    let fact =
                        Fact.AgentFact.Relay(
                            RelayFactCases.TransactionCommitted {| RoadId = roadId; Transaction = tx |}
                        )

                    match! AgentJournal.appendAgent (StreamId.Session sessionId) None fact harness.Journal with
                    | Ok _ -> ()
                    | Error error ->
                        return
                            raise (
                                InvalidOperationException(
                                    sprintf "Failed to append audit pending incumbency fact: %A" error
                                )
                            )
        }
        :> Task

    let disposeRuntime (value: obj) =
        unbox<ForkHarness> value |> fun harness -> harness.Dispose()
