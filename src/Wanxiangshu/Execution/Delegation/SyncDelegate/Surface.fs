namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Host
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

/// Delegation-owned opaque runtime harness. Host sessions, journal writers,
/// attached-session state and completion turns never cross into JS; callers
/// observe only invocation promises and child identities.
[<RequireQualifiedAccess>]
module SyncDelegateSurface =
    [<RequireQualifiedAccess>]
    type private RetryHarnessMode =
        | Scripted
        | ProviderRecovery

    /// Test-side plug of the retry decorator: the harness records each verdict
    /// request so a landing test can prove a transient failure stayed child-local
    /// (delegation-023) before only the terminal verdict failed the call.
    type private RetryScript() =
        let queue = Queue<Result<unit, string>>()
        // DSL-MUTABLE: resource — test retry call counter
        let mutable calls = 0

        member _.Push(verdict: Result<unit, string>) = queue.Enqueue verdict
        member _.Calls = calls

        member _.Next(error: string) : Result<unit, string> =
            calls <- calls + 1

            if queue.Count = 0 then Error error else queue.Dequeue()

    type private PromptReadiness() =
        let admitted = Dictionary<string, int>()
        let completed = Dictionary<string, int>()
        let waiters = Dictionary<string, TaskCompletionSource<unit>>()

        let countWaiters =
            Dictionary<string, ResizeArray<int * TaskCompletionSource<unit>>>()

        let prompts = Dictionary<string, ResizeArray<string>>()
        // DSL-MUTABLE: algorithm-scratch — prompt readiness revision counter
        let revision = ref 0
        /// DSL-cross-callback-proof: physical waiter — wakes lookup after a prompt emission registers its child.
        let revisionWaiters = ResizeArray<int * TaskCompletionSource<int>>()

        let count (source: Dictionary<string, int>) key =
            match source.TryGetValue key with
            | true, value -> value
            | false, _ -> 0

        member _.Mark(sessionId: SessionId, prompt: string) =
            let key = SessionId.value sessionId
            admitted[key] <- count admitted key + 1

            let history =
                match prompts.TryGetValue key with
                | true, current -> current
                | false, _ ->
                    let created = ResizeArray<string>()
                    prompts[key] <- created
                    created

            history.Add prompt
            revision.Value <- revision.Value + 1

            let readyRevisionWaiters =
                revisionWaiters
                |> Seq.filter (fun (observed, _) -> revision.Value > observed)
                |> Seq.toList

            for registration in readyRevisionWaiters do
                revisionWaiters.Remove registration |> ignore

                registration
                |> snd
                |> fun waiter -> AsyncSupport.trySetResult waiter revision.Value |> ignore

            match countWaiters.TryGetValue key with
            | true, registrations ->
                let ready, pending =
                    registrations
                    |> Seq.toList
                    |> List.partition (fun (target, _) -> history.Count >= target)

                if List.isEmpty pending then
                    countWaiters.Remove key |> ignore
                else
                    countWaiters[key] <- ResizeArray(pending)

                ready
                |> List.iter (fun (_, waiter) -> AsyncSupport.trySetResult waiter () |> ignore)
            | false, _ -> ()

            match waiters.TryGetValue key with
            | true, waiter ->
                waiters.Remove key |> ignore
                AsyncSupport.trySetResult waiter () |> ignore
            | false, _ -> ()

        member _.Complete(sessionId: SessionId) =
            let key = SessionId.value sessionId
            completed[key] <- count completed key + 1

        member _.AdmittedCount(sessionId: SessionId) =
            count admitted (SessionId.value sessionId)

        member _.Prompt(sessionId: SessionId, index: int) =
            match prompts.TryGetValue(SessionId.value sessionId) with
            | true, history when index >= 0 && index < history.Count -> Some history[index]
            | _ -> None

        member _.WaitForCount(sessionId: SessionId, target: int) : Task =
            let key = SessionId.value sessionId

            if count admitted key >= target then
                Task.FromResult(()) :> Task
            else
                let waiter =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                match countWaiters.TryGetValue key with
                | true, registrations -> registrations.Add(target, waiter)
                | false, _ -> countWaiters.Add(key, ResizeArray([ target, waiter ]))

                waiter.Task :> Task

        member _.Revision = revision.Value

        member _.WaitForRevisionAfter(observed: int) : Task<int> =
            if revision.Value > observed then
                Task.FromResult revision.Value
            else
                let waiter =
                    TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

                revisionWaiters.Add(observed, waiter)
                waiter.Task

        member _.Wait(sessionId: SessionId) : Task<unit> =
            let key = SessionId.value sessionId

            if count admitted key > count completed key then
                Task.FromResult()
            else
                match waiters.TryGetValue key with
                | true, waiter -> waiter.Task
                | false, _ ->
                    let waiter =
                        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                    waiters.Add(key, waiter)
                    waiter.Task

    type private RecordedPrompt =
        { SessionId: SessionId
          Index: int
          Text: string
          Options: OpenCodePromptOptions
          ListenerCount: int }

    type private RecordedCreation =
        { Parent: SessionId
          PhysicalParent: SessionId option
          SessionId: SessionId
          Options: OpenCodeChildOptions }

    type private Harness
        (
            journal: AgentJournal,
            dispatcher: PromptDispatcher.Runtime,
            runtime: SyncDelegateRuntime,
            scope: ToolRuntimeScope,
            sessions: SessionPort,
            readiness: PromptReadiness,
            children: ResizeArray<SessionId>,
            retryScript: RetryScript,
            plugin: PluginRuntimeScope option,
            gate: SessionQuiescenceGate,
            rootWorkspace: IRootWorkspaceReader,
            eventPort: IEventObservationPort
        ) =
        let nudgeSent = HashSet<string>()
        let joinGuardNudges = HashSet<string>()
        member _.Journal = journal
        member _.Dispatcher = dispatcher
        member _.Runtime = runtime
        member _.Scope = scope
        member _.Sessions = sessions
        member _.Readiness = readiness
        member _.Children = children
        member _.RetryScript = retryScript
        member _.UsesProviderRecovery = plugin.IsSome
        member _.Plugin = plugin
        member _.Gate = gate
        member _.RootWorkspace = rootWorkspace
        member _.EventPort = eventPort
        member _.NudgeSent = nudgeSent
        member _.JoinGuardNudges = joinGuardNudges
        member _.OwnerSession(owner: string) = SessionId.create owner

        member _.Dispose() =
            runtime.Dispose()

            plugin
            |> Option.iter (fun scope ->
                children |> Seq.iter ProviderAttemptStopFence.shared.Revoke
                scope.Dispose())

            sessions.ClosePendingSends()
            (scope :> IDisposable).Dispose()
            (journal :> IDisposable).Dispose()

        member _.CloseRecovery() : Task =
            task {
                runtime.Dispose()
                children |> Seq.iter ProviderAttemptStopFence.shared.Revoke
                sessions.ClosePendingSends()

                try
                    match plugin with
                    | Some owner ->
                        children |> Seq.iter owner.LoopSensor.DropSession
                        do! owner.DisposeAsync()
                    | None -> ()
                finally
                    (scope :> IDisposable).Dispose()
                    (journal :> IDisposable).Dispose()
            }

    and private SessionPort
        (children: ResizeArray<SessionId>, readiness: PromptReadiness, observationMode: string option) =
        // DSL-MUTABLE: algorithm-scratch — synthetic physical message id counter for the harness
        let physicalSequence = ref 0
        let listeners = Dictionary<string, ResizeArray<Events.ListenerRegistration>>()
        let childCountWaiters = ResizeArray<int * TaskCompletionSource<unit>>()

        let pendingAcceptances =
            Dictionary<string, ResizeArray<TaskCompletionSource<SendOutcome>>>()

        let promptOrigins = Dictionary<string, ResizeArray<string>>()
        let promptKeys = Dictionary<string, ResizeArray<PromptKey>>()
        let acceptedPhysical = Dictionary<string, ResizeArray<PhysicalUserMessageId>>()
        let listedFamilies = ResizeArray<string>()
        let createRequests = ResizeArray<string * string option * string option>()
        let parents = Dictionary<SessionId, SessionId>()
        let recordedPrompts = ResizeArray<RecordedPrompt>()
        let recordedSiblings = ResizeArray<RecordedCreation>()
        let recordedChildren = ResizeArray<RecordedCreation>()
        let isHostRecording = observationMode = Some "host-recording"

        let prompted =
            TaskCompletionSource<SessionId>(TaskCreationOptions.RunContinuationsAsynchronously)
        // DSL-MUTABLE: algorithm-scratch — exact title expected within one proof scenario
        let mutable expectedTitle: string option = None

        let acceptancesOf key =
            match pendingAcceptances.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<TaskCompletionSource<SendOutcome>>()
                pendingAcceptances[key] <- values
                values

        let originsOf key =
            match promptOrigins.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<string>()
                promptOrigins[key] <- values
                values

        let keysOf key =
            match promptKeys.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<PromptKey>()
                promptKeys[key] <- values
                values

        let physicalsOf key =
            match acceptedPhysical.TryGetValue key with
            | true, values -> values
            | false, _ ->
                let values = ResizeArray<PhysicalUserMessageId>()
                acceptedPhysical[key] <- values
                values

        let subscribe sessionId listener =
            let key = SessionId.value sessionId

            let registrations =
                match listeners.TryGetValue key with
                | true, current -> current
                | false, _ ->
                    let created = ResizeArray<Events.ListenerRegistration>()
                    listeners[key] <- created
                    created

            let registration: Events.ListenerRegistration = { Listener = listener; Live = true }
            registrations.Add registration

            { new IDisposable with
                member _.Dispose() =
                    registration.Live <- false
                    registrations.Remove registration |> ignore }

        let rejectReleasedSend (acceptance: TaskCompletionSource<SendOutcome>) =
            try
                acceptance.SetException(InvalidOperationException "SyncDelegate test Host was released")
            with _ ->
                ()

        let notifyRegistration sessionId outcome (registration: Events.ListenerRegistration) =
            if registration.Live then
                registration.Listener sessionId outcome

        let registerCreatedChild parent child =
            children.Add child
            parents[child] <- parent

            let ready =
                childCountWaiters
                |> Seq.filter (fun (target, _) -> children.Count >= target)
                |> Seq.toList

            for registration in ready do
                childCountWaiters.Remove registration |> ignore

                registration
                |> snd
                |> fun waiter -> AsyncSupport.trySetResult waiter () |> ignore

        let listenerCount sessionId =
            match listeners.TryGetValue(SessionId.value sessionId) with
            | true, registrations ->
                registrations
                |> Seq.filter (fun registration -> registration.Live)
                |> Seq.length
            | false, _ -> 0

        let recordPrompt sessionId prompt options =
            if isHostRecording then
                recordedPrompts.Add
                    { SessionId = sessionId
                      Index = (acceptancesOf (SessionId.value sessionId)).Count
                      Text = prompt
                      Options = options
                      ListenerCount = listenerCount sessionId }

        let recordMetadata sessionId (options: OpenCodePromptOptions) =
            match options.Metadata with
            | None when isHostRecording -> ()
            | _ ->
                let origin: string = options.Metadata.Value?wanxiangshu_origin
                let promptKey: string = options.Metadata.Value?wanxiangshu_prompt_key
                originsOf (SessionId.value sessionId) |> fun values -> values.Add origin

                keysOf (SessionId.value sessionId)
                |> fun values -> values.Add(PromptKey.create promptKey)

        member _.ListedFamilies = listedFamilies.ToArray()
        member _.CreateRequests = createRequests.ToArray()
        member _.IsHostRecording = isHostRecording
        member _.RecordedPrompts = recordedPrompts.ToArray()
        member _.RecordedSiblings = recordedSiblings.ToArray()
        member _.RecordedChildren = recordedChildren.ToArray()
        member _.RecordedPromptCount = recordedPrompts.Count

        member _.ParentFor(child: SessionId) =
            match parents.TryGetValue child with
            | true, parent -> Some parent
            | false, _ -> None

        member _.TerminalListenerCount =
            listeners.Values |> Seq.sumBy (fun registrations -> registrations.Count)

        member _.WaitForPrompt() = prompted.Task
        member _.SetExpectedTitle(title: string) = expectedTitle <- Some title

        member _.Notify(sessionId: SessionId, outcome: TerminalOutcome) =
            match listeners.TryGetValue(SessionId.value sessionId) with
            | true, registrations ->
                for registration in registrations |> Seq.toList do
                    notifyRegistration sessionId outcome registration
            | false, _ -> ()

        member _.ClosePendingSends() =
            pendingAcceptances.Values |> Seq.collect id |> Seq.iter rejectReleasedSend

        member _.WaitForChildCount(target: int) : Task =
            if children.Count >= target then
                Task.FromResult(()) :> Task
            else
                let waiter =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                childCountWaiters.Add(target, waiter)
                waiter.Task :> Task

        member _.ReturnPromptOutcome(sessionId: SessionId, index: int, outcome: SendOutcome) =
            let key = SessionId.value sessionId

            match pendingAcceptances.TryGetValue key with
            | true, values when index >= 0 && index < values.Count ->
                if AsyncSupport.trySetResult values[index] outcome then
                    match outcome with
                    | SendOutcome.AdmittedWithPhysicalMessage physical ->
                        physicalsOf key |> fun physicals -> physicals.Add physical
                    | _ -> ()

                    true
                else
                    false
            | _ -> false

        member this.AcceptPrompt(sessionId: SessionId, index: int) =
            match pendingAcceptances.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count ->
                physicalSequence.Value <- physicalSequence.Value + 1

                let physical =
                    PhysicalUserMessageId.create (sprintf "msg-physical-%d" physicalSequence.Value)

                this.ReturnPromptOutcome(sessionId, index, SendOutcome.AdmittedWithPhysicalMessage physical)
            | _ -> false

        member _.RejectPrompt(sessionId: SessionId, index: int, reason: string) =
            match pendingAcceptances.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count ->
                try
                    values[index].SetException(InvalidOperationException reason)
                    true
                with _ ->
                    false
            | _ -> false

        member _.RecordPhysical(sessionId: SessionId, physical: PhysicalUserMessageId) =
            physicalsOf (SessionId.value sessionId) |> fun values -> values.Add physical

        member _.PromptKey(sessionId: SessionId, index: int) =
            match promptKeys.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count -> Some values[index]
            | _ -> None

        member _.PromptOrigin(sessionId: SessionId, index: int) =
            match promptOrigins.TryGetValue(SessionId.value sessionId) with
            | true, values when index >= 0 && index < values.Count -> Some values[index]
            | _ -> None

        member _.LatestAcceptedPhysical(sessionId: SessionId) =
            match acceptedPhysical.TryGetValue(SessionId.value sessionId) with
            | true, values when values.Count > 0 -> Some values[values.Count - 1]
            | _ -> None

        interface ISessionHostPort with
            member _.SubscribeTerminal(sessionId, listener) = subscribe sessionId listener

            member _.SubscribeFutureTerminal(sessionId, listener) = subscribe sessionId listener

            member _.SendPrompt(sessionId, prompt, options) =
                recordPrompt sessionId prompt options
                readiness.Mark(sessionId, prompt)
                AsyncSupport.trySetResult prompted sessionId |> ignore
                recordMetadata sessionId options

                let acceptance =
                    TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

                acceptancesOf (SessionId.value sessionId) |> fun values -> values.Add acceptance
                acceptance.Task

            member _.AbortSession _ = Task.FromResult(Ok())
            member _.InterruptAttempt _ = Task.FromResult(Ok())
            member _.IsManagedChild _ = true
            member _.AbortChildren _ = Task.FromResult()

            member _.CreateSiblingSession(owner, physicalParent, options) =
                if isHostRecording then
                    let child =
                        SessionId.create (sprintf "%s-sibling-%d" (SessionId.value owner) (children.Count + 1))

                    recordedSiblings.Add
                        { Parent = owner
                          PhysicalParent = physicalParent
                          SessionId = child
                          Options = options }

                    registerCreatedChild owner child
                    Task.FromResult(Ok child)
                else
                    Task.FromResult(Error "sibling creation is outside a managed delegation")

            member _.TryGetParentSession _ = Task.FromResult(Ok None)

            member _.CreateChildSession(parent, options) =
                createRequests.Add(SessionId.value parent, options.Title, options.Agent)

                let child =
                    match observationMode with
                    | Some "other-scope" -> SessionId.create "host-child-created-exact-scope"
                    | Some mode when mode <> "host-recording" -> SessionId.create "host-child-created"
                    | Some _
                    | None -> SessionId.create (sprintf "%s-child-%d" (SessionId.value parent) (children.Count + 1))

                if isHostRecording then
                    recordedChildren.Add
                        { Parent = parent
                          PhysicalParent = None
                          SessionId = child
                          Options = options }

                registerCreatedChild parent child

                Task.FromResult(Ok child)

            member _.ListChildren parent =
                listedFamilies.Add(SessionId.value parent)

                match observationMode with
                | Some "query-error" -> Task.FromResult(Error "controlled ListChildren rejection")
                | Some "host-recording" -> Task.FromResult(Ok [])
                | Some mode ->
                    let descriptor id title agent =
                        { SessionId = SessionId.create id
                          ParentSessionId = Some parent
                          Title = Some title
                          Agent = Some agent }

                    let exactTitle = Option.defaultValue "missing exact title" expectedTitle

                    let children =
                        match mode with
                        | "matching" ->
                            [ descriptor "host-child-wrong-agent" exactTitle "coder"
                              descriptor "host-child-existing" exactTitle "engineer" ]
                        | "conflicting" ->
                            [ descriptor "host-child-existing-a" exactTitle "engineer"
                              descriptor "host-child-existing-b" exactTitle "engineer" ]
                        | "other-scope" ->
                            [ descriptor
                                  "host-child-other-scope"
                                  "wanxiangshu:sync-delegate:v1:scope=another-owner:role=engineer:agent=engineer"
                                  "engineer" ]
                        | _ -> [ descriptor "host-child-wrong-agent" exactTitle "coder" ]

                    Task.FromResult(Ok children)
                | None ->
                    children
                    |> Seq.filter (fun child ->
                        (SessionId.value child)
                            .StartsWith((SessionId.value parent) + "-child-", StringComparison.Ordinal))
                    |> Seq.collect (fun child ->
                        [ { SessionId = child
                            ParentSessionId = Some parent
                            Title = Some "managed delegate"
                            Agent = Some "engineer" }
                          { SessionId = child
                            ParentSessionId = Some parent
                            Title = Some "managed delegate"
                            Agent = Some "coder" } ])
                    |> Seq.toList
                    |> Ok
                    |> Task.FromResult

            member _.FamilyRootOf sessionId =
                match observationMode with
                | Some _ -> SessionId.create "host-family-root"
                | None -> sessionId

    let private waitForReadyCall
        (runtime: SyncDelegateRuntime)
        (readiness: PromptReadiness)
        (owner: SessionId)
        (role: SyncDelegateRole)
        : Task<SessionId option> =
        task {
            match runtime.TryFind(owner, role) with
            | None -> return None
            | Some child ->
                do! readiness.Wait child

                let! accepted = runtime.AwaitAssignmentReady child

                if accepted && runtime.HasOpeningCursor child then
                    return Some child
                else
                    return None
        }

    let private roleOf (value: string) : Result<SyncDelegateRole, string> =
        if String.IsNullOrWhiteSpace value then
            Error "role is required"
        elif value.Equals("Coder", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.Coder
        elif value.Equals("Inspector", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.Inspector
        elif value.Equals("Engineer", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.Engineer
        elif value.Equals("DevOps", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.DevOps
        else
            Error(sprintf "unknown role: %s" value)

    let private syncInvocationRole (value: string) : Result<SyncDelegateRole, string> =
        if String.IsNullOrWhiteSpace value then
            Error "role is required"
        elif value.Equals("Coder", StringComparison.OrdinalIgnoreCase) then
            Error "retired sync delegate role: Coder"
        elif value.Equals("Inspector", StringComparison.OrdinalIgnoreCase) then
            Error "retired sync delegate role: Inspector"
        elif value.Equals("Engineer", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.Engineer
        elif value.Equals("DevOps", StringComparison.OrdinalIgnoreCase) then
            Ok SyncDelegateRole.DevOps
        else
            Error(sprintf "unknown role: %s" value)

    let private outcomeOf (value: string) : Result<ReconcileProgram.TurnOutcome, string> =
        match value with
        | "TurnCompleted" -> Ok(ReconcileProgram.TurnCompleted)
        | "TurnFailed" -> Ok(ReconcileProgram.TurnFailed "transient provider failure")
        | "TurnNeedsContinuation" -> Ok(ReconcileProgram.TurnNeedsContinuation "retry")
        | "TurnAborted" -> Ok(ReconcileProgram.TurnAborted "aborted")
        | _ -> Error(sprintf "unknown outcome: %s" value)

    let private roleValue =
        function
        | SyncDelegateRole.Coder -> Role.Coder
        | SyncDelegateRole.Inspector -> Role.Inspector
        | SyncDelegateRole.Engineer -> Role.Engineer
        | SyncDelegateRole.DevOps -> Role.DevOps

    let private createJournal (directory: string) : Task<AgentJournal> =
        task {
            let integrator =
                CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown

            let store =
                EventStore.createLocal directory (Guid.NewGuid().ToString("N")) integrator

            let! result =
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create (sprintf "sync-delegate-surface-%s" (ToolHostCodec.digest directory)),
                    1,
                    DateTimeOffset.UtcNow,
                    store
                )

            match result with
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
            Error(sprintf "invalid sync delegate owner descriptor: %s must be a non-empty string" fieldName)
        else
            Ok(unbox<string> value)

    let private ownerAdmissionFor (sessionId: string) (agent: string) =
        ParticipantIdentity.resolveAtRoot agent
        |> Result.mapError (sprintf "invalid sync delegate owner descriptor agent: %A")
        |> Result.map (fun identity ->
            SessionId.create sessionId,
            PhysicalUserMessageId.create (sprintf "sync-delegate-owner-root:%s" sessionId),
            PromptAuthority.IdentitySeed.RootSelection identity)

    let private ownerAdmission (descriptor: obj) =
        let isPlainObject: bool =
            not (isNullish descriptor)
            && emitJsExpr
                descriptor
                "typeof $0 === 'object' && !Array.isArray($0) && (Object.getPrototypeOf($0) === Object.prototype || Object.getPrototypeOf($0) === null)"

        if not isPlainObject then
            Error "invalid sync delegate owner descriptor: descriptor must be a plain object"
        else
            match requiredOwnerString "sessionId" descriptor?sessionId with
            | Error error -> Error error
            | Ok sessionId ->
                match requiredOwnerString "agent" descriptor?agent with
                | Error error -> Error error
                | Ok agent -> ownerAdmissionFor sessionId agent

    let private ownerAdmissions (owners: obj) =
        let isArray: bool = emitJsExpr owners "Array.isArray($0)"

        if not isArray || (unbox<obj array> owners).Length = 0 then
            Error "invalid sync delegate owner descriptors: expected a non-empty array"
        else
            let rec collect seen admissions remaining =
                match remaining with
                | [] -> Ok(List.rev admissions)
                | descriptor :: tail ->
                    match ownerAdmission descriptor with
                    | Error error -> Error error
                    | Ok((sessionId, _, _) as admission) ->
                        let session = SessionId.value sessionId

                        if Set.contains session seen then
                            Error(sprintf "invalid sync delegate owner descriptors: duplicate sessionId '%s'" session)
                        else
                            collect (Set.add session seen) (admission :: admissions) tail

            unbox<obj array> owners |> Array.toList |> collect Set.empty []

    let rec private acceptOwnerRoots (dispatcher: PromptDispatcher.Runtime) admissions : Task<Result<unit, string>> =
        task {
            match admissions with
            | [] -> return Ok()
            | (sessionId, physicalMessageId, identitySeed) :: tail ->
                match! dispatcher.AcceptHumanRoot sessionId physicalMessageId (Some identitySeed) with
                | Error error ->
                    return
                        Error(
                            sprintf
                                "sync delegate owner '%s' root admission rejected: %s"
                                (SessionId.value sessionId)
                                (PromptDispatcher.describeHumanRootAcceptanceFailure error)
                        )
                | Ok _ -> return! acceptOwnerRoots dispatcher tail
        }

    let private requireAcceptedOwners (journal: AgentJournal) =
        function
        | Ok() -> ()
        | Error error ->
            (journal :> IDisposable).Dispose()
            raise (InvalidOperationException error)

    let private createWithAdmissions
        (directory: string)
        (observationMode: string option)
        (retryMode: RetryHarnessMode)
        admissions
        : Task<obj> =
        task {
            let! journal = createJournal directory
            let dispatcher = PromptDispatcher.Runtime(PromptJournalAdapter.create journal)

            let! acceptedOwners = acceptOwnerRoots dispatcher admissions
            requireAcceptedOwners journal acceptedOwners

            // DSL-MUTABLE: resource — session id backing registry for SessionPort
            let children = ResizeArray<SessionId>()
            let readiness = PromptReadiness()
            let sessionPort = SessionPort(children, readiness, observationMode)
            let sessions = sessionPort :> ISessionHostPort
            let attached = new AttachedSessionRuntime()

            let plugin =
                match retryMode with
                | RetryHarnessMode.Scripted -> None
                | RetryHarnessMode.ProviderRecovery ->
                    Some(new PluginRuntimeScope(Some journal, (fun _ -> false), None))

            let gate =
                match plugin with
                | Some owner -> owner.Sessions.Quiescence
                | None -> new SessionQuiescenceGate()

            let eventPort = new Events.HostEventPort() :> IEventObservationPort

            plugin
            |> Option.iter (fun owner ->
                eventPort.SubscribeFutureTerminalListener(fun session outcome -> sessionPort.Notify(session, outcome))
                |> Some
                |> owner.TrackSubscription)

            let waitObserver = CausalWaitRuntime().Observer

            let retryScript = RetryScript()

            let rootWorkspace =
                { new IRootWorkspaceReader with
                    member _.TryRead() = Some directory }

            let retryPort: SyncDelegateRetryPort =
                match plugin with
                | None -> { Retry = fun _ _ _ error -> Task.FromResult(retryScript.Next error) }
                | Some owner ->
                    { Retry =
                        fun turn observer failure error ->
                            task {
                                let! verdict =
                                    ProviderRecoveryWorkflow.continueDelegateCallAfterConfirmedFailure
                                        sessions
                                        rootWorkspace
                                        journal
                                        turn
                                        observer
                                        failure
                                        error

                                return
                                    match verdict with
                                    | RetryVerdict.Dispatched
                                    | RetryVerdict.Superseded -> Ok()
                                    | RetryVerdict.Terminal reason -> Error reason
                            } }

            let runtime =
                new SyncDelegateRuntime(
                    sessions,
                    CausalAwait.awaitTask waitObserver,
                    CausalAwait.awaitTask waitObserver,
                    dispatcher,
                    journal,
                    (attached :> IAttachedSessionPort),
                    (fun child _ ->
                        plugin
                        |> Option.iter (fun owner ->
                            match sessionPort.ParentFor child with
                            | Some parent ->
                                owner.Sessions.OwnedSessions.Add(SessionId.value child) |> ignore
                                owner.Sessions.SessionParents[SessionId.value child] <- SessionId.value parent
                            | None -> raise (InvalidOperationException "managed child has no Host parent"))),
                    gate,
                    retryPort,
                    workspaceDirectory = directory
                )

            plugin |> Option.iter (fun owner -> owner.AttachSyncDelegateRuntime runtime)

            plugin
            |> Option.iter (fun owner ->
                HostTurnObserver.attachLoopSensor sessions rootWorkspace (Some journal) owner (fun _ _ -> ()))

            let scope =
                new ToolRuntimeScope(
                    sessions,
                    waitObserver,
                    rootWorkspace,
                    Some journal,
                    Some directory,
                    Dictionary<string, string>(),
                    (fun _ -> None),
                    Dictionary<string, string>(),
                    None,
                    None,
                    None,
                    None
                )

            return
                box (
                    Harness(
                        journal,
                        dispatcher,
                        runtime,
                        scope,
                        sessionPort,
                        readiness,
                        children,
                        retryScript,
                        plugin,
                        gate,
                        rootWorkspace,
                        eventPort
                    )
                )
        }

    /// Create a real SyncDelegateRuntime with an opaque journal and Host port.
    /// Every owner must first be admitted as an explicit durable HumanRoot.
    let create (directory: string) (owners: obj) : Task<obj> =
        match ownerAdmissions owners with
        | Ok admissions -> createWithAdmissions directory None RetryHarnessMode.Scripted admissions
        | Error error -> raise (ArgumentException error)

    let createForHostRecording (directory: string) (owners: obj) : Task<obj> =
        match ownerAdmissions owners with
        | Ok admissions -> createWithAdmissions directory (Some "host-recording") RetryHarnessMode.Scripted admissions
        | Error error -> raise (ArgumentException error)

    let private hostRecordingHarness value =
        let harness = unbox<Harness> value

        if not harness.Sessions.IsHostRecording then
            invalidArg "value" "Host recording requires its owned runtime harness"

        harness

    let internal withHostRuntime
        (value: obj)
        (useRuntime: SyncDelegateRuntime -> ISessionHostPort -> 'result)
        : 'result =
        let harness = hostRecordingHarness value
        useRuntime harness.Runtime (harness.Sessions :> ISessionHostPort)

    let private recordedModel (model: OpencodeModel) =
        box
            {| providerID = model.providerID
               modelID = model.modelID
               variant = model.variant |> Option.map box |> Option.defaultValue null |}

    let private recordedTools (tools: Map<string, bool>) =
        tools
        |> Map.toArray
        |> Array.map (fun (name, enabled) -> box {| name = name; enabled = enabled |})
        |> box

    let private recordedPromptView (recorded: RecordedPrompt) =
        let options = recorded.Options

        box
            {| sessionId = SessionId.value recorded.SessionId
               index = recorded.Index
               text = recorded.Text
               agent = options.Agent |> Option.map box |> Option.defaultValue null
               model = options.Model |> Option.map recordedModel |> Option.defaultValue null
               tools = options.Tools |> Option.map recordedTools |> Option.defaultValue null
               metadata = options.Metadata |> Option.defaultValue null
               listenerCount = recorded.ListenerCount |}

    let recordingSnapshot (value: obj) : obj =
        let harness = hostRecordingHarness value

        let sibling (recorded: RecordedCreation) =
            box
                {| ownerSessionId = SessionId.value recorded.Parent
                   physicalParentId =
                    recorded.PhysicalParent
                    |> Option.map (SessionId.value >> box)
                    |> Option.defaultValue null
                   sessionId = SessionId.value recorded.SessionId
                   title = recorded.Options.Title |> Option.map box |> Option.defaultValue null
                   agent = recorded.Options.Agent |> Option.map box |> Option.defaultValue null |}

        let child (recorded: RecordedCreation) =
            box
                {| parentSessionId = SessionId.value recorded.Parent
                   sessionId = SessionId.value recorded.SessionId
                   title = recorded.Options.Title |> Option.map box |> Option.defaultValue null
                   agent = recorded.Options.Agent |> Option.map box |> Option.defaultValue null |}

        box
            {| prompts = harness.Sessions.RecordedPrompts |> Array.map recordedPromptView
               createSibling = harness.Sessions.RecordedSiblings |> Array.map sibling
               createChild = harness.Sessions.RecordedChildren |> Array.map child
               listedFamilies = harness.Sessions.ListedFamilies |}

    let awaitRecordingPromptCount (value: obj) (count: int) : Task =
        let harness = hostRecordingHarness value

        let rec wait observed =
            task {
                if harness.Sessions.RecordedPromptCount >= count then
                    return ()
                else
                    let! next = harness.Readiness.WaitForRevisionAfter observed
                    return! wait next
            }

        wait harness.Readiness.Revision :> Task

    let returnRecordingPromptOutcome (value: obj) (session: string) (index: int) (outcome: obj) : bool =
        let harness = hostRecordingHarness value
        harness.Sessions.ReturnPromptOutcome(SessionId.create session, index, unbox<SendOutcome> outcome)

    let createForProviderRecovery (directory: string) (owners: obj) : Task<obj> =
        match ownerAdmissions owners with
        | Error error -> raise (ArgumentException error)
        | Ok admissions ->
            task {
                do! ModelRouting.initialize ()
                return! createWithAdmissions directory None RetryHarnessMode.ProviderRecovery admissions
            }

    let createForGuardRecovery (directory: string) (owners: obj) : Task<obj> =
        createForProviderRecovery directory owners

    let closeRecovery (value: obj) : Task = (unbox<Harness> value).CloseRecovery()

    let closeGuardRecovery (value: obj) : Task = closeRecovery value

    let observeGuardDelta (value: obj) (raw: obj) : unit =
        (unbox<Harness> value).Plugin.Value.LoopSensor.Observe raw

    let awaitGuardInterrupt (value: obj) session providerRun : Task =
        match
            (unbox<Harness> value)
                .Plugin.Value.LoopSensor.ActiveInterruptTask(
                    SessionId.create session,
                    ProviderRunIdentity.create providerRun
                )
        with
        | Some running -> running
        | None -> raise (InvalidOperationException "no guard interrupt exists for the exact provider run")

    let private createForObservation (directory: string) (observationMode: string option) : Task<obj> =
        match ownerAdmissionFor "managed-child-reconciliation" "manager" with
        | Ok admission -> createWithAdmissions directory observationMode RetryHarnessMode.Scripted [ admission ]
        | Error error -> raise (InvalidOperationException error)

    /// managed-session-lifecycle-001: drive SyncDelegateRuntime's production child
    /// observation into AttachedSessionRuntime against controlled Host callbacks.
    let managedChildReconciliationScenario (directory: string) (mode: string) : Task<obj> =
        task {
            let! value = createForObservation directory (Some mode)
            let harness = unbox<Harness> value
            let owner = harness.OwnerSession "managed-child-reconciliation"
            let ownerScope = ReuseScope.ofSession owner

            harness.Sessions.SetExpectedTitle(
                SyncDelegatePhysicalIdentity.title ownerScope SyncDelegateRole.Engineer "engineer"
            )

            let invocation =
                harness.Runtime.Invoke(SessionId.value owner, SyncDelegateRole.Engineer, "probe")

            let! child, error =
                if mode = "matching" || mode = "missing" || mode = "other-scope" then
                    task {
                        let! prompted = harness.Sessions.WaitForPrompt()
                        return SessionId.value prompted, ""
                    }
                else
                    task {
                        match! invocation with
                        | Ok _ -> return "", "expected reconciliation to fail closed"
                        | Error rejection -> return "", rejection
                    }

            let request = harness.Sessions.CreateRequests |> Array.tryHead

            let createParent, createTitle, createAgent =
                match request with
                | Some(parent, title, agent) -> parent, Option.defaultValue "" title, Option.defaultValue "" agent
                | None -> "", "", ""

            let result =
                box
                    {| listedFamilies = harness.Sessions.ListedFamilies
                       ownerScope = ReuseScopeId.value ownerScope
                       createCount = harness.Sessions.CreateRequests.Length
                       createParent = createParent
                       createTitle = createTitle
                       createAgent = createAgent
                       child = child
                       error = error |}

            harness.Dispose()
            return result
        }

    /// managed-session-lifecycle-001: two simultaneous callers for one exact key share
    /// the complete physical reconciliation transaction and its result.
    let concurrentAttachedGetOrCreateScenario () : Task<obj> =
        task {
            let attached = new AttachedSessionRuntime()

            let entered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — one-scenario physical observation counter
            let mutable observeCount = 0
            // DSL-MUTABLE: algorithm-scratch — one-scenario physical creation counter
            let mutable createCount = 0
            let owner = SessionId.create "concurrent-owner"

            let observe _ _ _ _ =
                task {
                    observeCount <- observeCount + 1
                    AsyncSupport.trySetResult entered () |> ignore
                    do! release.Task
                    return Ok AttachedChildObservation.Missing
                }

            let create _ _ _ _ _ =
                createCount <- createCount + 1
                Task.FromResult(Ok(SessionId.create "concurrent-child"))

            let get () =
                attached.GetOrCreate(
                    owner,
                    SyncDelegateRole.Engineer,
                    "engineer",
                    None,
                    observe,
                    create,
                    (fun _ _ _ -> ()),
                    (fun _ _ -> ())
                )

            let first = get ()
            do! entered.Task
            let second = get ()
            AsyncSupport.trySetResult release () |> ignore
            let! firstResult = first
            let! secondResult = second
            let results = [| firstResult; secondResult |]

            let children =
                results
                |> Array.map (function
                    | Ok(child, _) -> SessionId.value child
                    | Error error -> failwith error)

            return
                box
                    {| observeCount = observeCount
                       createCount = createCount
                       children = children |}
        }

    /// Run one internal Engineer research charge. The returned promise remains
    /// pending until `settle` receives a reconciled provider turn; the charge is
    /// data for a standard Engineer, never a tool-module surface.
    let executeEngineerCharge (value: obj) (owner: string) (charge: string) : Task<string> =
        task {
            let harness = unbox<Harness> value

            let! result =
                harness.Runtime.Invoke(SessionId.value (harness.OwnerSession owner), SyncDelegateRole.Engineer, charge)

            return
                match result with
                | Ok view -> Wanxiangshu.OpenCode.ToolHostCodec.tomlObjectWithInstructions [ view ] []
                | Error error -> failwith error
        }

    /// Invoke one ordinary managed delegation. The returned promise remains
    /// pending until `settle` receives a reconciled provider turn.
    let invoke (value: obj) (owner: string) (role: string) (question: string) : Task<obj> =
        task {
            let harness = unbox<Harness> value

            match syncInvocationRole role with
            | Error error -> return box {| ok = false; error = error |}
            | Ok role ->
                let! result = harness.Runtime.Invoke(SessionId.value (harness.OwnerSession owner), role, question)

                return
                    match result with
                    | Ok view -> box {| ok = true; value = view |}
                    | Error error -> box {| ok = false; error = error |}
        }

    let invokeResponse (value: obj) (owner: string) (question: string) : Task<obj> =
        task {
            let harness = unbox<Harness> value

            let! result =
                harness.Runtime.InvokeResponsePrepared(
                    SessionId.value (harness.OwnerSession owner),
                    SyncDelegateRole.Engineer,
                    question,
                    (fun () -> Task.FromResult(LlmFacing.instruction question))
                )

            return
                match result with
                | Ok response -> box {| ok = true; value = response |}
                | Error error -> box {| ok = false; error = error |}
        }

    let private hostOutcomeObservation =
        function
        | SendOutcome.AdmittedWithReceipt receipt ->
            box
                {| kind = "AdmittedWithReceipt"
                   value = TransportReceipt.value receipt |}
        | SendOutcome.AdmittedWithPhysicalMessage physical ->
            box
                {| kind = "AdmittedWithPhysicalMessage"
                   value = PhysicalUserMessageId.value physical |}
        | SendOutcome.Retryable reason -> box {| kind = "Retryable"; value = reason |}
        | SendOutcome.AcceptanceUnknown reason ->
            box
                {| kind = "AcceptanceUnknown"
                   value = reason |}
        | SendOutcome.Fatal reason -> box {| kind = "Fatal"; value = reason |}

    let private unacceptedObservation kind (evidence: SyncDelegateDispatchEvidence) reason =
        box
            {| kind = kind
               sessionId = SessionId.value evidence.SessionId
               promptKey = PromptKey.value evidence.PromptKey
               hostOutcome =
                evidence.HostOutcome
                |> Option.map hostOutcomeObservation
                |> Option.defaultValue null
               reason = reason |}

    let private admissionObservation =
        function
        | SyncDelegateObservedAdmission.Accepted accepted ->
            box
                {| kind = "Accepted"
                   sessionId = SessionId.value accepted.Dispatch.SessionId
                   promptKey = PromptKey.value accepted.Dispatch.PromptKey
                   hostOutcome =
                    accepted.Dispatch.HostOutcome
                    |> Option.map hostOutcomeObservation
                    |> Option.defaultValue null
                   physicalUserMessageId = PhysicalUserMessageId.value accepted.PhysicalUserMessageId
                   authorityRootUserMessageId = AuthorityRootUserMessageId.value accepted.AuthorityRootUserMessageId |}
        | SyncDelegateObservedAdmission.NotDispatched reason ->
            box
                {| kind = "NotDispatched"
                   reason = reason |}
        | SyncDelegateObservedAdmission.Refused(evidence, reason) -> unacceptedObservation "Refused" evidence reason
        | SyncDelegateObservedAdmission.Unconfirmed(evidence, reason) ->
            unacceptedObservation "Unconfirmed" evidence reason

    let startObserved (value: obj) (owner: string) (charge: string) : obj =
        let harness = unbox<Harness> value

        harness.Runtime.InvokeObservedPrepared(
            harness.OwnerSession owner,
            charge,
            (fun () -> Task.FromResult(HostForkProse.chargeDocument (harness.OwnerSession owner) charge))
        )
        |> box

    let startObservedWithPreparationFailure (value: obj) (owner: string) (charge: string) (reason: string) : obj =
        let harness = unbox<Harness> value

        harness.Runtime.InvokeObservedPrepared(
            harness.OwnerSession owner,
            charge,
            (fun () -> task { return raise (InvalidOperationException reason) })
        )
        |> box

    let observedAdmission (execution: obj) : Task<obj> =
        task {
            let! admission = (unbox<SyncDelegateObservedExecution> execution).Admission
            return admissionObservation admission
        }

    let observedCompletion (execution: obj) : Task<obj> =
        task {
            let! result = (unbox<SyncDelegateObservedExecution> execution).Completion

            return
                match result with
                | Error reason -> box {| ok = false; error = reason |}
                | Ok response ->
                    box
                        {| ok = true
                           value =
                            {| sessionId = SessionId.value response.SessionId
                               physicalUserMessageId = PhysicalUserMessageId.value response.PhysicalUserMessageId
                               authorityRootUserMessageId =
                                AuthorityRootUserMessageId.value response.AuthorityRootUserMessageId
                               providerRun = ProviderRunIdentity.value response.ProviderRun
                               formalText = response.FormalText |} |}
        }

    let private requestedPrompt (harness: Harness) owner role index =
        roleOf role
        |> Result.toOption
        |> Option.bind (fun role -> harness.Runtime.TryFind(harness.OwnerSession owner, role))
        |> Option.bind (fun child -> harness.Sessions.PromptKey(child, index) |> Option.map (fun key -> child, key))

    let returnPromptOutcome (value: obj) owner role index (outcome: obj) : bool =
        let harness = unbox<Harness> value

        requestedPrompt harness owner role index
        |> Option.map (fun (child, _) -> harness.Sessions.ReturnPromptOutcome(child, index, unbox<SendOutcome> outcome))
        |> Option.defaultValue false

    let rejectPrompt (value: obj) owner role index reason : bool =
        let harness = unbox<Harness> value

        requestedPrompt harness owner role index
        |> Option.map (fun (child, _) -> harness.Sessions.RejectPrompt(child, index, reason))
        |> Option.defaultValue false

    let promptIdentity (value: obj) owner role index : obj =
        let harness = unbox<Harness> value

        requestedPrompt harness owner role index
        |> Option.map (fun (child, key) ->
            box
                {| sessionId = SessionId.value child
                   promptKey = PromptKey.value key |})
        |> Option.defaultValue null

    let promptClaimState (value: obj) owner role index : obj =
        let harness = unbox<Harness> value

        match requestedPrompt harness owner role index with
        | None -> null
        | Some(child, key) ->
            let projection = harness.Dispatcher.ProjectionFor child

            let kind =
                if Map.containsKey key projection.PendingClaims then
                    "Pending"
                elif
                    projection.PhysicalLandings
                    |> Map.exists (fun _ accepted -> accepted.PromptKey = key)
                then
                    "Accepted"
                else
                    "Missing"

            box
                {| kind = kind
                   promptKey = PromptKey.value key |}

    let confirmPromptPhysical (value: obj) owner role index (physical: string) : Task<bool> =
        task {
            let harness = unbox<Harness> value

            match requestedPrompt harness owner role index with
            | None -> return false
            | Some(child, key) ->
                let physical = PhysicalUserMessageId.create physical

                let acceptance =
                    match harness.Sessions.PromptOrigin(child, index) with
                    | Some "AgentOwnerRoot" ->
                        harness.Dispatcher.AcceptAgentOwnerRoot key child physical
                        |> TaskValue.map (Result.map ignore)
                    | Some "ManagedDelegationAssignment" ->
                        harness.Dispatcher.AcceptContinuation key child physical
                        |> TaskValue.map (Result.map ignore)
                    | _ -> Task.FromResult(Error "Prompt is not a managed delegation assignment")

                match! acceptance with
                | Error _ -> return false
                | Ok() ->
                    harness.Sessions.RecordPhysical(child, physical)
                    return true
        }

    let confirmManagedPromptPhysical (value: obj) owner role index (physical: string) : Task<obj> =
        task {
            let harness = unbox<Harness> value

            match requestedPrompt harness owner role index with
            | None ->
                return
                    box
                        {| ok = false
                           error = "Prompt is not captured by this managed child" |}
            | Some _ when String.IsNullOrWhiteSpace physical ->
                return
                    box
                        {| ok = false
                           error = "Physical user message id must be non-empty" |}
            | Some(child, key) ->
                let physicalId = PhysicalUserMessageId.create physical

                let message: ChatAdmissionIntent.DecodedMessage =
                    { SessionId = Some child
                      PhysicalUserMessageId = Some physicalId
                      InvalidIdentityCarrier = None
                      ExplicitAgent = None
                      PromptKey = Some key
                      IsHostCompaction = false
                      IsHostSynthetic = false
                      Text = None }

                let decision = PromptIngress.resolveDecision (Some harness.Journal) message
                let! accepted = harness.Dispatcher.AcceptManagedChatIntent decision

                match accepted with
                | Error error ->
                    return
                        box
                            {| ok = false
                               error = sprintf "%A" error |}
                | Ok witness ->
                    let evidence = ManagedChatAcceptanceWitness.evidence witness
                    harness.Sessions.RecordPhysical(child, evidence.PhysicalUserMessageId)

                    return
                        box
                            {| ok = true
                               sessionId = SessionId.value evidence.SessionId
                               physicalUserMessageId = PhysicalUserMessageId.value evidence.PhysicalUserMessageId
                               authorityRootUserMessageId =
                                AuthorityRootUserMessageId.value evidence.AuthorityRootUserMessageId
                               logicalRunId = LogicalRunId.value evidence.LogicalRunId
                               origin = PromptAuthority.originLabel evidence.Origin
                               participant = AcceptedChatExecutionEvidence.participant evidence
                               role = Roles.roleLabel (AcceptedChatExecutionEvidence.canonicalRole evidence) |}
        }

    let observeProviderFailureStop (value: obj) (session: string) (providerRun: string) : bool =
        let harness = unbox<Harness> value

        let child =
            harness.Children |> Seq.tryFind (fun child -> SessionId.value child = session)

        match harness.UsesProviderRecovery, child with
        | true, Some child when not (String.IsNullOrWhiteSpace providerRun) ->
            ProviderAttemptStopFence.shared.Observe(child, ProviderRunIdentity.create providerRun)
            true
        | _ -> false

    let recoveryStopSnapshot (value: obj) : obj =
        let harness = unbox<Harness> value

        if not harness.UsesProviderRecovery then
            invalidArg "value" "Provider recovery requires its owned runtime harness"

        let snapshot = ProviderAttemptStopFence.shared.Snapshot()

        box
            {| stopped = snapshot.Stopped
               waiting = snapshot.Waiting
               denied = snapshot.Denied |}

    let terminalListenerCount (value: obj) : int =
        (unbox<Harness> value).Sessions.TerminalListenerCount

    let private handleTurn
        (harness: Harness)
        (child: SessionId)
        (failure: ExecutionFailure option)
        (turn: ReconciledTurn)
        =
        task {
            // delegation-031: the terminal capture inside HandleTurn writes through
            // the production journal. A released writer surfaces as
            // XTraceCaptureError.StorageAppendFailed carrying the typed
            // JournalAppendFailure — not a crash. WriterUnavailable/WriteUnknown
            // (NotCommitted/Unknown) deliver the earned completion from the
            // turn's own parts; the checkpoint stays pending-evidence. Only the
            // FactRejected cut propagates (still fatal at the journal boundary).
            try
                let! handled = harness.Runtime.HandleTurn(turn, failure, None)

                if handled then
                    harness.Readiness.Complete child

                return handled
            with :? JournalAppendException as append ->
                match append.Failure with
                | JournalAppendFailure.FactRejected _ -> return raise append
                | JournalAppendFailure.WriterUnavailable _
                | JournalAppendFailure.NoNewWriteReleaseFailed _
                | JournalAppendFailure.WriteUnknown _ ->
                    let settled = harness.Runtime.SettleCompletedFromTurn turn

                    if settled then
                        harness.Readiness.Complete child

                    return settled
        }

    let private activeAuthorityRoot (harness: Harness) (child: SessionId) =
        PromptAuthorityProjectionQueries.activeProfile child (AgentJournal.snapshot harness.Journal).AgentProjections
        |> Option.map (fun profile -> AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)

    /// delegation-031: the causal root this call actually accepted, read from the
    /// live call — not from the durable projection. PromptAuthority facts are
    /// journal appends: after the writer is released the projection freezes and
    /// can no longer answer, but the in-memory call still knows the exact root
    /// its own acceptance bound. Falls back to the projection for the
    /// pre-acceptance path where no call exists yet.
    let private acceptedRootFor (harness: Harness) (owner: SessionId) (role: SyncDelegateRole) (child: SessionId) =
        match harness.Runtime.TryAcceptedAuthorityRoot child with
        | Some root -> Some root
        | None -> activeAuthorityRoot harness child

    let private exactTerminalTurn session physical authorityRoot providerRun parts : ReconciledTurn =
        { SessionId = SessionId.create session
          PhysicalUserMessageId = PhysicalUserMessageId.create physical
          AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRoot
          ProviderRun = ProviderRunIdentity.create providerRun
          Role = Some Role.Engineer
          Directory = None
          Parts = parts
          Finish = Some "stop"
          ErrorName = None
          Model = None
          Outcome = ReconcileProgram.TurnCompleted
          Observation = None }

    let settleExactTerminal
        (value: obj)
        session
        physical
        authorityRoot
        providerRun
        formalText
        reasoningText
        : Task<bool> =
        let harness = unbox<Harness> value

        let turn =
            exactTerminalTurn
                session
                physical
                authorityRoot
                providerRun
                [| MessagePart.Text formalText
                   MessagePart.Reasoning reasoningText
                   MessagePart.ToolResult("observed-tool", "TOOL-RESULT-ONLY") |]

        handleTurn harness turn.SessionId None turn

    let settleExactFallback (value: obj) session physical authorityRoot providerRun formalText : bool =
        let harness = unbox<Harness> value

        let turn =
            exactTerminalTurn session physical authorityRoot providerRun [| MessagePart.Text formalText |]

        harness.Runtime.SettleCompletedFromTurn turn

    let observeExactProviderFailure (value: obj) session physical authorityRoot providerRun reason : Task<bool> =
        let harness = unbox<Harness> value

        let turn =
            { exactTerminalTurn session physical authorityRoot providerRun [||] with
                Outcome = ReconcileProgram.TurnFailed reason }

        handleTurn harness turn.SessionId (Some ExecutionFailure.ProviderTransient) turn

    let private observeRecoveryTurn (harness: Harness) abortCause context : Task =
        let owner = harness.Plugin |> Option.get

        TurnWorkflow.observe
            (harness.Sessions :> ISessionHostPort)
            harness.RootWorkspace
            harness.EventPort
            (Some harness.Journal)
            (Some harness.Runtime)
            harness.NudgeSent
            harness.JoinGuardNudges
            (fun _ -> false)
            abortCause
            (harness.Gate :> ISessionQuiescenceGate)
            context

    let observeExactGuardAbort (value: obj) session physical authorityRoot providerRun : Task =
        let harness = unbox<Harness> value

        let context: ReconciledTurnContext =
            { Turn =
                { exactTerminalTurn session physical authorityRoot providerRun [||] with
                    Directory = harness.RootWorkspace.TryRead()
                    Outcome = ReconcileProgram.TurnAborted "guard interrupt" }
              Failure = None
              Quiescence = None
              Delivery = ReconciledTurnDelivery.Observation }

        HostTurnObserver.observe
            (observeRecoveryTurn harness)
            (harness.Sessions :> ISessionHostPort)
            harness.RootWorkspace
            harness.EventPort
            (Some harness.Journal)
            harness.Plugin.Value
            context


    let observeRepairTurn
        (value: obj)
        session
        physical
        authorityRoot
        providerRun
        (finish: string)
        delivery
        idleEvidence
        : Task =
        let harness = unbox<Harness> value
        let sessionId = SessionId.create session
        let physicalId = PhysicalUserMessageId.create physical

        let assistant: SessionMessage =
            { Id = providerRun
              Role = "assistant"
              Agent = Some "engineer"
              Finish = if isNullish (box finish) then None else Some finish
              ErrorName = None
              Model = None
              ParentId = Some physical
              CreatedAt = None
              Completed = finish = "length"
              IsCompaction = false
              PromptKey = None
              Parts = [||]
              PartIds = [||]
              ToolParts = [||] }

        let permit =
            if idleEvidence then
                harness.Gate.ObservePhysicalUserMessage(sessionId, physicalId)
                harness.Gate.BeginProviderAttempt sessionId
                Some(harness.Gate.ObserveIdle sessionId)
            else
                None

        let context: ReconciledTurnContext =
            { Turn =
                CompletedTurnClassifier.buildTurn
                    sessionId
                    physicalId
                    (AuthorityRootUserMessageId.create authorityRoot)
                    assistant
                    (Some Role.Engineer)
                    (harness.RootWorkspace.TryRead())
              Failure = None
              Quiescence = permit
              Delivery =
                match delivery with
                | "observation" -> ReconciledTurnDelivery.Observation
                | "idle" -> ReconciledTurnDelivery.IdleRevisit
                | _ -> raise (ArgumentException "unknown repair delivery") }

        observeRecoveryTurn harness AbortCause.External context

    let private settleReadyChild
        (harness: Harness)
        (role: SyncDelegateRole)
        (child: SessionId)
        (answer: string)
        (runId: string)
        (physical: PhysicalUserMessageId)
        (authorityRoot: string)
        =
        let parts =
            if String.IsNullOrWhiteSpace answer then
                [||]
            else
                [| MessagePart.Text answer |]

        handleTurn
            harness
            child
            None
            { SessionId = child
              PhysicalUserMessageId = physical
              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create authorityRoot
              ProviderRun = ProviderRunIdentity.create runId
              Role = Some(roleValue role)
              Directory = None
              Parts = parts
              Finish = Some "stop"
              ErrorName = None
              Model = None
              Outcome = ReconcileProgram.TurnCompleted
              Observation = None }

    /// Settle the current managed child through the real HandleTurn path.
    let settleWithAuthorityRoot
        (value: obj)
        (owner: string)
        (role: string)
        (answer: string)
        (runId: string)
        (authorityRoot: string)
        : Task<bool> =
        task {
            let harness = unbox<Harness> value

            match roleOf role with
            | Error _ -> return false
            | Ok role ->
                match! waitForReadyCall harness.Runtime harness.Readiness (harness.OwnerSession owner) role with
                | None -> return false
                | Some child ->
                    match harness.Sessions.LatestAcceptedPhysical child with
                    | Some physical -> return! settleReadyChild harness role child answer runId physical authorityRoot
                    | None -> return false
        }

    let settle (value: obj) (owner: string) (role: string) (answer: string) (runId: string) : Task<bool> =
        task {
            let harness = unbox<Harness> value

            match roleOf role with
            | Error _ -> return false
            | Ok role ->
                match! waitForReadyCall harness.Runtime harness.Readiness (harness.OwnerSession owner) role with
                | None -> return false
                | Some child ->
                    let ownerSession = harness.OwnerSession owner

                    match
                        harness.Sessions.LatestAcceptedPhysical child, acceptedRootFor harness ownerSession role child
                    with
                    | Some physical, Some root ->
                        try
                            return! settleReadyChild harness role child answer runId physical root
                        with :? JournalAppendException ->
                            return false
                    | _ -> return false
        }

    let failWithAuthorityRoot
        (value: obj)
        (owner: string)
        (role: string)
        (reason: string)
        (authorityRoot: string)
        : Task<string> =
        task {
            let harness = unbox<Harness> value

            match roleOf role with
            | Error _ -> return "Unavailable"
            | Ok role ->
                match harness.Runtime.TryFind(harness.OwnerSession owner, role) with
                | None -> return "Unavailable"
                | Some child ->
                    let! accepted = harness.Runtime.AwaitAssignmentReady child

                    if not accepted then
                        return "Unavailable"
                    else
                        harness.Sessions.Notify(
                            child,
                            TerminalOutcome.Failed(
                                TerminalStop.forAuthority (AuthorityRootUserMessageId.create authorityRoot) reason
                            )
                        )

                        return
                            if harness.Runtime.HasOpeningCursor child then
                                "Ignored"
                            else
                                "Claimed"
        }

    let observeTurn
        (value: obj)
        (owner: string)
        (role: string)
        (outcomeName: string)
        (answer: string)
        (runId: string)
        : Task<bool> =
        task {
            let harness = unbox<Harness> value

            match roleOf role, outcomeOf outcomeName with
            | Error _, _
            | _, Error _ -> return false
            | Ok role, Ok outcome ->
                let outcome =
                    match outcome with
                    | ReconcileProgram.TurnFailed _ when not (String.IsNullOrWhiteSpace answer) ->
                        ReconcileProgram.TurnFailed answer
                    | current -> current

                match! waitForReadyCall harness.Runtime harness.Readiness (harness.OwnerSession owner) role with
                | None -> return false
                | Some child ->
                    let ownerSession = harness.OwnerSession owner

                    match
                        harness.Sessions.LatestAcceptedPhysical child, acceptedRootFor harness ownerSession role child
                    with
                    | Some physical, Some root ->
                        let parts =
                            if outcomeName = "TurnCompleted" && not (String.IsNullOrWhiteSpace answer) then
                                [| MessagePart.Text answer |]
                            else
                                [||]

                        // A reconciled failure carries the confirmed typed witness;
                        // the injected retry decorator owns what happens next.
                        let failure =
                            match outcome with
                            | ReconcileProgram.TurnFailed _ -> Some ExecutionFailure.ProviderTransient
                            | _ -> None

                        let turn =
                            { SessionId = child
                              PhysicalUserMessageId = physical
                              AuthorityRootUserMessageId = AuthorityRootUserMessageId.create root
                              ProviderRun = ProviderRunIdentity.create runId
                              Role = Some(roleValue role)
                              Directory = None
                              Parts = parts
                              Finish = Some "stop"
                              ErrorName = None
                              Model = None
                              Outcome = outcome
                              Observation = None }

                        return! handleTurn harness child failure turn
                    | _ -> return false
        }

    let child (value: obj) (owner: string) (role: string) : obj =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> null
        | Ok role ->
            match harness.Runtime.TryFind(harness.OwnerSession owner, role) with
            | Some sessionId -> box (SessionId.value sessionId)
            | None -> null

    let stageDeletedDelegate (value: obj) (owner: string) : bool =
        let harness = unbox<Harness> value
        let ownerSession = harness.OwnerSession owner

        match harness.Runtime.TryFind(ownerSession, SyncDelegateRole.Engineer) with
        | Some child -> harness.Runtime.StageDeletedDelegate(ownerSession, child)
        | None -> false

    let scopeCloseChild (value: obj) (owner: string) (role: string) : obj =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> null
        | Ok role ->
            harness.Runtime.TryFindForScopeClose(harness.OwnerSession owner, role)
            |> Option.map (SessionId.value >> box)
            |> Option.defaultValue null

    let cancelSession (value: obj) (session: string) : unit =
        let harness = unbox<Harness> value
        harness.Runtime.CancelSession(harness.OwnerSession session)

    let vocabulary (roleName: string) (tierName: string) (scope: string) : obj =
        ignore tierName

        match roleOf roleName with
        | Error error -> box {| ok = false; error = error |}
        | Ok role ->
            let key = DedicatedDelegateKey.create (ReuseScopeId.create scope) role

            box
                {| agent = SyncDelegate.agentNameFor role
                   scope = ReuseScopeId.value key.Scope
                   role = SyncDelegate.roleLabel key.Role |}

    let childCount (value: obj) : int =
        let harness = unbox<Harness> value
        harness.Children.Count

    let promptCount (value: obj) (owner: string) (role: string) : int =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> 0
        | Ok role ->
            harness.Runtime.TryFind(harness.OwnerSession owner, role)
            |> Option.map harness.Readiness.AdmittedCount
            |> Option.defaultValue 0

    let rec private waitForRegisteredPrompt
        (harness: Harness)
        (owner: string)
        (role: SyncDelegateRole)
        (count: int)
        (observedRevision: int)
        : Task =
        task {
            match harness.Runtime.TryFind(harness.OwnerSession owner, role) with
            | Some child -> do! harness.Readiness.WaitForCount(child, count)
            | None ->
                let! nextRevision = harness.Readiness.WaitForRevisionAfter observedRevision
                return! waitForRegisteredPrompt harness owner role count nextRevision
        }
        :> Task

    let awaitPromptCount (value: obj) (owner: string) (role: string) (count: int) : Task =
        task {
            let harness = unbox<Harness> value

            match roleOf role with
            | Error error -> return raise (ArgumentException error)
            | Ok role -> do! waitForRegisteredPrompt harness owner role count harness.Readiness.Revision
        }
        :> Task

    let acceptPrompt (value: obj) (owner: string) (role: string) (index: int) : bool =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> false
        | Ok role ->
            match harness.Runtime.TryFind(harness.OwnerSession owner, role) with
            | Some child -> harness.Sessions.AcceptPrompt(child, index)
            | None -> false

    let promptOrigin (value: obj) (owner: string) (role: string) (index: int) : obj =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> null
        | Ok role ->
            harness.Runtime.TryFind(harness.OwnerSession owner, role)
            |> Option.bind (fun child -> harness.Sessions.PromptOrigin(child, index))
            |> Option.map box
            |> Option.defaultValue null

    let prompt (value: obj) (owner: string) (role: string) (index: int) : obj =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> null
        | Ok role ->
            harness.Runtime.TryFind(harness.OwnerSession owner, role)
            |> Option.bind (fun child -> harness.Readiness.Prompt(child, index))
            |> Option.map box
            |> Option.defaultValue null

    let captureOwnerOpening (value: obj) (owner: string) (text: string) : Task =
        task {
            let harness = unbox<Harness> value

            match!
                XTraceCapture.captureOpeningWithReceipt (Some harness.Journal) (harness.OwnerSession owner) text []
            with
            | Ok _ -> ()
            | Error error -> return raise (InvalidOperationException(sprintf "%A" error))
        }
        :> Task

    let captureOwnerDeltaPart (value: obj) (owner: string) (text: string) (providerRun: string) : Task =
        task {
            let harness = unbox<Harness> value

            match! harness.Journal.WriteBlob text with
            | Error error -> return raise (InvalidOperationException error)
            | Ok blob ->
                match!
                    XTraceCapture.captureLastWordsWithReceipt
                        (Some harness.Journal)
                        (harness.OwnerSession owner)
                        blob.BlobRef
                        blob.BlobDigest
                        (ProviderRunIdentity.create providerRun)
                with
                | Ok _ -> ()
                | Error error -> return raise (InvalidOperationException(sprintf "%A" error))
        }
        :> Task

    let batchOrder (roleName: string) (toolNames: string array) (currentCall: string) : obj =
        match roleOf roleName with
        | Error error -> box {| ok = false; error = error |}
        | Ok role ->
            let order =
                toolNames
                |> Array.choose (fun name ->
                    match SyncDelegate.tryRoleOfToolName name with
                    | Some matched when matched = role -> Some name
                    | _ -> None)

            box
                {| order = order
                   currentPresent = order |> Array.exists (fun name -> name = currentCall) |}

    let invokeBatch
        (value: obj)
        (owner: string)
        (role: string)
        (charge: string)
        (providerRun: string)
        (callId: string)
        (callOrder: string array)
        : Task<obj> =
        task {
            let harness = unbox<Harness> value

            match syncInvocationRole role with
            | Error error -> return box {| kind = "Error"; error = error |}
            | Ok role ->
                let batch =
                    { ProviderRun = ProviderRunIdentity.create providerRun
                      CallOrder = callOrder |> Array.toList |> List.map ToolCallId.create
                      CurrentCall = ToolCallId.create callId }

                let! result =
                    harness.Runtime.InvokeBatchPrepared(
                        SessionId.value (harness.OwnerSession owner),
                        role,
                        charge,
                        batch,
                        (fun () -> Task.FromResult(HostForkProse.chargeDocument (harness.OwnerSession owner) charge))
                    )

                return
                    match result with
                    | Ok(SyncDelegateInvocationResult.View record) -> box {| kind = "View"; value = record |}
                    | Ok(SyncDelegateInvocationResult.MergedInto canonical) ->
                        box
                            {| kind = "MergedInto"
                               canonical = ToolCallId.value canonical |}
                    | Error error -> box {| kind = "Error"; error = error |}
        }


    let serializationDecision (firstScope: string) (secondScope: string) (sameProviderRun: bool) : obj =
        let sameScope =
            ReuseScopeId.equals (ReuseScopeId.create firstScope) (ReuseScopeId.create secondScope)

        if sameScope && not sameProviderRun then
            box
                {| accepted = false
                   reason = "same ReuseScope already has an active batch" |}
        else
            box
                {| accepted = true
                   reason = "independent provider batch" |}

    let evidenceBoundary (charge: string) (view: string) : obj =
        box
            {| charge = charge
               view = view
               authorityTransferred = false |}

    /// Script the retry decorator's verdicts for the next confirmed failures:
    /// `"dispatched"`, `"superseded"` or `"terminal:<reason>"`.
    let scriptRetry (value: obj) (verdicts: string array) : unit =
        let harness = unbox<Harness> value

        for verdict in verdicts do
            match verdict.Split(':', 2) with
            | [| "dispatched" |] -> harness.RetryScript.Push(Ok())
            | [| "superseded" |] -> harness.RetryScript.Push(Ok())
            | [| "terminal"; reason |] -> harness.RetryScript.Push(Error reason)
            | _ -> invalidArg "verdicts" (sprintf "unknown retry verdict script: %s" verdict)

    /// How many times the decorated path asked the retry decorator for a verdict.
    let retryCalls (value: obj) : int =
        (unbox<Harness> value).RetryScript.Calls

    /// Model the Host accepting the retry attempt the decorator dispatched: the
    /// same child receives a fresh ready prompt, exactly as in production.
    let dispatchRetryAttempt (value: obj) (owner: string) (role: string) : bool =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> false
        | Ok role ->
            match harness.Runtime.TryFind(harness.OwnerSession owner, role) with
            | None -> false
            | Some child ->
                harness.Readiness.Mark(child, "provider-retry-attempt")
                true

    let dispose (value: obj) : unit =
        unbox<Harness> value |> fun harness -> harness.Dispose()

    /// delegation-031 probe: the parent supersede guard — abandon the pending call
    /// for this delegate so a stale completion afterwards cannot claim it.
    let abandonPendingCall (value: obj) (owner: string) (role: string) : bool =
        let harness = unbox<Harness> value

        match roleOf role with
        | Error _ -> false
        | Ok syncRole ->
            match harness.Runtime.TryFind(harness.OwnerSession owner, syncRole) with
            | None -> false
            | Some _ ->
                harness.Runtime.CancelSession(harness.OwnerSession owner)
                true

    let stageDeferredInspection
        (sessionId: string)
        (callId: string)
        (charge: string)
        (keywords: string)
        (estimate: int option)
        : string =
        SyncDelegateBatching.stageDeferredInspection sessionId (ToolCallId.create callId) charge keywords estimate

    let applyReplacedResults (messages: obj list) : obj list =
        SyncDelegateBatching.applyReplacedResults messages
