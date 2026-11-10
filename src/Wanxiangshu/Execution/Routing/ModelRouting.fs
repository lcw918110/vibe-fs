namespace Wanxiangshu.Execution.Routing

open Wanxiangshu.Foundation.HostSession
open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution

[<RequireQualifiedAccess>]
type internal PhysicalExecutionReleaseOutcome =
    | Released of CapacityTransitionOutcome
    | HeldForInput

module ModelRouting =

    let internal failureOfExecutionAdmissionAcquisition =
        function
        | ExecutionAdmissionAcquisition.QueueFull -> Some ExecutionFailure.CapacityQueueFull
        | ExecutionAdmissionAcquisition.Cancelled -> Some ExecutionFailure.UserCancelled
        | ExecutionAdmissionAcquisition.Superseded -> Some ExecutionFailure.Superseded
        | ExecutionAdmissionAcquisition.Admitted _
        | ExecutionAdmissionAcquisition.Queued _ -> None

    let internal capacityOwnership (lease: ExecutionAdmissionLease) =
        lease
        |> box
        |> ExactCapacityFenceReference.Create
        |> CapacityOwnership.OwnsExactFence

    type private RoutingProtocolLibrary =
        abstract route: route: obj * role: string * running: obj array * previous: obj -> obj

    [<Import("RoutingProtocol", "wanxiangli/routing")>]
    let private protocolClass: obj = jsNative

    [<Emit("new $0()")>]
    let private createProtocol (cls: obj) : RoutingProtocolLibrary = jsNative

    let private protocol = createProtocol protocolClass

    let private targetOf (value: obj) : ModelRoutingTarget =
        { Model = unbox<string> value?model
          Reasoning = unbox<string> value?reasoning }

    let private targetObject (target: ModelRoutingTarget) : obj =
        createObj [ "model" ==> target.Model; "reasoning" ==> target.Reasoning ]

    let private optionalValue convert (value: obj) =
        if isNull value then None else Some(convert value)

    let private optionalObject convert value =
        value |> Option.map convert |> Option.defaultValue null

    let private roleOf (value: obj) =
        let name = unbox<string> value

        Roles.tryParseRole name
        |> Option.defaultWith (fun () -> invalidOp ("wanxiangli/routing returned an unknown role: " + name))

    let internal executionAdmissionIdentity (lease: ExecutionAdmissionLease) : ExecutionAdmissionExactIdentity =
        let identity = lease.identity

        { SessionId = unbox<string> identity?sessionId
          PhysicalUserMessageId = unbox<string> identity?physicalUserMessageId
          Role = roleOf identity?role
          Participant = unbox<string> identity?participant
          Target = targetOf identity?target }

    let private identityObject (identity: ExecutionAdmissionExactIdentity) : obj =
        createObj
            [ "sessionId" ==> identity.SessionId
              "physicalUserMessageId" ==> identity.PhysicalUserMessageId
              "role" ==> Roles.roleLabel identity.Role
              "participant" ==> identity.Participant
              "target" ==> targetObject identity.Target ]

    let private purposeLabel =
        function
        | ModelExecutionPurpose.Normal -> "normal"

    let private acquisitionOf (value: obj) =
        match unbox<string> value?kind with
        | "Admitted" -> ExecutionAdmissionAcquisition.Admitted(unbox<ExecutionAdmissionLease> value?lease)
        | "Queued" -> ExecutionAdmissionAcquisition.Queued(unbox<ExecutionAdmissionQueueNode> value?node)
        | "QueueFull" -> ExecutionAdmissionAcquisition.QueueFull
        | "Cancelled" -> ExecutionAdmissionAcquisition.Cancelled
        | "Superseded" -> ExecutionAdmissionAcquisition.Superseded
        | other -> invalidOp ("wanxiangli/routing returned an unknown acquisition: " + other)

    let private acquisitionTask (pending: Task<obj>) =
        task {
            let! value = pending
            return acquisitionOf value
        }

    let internal awaitQueuedAdmission (node: ExecutionAdmissionQueueNode) = acquisitionTask node.completion

    let private outcomeOf =
        function
        | "Applied" -> CapacityTransitionOutcome.Applied
        | "AlreadyApplied" -> CapacityTransitionOutcome.AlreadyApplied
        | "StaleFence" -> CapacityTransitionOutcome.StaleFence
        | "Conflict" -> CapacityTransitionOutcome.Conflict
        | other -> invalidOp ("wanxiangli/routing returned an unknown transition: " + other)

    let private rejectionOf =
        function
        | "UnknownLease" -> ExecutionAdmissionRejection.UnknownLease
        | "WrongFence" -> ExecutionAdmissionRejection.WrongFence
        | "StaleLease" -> ExecutionAdmissionRejection.StaleLease
        | "WrongSession" -> ExecutionAdmissionRejection.WrongSession
        | "WrongPhysicalUserMessage" -> ExecutionAdmissionRejection.WrongPhysicalUserMessage
        | "WrongRole" -> ExecutionAdmissionRejection.WrongRole
        | "WrongParticipant" -> ExecutionAdmissionRejection.WrongParticipant
        | "WrongTarget" -> ExecutionAdmissionRejection.WrongTarget
        | "IllegalTransition" -> ExecutionAdmissionRejection.IllegalTransition
        | "OppositeTerminalConflict" -> ExecutionAdmissionRejection.OppositeTerminalConflict
        | other -> invalidOp ("wanxiangli/routing returned an unknown rejection: " + other)

    let private permitResult convert (value: obj) =
        if unbox<bool> value?ok then
            Ok(convert value?value)
        else
            Error(rejectionOf (unbox<string> value?rejection))

    [<Emit("BigInt($0)")>]
    let private integerOf (_value: obj) : int64 = jsNative

    let private ownerOf (value: obj) : CapacityExactOwnerSnapshot =
        { SessionId = unbox<string> value?sessionId
          PhysicalUserMessageId = unbox<string> value?physicalUserMessageId
          Role = optionalValue roleOf value?role
          Participant = optionalValue unbox<string> value?participant }

    let private snapshotOf (value: obj) : CapacityInvariantEvidence =
        let counters: obj = value?counters

        { LedgerEntries =
            unbox<obj array> value?ledgerEntries
            |> Array.map (fun entry ->
                { Credit = integerOf entry?credit
                  Target = targetOf entry?target })
          Tokens =
            unbox<obj array> value?tokens
            |> Array.map (fun token ->
                { Credit = integerOf token?credit
                  State = unbox<string> token?state
                  Owner = ownerOf token?owner
                  Target = targetOf token?target })
          Custodies =
            unbox<obj array> value?custodies
            |> Array.map (fun custody ->
                { Credit = integerOf custody?credit
                  Owner = ownerOf custody?owner })
          Executions = unbox<obj array> value?executions |> Array.map ownerOf
          Waiters =
            unbox<obj array> value?waiters
            |> Array.map (fun waiter ->
                { Owner = ownerOf waiter?owner
                  Sequence = integerOf waiter?sequence
                  Kind = unbox<string> waiter?kind })
          Owners = unbox<obj array> value?owners |> Array.map ownerOf
          Lineage =
            unbox<obj array> value?lineage
            |> Array.map (fun edge ->
                { ParentSessionId = unbox<string> edge?parentSessionId
                  ChildSessionId = unbox<string> edge?childSessionId })
          IdleCount = unbox<int> value?idleCount
          InFlightCount = unbox<int> value?inFlightCount
          RetiringCount = unbox<int> value?retiringCount
          ActiveCount = unbox<int> value?activeCount
          Counters =
            { Duplicate = integerOf counters?duplicate
              Stale = integerOf counters?stale
              Conflict = integerOf counters?conflict } }

    let invokeScheduler
        (scheduler: obj)
        (role: string)
        (running: ModelRoutingTarget array)
        (previous: ModelRoutingTarget option)
        (purpose: ModelExecutionPurpose)
        : ModelRoutingTarget option =
        ignore purpose

        protocol.route (scheduler, role, Array.map targetObject running, optionalObject targetObject previous)
        |> optionalValue targetOf

    let toHostModel (target: ModelRoutingTarget) : HostModel =
        let slash = target.Model.IndexOf '/'

        { providerID = target.Model.Substring(0, slash)
          modelID = target.Model.Substring(slash + 1)
          variant =
            if String.IsNullOrWhiteSpace target.Reasoning then
                None
            else
                Some target.Reasoning }

    let toOpenCodeModel (target: ModelRoutingTarget) : HostModel = toHostModel target

    let private matchReasoning (expectedReasoning: string) (observedReasoning: string) =
        if String.IsNullOrWhiteSpace expectedReasoning then
            String.IsNullOrWhiteSpace observedReasoning
        else
            String.Equals(observedReasoning, expectedReasoning, StringComparison.OrdinalIgnoreCase)

    let sameTarget (expected: ModelRoutingTarget) (observed: HostModel) =
        if
            String.IsNullOrWhiteSpace observed.providerID
            || String.IsNullOrWhiteSpace observed.modelID
        then
            false
        else
            let model = observed.providerID.Trim() + "/" + observed.modelID.Trim()

            let reasoning =
                observed.variant
                |> Option.bind (fun value ->
                    if String.IsNullOrWhiteSpace value then
                        None
                    else
                        Some(value.Trim()))
                |> Option.defaultValue ""

            String.Equals(model, expected.Model.Trim(), StringComparison.OrdinalIgnoreCase)
            && matchReasoning (expected.Reasoning.Trim()) reasoning

    let private failedTask<'T> (error: exn) : Task<'T> =
        let completion =
            TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

        completion.SetException error
        completion.Task

    let private isDeprecatedRole =
        function
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> true
        | _ -> false

    let private normalizeReservationInput (sessionId: string) (role: Role) =
        if String.IsNullOrWhiteSpace sessionId then
            Error(ArgumentException("sessionId must be non-empty") :> exn)
        elif isDeprecatedRole role then
            Error(
                InvalidOperationException(
                    sprintf
                        "execution-model-routing: deprecated role %s cannot acquire model lease"
                        (Roles.roleLabel role)
                )
                :> exn
            )
        else
            Ok(sessionId.Trim(), role)

    let private normalizeExecutionInput sessionId (physical: string) role (participant: string) =
        match normalizeReservationInput sessionId role with
        | Error error -> Error error
        | Ok _ when String.IsNullOrWhiteSpace physical ->
            Error(ArgumentException("physicalUserMessageId must be non-empty") :> exn)
        | Ok _ when String.IsNullOrWhiteSpace participant ->
            Error(ArgumentException("participant must be non-empty") :> exn)
        | Ok(session, role) -> Ok(session, physical.Trim(), role, participant.Trim())

    let private normalizeSessionId (sessionId: string) =
        if String.IsNullOrWhiteSpace sessionId then
            None
        else
            Some(sessionId.Trim())

    type private ExecutionRoutingLibrary =
        abstract acquire: input: obj -> Task<obj>
        abstract reserve: input: obj -> obj
        abstract tryLease: input: obj -> obj
        abstract target: lease: ExecutionAdmissionLease -> obj
        abstract lifecycle: lease: ExecutionAdmissionLease -> obj
        abstract commit: lease: ExecutionAdmissionLease * observed: obj -> string
        abstract releaseBeforeProvider: lease: ExecutionAdmissionLease * observed: obj -> string
        abstract tryRead: sessionId: string * physical: string -> obj
        abstract retainInput: lease: ExecutionAdmissionLease * physical: string -> obj
        abstract tryInput: sessionId: string * physical: string -> obj
        abstract cancelInput: sessionId: string * physical: string -> unit
        abstract cancelSessionInputs: sessionId: string -> unit
        abstract releasePendingPhysical: sessionId: string * physical: string -> string
        abstract ``continue``: lease: ExecutionAdmissionLease * physical: string * target: obj -> Task<obj>
        abstract wasSuperseded: sessionId: string * physical: string -> bool
        abstract wasStoppedSuperseded: sessionId: string * physical: string -> bool
        abstract owns: sessionId: string * physical: string -> bool
        abstract rememberStep: sessionId: string * physical: string * providerRun: string -> unit
        abstract stepOf: providerRun: string -> obj
        abstract activePhysical: sessionId: string -> obj
        abstract dropSession: sessionId: string -> unit
        abstract seedBoundTarget: sessionId: string * value: string -> unit
        abstract boundTarget: sessionId: string -> obj
        abstract releaseSession: sessionId: string -> string
        abstract releasePhysical: sessionId: string * physical: string -> obj
        abstract cancelPending: sessionId: string -> string
        abstract cancelPendingPhysical: sessionId: string * physical: string -> string
        abstract snapshot: unit -> obj
        abstract holdsPhysical: sessionId: string * physical: string -> bool
        abstract occupied: unit -> obj array
        abstract pendingCount: int
        abstract pendingBound: int
        abstract pendingContractVersion: int
        abstract enterStep: input: obj -> Task
        abstract endStep: sessionId: string * physical: string * providerRun: string -> unit
        abstract suppressStep: sessionId: string * physical: string -> unit
        abstract takeRunTarget: providerRun: string -> obj
        abstract hasTheoreticalCapacity: role: string * purpose: string -> bool

    [<Import("ExecutionRouting", "wanxiangli/routing")>]
    let private executionRoutingClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private createRouting (cls: obj) (options: obj) : ExecutionRoutingLibrary = jsNative

    let private inputObject sessionId physical role participant purpose lender target =
        createObj
            [ "sessionId" ==> sessionId
              "physicalUserMessageId" ==> physical
              "role" ==> Roles.roleLabel role
              "participant" ==> participant
              "purpose" ==> purposeLabel purpose
              "lenderSessionId"
              ==> (lender |> Option.bind normalizeSessionId |> optionalObject box)
              "hostTarget" ==> optionalObject targetObject target ]

    let mutable private sharedDurableResolver: (string -> string option) option = None

    type internal ModelRoutingRuntime(scheduler: obj, ?durableDevopsResolver: string -> string option) =
        let mutable durableResolver =
            match durableDevopsResolver with
            | Some resolver -> Some resolver
            | None -> sharedDurableResolver

        let bindingError (details: obj) : exn =
            match unbox<string> details?kind with
            | "durable" ->
                InvalidOperationException "execution-model-routing: durable DevOps model binding is immutable" :> exn
            | "immutable" ->
                let bound = targetOf details?bound
                let target = targetOf details?target

                InvalidOperationException(
                    sprintf
                        "execution-model-routing: DevOps model binding is immutable (%s/%s vs %s/%s)"
                        bound.Model
                        bound.Reasoning
                        target.Model
                        target.Reasoning
                )
                :> exn
            | other -> invalidOp ("wanxiangli/routing requested an unknown binding error: " + other)

        let library =
            createRouting
                executionRoutingClass
                (createObj
                    [ "scheduler" ==> scheduler
                      "binding"
                      ==> createObj
                              [ "applies" ==> Func<string, bool>(fun role -> role = Roles.roleLabel Role.DevOps)
                                "read"
                                ==> Func<string, obj>(fun session ->
                                    durableResolver
                                    |> Option.bind (fun resolve -> resolve session)
                                    |> optionalObject box)
                                "error" ==> Func<obj, exn>(bindingError) ] ])

        new(scheduler: obj) = ModelRoutingRuntime(scheduler, ?durableDevopsResolver = None)

        member _.AcquireExecutionAdmission
            (
                sessionId: string,
                physicalUserMessageId: string,
                role: Role,
                participant: string,
                purpose: ModelExecutionPurpose,
                lenderSessionId: string option,
                ?hostTarget: ModelRoutingTarget
            ) : Task<ExecutionAdmissionAcquisition> =
            match normalizeExecutionInput sessionId physicalUserMessageId role participant with
            | Error error -> failedTask error
            | Ok(session, physical, role, participant) ->
                inputObject session physical role participant purpose lenderSessionId hostTarget
                |> library.acquire
                |> acquisitionTask

        member _.ExecutionAdmissionTarget(lease: ExecutionAdmissionLease) =
            library.target lease |> permitResult targetOf

        member internal _.ContinueExecutionAdmission
            (previous: ExecutionAdmissionLease, physicalUserMessageId: string, ?selectedTarget: ModelRoutingTarget)
            =
            library.``continue`` (previous, physicalUserMessageId, optionalObject targetObject selectedTarget)
            |> acquisitionTask

        member internal _.RetainContinuationInput(previous: ExecutionAdmissionLease, physicalUserMessageId: string) =
            library.retainInput (previous, physicalUserMessageId) |> targetOf

        member internal _.TryContinuationInput(sessionId: string, physicalUserMessageId: string) =
            library.tryInput (sessionId, physicalUserMessageId)
            |> optionalValue unbox<ExecutionAdmissionLease>

        member internal _.CancelContinuationInput(sessionId: string, physicalUserMessageId: string) =
            library.cancelInput (sessionId, physicalUserMessageId)

        member internal _.CancelRetainedInputsForSession(sessionId: string) = library.cancelSessionInputs sessionId

        member _.CommitExecutionAdmission(lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity) =
            library.commit (lease, identityObject observed) |> outcomeOf

        member _.ReleaseExecutionAdmissionBeforeProvider
            (lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity)
            =
            library.releaseBeforeProvider (lease, identityObject observed) |> outcomeOf

        member _.ExecutionAdmissionLifecycle(lease: ExecutionAdmissionLease) =
            library.lifecycle lease |> permitResult unbox<string>

        member _.TryReserveManaged
            (sessionId: string, role: Role, purpose: ModelExecutionPurpose, lenderSessionId: string option)
            =
            match normalizeReservationInput sessionId role with
            | Error _ -> None
            | Ok(session, role) ->
                inputObject session null role null purpose lenderSessionId None
                |> library.reserve
                |> optionalValue targetOf

        member _.TryLease
            (
                sessionId: string,
                physicalUserMessageId: string,
                role: Role,
                participant: string,
                purpose: ModelExecutionPurpose,
                lenderSessionId: string option
            ) =
            match normalizeExecutionInput sessionId physicalUserMessageId role participant with
            | Error _ -> None
            | Ok(session, physical, role, participant) ->
                inputObject session physical role participant purpose lenderSessionId None
                |> library.tryLease
                |> optionalValue targetOf

        member _.TryReadExecution(sessionId: string, physicalUserMessageId: string) =
            library.tryRead (sessionId, physicalUserMessageId)
            |> optionalValue unbox<ExecutionAdmissionLease>

        member _.WasExecutionSuperseded(sessionId: string, physicalUserMessageId: string) =
            library.wasSuperseded (sessionId, physicalUserMessageId)

        member _.WasStoppedExecutionSuperseded(sessionId: string, physicalUserMessageId: string) =
            library.wasStoppedSuperseded (sessionId, physicalUserMessageId)

        member _.OwnsExecutionAdmission(sessionId: string, physicalUserMessageId: string) =
            library.owns (sessionId, physicalUserMessageId)

        member _.DropSession(sessionId: string) = library.dropSession sessionId
        member _.SetDurableDevopsResolver(resolver: string -> string option) = durableResolver <- Some resolver

        member _.SeedBoundDevOpsModel(sessionId: string, value: string) =
            library.seedBoundTarget (sessionId, value)

        member _.RememberProviderStepIdentity(sessionId: string, physicalUserMessageId: string, providerRun: string) =
            library.rememberStep (sessionId, physicalUserMessageId, providerRun)

        member _.TryProviderStepIdentity(providerRun: string) =
            library.stepOf providerRun
            |> optionalValue (fun value -> unbox<string> value?sessionId, unbox<string> value?physicalUserMessageId)

        member _.TryActivePhysical(sessionId: string) =
            library.activePhysical sessionId |> optionalValue unbox<string>

        member _.BoundDevopsTarget(sessionId: string) =
            library.boundTarget sessionId |> optionalValue targetOf

        member internal _.ReleaseExecution(sessionId: string) =
            library.releaseSession sessionId |> outcomeOf

        member internal _.ReleasePhysicalExecution(sessionId: string, physicalUserMessageId: string) =
            let value = library.releasePhysical (sessionId, physicalUserMessageId)

            match unbox<string> value?kind with
            | "Released" -> PhysicalExecutionReleaseOutcome.Released(outcomeOf (unbox<string> value?outcome))
            | "HeldForInput" -> PhysicalExecutionReleaseOutcome.HeldForInput
            | other -> invalidOp ("wanxiangli/routing returned an unknown physical release: " + other)

        member _.CancelPendingExecution(sessionId: string) =
            library.cancelPending sessionId |> outcomeOf

        member _.CancelPendingPhysicalExecution(sessionId: string, physicalUserMessageId: string) =
            library.cancelPendingPhysical (sessionId, physicalUserMessageId) |> outcomeOf

        member _.ReleasePendingPhysicalExecution(sessionId: string, physicalUserMessageId: string) =
            library.releasePendingPhysical (sessionId, physicalUserMessageId) |> outcomeOf

        member _.CapacitySnapshot() = library.snapshot () |> snapshotOf

        member internal _.HoldsPhysical(sessionId: string, physicalUserMessageId: string) =
            library.holdsPhysical (sessionId, physicalUserMessageId)

        member _.EnterProviderStep
            (sessionId: string, physicalUserMessageId: string, visibleProviderRuns: Set<string>, ?requestKey: string)
            =
            createObj
                [ "sessionId" ==> sessionId
                  "physicalUserMessageId" ==> physicalUserMessageId
                  "visibleProviderRuns" ==> Set.toArray visibleProviderRuns
                  "requestKey" ==> optionalObject box requestKey ]
            |> library.enterStep

        member _.EndProviderStep(sessionId: string, physicalUserMessageId: string, providerRun: string) =
            library.endStep (sessionId, physicalUserMessageId, providerRun)

        member _.TakeProviderRunTarget(providerRun: string) =
            library.takeRunTarget providerRun |> optionalValue targetOf

        member _.SuppressProviderStep(sessionId: string, physicalUserMessageId: string) =
            library.suppressStep (sessionId, physicalUserMessageId)

        member _.SnapshotOccupied() =
            library.occupied () |> Array.map targetOf

        member _.PendingCount = library.pendingCount
        member _.PendingBound = library.pendingBound
        member _.PendingContractVersion = library.pendingContractVersion

        member _.HasTheoreticalCapacity (role: string) (purpose: ModelExecutionPurpose) =
            library.hasTheoreticalCapacity (role, purposeLabel purpose)

    let private sharedGate = obj ()

    let internal setSharedDurableDevopsResolver (resolver: string -> string option) =
        sharedDurableResolver <- Some resolver

    let mutable private sharedRuntime: ModelRoutingRuntime option = None
    let mutable private sharedLoad: Task<ModelRoutingRuntime> option = None

    let private ensureShared () : Task<ModelRoutingRuntime> =
        lock sharedGate (fun () ->
            match sharedRuntime, sharedLoad with
            | Some runtime, _ -> Task.FromResult runtime
            | None, Some loading -> loading
            | None, None ->
                let loading =
                    task {
                        let runtime = ModelRoutingRuntime(null)
                        lock sharedGate (fun () -> sharedRuntime <- Some runtime)
                        return runtime
                    }

                sharedLoad <- Some loading
                loading)

    let initialize () : Task =
        task {
            let! _ = ensureShared ()
            return ()
        }
        :> Task

    let private current () =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime
        | None -> invalidOp "execution-model-routing: scheduler runtime was not initialized during plugin load"

    let private tryCurrent () =
        lock sharedGate (fun () -> sharedRuntime)

    let internal takeProviderRunTarget (providerRun: ProviderRunIdentity) =
        current().TakeProviderRunTarget(ProviderRunIdentity.value providerRun)

    let internal hasTheoreticalCapacity (role: string) (purpose: ModelExecutionPurpose) =
        current().HasTheoreticalCapacity role purpose

    let internal acquireExecutionAdmission
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (role: Role)
        (participant: string)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        (hostTarget: ModelRoutingTarget option)
        =
        current()
            .AcquireExecutionAdmission(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                role,
                participant,
                purpose,
                lenderSessionId,
                ?hostTarget = hostTarget
            )

    let internal executionAdmissionTarget (lease: ExecutionAdmissionLease) =
        current().ExecutionAdmissionTarget lease

    let internal commitExecutionAdmission (lease: ExecutionAdmissionLease) (observed: ExecutionAdmissionExactIdentity) =
        current().CommitExecutionAdmission(lease, observed)

    let internal releaseExecutionAdmissionBeforeProvider
        (lease: ExecutionAdmissionLease)
        (observed: ExecutionAdmissionExactIdentity)
        =
        current().ReleaseExecutionAdmissionBeforeProvider(lease, observed)

    let tryReserveManaged
        (sessionId: SessionId)
        (role: Role)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        =
        tryCurrent ()
        |> Option.bind (fun runtime ->
            runtime.TryReserveManaged(SessionId.value sessionId, role, purpose, lenderSessionId))

    let tryLease
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (role: Role)
        (participant: string)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        =
        tryCurrent ()
        |> Option.bind (fun runtime ->
            runtime.TryLease(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                role,
                participant,
                purpose,
                lenderSessionId
            ))

    let internal continueExecutionAdmission
        (previous: ExecutionAdmissionLease)
        (physicalId: PhysicalUserMessageId)
        (target: ModelRoutingTarget)
        =
        match tryCurrent () with
        | Some runtime ->
            runtime.ContinueExecutionAdmission(
                previous,
                PhysicalUserMessageId.value physicalId,
                selectedTarget = target
            )
        | None ->
            failedTask<ExecutionAdmissionAcquisition> (InvalidOperationException "model-routing runtime is unavailable")

    let internal retainContinuationInput (previous: ExecutionAdmissionLease) (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.RetainContinuationInput(previous, PhysicalUserMessageId.value key.PhysicalUserMessageId)
        | None -> invalidOp "model-routing runtime is unavailable"

    let internal retainContinuationInputForPhysical
        (previous: ExecutionAdmissionLease)
        (physicalUserMessageId: PhysicalUserMessageId)
        =
        match tryCurrent () with
        | Some runtime -> runtime.RetainContinuationInput(previous, PhysicalUserMessageId.value physicalUserMessageId)
        | None -> invalidOp "model-routing runtime is unavailable"

    let internal tryContinuationInput (key: ChatExecutionKey) =
        tryCurrent ()
        |> Option.bind (fun runtime ->
            runtime.TryContinuationInput(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            ))

    let internal cancelContinuationInput (key: ChatExecutionKey) =
        tryCurrent ()
        |> Option.iter (fun runtime ->
            runtime.CancelContinuationInput(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            ))

    let internal cancelRetainedInputsForSession (sessionId: SessionId) =
        tryCurrent ()
        |> Option.iter (fun runtime -> runtime.CancelRetainedInputsForSession(SessionId.value sessionId))

    let internal tryReadExecution (key: ChatExecutionKey) : ExecutionAdmissionLease option =
        tryCurrent ()
        |> Option.bind (fun runtime ->
            runtime.TryReadExecution(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            ))

    let internal wasExecutionSuperseded (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.WasExecutionSuperseded(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> false

    let internal wasStoppedExecutionSuperseded (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.WasStoppedExecutionSuperseded(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> false

    let internal ownsExecutionAdmission (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.OwnsExecutionAdmission(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> false

    let internal rememberProviderStepIdentity
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        =
        tryCurrent ()
        |> Option.iter (fun runtime ->
            runtime.RememberProviderStepIdentity(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                ProviderRunIdentity.value providerRun
            ))

    let internal tryProviderStepIdentity
        (providerRun: ProviderRunIdentity)
        : (SessionId * PhysicalUserMessageId) option =
        tryCurrent ()
        |> Option.bind (fun runtime -> runtime.TryProviderStepIdentity(ProviderRunIdentity.value providerRun))
        |> Option.map (fun (session, physical) -> SessionId.create session, PhysicalUserMessageId.create physical)

    let readExecutionAdmission (key: ChatExecutionKey) : (string * ModelRoutingTarget) option =
        tryReadExecution key
        |> Option.map (fun lease ->
            let identity = executionAdmissionIdentity lease
            identity.Participant, identity.Target)

    let internal boundDevopsModel (sessionId: SessionId) : HostModel option =
        tryCurrent ()
        |> Option.bind (fun runtime -> runtime.BoundDevopsTarget(SessionId.value sessionId))
        |> Option.map toHostModel

    let internal dropSession (sessionId: SessionId) =
        tryCurrent ()
        |> Option.iter (fun runtime -> runtime.DropSession(SessionId.value sessionId))

    let internal seedBoundDevOpsModel (sessionId: SessionId) (value: string) =
        match tryCurrent () with
        | Some runtime -> runtime.SeedBoundDevOpsModel(SessionId.value sessionId, value)
        | None ->
            invalidOp
                "execution-model-routing: the shared runtime is not loaded, so the durable DevOps model target cannot be seeded; refusing to fall back to the current scheduler preference."

    let internal releaseExecution (sessionId: SessionId) =
        match tryCurrent () with
        | Some runtime -> runtime.ReleaseExecution(SessionId.value sessionId)
        | None -> CapacityTransitionOutcome.AlreadyApplied

    let internal releasePhysicalExecution (sessionId: SessionId) (physicalUserMessageId: PhysicalUserMessageId) =
        match tryCurrent () with
        | Some runtime ->
            runtime.ReleasePhysicalExecution(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId
            )
        | None -> PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.AlreadyApplied

    let internal observePhysicalResource (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime when
            runtime.HoldsPhysical(SessionId.value key.SessionId, PhysicalUserMessageId.value key.PhysicalUserMessageId)
            ->
            PhysicalResourceObservation.ResourceHeld key
        | _ -> PhysicalResourceObservation.ResourceAbsent key

    let internal cancelUnacquiredExecution (sessionId: SessionId) =
        match tryCurrent () with
        | Some runtime -> runtime.CancelPendingExecution(SessionId.value sessionId)
        | None -> CapacityTransitionOutcome.AlreadyApplied

    let internal cancelPendingPhysicalExecution (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.CancelPendingPhysicalExecution(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> CapacityTransitionOutcome.AlreadyApplied

    /// Release an execution that never reached the provider, addressed by its key.
    ///
    /// The permit of such an execution is still Pending, and physical completion is
    /// only legal after a commit proves the provider started. The before-provider
    /// release is the matching path; an execution already gone answers AlreadyApplied.
    let internal releaseBeforeProviderExecution (key: ChatExecutionKey) =
        match tryCurrent () with
        | Some runtime ->
            runtime.ReleasePendingPhysicalExecution(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> CapacityTransitionOutcome.AlreadyApplied

    /// Return this key's capacity credit whatever lifecycle its permit reached.
    ///
    /// The permit is the authority on whether the provider ever started; a durable
    /// journal fact is not that proof. Try the before-provider release first: it
    /// settles a Pending permit and refuses a committed one, which then falls
    /// through to the physical completion that owns that committed permit.
    let internal releaseExactExecution (key: ChatExecutionKey) =
        match releaseBeforeProviderExecution key with
        | CapacityTransitionOutcome.Conflict ->
            match tryCurrent () with
            | Some runtime ->
                runtime.ReleasePhysicalExecution(
                    SessionId.value key.SessionId,
                    PhysicalUserMessageId.value key.PhysicalUserMessageId
                )
            | None -> PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.AlreadyApplied
        | outcome -> PhysicalExecutionReleaseOutcome.Released outcome

    let internal capacitySnapshot () = current().CapacitySnapshot()

    let enterProviderStep
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (visibleProviderRuns: Set<ProviderRunIdentity>)
        (requestKey: string option)
        =
        current()
            .EnterProviderStep(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                visibleProviderRuns |> Set.map ProviderRunIdentity.value,
                ?requestKey = requestKey
            )

    let endProviderStep
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        =
        tryCurrent ()
        |> Option.iter (fun runtime ->
            runtime.EndProviderStep(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                ProviderRunIdentity.value providerRun
            ))

    let suppressProviderStep (sessionId: SessionId) (physicalUserMessageId: PhysicalUserMessageId) =
        tryCurrent ()
        |> Option.iter (fun runtime ->
            runtime.SuppressProviderStep(SessionId.value sessionId, PhysicalUserMessageId.value physicalUserMessageId))

    let tryActivePhysical (sessionId: string) =
        tryCurrent ()
        |> Option.bind (fun runtime -> runtime.TryActivePhysical(sessionId.Trim()))
