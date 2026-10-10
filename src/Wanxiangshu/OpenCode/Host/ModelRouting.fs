namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Resources
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution

[<RequireQualifiedAccess>]
type internal PhysicalExecutionReleaseOutcome =
    | Released of CapacityTransitionOutcome
    | HeldForInput

type private ExecutionLease =
    { PhysicalUserMessageId: string option
      Participant: string option
      RoutingRole: Role
      Target: ModelRoutingTarget
      Purpose: ModelExecutionPurpose }

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

    [<Import("homedir", "node:os")>]
    let private homeDir () : string = jsNative

    [<Import("dirname", "node:path")>]
    let private dirName (path: string) : string = jsNative

    [<Import("join", "node:path")>]
    let private pathJoin (left: string, right: string) : string = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (path: string, options: obj) : unit = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeFileSync (path: string, content: string, options: obj) : unit = jsNative

    [<Import("linkSync", "node:fs")>]
    let private linkSync (existingPath: string, newPath: string) : unit = jsNative

    [<Import("unlinkSync", "node:fs")>]
    let private unlinkSync (path: string) : unit = jsNative

    [<Import("randomUUID", "node:crypto")>]
    let private randomUuid () : string = jsNative

    [<Import("pathToFileURL", "node:url")>]
    let private pathToFileUrl (path: string) : obj = jsNative

    [<Emit("import($0)")>]
    let private importModule (url: string) : Task<obj> = jsNative

    [<Emit("typeof $0 === 'function'")>]
    let private isFunction (value: obj) : bool = jsNative

    [<Emit("$0 != null && typeof $0.then === 'function'")>]
    let private isThenable (value: obj) : bool = jsNative

    [<Emit("$0($1, $2, $3, $4)")>]
    let private callScheduler
        (scheduler: obj)
        (role: string)
        (running: obj array)
        (previous: obj)
        (purpose: string)
        : obj =
        jsNative

    [<Emit("$0($1)")>]
    let private callMarkProviderFailed (markFailed: obj) (provider: string) : unit = jsNative

    [<Emit("$0($1, $2)")>]
    let private callCapacityQuery (query: obj) (role: string) (purpose: string) : obj = jsNative

    [<Emit("$0()")>]
    let private callPredictorConfiguration (query: obj) : obj = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isNumber (value: obj) : bool = jsNative

    let private nonEmpty name (value: obj) =
        match value with
        | :? string as text when not (String.IsNullOrWhiteSpace text) -> text.Trim()
        | _ -> invalidOp (sprintf "execution-model-routing: scheduler target requires non-empty %s" name)

    let private requireFullModelSelector (model: string) =
        let slash = model.IndexOf '/'

        if slash <= 0 || slash >= model.Length - 1 then
            invalidOp "execution-model-routing: scheduler target model must be full provider/model"

        model

    let private parseTarget (value: obj) : ModelRoutingTarget =
        if isNull value then
            invalidOp "execution-model-routing: null is not a target"

        let model = nonEmpty "model" value?model |> requireFullModelSelector
        let reasoning = nonEmpty "reasoning" value?reasoning
        { Model = model; Reasoning = reasoning }

    /// A durable road stores the fixed DevOps target as the wire string
    /// 'provider/model:reasoning'. Decoding it is fail-closed: the result must
    /// satisfy exactly the field requirements parseTarget enforces on a target
    /// the scheduler resolved, so a malformed road target is refused instead of
    /// being silently dropped or guessed from the current configuration.
    let private parseDurableModelTarget (value: string) : ModelRoutingTarget =
        if String.IsNullOrWhiteSpace value then
            invalidOp "execution-model-routing: durable DevOps model target is empty"

        let text = value.Trim()
        let separator = text.IndexOf ':'

        if separator <= 0 || separator >= text.Length - 1 then
            invalidOp (
                sprintf
                    "execution-model-routing: durable DevOps model target must be 'provider/model:reasoning' (got '%s')"
                    text
            )

        parseTarget (
            createObj
                [ "model" ==> text.Substring(0, separator)
                  "reasoning" ==> text.Substring(separator + 1) ]
        )

    let private targetObject (target: ModelRoutingTarget) =
        createObj [ "model" ==> target.Model; "reasoning" ==> target.Reasoning ]

    let private routingProtocolVersion = 2.0

    let private purposeLabel =
        function
        | ModelExecutionPurpose.Normal -> "normal"
        | ModelExecutionPurpose.ReadonlyDelegate -> "readonly-delegate"

    /// The protocol version is a stable contract version of the scheduler ABI,
    /// not an enable switch. A three-parameter JS function silently ignores the
    /// extra purpose argument, so neither `function.length` nor a quiet call can
    /// prove the upgrade; the loader requires the explicit marker.
    let private requireRoutingProtocol (moduleObj: obj) =
        let declared = if isNull moduleObj then null else moduleObj?routingProtocol

        if isNull declared then
            invalidOp
                "execution-model-routing: wanxiangshu.mjs must export `routingProtocol = 2`; the scheduler ABI is route(role, running, previous, purpose). Migrate the existing configuration manually (the recommended template in resources/wanxiangshu.mjs shows the new shape); an existing user file is never overwritten."
        elif not (isNumber declared) then
            invalidOp
                "execution-model-routing: wanxiangshu.mjs routingProtocol must be the number 2; the scheduler ABI is route(role, running, previous, purpose). Migrate the existing configuration manually (the recommended template in resources/wanxiangshu.mjs shows the new shape); an existing user file is never overwritten."
        elif unbox<float> declared <> routingProtocolVersion then
            invalidOp (
                sprintf
                    "execution-model-routing: wanxiangshu.mjs declares routingProtocol = %s but this runtime requires 2 (route(role, running, previous, purpose)). Migrate the existing configuration manually (the recommended template in resources/wanxiangshu.mjs shows the new shape); an existing user file is never overwritten."
                    (string declared)
            )

    [<RequireQualifiedAccess>]
    type PredictorConfiguration =
        | Configured
        | NotConfigured
        | ConfigurationInvalid of reason: string

    let private matchPredictorState (value: obj) : PredictorConfiguration =
        match string value?state with
        | "configured" -> PredictorConfiguration.Configured
        | "unconfigured" -> PredictorConfiguration.NotConfigured
        | "invalid" ->
            let reason = if isNull value?reason then "" else string value?reason
            PredictorConfiguration.ConfigurationInvalid reason
        | other ->
            PredictorConfiguration.ConfigurationInvalid(
                sprintf "predictor configuration query returned unknown state %s" other
            )

    let private predictorConfigurationOf (value: obj) : PredictorConfiguration =
        if isNull value then
            PredictorConfiguration.ConfigurationInvalid "predictor configuration query returned no result"
        else
            matchPredictorState value

    /// Read-only existence query owned by the same MJS model configuration:
    /// absent Predictor slot or empty candidates are not configured; valid
    /// non-empty targets are configured; a malformed structure is a
    /// configuration error. It is deliberately independent of capacity and
    /// provider health, and must not be inferred from one route returning null.
    let predictorConfiguration (scheduler: obj) : PredictorConfiguration =
        let query =
            if isNull scheduler then
                null
            else
                scheduler?predictorConfiguration

        if not (isFunction query) then
            PredictorConfiguration.ConfigurationInvalid
                "wanxiangshu.mjs must export predictorConfiguration() as the read-only Predictor slot existence query"
        else
            callPredictorConfiguration query |> predictorConfigurationOf

    let invokeScheduler
        (scheduler: obj)
        (role: string)
        (running: ModelRoutingTarget array)
        (previous: ModelRoutingTarget option)
        (purpose: ModelExecutionPurpose)
        : ModelRoutingTarget option =
        if not (isFunction scheduler) then
            invalidOp "execution-model-routing: scheduler default export must be a function"

        if String.IsNullOrWhiteSpace role then
            invalidOp "execution-model-routing: scheduler role must be non-empty"

        let result =
            callScheduler
                scheduler
                (role.Trim())
                (running |> Array.map targetObject)
                (previous |> Option.map targetObject |> Option.defaultValue null)
                (purposeLabel purpose)

        if isThenable result then
            invalidOp "execution-model-routing: scheduler must be synchronous and must not return a Promise"

        if isNull result then None else Some(parseTarget result)

    let configPath () =
        pathJoin (pathJoin (homeDir (), ".config"), "opencode")
        |> fun root -> pathJoin (root, "wanxiangshu.mjs")

    let private errorCode (error: exn) =
        let value = box error

        if isNull value?code then None else Some(string value?code)

    let private acceptBootstrapLinkError error =
        match errorCode error with
        | Some "EEXIST" -> ()
        | _ -> raise error

    let private publishBootstrap tempPath path =
        try
            linkSync (tempPath, path)
        with ex ->
            acceptBootstrapLinkError ex

    let private removeBootstrapTemp tempPath =
        try
            unlinkSync tempPath
        with _ ->
            ()

    let private publishBootstrapTemplate tempPath path template =
        try
            writeFileSync (tempPath, template, createObj [ "encoding" ==> "utf8"; "flag" ==> "wx" ])
            publishBootstrap tempPath path
        finally
            removeBootstrapTemp tempPath

    let bootstrapAndLoadAt (path: string) (template: string) : Task<obj> =
        task {
            if String.IsNullOrWhiteSpace path then
                invalidArg "path" "execution-model-routing: scheduler path must be non-empty"

            mkdirSync (dirName path, createObj [ "recursive" ==> true ])

            // A direct O_EXCL write makes the destination name visible before all
            // bytes are written, so another OpenCode process could import a partial
            // module. Publish a fully-written same-directory inode with an atomic
            // hard-link instead; EEXIST means another bootstrap already won.
            let tempPath = path + ".tmp-" + randomUuid ()
            publishBootstrapTemplate tempPath path template

            let fileUrl = string (pathToFileUrl path)?href
            let! moduleObj = importModule fileUrl
            let scheduler = if isNull moduleObj then null else moduleObj?``default``

            if not (isFunction scheduler) then
                invalidOp "execution-model-routing: scheduler default export must be a function"

            requireRoutingProtocol moduleObj

            if not (isNull moduleObj) then
                scheduler?markProviderFailed <- moduleObj?markProviderFailed
                scheduler?hasTheoreticalCapacity <- moduleObj?hasTheoreticalCapacity
                scheduler?predictorConfiguration <- moduleObj?predictorConfiguration

            return scheduler
        }

    let private recommendedTemplate () =
        ModelRoutingResource.recommendedTemplate ()

    let bootstrapDefault () =
        bootstrapAndLoadAt (configPath ()) (recommendedTemplate ())

    let toOpenCodeModel (target: ModelRoutingTarget) : OpencodeModel =
        let slash = target.Model.IndexOf '/'

        { providerID = target.Model.Substring(0, slash)
          modelID = target.Model.Substring(slash + 1)
          variant = Some target.Reasoning }

    let ofOpenCodeModel (model: OpencodeModel) : ModelRoutingTarget option =
        if
            String.IsNullOrWhiteSpace model.providerID
            || String.IsNullOrWhiteSpace model.modelID
        then
            None
        else
            model.variant
            |> Option.bind (fun reasoning ->
                if String.IsNullOrWhiteSpace reasoning then
                    None
                else
                    Some
                        { Model = model.providerID.Trim() + "/" + model.modelID.Trim()
                          Reasoning = reasoning.Trim() })

    let private matchReasoning (expectedReasoning: string) (obsReasoning: string) =
        if String.IsNullOrWhiteSpace expectedReasoning then
            String.IsNullOrWhiteSpace obsReasoning
        else
            String.Equals(obsReasoning, expectedReasoning, StringComparison.OrdinalIgnoreCase)

    let sameTarget (expected: ModelRoutingTarget) (observed: OpencodeModel) =
        if
            String.IsNullOrWhiteSpace observed.providerID
            || String.IsNullOrWhiteSpace observed.modelID
        then
            false
        else
            let obsModel = observed.providerID.Trim() + "/" + observed.modelID.Trim()

            let obsReasoning =
                observed.variant
                |> Option.bind (fun v -> if String.IsNullOrWhiteSpace v then None else Some(v.Trim()))
                |> Option.defaultValue ""

            let expectedModel = expected.Model.Trim()
            let expectedReasoning = expected.Reasoning.Trim()

            let modelMatch =
                String.Equals(obsModel, expectedModel, StringComparison.OrdinalIgnoreCase)

            let reasoningMatch = matchReasoning expectedReasoning obsReasoning
            modelMatch && reasoningMatch

    /// A SessionId is a reusable container. Model occupancy belongs to the exact
    /// physical user material that caused the provider execution, never to the
    /// session lifecycle or to a cursor-selected identity. Strength may reserve one
    /// target before its physical prompt exists; chat.message later adopts that
    /// reservation into the exact PhysicalUserMessageId without double-counting.
    let private failedTask<'T> (error: exn) : Task<'T> =
        let completion =
            TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

        completion.SetException(error)
        completion.Task

    let private isDeprecatedRole (role: Role) =
        match role with
        | Role.Coder
        | Role.Inspector
        | Role.Browser
        | Role.Inquiry
        | Role.Distiller -> true
        | _ -> false

    let private normalizeReservationInput sessionId (role: Role) =
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

    let private normalizeExecutionInput sessionId physicalUserMessageId (role: Role) (participant: string) =
        match normalizeReservationInput sessionId role with
        | Error error -> Error error
        | Ok(normSessionId, normAgent) when String.IsNullOrWhiteSpace physicalUserMessageId ->
            Error(ArgumentException("physicalUserMessageId must be non-empty") :> exn)
        | Ok(normSessionId, normRole) when String.IsNullOrWhiteSpace participant ->
            Error(ArgumentException("participant must be non-empty") :> exn)
        | Ok(normSessionId, normRole) -> Ok(normSessionId, physicalUserMessageId.Trim(), normRole, participant.Trim())

    let private normalizeSessionId sessionId =
        if String.IsNullOrWhiteSpace sessionId then
            None
        else
            Some(sessionId.Trim())

    let private normalizePhysicalExecutionKey sessionId physicalUserMessageId =
        match normalizeSessionId sessionId with
        | None -> None
        | Some _ when String.IsNullOrWhiteSpace physicalUserMessageId -> None
        | Some normSessionId -> Some(normSessionId, physicalUserMessageId.Trim())

    let private targetProvider (target: ModelRoutingTarget) =
        target.Model.Substring(0, target.Model.IndexOf '/')

    type internal ModelRoutingRuntime(scheduler: obj) =
        let gate = obj ()
        let transitionCounters = CapacityTransitionCounters()

        let capacity =
            BorrowingCapacity<ModelRoutingTarget>(CapacityLedger<ModelRoutingTarget>(), targetProvider, (=))
        // DSL-MUTABLE: resource — active execution lease map per session
        let activeBySession = Dictionary<string, ExecutionLease>()
        let activeProviderStepTasks = Dictionary<string, Task>()
        let continuationInputs = Dictionary<string * string, ExecutionAdmissionLease>()
        let heldPhysicalReleases = HashSet<string * string>()
        // DSL-MUTABLE: resource — one exact provider-run witness per live session
        let targetByProviderRun = Dictionary<string, struct (string * ModelRoutingTarget)>()
        let latestProviderRunBySession = Dictionary<string, string>()
        // DSL-MUTABLE: resource — exact provider-run → (session, physical user message).
        // Written only from the authoritative Host start observation or the exact
        // provider-step end; a tool boundary reads it by the run id the Host put
        // in its own tool context, so a stale tool call can never release the
        // step of a newer execution.
        let providerStepIdentityByRun = Dictionary<string, struct (string * string)>()
        // DSL-MUTABLE: resource — one recovery retry target per session: the
        // single-consumption binding written when a confirmed provider failure
        // keeps its target (provider-attempt-recovery-021). Consumed by the next fresh admission.
        let recoveryRetryTargetBySession = Dictionary<string, ModelRoutingTarget>()
        // DSL-MUTABLE: resource — bound ModelTarget per DevOps session (execution-model-routing-019 / managed-session-lifecycle-024 / interaction-authority-022)
        let boundDevopsTargetBySession = Dictionary<string, ModelRoutingTarget>()
        // DSL-MUTABLE: resource — superseded physical user message identities per session
        let supersededPhysical = HashSet<string * string>()
        // DSL-MUTABLE: resource — execution purpose per admitted physical user
        // message. The purpose outlives the active lease so a stale or superseded
        // commit still knows whether it was a readonly-delegate execution;
        // purged with the session.
        let leasePurposeBySession =
            Dictionary<string, (string * ModelExecutionPurpose) list>()

        let admissionQueue = ExecutionAdmissionQueue(gate, transitionCounters)
        let admissionOwner = ExecutionCapacityOwner(transitionCounters)
        // DSL-MUTABLE: resource — process-local scheduler poison
        let mutable fatalError: exn option = None

        let markFailedFn: obj =
            if isNull scheduler then
                null
            else
                scheduler?markProviderFailed

        let hasCapFn: obj =
            if isNull scheduler then
                null
            else
                scheduler?hasTheoreticalCapacity

        let hasTheoreticalCapacityLocked (role: string) (purpose: ModelExecutionPurpose) : bool =
            if not (isNull hasCapFn) && isFunction hasCapFn then
                unbox<bool> (callCapacityQuery hasCapFn role (purposeLabel purpose))
            else
                true

        let running () = capacity.Snapshot()

        /// A fresh execution inherits the replaced execution's target only as a
        /// same-purpose continuation (execution-model-routing-002): an owner
        /// execution never inherits a readonly-delegate target, and a readonly
        /// delegate never inherits the owner target.
        let activeTargetOfPurpose (purpose: ModelExecutionPurpose) sessionId =
            match activeBySession.TryGetValue sessionId with
            | true, lease when lease.PhysicalUserMessageId.IsSome && lease.Purpose = purpose -> Some lease.Target
            | _ -> None

        let recordLeasePurposeEntry sessionId physical purpose =
            let existing =
                match leasePurposeBySession.TryGetValue sessionId with
                | true, entries -> entries
                | false, _ -> []

            leasePurposeBySession.[sessionId] <- (physical, purpose) :: existing

        /// Record the purpose of an admitted physical execution. Reservations carry
        /// no physical identity yet; their adoption records the real one.
        let rememberLeasePurpose sessionId (lease: ExecutionLease) =
            match lease.PhysicalUserMessageId with
            | Some physical -> recordLeasePurposeEntry sessionId physical lease.Purpose
            | None -> ()

        /// Purpose of a possibly stale admitted physical execution. An unknown
        /// execution is treated as an ordinary owner execution: the conservative
        /// side of the fixed DevOps binding rule (execution-model-routing-019).
        let staleLeasePurpose sessionId physicalUserMessageId =
            match leasePurposeBySession.TryGetValue sessionId with
            | true, entries ->
                entries
                |> List.tryFind (fun (physical, _) -> physical = physicalUserMessageId)
                |> Option.map snd
            | false, _ -> None

        let retireProviderRunTarget sessionId =
            match latestProviderRunBySession.TryGetValue sessionId with
            | true, providerRun ->
                latestProviderRunBySession.Remove sessionId |> ignore
                targetByProviderRun.Remove providerRun |> ignore
            | false, _ -> ()

        let rememberProviderRunTarget sessionId physicalUserMessageId providerRun =
            match activeBySession.TryGetValue sessionId with
            | true, lease when lease.PhysicalUserMessageId = Some physicalUserMessageId ->
                retireProviderRunTarget sessionId
                targetByProviderRun.[providerRun] <- struct (sessionId, lease.Target)
                latestProviderRunBySession.[sessionId] <- providerRun
            | _ -> ()

        let forgetLatestProviderRunIfCurrent sessionId providerRun =
            match latestProviderRunBySession.TryGetValue sessionId with
            | true, latest when latest = providerRun -> latestProviderRunBySession.Remove sessionId |> ignore
            | _ -> ()

        let takeProviderRunWitness providerRun =
            match targetByProviderRun.TryGetValue providerRun with
            | true, struct (sessionId, target) ->
                targetByProviderRun.Remove providerRun |> ignore
                forgetLatestProviderRunIfCurrent sessionId providerRun
                Some(sessionId, target)
            | false, _ -> None

        let takeProviderRunTarget providerRun =
            takeProviderRunWitness providerRun |> Option.map snd

        /// Exact (session, physical user message) for one Host-observed provider
        /// run. Written only here and by the authoritative start observation; a
        /// lookup for a run this process never observed yields None, and the
        /// caller must not guess a session-current substitute.
        let rememberProviderStepIdentity sessionId physicalUserMessageId providerRun =
            providerStepIdentityByRun.[providerRun] <- struct (sessionId, physicalUserMessageId)

        let tryProviderStepIdentity providerRun =
            match providerStepIdentityByRun.TryGetValue providerRun with
            | true, struct (sessionId, physicalUserMessageId) -> Some(sessionId, physicalUserMessageId)
            | false, _ -> None

        let takeRecoveryRetryTarget sessionId =
            match recoveryRetryTargetBySession.TryGetValue sessionId with
            | true, target ->
                recoveryRetryTargetBySession.Remove sessionId |> ignore
                Some target
            | false, _ -> None

        let retainFailedTargetOfRun sessionId providerRun =
            match takeProviderRunWitness providerRun with
            | Some(witnessSession, target) when witnessSession = sessionId ->
                recoveryRetryTargetBySession.[sessionId] <- target
                Some target
            | Some _
            | None -> None

        let markProviderOfTarget (target: ModelRoutingTarget) =
            if not (isNull markFailedFn) && isFunction markFailedFn then
                callMarkProviderFailed markFailedFn (targetProvider target)

        // provider-attempt-recovery-021: a confirmed provider failure that kept its target binds this
        // session's next fresh admission to that target; the binding is consumed
        // once, and only a still-active replaced execution of the same purpose can
        // otherwise supply the ordinary previous hint.
        let recoveryPreviousTarget (purpose: ModelExecutionPurpose) sessionId =
            match takeRecoveryRetryTarget sessionId with
            | Some target -> Some target
            | None -> activeTargetOfPurpose purpose sessionId

        let ensureHealthy () = fatalError |> Option.iter raise

        let poison (error: exn) =
            fatalError <- Some error
            capacity.Fail error
            admissionQueue.Fail error

        let scheduleOrPoison running (role: Role) previous (purpose: ModelExecutionPurpose) =
            try
                invokeScheduler scheduler (Roles.roleLabel role) running previous purpose
            with ex ->
                poison ex
                raise ex

        let exactTargetAvailable (role: Role) target running (purpose: ModelExecutionPurpose) =
            match scheduleOrPoison running role (Some target) purpose with
            | Some candidate -> candidate = target
            | None -> false

        let routeFreshOrPoison
            retireOnUnavailable
            sessionId
            oldPhysicalUserMessageId
            physicalUserMessageId
            (role: Role)
            lenderSessionId
            previous
            (purpose: ModelExecutionPurpose)
            =
            try
                capacity.RouteFresh(
                    sessionId,
                    oldPhysicalUserMessageId,
                    physicalUserMessageId,
                    lenderSessionId,
                    (fun running -> scheduleOrPoison running role previous purpose),
                    retireOnUnavailable = retireOnUnavailable
                )
            with ex ->
                poison ex
                raise ex

        let reserveFreshOrPoison sessionId (role: Role) lenderSessionId previous (purpose: ModelExecutionPurpose) =
            try
                capacity.ReserveFresh(
                    sessionId,
                    lenderSessionId,
                    (fun running -> scheduleOrPoison running role previous purpose)
                )
            with ex ->
                poison ex
                raise ex

        let rememberExecution (demand: ExecutionAdmissionDemand) (target: ModelRoutingTarget) =
            let lease: ExecutionLease =
                { PhysicalUserMessageId = Some demand.PhysicalUserMessageId
                  Participant = Some demand.Participant
                  RoutingRole = demand.Role
                  Target = target
                  Purpose = demand.Purpose }

            activeBySession.[demand.SessionId] <- lease
            rememberLeasePurpose demand.SessionId lease

        let enforceDevopsBindingNormal sessionId role target =
            match role = Role.DevOps, boundDevopsTargetBySession.TryGetValue sessionId with
            | true, (true, bound) when
                bound <> target
                && exactTargetAvailable role bound (running ()) ModelExecutionPurpose.Normal
                ->
                invalidOp (
                    sprintf
                        "execution-model-routing: DevOps model binding is immutable (%s/%s vs %s/%s)"
                        bound.Model
                        bound.Reasoning
                        target.Model
                        target.Reasoning
                )
            | true, _ -> boundDevopsTargetBySession.[sessionId] <- target
            | _ -> ()

        /// The fixed DevOps target binding belongs to the owner execution only
        /// (execution-model-routing-019). A readonly-delegate execution is a new
        /// physical execution: it must neither inherit nor overwrite the binding.
        let enforceImmutableDevopsBinding
            (sessionId: string)
            (role: Role)
            (target: ModelRoutingTarget)
            (purpose: ModelExecutionPurpose)
            =
            match purpose with
            | ModelExecutionPurpose.ReadonlyDelegate -> ()
            | ModelExecutionPurpose.Normal -> enforceDevopsBindingNormal sessionId role target

        let tryGetBoundDevopsTarget (sessionId: string) (role: Role) =
            match role = Role.DevOps, boundDevopsTargetBySession.TryGetValue sessionId with
            | true, (true, t) -> Some t
            | _ -> None

        let resolvePreviousTarget (purpose: ModelExecutionPurpose) sessionId role =
            match activeTargetOfPurpose purpose sessionId with
            | Some t -> Some t
            | None -> tryGetBoundDevopsTarget sessionId role

        /// A fresh readonly-delegate execution never inherits the owner's active
        /// target or fixed DevOps binding as its previous hint: it enters as a
        /// new physical execution and receives null (or the session's own
        /// single-consumption recovery retry preference) while purpose selects
        /// the Predictor pool.
        let previousForFreshPurpose (purpose: ModelExecutionPurpose) sessionId (role: Role) =
            match purpose with
            | ModelExecutionPurpose.ReadonlyDelegate -> takeRecoveryRetryTarget sessionId
            | ModelExecutionPurpose.Normal ->
                recoveryPreviousTarget ModelExecutionPurpose.Normal sessionId
                |> Option.orElseWith (fun () -> tryGetBoundDevopsTarget sessionId role)

        let previousForReservationPurpose (purpose: ModelExecutionPurpose) sessionId (role: Role) =
            match purpose with
            | ModelExecutionPurpose.ReadonlyDelegate -> None
            | ModelExecutionPurpose.Normal -> resolvePreviousTarget ModelExecutionPurpose.Normal sessionId role

        let commit (demand: ExecutionAdmissionDemand) (target: ModelRoutingTarget) =
            enforceImmutableDevopsBinding demand.SessionId demand.Role target demand.Purpose
            rememberExecution demand target

            let identity: ExecutionAdmissionExactIdentity =
                { SessionId = demand.SessionId
                  PhysicalUserMessageId = demand.PhysicalUserMessageId
                  Role = demand.Role
                  Participant = demand.Participant
                  Target = target }

            let lease =
                capacity.ExactCredit(demand.SessionId, demand.PhysicalUserMessageId)
                |> fun credit -> admissionOwner.Issue(identity, credit)

            admissionQueue.Admit(demand.Node, lease) |> ignore

        let commitScheduled (demand: ExecutionAdmissionDemand) (scheduled: ModelRoutingTarget option) =
            match scheduled with
            | None -> false
            | Some target ->
                commit demand target
                true

        let schedulePendingDemand (demand: ExecutionAdmissionDemand) =
            routeFreshOrPoison
                true
                demand.SessionId
                None
                demand.PhysicalUserMessageId
                demand.Role
                demand.LenderSessionId
                demand.PreviousTarget
                demand.Purpose
            |> commitScheduled demand

        let rec drainDemands () =
            ensureHealthy ()

            admissionQueue.Snapshot()
            |> Array.map schedulePendingDemand
            |> Array.exists id
            |> continueDrain

        and continueDrain progressed =
            if progressed && admissionQueue.Count > 0 then
                drainDemands ()

        let retireCurrentExecution sessionId =
            let changed = activeBySession.Remove sessionId
            retireProviderRunTarget sessionId
            recoveryRetryTargetBySession.Remove sessionId |> ignore
            leasePurposeBySession.Remove sessionId |> ignore

            continuationInputs.Keys
            |> Seq.filter (fun (inputSession, _) -> inputSession = sessionId)
            |> Seq.toArray
            |> Array.iter (fun key -> continuationInputs.Remove key |> ignore)

            heldPhysicalReleases
            |> Seq.filter (fun (inputSession, _) -> inputSession = sessionId)
            |> Seq.toArray
            |> Array.iter (fun key -> heldPhysicalReleases.Remove key |> ignore)

            // The exact run->physical relations of this session's retired
            // execution are process-local observations of an execution that no
            // longer exists; keeping them would let a later tool call end a step
            // of an execution the capacity owner already settled.
            providerStepIdentityByRun.Keys
            |> Seq.filter (fun run ->
                match providerStepIdentityByRun.TryGetValue run with
                | true, struct (runSession, _) -> runSession = sessionId
                | false, _ -> false)
            |> Seq.toArray
            |> Array.iter (fun run -> providerStepIdentityByRun.Remove run |> ignore)

            capacity.ReleaseSession sessionId |> ignore

            if admissionQueue.ContainsSession sessionId then
                admissionQueue.CancelSession sessionId |> ignore

            if changed && fatalError.IsNone then
                drainDemands ()

        let retirePhysicalExecution sessionId physicalUserMessageId =
            let changed =
                match activeBySession.TryGetValue sessionId with
                | true, lease when lease.PhysicalUserMessageId = Some physicalUserMessageId ->
                    let removed = activeBySession.Remove sessionId
                    capacity.ReleasePhysical(sessionId, physicalUserMessageId) |> ignore
                    removed
                | _ -> false

            // Exact terminal evidence for an older physical execution must never
            // cancel a newer pending demand for the same reusable SessionId.
            if changed && fatalError.IsNone then
                drainDemands ()

        let requireSameIdentity
            sessionId
            physicalUserMessageId
            (expectedRole: Role)
            (expectedParticipant: string option)
            (expectedPurpose: ModelExecutionPurpose)
            (observedRole: Role)
            (observedParticipant: string)
            (observedPurpose: ModelExecutionPurpose)
            =
            if expectedRole <> observedRole then
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed role (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        (Roles.roleLabel expectedRole)
                        (Roles.roleLabel observedRole)
                )

            match expectedParticipant with
            | Some expected when expected <> observedParticipant ->
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed participant (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        expected
                        observedParticipant
                )
            | _ -> ()

            if expectedPurpose <> observedPurpose then
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed execution purpose (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        (purposeLabel expectedPurpose)
                        (purposeLabel observedPurpose)
                )

        let issueAdmission sessionId physicalUserMessageId (role: Role) (participant: string) target =
            let identity: ExecutionAdmissionExactIdentity =
                { SessionId = sessionId
                  PhysicalUserMessageId = physicalUserMessageId
                  Role = role
                  Participant = participant
                  Target = target }

            capacity.ExactCredit(sessionId, physicalUserMessageId)
            |> fun credit -> admissionOwner.Issue(identity, credit)
            |> ExecutionAdmissionAcquisition.Admitted


        let reuseOrAdoptActiveExecution
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lease: ExecutionLease)
            =
            match lease.PhysicalUserMessageId with
            | Some current when current = physicalUserMessageId ->
                requireSameIdentity
                    sessionId
                    physicalUserMessageId
                    lease.RoutingRole
                    lease.Participant
                    lease.Purpose
                    role
                    participant
                    purpose

                Some(issueAdmission sessionId physicalUserMessageId role participant lease.Target)
            | None ->
                let effectivePurpose =
                    if
                        lease.Purpose = ModelExecutionPurpose.ReadonlyDelegate
                        && purpose = ModelExecutionPurpose.Normal
                    then
                        ModelExecutionPurpose.ReadonlyDelegate
                    else
                        purpose

                requireSameIdentity
                    sessionId
                    physicalUserMessageId
                    lease.RoutingRole
                    lease.Participant
                    lease.Purpose
                    role
                    participant
                    effectivePurpose

                capacity.AdoptReservation(sessionId, physicalUserMessageId, lease.Target)

                let adopted =
                    { lease with
                        PhysicalUserMessageId = Some physicalUserMessageId
                        Participant = Some participant }

                activeBySession.[sessionId] <- adopted
                rememberLeasePurpose sessionId adopted

                Some(issueAdmission sessionId physicalUserMessageId role participant lease.Target)
            | Some _ -> None

        let reusePendingExecution
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            =
            match admissionQueue.TryCurrent sessionId with
            | Some demand when demand.PhysicalUserMessageId = physicalUserMessageId ->
                requireSameIdentity
                    sessionId
                    physicalUserMessageId
                    demand.Role
                    (Some demand.Participant)
                    demand.Purpose
                    role
                    participant
                    purpose

                Some(ExecutionAdmissionAcquisition.Queued demand.Node)
            | Some _
            | None -> None

        let currentExecutionOutcome
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            =
            match activeBySession.TryGetValue sessionId with
            | true, lease -> reuseOrAdoptActiveExecution sessionId physicalUserMessageId role participant purpose lease
            | false, _ -> reusePendingExecution sessionId physicalUserMessageId role participant purpose

        let currentPhysicalUserMessageId sessionId =
            match activeBySession.TryGetValue sessionId with
            | true, lease -> lease.PhysicalUserMessageId
            | false, _ -> None

        let supersedeCurrentDemand sessionId =
            if admissionQueue.ContainsSession sessionId then
                admissionQueue.SupersedeSession sessionId |> ignore

        let acquireFreshDemand
            replacePrevious
            sessionId
            oldPhysicalUserMessageId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            (previous: ModelRoutingTarget option)
            =
            match
                routeFreshOrPoison
                    (admissionQueue.ContainsSession sessionId
                     || admissionQueue.Count < ModelCapacityQueue.MaximumPendingDemands)
                    sessionId
                    oldPhysicalUserMessageId
                    physicalUserMessageId
                    role
                    lenderSessionId
                    previous
                    purpose
            with
            | Some target ->
                replacePrevious ()

                let lease: ExecutionLease =
                    { PhysicalUserMessageId = Some physicalUserMessageId
                      Participant = Some participant
                      RoutingRole = role
                      Target = target
                      Purpose = purpose }

                activeBySession.[sessionId] <- lease
                rememberLeasePurpose sessionId lease

                drainDemands ()
                issueAdmission sessionId physicalUserMessageId role participant target
            | None ->
                if
                    not (admissionQueue.ContainsSession sessionId)
                    && admissionQueue.Count >= ModelCapacityQueue.MaximumPendingDemands
                then
                    ExecutionAdmissionAcquisition.QueueFull
                else
                    replacePrevious ()

                    let enqueued =
                        admissionQueue.Enqueue(
                            sessionId,
                            physicalUserMessageId,
                            role,
                            participant,
                            purpose,
                            lenderSessionId,
                            previous
                        )

                    drainDemands ()
                    enqueued

        let acquireFreshOrAdopt
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            =
            match currentExecutionOutcome sessionId physicalUserMessageId role participant purpose with
            | Some current -> current
            | None ->
                let oldPhysicalUserMessageId = currentPhysicalUserMessageId sessionId

                let previous = previousForFreshPurpose purpose sessionId role

                let replacePrevious () =
                    let previousPhysical =
                        oldPhysicalUserMessageId
                        |> Option.orElseWith (fun () ->
                            admissionQueue.TryCurrent sessionId |> Option.map _.PhysicalUserMessageId)

                    previousPhysical
                    |> Option.iter (fun oldId -> supersededPhysical.Add(sessionId, oldId) |> ignore)

                    activeBySession.Remove sessionId |> ignore
                    supersedeCurrentDemand sessionId

                acquireFreshDemand
                    replacePrevious
                    sessionId
                    oldPhysicalUserMessageId
                    physicalUserMessageId
                    role
                    participant
                    purpose
                    lenderSessionId
                    previous

        let acquireManagedTask
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            =
            lock gate (fun () ->
                ensureHealthy ()

                if not (hasTheoreticalCapacityLocked (Roles.roleLabel role) purpose) then
                    ExecutionAdmissionAcquisition.QueueFull
                else
                    acquireFreshOrAdopt sessionId physicalUserMessageId role participant purpose lenderSessionId)

        let acquireManagedSafe
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            =
            try
                acquireManagedTask sessionId physicalUserMessageId role participant purpose lenderSessionId
                |> Task.FromResult
            with ex ->
                failedTask<ExecutionAdmissionAcquisition> ex

        let tryReserveFresh sessionId (role: Role) (purpose: ModelExecutionPurpose) (lenderSessionId: string option) =
            let previous = previousForReservationPurpose purpose sessionId role

            match reserveFreshOrPoison sessionId role lenderSessionId previous purpose with
            | None -> None
            | Some target ->
                enforceImmutableDevopsBinding sessionId role target purpose

                let lease: ExecutionLease =
                    { PhysicalUserMessageId = None
                      Participant = None
                      RoutingRole = role
                      Target = target
                      Purpose = purpose }

                activeBySession.[sessionId] <- lease
                rememberLeasePurpose sessionId lease

                drainDemands ()
                Some target

        let reserveFreshAndEnforce sessionId role purpose lenderSessionId =
            match tryReserveFresh sessionId role purpose lenderSessionId with
            | None -> None
            | Some target ->
                enforceImmutableDevopsBinding sessionId role target purpose
                Some target

        let tryReserveLocked sessionId (role: Role) (purpose: ModelExecutionPurpose) (lenderSessionId: string option) =
            ensureHealthy ()

            match activeBySession.TryGetValue sessionId, admissionQueue.ContainsSession sessionId with
            | (true, lease), _ when
                lease.PhysicalUserMessageId.IsNone
                && lease.RoutingRole = role
                && lease.Purpose = purpose
                ->
                Some lease.Target
            | (true, _), _ -> None
            | (false, _), true -> None
            | (false, _), false -> reserveFreshAndEnforce sessionId role purpose lenderSessionId

        let adoptExistingReservation
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lease: ExecutionLease)
            =
            if lease.RoutingRole <> role then
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed role (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        (Roles.roleLabel lease.RoutingRole)
                        (Roles.roleLabel role)
                )

            if lease.Purpose <> purpose then
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed execution purpose (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        (purposeLabel lease.Purpose)
                        (purposeLabel purpose)
                )

            match lease.Participant with
            | Some expected when expected <> participant ->
                invalidOp (
                    sprintf
                        "execution-model-routing: physical execution %s/%s changed participant (%s -> %s)"
                        sessionId
                        physicalUserMessageId
                        expected
                        participant
                )
            | _ -> ()

            capacity.AdoptReservation(sessionId, physicalUserMessageId, lease.Target)

            let updated =
                { lease with
                    PhysicalUserMessageId = Some physicalUserMessageId
                    Participant = Some participant }

            activeBySession.[sessionId] <- updated
            Some lease.Target

        let routeFreshOrNone
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            lenderSessionId
            previous
            =
            match
                routeFreshOrPoison true sessionId None physicalUserMessageId role lenderSessionId previous purpose
            with
            | Some target ->
                let lease: ExecutionLease =
                    { PhysicalUserMessageId = Some physicalUserMessageId
                      Participant = Some participant
                      RoutingRole = role
                      Target = target
                      Purpose = purpose }

                activeBySession.[sessionId] <- lease
                rememberLeasePurpose sessionId lease

                drainDemands ()
                Some target
            | None -> None

        let tryAcquireFreshLease
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            =
            if admissionQueue.ContainsSession sessionId then
                None
            else
                let previous = previousForReservationPurpose purpose sessionId role
                routeFreshOrNone sessionId physicalUserMessageId role participant purpose lenderSessionId previous

        let acquireFreshLeaseAndEnforce sessionId physicalUserMessageId role participant purpose lenderSessionId =
            match tryAcquireFreshLease sessionId physicalUserMessageId role participant purpose lenderSessionId with
            | None -> None
            | Some target ->
                enforceImmutableDevopsBinding sessionId role target purpose
                Some target

        let replaceActiveLeaseAndEnforce
            sessionId
            physicalUserMessageId
            role
            participant
            purpose
            lenderSessionId
            (oldLease: ExecutionLease)
            =
            /// A fresh physical execution inherits the replaced execution's target
            /// only as a same-purpose continuation; a purpose change is a
            /// different execution and receives no previous hint.
            let previous =
                match purpose, oldLease.Purpose with
                | ModelExecutionPurpose.Normal, ModelExecutionPurpose.Normal -> Some oldLease.Target
                | _ -> None

            let oldPhysicalUserMessageId = oldLease.PhysicalUserMessageId

            oldPhysicalUserMessageId
            |> Option.iter (fun oldId -> supersededPhysical.Add(sessionId, oldId) |> ignore)

            activeBySession.Remove sessionId |> ignore

            let targetOpt =
                routeFreshOrPoison
                    true
                    sessionId
                    oldPhysicalUserMessageId
                    physicalUserMessageId
                    role
                    lenderSessionId
                    previous
                    purpose

            match targetOpt with
            | Some target ->
                let lease: ExecutionLease =
                    { PhysicalUserMessageId = Some physicalUserMessageId
                      Participant = Some participant
                      RoutingRole = role
                      Target = target
                      Purpose = purpose }

                activeBySession.[sessionId] <- lease
                rememberLeasePurpose sessionId lease

                drainDemands ()
                enforceImmutableDevopsBinding sessionId role target purpose
                Some target
            | None -> None

        let tryLeaseLocked
            sessionId
            physicalUserMessageId
            (role: Role)
            (participant: string)
            (purpose: ModelExecutionPurpose)
            (lenderSessionId: string option)
            =
            match
                supersededPhysical.Contains(sessionId, physicalUserMessageId), activeBySession.TryGetValue sessionId
            with
            | true, _ -> None
            | false, (true, lease) when
                lease.PhysicalUserMessageId = Some physicalUserMessageId
                && lease.RoutingRole = role
                && lease.Purpose = purpose
                && (lease.Participant = Some participant || lease.Participant.IsNone)
                ->
                Some lease.Target
            | false, (true, lease) when
                lease.PhysicalUserMessageId.IsNone
                && lease.RoutingRole = role
                && lease.Purpose = purpose
                && (lease.Participant = Some participant || lease.Participant.IsNone)
                ->
                adoptExistingReservation sessionId physicalUserMessageId role participant purpose lease
            | false, (true, lease) when lease.PhysicalUserMessageId <> Some physicalUserMessageId ->
                replaceActiveLeaseAndEnforce
                    sessionId
                    physicalUserMessageId
                    role
                    participant
                    purpose
                    lenderSessionId
                    lease
            | false, (true, _) -> None
            | false, (false, _) ->
                acquireFreshLeaseAndEnforce sessionId physicalUserMessageId role participant purpose lenderSessionId

        let enterProviderStepLocked sessionId physicalUserMessageId fence (requestKey: string option) =
            ensureHealthy ()

            match activeBySession.TryGetValue sessionId with
            | true, lease when lease.PhysicalUserMessageId = Some physicalUserMessageId ->
                capacity.EnterStep(
                    sessionId,
                    physicalUserMessageId,
                    lease.Target,
                    fence,
                    (fun running -> exactTargetAvailable lease.RoutingRole lease.Target running lease.Purpose),
                    ?requestKey = requestKey
                )
            | _ ->
                failedTask<unit> (
                    InvalidOperationException(
                        sprintf
                            "execution-model-routing: provider step %s/%s has no active execution binding"
                            sessionId
                            physicalUserMessageId
                    )
                )
                :> Task

        let drainIfHealthy () =
            if fatalError.IsNone then
                drainDemands ()

        let normalizeAdmissionInput sessionId physicalUserMessageId (role: Role) (participant: string) =
            normalizeExecutionInput sessionId physicalUserMessageId role participant

        let completePhysicalRelease sessionId physicalUserMessageId =
            function
            | CapacityTransitionOutcome.Applied as applied ->
                retirePhysicalExecution sessionId physicalUserMessageId
                drainIfHealthy ()
                applied
            | outcome -> outcome

        let releasePhysicalExecutionLocked (sessionId, physicalUserMessageId) =
            lock gate (fun () ->
                (match admissionQueue.TryCurrent sessionId with
                 | Some _ -> admissionQueue.CancelExecution(sessionId, physicalUserMessageId)
                 | None -> admissionOwner.ReleasePhysical(sessionId, physicalUserMessageId))
                |> completePhysicalRelease sessionId physicalUserMessageId)

        /// managed-session-lifecycle-027: a delayed return replays the release of
        /// an exact execution whose credit is still owned by the ledger. It must
        /// not take the pending-queue branch: that branch cancels a current
        /// demand only when the physical keys match, and a retained input's old
        /// key never matches the newer pending demand — the queue branch would
        /// answer StaleFence and leave the old exact credit silently held.
        let releasePhysicalExecutionExactLocked (sessionId, physicalUserMessageId) =
            lock gate (fun () ->
                admissionOwner.ReleasePhysical(sessionId, physicalUserMessageId)
                |> completePhysicalRelease sessionId physicalUserMessageId)

        let hasContinuationInput (sessionId, physicalId) =
            continuationInputs.Values
            |> Seq.exists (fun lease ->
                lease.Identity.SessionId = sessionId
                && lease.Identity.PhysicalUserMessageId = physicalId)

        /// managed-session-lifecycle-027: the delayed return accepts the
        /// idempotent outcomes and clears the pending record. A Conflict is a
        /// real ownership violation: it must surface instead of silently
        /// dropping the held credit, and the pending record stays in place so
        /// the cancelled-retention / not-yet-returned state is not rewritten.
        /// Callers already hold the gate.
        let releaseUnretainedOutcome oldKey =
            match releasePhysicalExecutionExactLocked oldKey with
            | CapacityTransitionOutcome.Conflict ->
                let sessionId, physicalUserMessageId = oldKey

                invalidOp (
                    sprintf
                        "managed-session-lifecycle-027: retained continuation input release was rejected (%s/%s)"
                        sessionId
                        physicalUserMessageId
                )
            | CapacityTransitionOutcome.Applied
            | CapacityTransitionOutcome.AlreadyApplied
            | CapacityTransitionOutcome.StaleFence -> heldPhysicalReleases.Remove oldKey |> ignore

        let releaseUnretainedPhysical oldKey =
            if not (hasContinuationInput oldKey) && heldPhysicalReleases.Contains oldKey then
                releaseUnretainedOutcome oldKey

        let releaseContinuationInputLocked key =
            match continuationInputs.TryGetValue key with
            | true, previous ->
                continuationInputs.Remove key |> ignore
                let oldKey = previous.Identity.SessionId, previous.Identity.PhysicalUserMessageId

                releaseUnretainedPhysical oldKey
            | _ -> ()

        let cancelPendingPhysicalExecutionLocked (sessionId, physicalUserMessageId) =
            releaseContinuationInputLocked (sessionId, physicalUserMessageId)

            match admissionQueue.TryCurrent sessionId with
            | Some demand when demand.PhysicalUserMessageId = physicalUserMessageId ->
                admissionQueue.CancelSession sessionId
            | Some _ -> CapacityTransitionOutcome.StaleFence
            | None -> releasePhysicalExecutionLocked (sessionId, physicalUserMessageId)

        let capacitySnapshotLocked () =
            let physical: BorrowingCapacitySnapshot<ModelRoutingTarget> =
                capacity.InvariantSnapshot()

            let executions: CapacityExactOwnerSnapshot array =
                activeBySession
                |> Seq.choose (fun (KeyValue(sessionId, execution)) ->
                    execution.PhysicalUserMessageId
                    |> Option.map (fun physicalUserMessageId ->
                        ({ SessionId = sessionId
                           PhysicalUserMessageId = physicalUserMessageId
                           Role = Some execution.RoutingRole
                           Participant = execution.Participant }
                        : CapacityExactOwnerSnapshot)))
                |> Seq.sortBy (fun owner -> owner.SessionId, owner.PhysicalUserMessageId)
                |> Seq.toArray

            let identityByExecution: Map<string * string, Role option * string option> =
                executions
                |> Array.map (fun owner ->
                    (owner.SessionId, owner.PhysicalUserMessageId), (owner.Role, owner.Participant))
                |> Map.ofArray

            let enrichOwner (owner: CapacityExactOwnerSnapshot) : CapacityExactOwnerSnapshot =
                match Map.tryFind (owner.SessionId, owner.PhysicalUserMessageId) identityByExecution with
                | Some(role, participant) ->
                    { owner with
                        Role = role |> Option.orElse owner.Role
                        Participant = participant |> Option.orElse owner.Participant }
                | None -> owner

            let tokens: CapacityTokenSnapshot<ModelRoutingTarget> array =
                physical.Tokens
                |> Array.map (fun token ->
                    { token with
                        Owner = enrichOwner token.Owner })

            let custodies: CapacityCustodySnapshot array =
                physical.Custodies
                |> Array.map (fun custody ->
                    { custody with
                        Owner = enrichOwner custody.Owner })

            let admissionWaiters: CapacityWaiterSnapshot array =
                admissionQueue.Snapshot()
                |> Array.map (fun (demand: ExecutionAdmissionDemand) ->
                    ({ Owner =
                        ({ SessionId = demand.SessionId
                           PhysicalUserMessageId = demand.PhysicalUserMessageId
                           Role = Some demand.Role
                           Participant = Some demand.Participant }
                        : CapacityExactOwnerSnapshot)
                       Sequence = demand.Sequence
                       Kind = "Admission" }
                    : CapacityWaiterSnapshot))

            let waiters: CapacityWaiterSnapshot array =
                Array.append
                    (physical.Waiters
                     |> Array.map (fun waiter ->
                         { waiter with
                             Owner = enrichOwner waiter.Owner }))
                    admissionWaiters
                |> Array.sortBy (fun waiter -> waiter.Sequence, waiter.Owner.SessionId)

            let owners: CapacityExactOwnerSnapshot array =
                seq {
                    yield! executions
                    yield! tokens |> Seq.map _.Owner
                    yield! waiters |> Seq.map _.Owner
                }
                |> Seq.distinctBy (fun owner -> owner.SessionId, owner.PhysicalUserMessageId)
                |> Seq.sortBy (fun owner -> owner.SessionId, owner.PhysicalUserMessageId)
                |> Seq.toArray

            { LedgerEntries = physical.LedgerEntries
              Tokens = tokens
              Custodies = custodies
              Executions = executions
              Waiters = waiters
              Owners = owners
              Lineage = physical.Lineage
              IdleCount = physical.IdleCount
              InFlightCount = physical.InFlightCount
              RetiringCount = physical.RetiringCount
              ActiveCount = physical.InFlightCount + physical.RetiringCount
              Counters = transitionCounters.Snapshot() }

        let makeStepCacheKey normSessionId normPhysicalUserMessageId (requestKey: string option) =
            requestKey
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map (fun key -> sprintf "%s:%s:%s" normSessionId normPhysicalUserMessageId key)

        let enterProviderStepInternal
            normSessionId
            normPhysicalUserMessageId
            visibleProviderRuns
            requestKey
            cacheKeyOpt
            =
            lock gate (fun () ->
                let existing =
                    match cacheKeyOpt with
                    | Some k when activeProviderStepTasks.ContainsKey k -> Some activeProviderStepTasks.[k]
                    | _ -> None

                match existing with
                | Some task -> task
                | None ->
                    let admission =
                        enterProviderStepLocked normSessionId normPhysicalUserMessageId visibleProviderRuns requestKey

                    let t =
                        task {
                            do! admission
                            lock gate drainIfHealthy
                        }
                        :> Task

                    cacheKeyOpt |> Option.iter (fun k -> activeProviderStepTasks.[k] <- t)
                    t)

        let enforceStaleObservedBinding (observed: ExecutionAdmissionExactIdentity) =
            match staleLeasePurpose observed.SessionId observed.PhysicalUserMessageId with
            | Some purpose -> enforceImmutableDevopsBinding observed.SessionId observed.Role observed.Target purpose
            | None ->
                enforceImmutableDevopsBinding
                    observed.SessionId
                    observed.Role
                    observed.Target
                    ModelExecutionPurpose.Normal

        let enforceObservedBinding (observed: ExecutionAdmissionExactIdentity) =
            match activeBySession.TryGetValue observed.SessionId with
            | true, active when active.PhysicalUserMessageId = Some observed.PhysicalUserMessageId ->
                enforceImmutableDevopsBinding observed.SessionId observed.Role observed.Target active.Purpose
            | _ -> enforceStaleObservedBinding observed

        let retargetContinuationInputs sessionId previousPhysicalId physicalUserMessageId admission =
            match admission with
            | ExecutionAdmissionAcquisition.Admitted lease ->
                continuationInputs.Remove((sessionId, physicalUserMessageId)) |> ignore
                heldPhysicalReleases.Remove(sessionId, previousPhysicalId) |> ignore

                continuationInputs.Keys
                |> Seq.filter (fun key ->
                    let retained = continuationInputs.[key].Identity

                    retained.SessionId = sessionId
                    && retained.PhysicalUserMessageId = previousPhysicalId)
                |> Seq.toArray
                |> Array.iter (fun key -> continuationInputs.[key] <- lease)
            | _ -> ()

        let continueExecutionAdmissionLocked (previous: ExecutionAdmissionLease) physicalUserMessageId =
            ensureHealthy ()
            let identity = previous.Identity
            let sessionId = identity.SessionId

            match activeBySession.TryGetValue sessionId, admissionOwner.Target previous with
            | (true, current), Ok target when
                current.PhysicalUserMessageId = Some identity.PhysicalUserMessageId
                && current.Participant = Some identity.Participant
                && current.Target = target
                ->
                let replacement =
                    capacity.ContinueExecution(sessionId, identity.PhysicalUserMessageId, physicalUserMessageId, target)

                supersededPhysical.Add(sessionId, identity.PhysicalUserMessageId) |> ignore
                supersedeCurrentDemand sessionId

                let next =
                    { current with
                        PhysicalUserMessageId = Some physicalUserMessageId }

                activeBySession.[sessionId] <- next
                rememberLeasePurpose sessionId next
                drainDemands ()

                let admission =
                    issueAdmission sessionId physicalUserMessageId current.RoutingRole identity.Participant replacement

                retargetContinuationInputs sessionId identity.PhysicalUserMessageId physicalUserMessageId admission
                admission
            | _ -> invalidOp "continuation no longer owns its previous model lease"

        member _.AcquireExecutionAdmission
            (
                sessionId: string,
                physicalUserMessageId: string,
                role: Role,
                participant: string,
                purpose: ModelExecutionPurpose,
                lenderSessionId: string option
            ) : Task<ExecutionAdmissionAcquisition> =
            match normalizeAdmissionInput sessionId physicalUserMessageId role participant with
            | Error error -> failedTask<ExecutionAdmissionAcquisition> error
            | Ok(normSessionId, normPhysicalUserMessageId, normRole, normParticipant) ->
                let normLender = lenderSessionId |> Option.bind normalizeSessionId
                acquireManagedSafe normSessionId normPhysicalUserMessageId normRole normParticipant purpose normLender

        member internal _.RetainContinuationInput(previous: ExecutionAdmissionLease, physicalUserMessageId: string) =
            lock gate (fun () ->
                let identity = previous.Identity

                match activeBySession.TryGetValue identity.SessionId, admissionOwner.Target previous with
                | (true, current), Ok target when
                    current.PhysicalUserMessageId = Some identity.PhysicalUserMessageId
                    && current.Participant = Some identity.Participant
                    && current.Target = target
                    ->
                    continuationInputs.[(identity.SessionId, physicalUserMessageId)] <- previous
                    target
                | _ -> invalidOp "input no longer owns its previous model lease")

        member internal _.TryContinuationInput(sessionId: string, physicalUserMessageId: string) =
            lock gate (fun () ->
                match continuationInputs.TryGetValue((sessionId, physicalUserMessageId)) with
                | true, previous -> Some previous
                | _ -> None)

        member internal _.CancelContinuationInput(sessionId: string, physicalUserMessageId: string) =
            lock gate (fun () -> releaseContinuationInputLocked (sessionId, physicalUserMessageId))

        /// managed-session-lifecycle-027: scope close cancels every retained
        /// continuation input this session owns, completing the delayed exact
        /// return of the old credit once the last retention is gone. Exact to
        /// this session's retention keys; no session-wide or force release.
        member internal _.CancelRetainedInputsForSession(sessionId: string) =
            normalizeSessionId sessionId
            |> Option.iter (fun normSessionId ->
                lock gate (fun () ->
                    continuationInputs.Keys
                    |> Seq.filter (fun (inputSession, _) -> inputSession = normSessionId)
                    |> Seq.toArray
                    |> Array.iter releaseContinuationInputLocked))

        member internal _.ContinueExecutionAdmission(previous: ExecutionAdmissionLease, physicalUserMessageId: string) =
            try
                let acquired =
                    lock gate (fun () -> continueExecutionAdmissionLocked previous physicalUserMessageId)

                Task.FromResult acquired
            with error ->
                failedTask<ExecutionAdmissionAcquisition> error

        member _.ExecutionAdmissionTarget(lease: ExecutionAdmissionLease) = admissionOwner.Target lease

        member _.CommitExecutionAdmission(lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity) =
            /// The fixed DevOps binding is a per-road rule of the owner execution.
            /// While the committing physical execution is still the active one we
            /// enforce with its own purpose: a readonly-delegate execution is a
            /// new execution and never touches the owner binding. A stale or
            /// superseded commit has no live purpose to read, and the ordinary
            /// owner rule still applies — the immutable-binding check is keyed to
            /// the road, not to the currently active physical execution, and the
            /// commit itself settles as a stale fence outcome when the lease no
            /// longer matches.
            lock gate (fun () -> enforceObservedBinding observed)
            admissionOwner.Commit(lease, observed)

        member _.ReleaseExecutionAdmissionBeforeProvider
            (lease: ExecutionAdmissionLease, observed: ExecutionAdmissionExactIdentity)
            =
            match admissionOwner.ReleaseBeforeProvider(lease, observed) with
            | CapacityTransitionOutcome.Applied as applied ->
                lock gate (fun () ->
                    retirePhysicalExecution lease.Identity.SessionId lease.Identity.PhysicalUserMessageId)

                applied
            | settlement -> settlement

        member _.ExecutionAdmissionLifecycle(lease: ExecutionAdmissionLease) = admissionOwner.LifecycleName lease

        /// Strength-only nonwaiting reservation. It is capacity-bearing, but not
        /// yet a provider execution identity. The exact chat.message later adopts
        /// it without another scheduler decision or another running occurrence.
        member _.TryReserveManaged
            (sessionId: string, role: Role, purpose: ModelExecutionPurpose, lenderSessionId: string option)
            : ModelRoutingTarget option =
            match normalizeReservationInput sessionId role with
            | Error _ -> None
            | Ok(normSessionId, normRole) ->
                let normLender = lenderSessionId |> Option.bind normalizeSessionId
                lock gate (fun () -> tryReserveLocked normSessionId normRole purpose normLender)

        member _.TryLease
            (
                sessionId: string,
                physicalUserMessageId: string,
                role: Role,
                participant: string,
                purpose: ModelExecutionPurpose,
                lenderSessionId: string option
            ) : ModelRoutingTarget option =
            match normalizeExecutionInput sessionId physicalUserMessageId role participant with
            | Error _ -> None
            | Ok(normSessionId, normPhysicalUserMessageId, normRole, normParticipant) ->
                let normLender = lenderSessionId |> Option.bind normalizeSessionId

                lock gate (fun () ->
                    tryLeaseLocked normSessionId normPhysicalUserMessageId normRole normParticipant purpose normLender)

        member private _.ReadCommittedExecutionLocked(normSessionId, normPhysicalUserMessageId) =
            match activeBySession.TryGetValue normSessionId with
            | true, lease when
                lease.PhysicalUserMessageId = Some normPhysicalUserMessageId
                && not (supersededPhysical.Contains(normSessionId, normPhysicalUserMessageId))
                ->
                admissionOwner.TryReadCommittedLease(normSessionId, normPhysicalUserMessageId)
            | _ -> None

        /// Read-only exact committed lease query: a superseded generation, a
        /// reservation or another physical message never yields an executable
        /// lease. The query allocates, adopts and releases nothing.
        member this.TryReadExecution
            (sessionId: string, physicalUserMessageId: string)
            : ExecutionAdmissionLease option =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> None
            | Some(normSessionId, normPhysicalUserMessageId) ->
                lock gate (fun () -> this.ReadCommittedExecutionLocked(normSessionId, normPhysicalUserMessageId))

        member _.WasExecutionSuperseded(sessionId: string, physicalUserMessageId: string) : bool =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> false
            | Some exact -> lock gate (fun () -> supersededPhysical.Contains exact)

        member _.OwnsExecutionAdmission(sessionId: string, physicalUserMessageId: string) : bool =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> false
            | Some(exactSession, exactPhysical) ->
                lock gate (fun () ->
                    not (supersededPhysical.Contains(exactSession, exactPhysical))
                    && ((match activeBySession.TryGetValue exactSession with
                         | true, lease -> lease.PhysicalUserMessageId = Some exactPhysical
                         | false, _ -> false)
                        || (admissionQueue.TryCurrent exactSession
                            |> Option.exists (fun demand -> demand.PhysicalUserMessageId = exactPhysical))))

        /// Record the exact provider-run → physical-message relation. Called only
        /// from the authoritative Host start observation, so the tool boundary
        /// can end this run's step without reading any session-current binding.
        member _.RememberProviderStepIdentity(sessionId: string, physicalUserMessageId: string, providerRun: string) =
            let trimmedRun = if isNull providerRun then "" else providerRun.Trim()

            if not (String.IsNullOrEmpty trimmedRun) then
                let keyOpt = normalizePhysicalExecutionKey sessionId physicalUserMessageId

                keyOpt
                |> Option.iter (fun (normSessionId, normPhysicalUserMessageId) ->
                    lock gate (fun () ->
                        rememberProviderStepIdentity normSessionId normPhysicalUserMessageId trimmedRun))

        /// Read-only exact lookup: no run, or a run this process never observed,
        /// yields None. The query allocates nothing and never falls back to the
        /// session's current physical message.
        member _.TryProviderStepIdentity(providerRun: string) : (string * string) option =
            if String.IsNullOrWhiteSpace providerRun then
                None
            else
                lock gate (fun () -> tryProviderStepIdentity (providerRun.Trim()))

        /// Read-only: the physical user message of this session's current
        /// active lease, when the lease is bound to an exact physical message.
        /// Callers use it to settle an in-flight execution before replacing it
        /// with a new admission on the same session.
        member _.TryActivePhysical(sessionId: string) : string option =
            lock gate (fun () ->
                match activeBySession.TryGetValue sessionId with
                | true, lease -> lease.PhysicalUserMessageId
                | false, _ -> None)

        member _.BindDevopsTarget(sessionId: string, target: ModelRoutingTarget) =
            lock gate (fun () ->
                match boundDevopsTargetBySession.TryGetValue sessionId with
                | true, bound when
                    bound <> target
                    && exactTargetAvailable Role.DevOps bound (running ()) ModelExecutionPurpose.Normal
                    ->
                    invalidOp (
                        sprintf
                            "execution-model-routing: DevOps model binding is immutable (%s/%s vs %s/%s)"
                            bound.Model
                            bound.Reasoning
                            target.Model
                            target.Reasoning
                    )
                | _ -> boundDevopsTargetBySession.[sessionId] <- target)

        member _.SeedBoundDevOpsModel(sessionId: string, value: string) =
            let target = parseDurableModelTarget value

            lock gate (fun () ->
                match boundDevopsTargetBySession.TryGetValue sessionId with
                | true, bound when bound <> target ->
                    invalidOp "execution-model-routing: durable DevOps model binding is immutable"
                | _ -> boundDevopsTargetBySession.[sessionId] <- target)

        member _.BoundDevopsTarget(sessionId: string) : ModelRoutingTarget option =
            lock gate (fun () ->
                match boundDevopsTargetBySession.TryGetValue sessionId with
                | true, target -> Some target
                | false, _ -> None)

        /// Physical end signals are cleanup evidence, not the sole correctness
        /// mechanism. A newer chat.message also supersedes this lease atomically.
        member internal _.ReleaseExecution(sessionId: string) =
            normalizeSessionId sessionId
            |> Option.map (fun normSessionId ->
                lock gate (fun () ->
                    let outcome =
                        currentPhysicalUserMessageId normSessionId
                        |> Option.map (fun physical -> admissionOwner.ReleasePhysical(normSessionId, physical))
                        |> Option.defaultWith (fun () -> admissionQueue.CancelSession normSessionId)

                    match outcome with
                    | CapacityTransitionOutcome.Applied -> retireCurrentExecution normSessionId
                    | CapacityTransitionOutcome.AlreadyApplied
                    | CapacityTransitionOutcome.StaleFence
                    | CapacityTransitionOutcome.Conflict -> ()

                    outcome))
            |> Option.defaultValue CapacityTransitionOutcome.Conflict

        /// Exact physical terminal evidence. Unlike force cleanup, this cannot
        /// retire a newer execution or pending demand that happens to reuse the
        /// same SessionId after the terminal event was produced.
        member internal _.ReleasePhysicalExecution(sessionId: string, physicalUserMessageId: string) =
            normalizePhysicalExecutionKey sessionId physicalUserMessageId
            |> Option.map (fun normKey ->
                lock gate (fun () ->
                    providerStepIdentityByRun.Keys
                    |> Seq.filter (fun run ->
                        match providerStepIdentityByRun.TryGetValue run with
                        | true, struct (runSession, runPhysical) ->
                            runSession = fst normKey && runPhysical = snd normKey
                        | false, _ -> false)
                    |> Seq.toArray
                    |> Array.iter (fun run -> providerStepIdentityByRun.Remove run |> ignore))

                lock gate (fun () ->
                    releaseContinuationInputLocked normKey

                    if hasContinuationInput normKey then
                        heldPhysicalReleases.Add normKey |> ignore
                        PhysicalExecutionReleaseOutcome.HeldForInput
                    else
                        releasePhysicalExecutionLocked normKey
                        |> PhysicalExecutionReleaseOutcome.Released))
            |> Option.defaultValue (PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.Conflict)

        member _.CancelPendingExecution(sessionId: string) =
            normalizeSessionId sessionId
            |> Option.map (fun normSessionId -> lock gate (fun () -> admissionQueue.CancelSession normSessionId))
            |> Option.defaultValue CapacityTransitionOutcome.Conflict

        member _.CancelPendingPhysicalExecution(sessionId: string, physicalUserMessageId: string) =
            normalizePhysicalExecutionKey sessionId physicalUserMessageId
            |> Option.map (fun key -> lock gate (fun () -> cancelPendingPhysicalExecutionLocked key))
            |> Option.defaultValue CapacityTransitionOutcome.Conflict

        member _.CapacitySnapshot() = lock gate capacitySnapshotLocked

        member _.EnterProviderStep
            (sessionId: string, physicalUserMessageId: string, visibleProviderRuns: Set<string>, ?requestKey: string)
            : Task =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> failedTask<unit> (ArgumentException("provider step identity must be non-empty")) :> Task
            | Some(normSessionId, normPhysicalUserMessageId) ->
                let cacheKeyOpt =
                    makeStepCacheKey normSessionId normPhysicalUserMessageId requestKey

                enterProviderStepInternal
                    normSessionId
                    normPhysicalUserMessageId
                    visibleProviderRuns
                    requestKey
                    cacheKeyOpt

        member _.EndProviderStep(sessionId: string, physicalUserMessageId: string, providerRun: string) =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> ()
            | Some _ when String.IsNullOrWhiteSpace providerRun -> ()
            | Some(normSessionId, normPhysicalUserMessageId) ->
                lock gate (fun () ->
                    let normalizedProviderRun = providerRun.Trim()
                    rememberProviderRunTarget normSessionId normPhysicalUserMessageId normalizedProviderRun
                    rememberProviderStepIdentity normSessionId normPhysicalUserMessageId normalizedProviderRun
                    capacity.EndStep(normSessionId, normPhysicalUserMessageId, normalizedProviderRun)
                    let prefix = sprintf "%s:%s:" normSessionId normPhysicalUserMessageId

                    activeProviderStepTasks.Keys
                    |> Seq.filter (fun k -> k.StartsWith prefix)
                    |> Seq.toArray
                    |> Array.iter (fun k -> activeProviderStepTasks.Remove k |> ignore)

                    drainIfHealthy ())

        member _.SuppressProviderStep(sessionId: string, physicalUserMessageId: string) =
            match normalizePhysicalExecutionKey sessionId physicalUserMessageId with
            | None -> ()
            | Some(normSessionId, normPhysicalUserMessageId) ->
                lock gate (fun () ->
                    capacity.SuppressStep(normSessionId, normPhysicalUserMessageId)
                    let prefix = sprintf "%s:%s:" normSessionId normPhysicalUserMessageId

                    activeProviderStepTasks.Keys
                    |> Seq.filter (fun k -> k.StartsWith prefix)
                    |> Seq.toArray
                    |> Array.iter (fun k -> activeProviderStepTasks.Remove k |> ignore)

                    drainIfHealthy ())

        member _.SnapshotOccupied() = lock gate (fun () -> running ())
        member _.PendingCount = lock gate (fun () -> admissionQueue.Count)
        member _.PendingBound = ModelCapacityQueue.MaximumPendingDemands
        member _.PendingContractVersion = ModelCapacityQueue.ContractVersion

        member _.TakeProviderRunTarget(providerRun: string) : ModelRoutingTarget option =
            lock gate (fun () -> takeProviderRunTarget providerRun)

        /// provider-attempt-recovery-021: the failed attempt itself carried the LWR-replaced context,
        /// so its confirmed failure condemns the provider of the exact witness
        /// target. The witness is single-consumption: duplicate observations,
        /// stale callbacks, cancellations and unknown submissions hold no
        /// second permission.
        member _.CondemnFailedTarget(providerRun: string) : ModelRoutingTarget option =
            lock gate (fun () ->
                match takeProviderRunWitness (providerRun.Trim()) with
                | Some(_, target) ->
                    markProviderOfTarget target
                    Some target
                | None -> None)

        /// provider-attempt-recovery-021: the failed attempt carried the original context, so its
        /// provider is kept and the next fresh admission of this session is
        /// bound to the exact failed target for the LWR retry. The witness
        /// must belong to this session and the binding is consumed once.
        member _.RetainFailedTargetForRetry(sessionId: string, providerRun: string) : ModelRoutingTarget option =
            match normalizeSessionId sessionId with
            | None -> None
            | Some normSessionId -> lock gate (fun () -> retainFailedTargetOfRun normSessionId (providerRun.Trim()))

        member _.HasTheoreticalCapacity (role: string) (purpose: ModelExecutionPurpose) : bool =
            lock gate (fun () -> hasTheoreticalCapacityLocked role purpose)

        /// Read-only Predictor slot existence query owned by the loaded MJS
        /// configuration. Tool decoration and readonly-delegate admission share
        /// this one result instead of keeping a second enabled truth.
        member _.PredictorConfiguration: PredictorConfiguration =
            predictorConfiguration scheduler

    let private sharedGate = obj ()
    // DSL-MUTABLE: resource — process-shared scheduler runtime singleton
    let mutable private sharedRuntime: ModelRoutingRuntime option = None
    // DSL-MUTABLE: single-flight — in-flight scheduler bootstrap
    let mutable private sharedLoad: Task<ModelRoutingRuntime> option = None

    let private ensureShared () : Task<ModelRoutingRuntime> =
        lock sharedGate (fun () ->
            match sharedRuntime, sharedLoad with
            | Some runtime, _ -> Task.FromResult runtime
            | None, Some loading -> loading
            | None, None ->
                let loading =
                    task {
                        let! scheduler = bootstrapDefault ()
                        let runtime = ModelRoutingRuntime(scheduler)
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

    let internal takeProviderRunTarget (providerRun: ProviderRunIdentity) : ModelRoutingTarget option =
        current().TakeProviderRunTarget(ProviderRunIdentity.value providerRun)

    let internal condemnFailedTarget (providerRun: ProviderRunIdentity) : ModelRoutingTarget option =
        current().CondemnFailedTarget(ProviderRunIdentity.value providerRun)

    let internal retainFailedTargetForRetry
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        : ModelRoutingTarget option =
        current()
            .RetainFailedTargetForRetry(SessionId.value sessionId, ProviderRunIdentity.value providerRun)

    /// purpose defaults to Normal: the provider recovery retry decisions ask
    /// about the owner execution of the failing role.
    let internal hasTheoreticalCapacity (role: string) (purpose: ModelExecutionPurpose) : bool =
        current().HasTheoreticalCapacity role purpose

    let internal acquireExecutionAdmission
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (role: Role)
        (participant: string)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        =
        current()
            .AcquireExecutionAdmission(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                role,
                participant,
                purpose,
                lenderSessionId
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

    let hasRuntime () : bool =
        lock sharedGate (fun () -> sharedRuntime.IsSome)

    let tryReserveManaged
        (sessionId: SessionId)
        (role: Role)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.TryReserveManaged(SessionId.value sessionId, role, purpose, lenderSessionId)
        | None -> None

    let tryLease
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (role: Role)
        (participant: string)
        (purpose: ModelExecutionPurpose)
        (lenderSessionId: string option)
        =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.TryLease(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                role,
                participant,
                purpose,
                lenderSessionId
            )
        | None -> None

    let internal continueExecutionAdmission (previous: ExecutionAdmissionLease) (physicalId: PhysicalUserMessageId) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.ContinueExecutionAdmission(previous, PhysicalUserMessageId.value physicalId)
        | None ->
            failedTask<ExecutionAdmissionAcquisition> (InvalidOperationException "model-routing runtime is unavailable")

    let internal retainContinuationInput (previous: ExecutionAdmissionLease) (key: ChatExecutionKey) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.RetainContinuationInput(previous, PhysicalUserMessageId.value key.PhysicalUserMessageId)
        | None -> invalidOp "model-routing runtime is unavailable"

    /// managed-session-lifecycle-027: the physical-only face of the shared
    /// retention operation used by the Surface. The session of the retention
    /// key is the previous lease's own session, so no caller needs to construct
    /// a ChatExecutionKey for it.
    let internal retainContinuationInputForPhysical
        (previous: ExecutionAdmissionLease)
        (physicalUserMessageId: PhysicalUserMessageId)
        =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.RetainContinuationInput(previous, PhysicalUserMessageId.value physicalUserMessageId)
        | None -> invalidOp "model-routing runtime is unavailable"

    let internal tryContinuationInput (key: ChatExecutionKey) =
        lock sharedGate (fun () -> sharedRuntime)
        |> Option.bind (fun runtime ->
            runtime.TryContinuationInput(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            ))

    let internal cancelContinuationInput (key: ChatExecutionKey) =
        lock sharedGate (fun () -> sharedRuntime)
        |> Option.iter (fun runtime ->
            runtime.CancelContinuationInput(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            ))

    /// managed-session-lifecycle-027: cancel every retained continuation input
    /// this session owns on the process-shared runtime.
    let internal cancelRetainedInputsForSession (sessionId: SessionId) =
        lock sharedGate (fun () -> sharedRuntime)
        |> Option.iter (fun runtime -> runtime.CancelRetainedInputsForSession(SessionId.value sessionId))

    /// Read-only exact committed lease query on the process-shared runtime; an
    /// unloaded runtime observes nothing.
    let internal tryReadExecution (key: ChatExecutionKey) : ExecutionAdmissionLease option =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.TryReadExecution(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> None

    let internal wasExecutionSuperseded (key: ChatExecutionKey) : bool =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.WasExecutionSuperseded(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> false

    let internal ownsExecutionAdmission (key: ChatExecutionKey) : bool =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.OwnsExecutionAdmission(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> false

    /// Exact provider-run → physical-message relation, written only from the
    /// authoritative Host start observation.
    let internal rememberProviderStepIdentity
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        (providerRun: ProviderRunIdentity)
        =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.RememberProviderStepIdentity(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                ProviderRunIdentity.value providerRun
            )
        | None -> ()

    /// Read-only exact lookup for the physical message a Host-observed provider
    /// run answers. Unobserved run or unloaded runtime yields None; never the
    /// session's current physical message.
    let internal tryProviderStepIdentity
        (providerRun: ProviderRunIdentity)
        : (SessionId * PhysicalUserMessageId) option =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.TryProviderStepIdentity(ProviderRunIdentity.value providerRun)
            |> Option.map (fun (sessionId, physicalUserMessageId) ->
                SessionId.create sessionId, PhysicalUserMessageId.create physicalUserMessageId)
        | None -> None

    /// What a read-only observation barrier needs about one exact execution:
    /// the participant it must run as and the model target it must observe.
    /// Plain values only — the lease itself stays inside the capacity owner, so
    /// a consumer cannot commit, release or re-fence by holding it.
    let readExecutionAdmission (key: ChatExecutionKey) : (string * ModelRoutingTarget) option =
        tryReadExecution key
        |> Option.map (fun lease -> lease.Identity.Participant, lease.Identity.Target)

    let internal boundDevopsModel (sessionId: SessionId) : OpencodeModel option =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.BoundDevopsTarget(SessionId.value sessionId)
            |> Option.map toOpenCodeModel
        | None -> None

    /// Seed the fixed DevOps target from a road's durable projection
    /// (execution-model-routing-019).
    ///
    /// Reseeding is idempotent and conflicts fail closed even when the scheduler
    /// no longer offers the old target. An unloaded runtime is refused.
    let internal seedBoundDevOpsModel (sessionId: SessionId) (value: string) : unit =
        match lock sharedGate (fun () -> sharedRuntime) with
        | None ->
            invalidOp
                "execution-model-routing: the shared runtime is not loaded, so the durable DevOps model target cannot be seeded; refusing to fall back to the current scheduler preference."
        | Some runtime -> runtime.SeedBoundDevOpsModel(SessionId.value sessionId, value)

    let internal releaseExecution (sessionId: SessionId) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.ReleaseExecution(SessionId.value sessionId)
        | None -> CapacityTransitionOutcome.AlreadyApplied

    let internal releasePhysicalExecution (sessionId: SessionId) (physicalUserMessageId: PhysicalUserMessageId) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.ReleasePhysicalExecution(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId
            )
        | None -> PhysicalExecutionReleaseOutcome.Released CapacityTransitionOutcome.AlreadyApplied

    let internal observePhysicalResource (key: ChatExecutionKey) =
        let observation held =
            if held then
                PhysicalResourceObservation.ResourceHeld key
            else
                PhysicalResourceObservation.ResourceAbsent key

        match lock sharedGate (fun () -> sharedRuntime) with
        | None -> PhysicalResourceObservation.ResourceAbsent key
        | Some runtime ->
            let sessionId = SessionId.value key.SessionId
            let physicalUserMessageId = PhysicalUserMessageId.value key.PhysicalUserMessageId

            let exact (owner: CapacityExactOwnerSnapshot) =
                owner.SessionId = sessionId
                && owner.PhysicalUserMessageId = physicalUserMessageId

            let snapshot = runtime.CapacitySnapshot()

            let held =
                snapshot.Owners |> Array.exists exact
                || snapshot.Custodies |> Array.exists (fun custody -> exact custody.Owner)
                || runtime.TryContinuationInput(sessionId, physicalUserMessageId) |> Option.isSome

            observation held

    let internal cancelUnacquiredExecution (sessionId: SessionId) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.CancelPendingExecution(SessionId.value sessionId)
        | None -> CapacityTransitionOutcome.AlreadyApplied

    let internal cancelPendingPhysicalExecution (key: ChatExecutionKey) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.CancelPendingPhysicalExecution(
                SessionId.value key.SessionId,
                PhysicalUserMessageId.value key.PhysicalUserMessageId
            )
        | None -> CapacityTransitionOutcome.AlreadyApplied

    let internal capacitySnapshot () = current().CapacitySnapshot()

    /// Same Predictor existence query on the process-shared scheduler, resolved
    /// from the one loaded model configuration.
    let internal sharedPredictorConfiguration () : PredictorConfiguration = current().PredictorConfiguration

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
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.EndProviderStep(
                SessionId.value sessionId,
                PhysicalUserMessageId.value physicalUserMessageId,
                ProviderRunIdentity.value providerRun
            )
        | None -> ()

    let suppressProviderStep (sessionId: SessionId) (physicalUserMessageId: PhysicalUserMessageId) =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime ->
            runtime.SuppressProviderStep(SessionId.value sessionId, PhysicalUserMessageId.value physicalUserMessageId)
        | None -> ()

    /// The physical user message of a session's current active lease, when the
    /// lease is bound to an exact physical message. Read-only.
    let tryActivePhysical (sessionId: string) : string option =
        match lock sharedGate (fun () -> sharedRuntime) with
        | Some runtime -> runtime.TryActivePhysical(sessionId.Trim())
        | None -> None

    let private requireOutputMessage output =
        let message = if isNull output then null else output?message

        if isNull message then
            invalidOp "EMR-009: managed chat.message routing has no mutable output.message"

        message

    /// execution-model-routing-009 Host projection. Routing owns both which outcomes carry a model
    /// and the exact mutable Host field that receives that model; composition
    /// roots only invoke this published projection.
    let projectHostModel (output: obj) (model: OpencodeModel) =
        try
            let message = requireOutputMessage output
            message?model <- box model
            Ok()
        with error ->
            Error error
