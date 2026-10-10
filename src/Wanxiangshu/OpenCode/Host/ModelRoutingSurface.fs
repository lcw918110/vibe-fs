namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Execution.Failure

/// JS-native model-routing observation boundary. Scheduler policy remains in
/// the configured MJS provider; JS tests observe only the selected target.
module ModelRoutingSurface =

    type private RuntimeHandle(runtime: ModelRouting.ModelRoutingRuntime) =
        member _.Runtime = runtime

    type private PortHandle(port: IOpenCodePort) =
        member _.Port = port

    [<Emit("new WeakMap()")>]
    let private createWeakMap () : obj = jsNative

    let private executionAdmissionLeases = createWeakMap ()
    let private executionAdmissionTokens = createWeakMap ()
    let private queuedAdmissionNodes = createWeakMap ()

    [<Emit("Object.freeze(Object.defineProperty(Object.create(null), 'toJSON', { value: function () { throw new Error('ExecutionAdmissionToken is process-local and cannot be serialized'); } }))")>]
    let private opaqueLeaseToken () : obj = jsNative

    [<Emit("$0.set($1, $2)")>]
    let private rememberOpaqueLease (leases: obj) (token: obj) (lease: ExecutionAdmissionLease) : unit = jsNative

    [<Emit("$0.set($1, $2)")>]
    let private rememberQueuedNode (nodes: obj) (token: obj) (node: ExecutionAdmissionQueueNode) : unit = jsNative

    [<Emit("$0.get($1)")>]
    let private queuedNodeValue (nodes: obj) (token: obj) : ExecutionAdmissionQueueNode = jsNative

    [<Emit("$0.has($1)")>]
    let private hasOpaqueLease (leases: obj) (token: obj) : bool = jsNative

    [<Emit("$0.get($1)")>]
    let private opaqueLeaseValue (leases: obj) (token: obj) : ExecutionAdmissionLease = jsNative

    [<Emit("$0.set($1, $2)")>]
    let private rememberOpaqueToken (tokens: obj) (lease: ExecutionAdmissionLease) (token: obj) : unit = jsNative

    [<Emit("$0.has($1)")>]
    let private hasOpaqueToken (tokens: obj) (lease: ExecutionAdmissionLease) : bool = jsNative

    [<Emit("$0.get($1)")>]
    let private opaqueTokenValue (tokens: obj) (lease: ExecutionAdmissionLease) : obj = jsNative

    [<Emit("$0 == null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    let private field (value: obj) (names: string list) : obj =
        if isNullish value then
            null
        else
            names
            |> List.tryPick (fun name ->
                let item = property value name
                if isNullish item then None else Some item)
            |> Option.defaultValue null

    let private text (value: obj) : string =
        if isNullish value then "" else string value

    let private arrayOf (value: obj) : obj array =
        if isNullish value then [||] else unbox<obj array> value

    let private optionalText (value: obj) : string option =
        if isNullish value then None else Some(text value)

    let private normalizeRoleText (value: obj) : Role =
        let raw = text value

        match Roles.tryParseRole raw with
        | Some role -> role
        | None -> invalidArg "role" (sprintf "unknown role: %s" raw)

    let private optionalObject (value: obj) : obj option =
        if isNullish value then None else Some value

    let private targetObject (target: ModelRoutingTarget) : obj =
        box
            {| model = target.Model
               reasoning = target.Reasoning |}

    /// The JS observation boundary mirrors the MJS ABI vocabulary exactly.
    /// An absent purpose is the ordinary owner admission; only the two protocol
    /// values are accepted and anything else fails closed. The purpose never
    /// comes from tool arguments, user text or a role name.
    let private purposeOf (value: obj) : ModelExecutionPurpose =
        match text value with
        | ""
        | "normal" -> ModelExecutionPurpose.Normal
        | "readonly-delegate" -> ModelExecutionPurpose.ReadonlyDelegate
        | other -> invalidArg "purpose" (sprintf "execution-model-routing: unknown scheduler purpose %s" other)

    [<Emit("(function freeze(value) { if (value !== null && typeof value === 'object' && !Object.isFrozen(value)) { Object.freeze(value); Object.keys(value).forEach(function (key) { freeze(value[key]); }); } return value; })($0)")>]
    let private deepFreeze (value: obj) : obj = jsNative


    let private targetOf (value: obj) : ModelRoutingTarget =
        if isNullish value then
            invalidArg "running" "execution-model-routing: running target must be non-null"

        { Model = text (field value [ "model"; "Model" ])
          Reasoning = text (field value [ "reasoning"; "Reasoning" ]) }

    let private admissionIdentityOf (value: obj) : ExecutionAdmissionExactIdentity =
        { SessionId = text (field value [ "sessionId"; "SessionId" ])
          PhysicalUserMessageId = text (field value [ "physicalUserMessageId"; "PhysicalUserMessageId" ])
          Role = normalizeRoleText (field value [ "role"; "Role" ])
          Participant = text (field value [ "participant"; "Participant" ])
          Target = targetOf (field value [ "target"; "Target" ]) }

    let private int64Of (value: obj) : int64 = value |> unbox<float> |> int64

    let private intOf (value: obj) : int = unbox value

    [<Emit("Number($0)")>]
    let private numberOfInt64 (value: int64) : float = jsNative

    let private ownerObject (owner: CapacityExactOwnerSnapshot) : obj =
        box
            {| sessionId = owner.SessionId
               physicalUserMessageId =
                if System.String.IsNullOrEmpty owner.PhysicalUserMessageId then
                    null
                else
                    owner.PhysicalUserMessageId
               role =
                owner.Role
                |> Option.map Roles.roleLabel
                |> Option.map box
                |> Option.defaultValue null
               participant = owner.Participant |> Option.map box |> Option.defaultValue null |}

    let private ownerOf (value: obj) : CapacityExactOwnerSnapshot =
        let rawRole = optionalText (field value [ "role"; "Role" ])

        let roleOpt =
            match rawRole with
            | Some r when not (System.String.IsNullOrWhiteSpace r) -> Roles.tryParseRole r
            | _ -> None

        { SessionId = text (field value [ "sessionId"; "SessionId" ])
          PhysicalUserMessageId = text (field value [ "physicalUserMessageId"; "PhysicalUserMessageId" ])
          Role = roleOpt
          Participant = optionalText (field value [ "participant"; "Participant" ]) }

    let private invariantEvidenceOf (value: obj) : CapacityInvariantEvidence =
        let ledgerEntries =
            field value [ "ledgerEntries"; "LedgerEntries" ]
            |> arrayOf
            |> Array.map (fun entry ->
                { Credit = int64Of (field entry [ "credit"; "Credit" ])
                  Target = targetOf (field entry [ "target"; "Target" ]) })

        let tokens =
            field value [ "tokens"; "Tokens" ]
            |> arrayOf
            |> Array.map (fun token ->
                { Credit = int64Of (field token [ "credit"; "Credit" ])
                  State = text (field token [ "state"; "State" ])
                  Owner = ownerOf (field token [ "owner"; "Owner" ])
                  Target = targetOf (field token [ "target"; "Target" ]) })

        let custodies =
            field value [ "custodies"; "Custodies" ]
            |> arrayOf
            |> Array.map (fun custody ->
                { Credit = int64Of (field custody [ "credit"; "Credit" ])
                  Owner = ownerOf (field custody [ "owner"; "Owner" ]) })

        let waiters =
            field value [ "waiters"; "Waiters" ]
            |> arrayOf
            |> Array.map (fun waiter ->
                { Owner = ownerOf waiter
                  Sequence = int64Of (field waiter [ "sequence"; "Sequence" ])
                  Kind = text (field waiter [ "kind"; "Kind" ]) })

        let lineage =
            field value [ "lineage"; "Lineage" ]
            |> arrayOf
            |> Array.map (fun edge ->
                { ParentSessionId = text (field edge [ "parentSessionId"; "ParentSessionId" ])
                  ChildSessionId = text (field edge [ "childSessionId"; "ChildSessionId" ]) })

        let stateCounts = field value [ "tokenStateCounts"; "TokenStateCounts" ]
        let counters = field value [ "counters"; "Counters" ]

        { LedgerEntries = ledgerEntries
          Tokens = tokens
          Custodies = custodies
          Executions = field value [ "executions"; "Executions" ] |> arrayOf |> Array.map ownerOf
          Waiters = waiters
          Owners = field value [ "owners"; "Owners" ] |> arrayOf |> Array.map ownerOf
          Lineage = lineage
          IdleCount = intOf (field stateCounts [ "idle"; "Idle" ])
          InFlightCount = intOf (field stateCounts [ "inFlight"; "InFlight" ])
          RetiringCount = intOf (field stateCounts [ "retiring"; "Retiring" ])
          ActiveCount = intOf (field value [ "activeCount"; "ActiveCount" ])
          Counters =
            { Duplicate = int64Of (field counters [ "duplicate"; "Duplicate" ])
              Stale = int64Of (field counters [ "stale"; "Stale" ])
              Conflict = int64Of (field counters [ "conflict"; "Conflict" ]) } }

    let private reconciliationFailureName =
        function
        | CapacityReconciliationFailure.ActiveOutsideLedgerBounds -> "ActiveOutsideLedgerBounds"
        | CapacityReconciliationFailure.TokenStateCountMismatch -> "TokenStateCountMismatch"
        | CapacityReconciliationFailure.MapLedgerDivergence -> "MapLedgerDivergence"
        | CapacityReconciliationFailure.UntraceableTokenOwner -> "UntraceableTokenOwner"
        | CapacityReconciliationFailure.UntraceableWaiterOwner -> "UntraceableWaiterOwner"
        | CapacityReconciliationFailure.UntraceableExecutionCustody -> "UntraceableExecutionCustody"
        | CapacityReconciliationFailure.CounterRegression -> "CounterRegression"

    let private capacitySnapshotObject (snapshot: CapacityInvariantEvidence) : obj =
        let ledgerEntries =
            snapshot.LedgerEntries
            |> Array.map (fun entry ->
                box
                    {| credit = numberOfInt64 entry.Credit
                       target = targetObject entry.Target |})

        let tokens =
            snapshot.Tokens
            |> Array.map (fun token ->
                box
                    {| credit = numberOfInt64 token.Credit
                       state = token.State
                       owner = ownerObject token.Owner
                       target = targetObject token.Target |})

        let custodies =
            snapshot.Custodies
            |> Array.map (fun custody ->
                box
                    {| credit = numberOfInt64 custody.Credit
                       owner = ownerObject custody.Owner |})

        let waiters =
            snapshot.Waiters
            |> Array.map (fun waiter ->
                box
                    {| sessionId = waiter.Owner.SessionId
                       physicalUserMessageId = waiter.Owner.PhysicalUserMessageId
                       role =
                        waiter.Owner.Role
                        |> Option.map Roles.roleLabel
                        |> Option.map box
                        |> Option.defaultValue null
                       participant = waiter.Owner.Participant |> Option.map box |> Option.defaultValue null
                       sequence = numberOfInt64 waiter.Sequence
                       kind = waiter.Kind |})

        box
            {| ledgerEntries = ledgerEntries
               tokens = tokens
               custodies = custodies
               executions = snapshot.Executions |> Array.map ownerObject
               waiters = waiters
               owners = snapshot.Owners |> Array.map ownerObject
               lineage =
                snapshot.Lineage
                |> Array.map (fun edge ->
                    box
                        {| parentSessionId = edge.ParentSessionId
                           childSessionId = edge.ChildSessionId |})
               tokenStateCounts =
                {| idle = snapshot.IdleCount
                   inFlight = snapshot.InFlightCount
                   retiring = snapshot.RetiringCount |}
               activeCount = snapshot.ActiveCount
               counters =
                {| duplicate = numberOfInt64 snapshot.Counters.Duplicate
                   stale = numberOfInt64 snapshot.Counters.Stale
                   conflict = numberOfInt64 snapshot.Counters.Conflict |} |}
        |> deepFreeze

    let private failureName =
        function
        | ExecutionFailure.ProtocolRejection -> "ProtocolRejection"
        | ExecutionFailure.Superseded -> "Superseded"
        | ExecutionFailure.UserCancelled -> "UserCancelled"
        | ExecutionFailure.CapacityQueueFull -> "CapacityQueueFull"
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.ProviderTransient
        | ExecutionFailure.ProviderPermanent
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.StreamInterruptedAfterFirstToken
        | ExecutionFailure.PersistenceFailure _ -> invalidOp "unexpected capacity boundary failure"

    let private transitionOutcomeObject =
        function
        | CapacityTransitionOutcome.Applied -> box {| kind = "Applied" |}
        | CapacityTransitionOutcome.AlreadyApplied -> box {| kind = "AlreadyApplied" |}
        | CapacityTransitionOutcome.StaleFence -> box {| kind = "StaleFence" |}
        | CapacityTransitionOutcome.Conflict -> box {| kind = "Conflict" |}

    let private physicalReleaseObject =
        function
        | PhysicalExecutionReleaseOutcome.Released outcome -> transitionOutcomeObject outcome
        | PhysicalExecutionReleaseOutcome.HeldForInput -> box {| kind = "HeldForInput" |}

    let private leaseOf token =
        if hasOpaqueLease executionAdmissionLeases token then
            Some(opaqueLeaseValue executionAdmissionLeases token)
        else
            None

    let private terminalAcquisitionObject kind acquisition =
        let failure =
            ModelRouting.failureOfExecutionAdmissionAcquisition acquisition
            |> Option.map failureName
            |> Option.defaultWith (fun () -> invalidOp "terminal acquisition requires typed failure")

        box
            {| kind = kind
               failure = failure
               lease = null
               queue = null |}

    let private acquireAdmissionObject =
        function
        | ExecutionAdmissionAcquisition.QueueFull as acquisition -> terminalAcquisitionObject "QueueFull" acquisition
        | ExecutionAdmissionAcquisition.Cancelled as acquisition -> terminalAcquisitionObject "Cancelled" acquisition
        | ExecutionAdmissionAcquisition.Superseded as acquisition -> terminalAcquisitionObject "Superseded" acquisition
        | ExecutionAdmissionAcquisition.Queued node ->
            let token = opaqueLeaseToken ()
            rememberQueuedNode queuedAdmissionNodes token node

            box
                {| kind = "Queued"
                   failure = null
                   lease = null
                   queue = token |}
        | ExecutionAdmissionAcquisition.Admitted lease ->
            match ModelRouting.capacityOwnership lease with
            | CapacityOwnership.NoCapacityFence -> invalidOp "acquired admission requires exact fence"
            | CapacityOwnership.OwnsExactFence _ -> ()

            let token =
                if hasOpaqueToken executionAdmissionTokens lease then
                    opaqueTokenValue executionAdmissionTokens lease
                else
                    let created = opaqueLeaseToken ()
                    rememberOpaqueToken executionAdmissionTokens lease created
                    rememberOpaqueLease executionAdmissionLeases created lease
                    created

            box
                {| kind = "Acquired"
                   failure = null
                   lease = token
                   queue = null |}

    let rec private awaitAdmission =
        function
        | ExecutionAdmissionAcquisition.Queued node ->
            task {
                let! completed = node.Completion.Task
                return! awaitAdmission completed
            }
        | acquisition -> Task.FromResult acquisition

    let private targetsOf (value: obj) : ModelRoutingTarget array = arrayOf value |> Array.map targetOf

    let private runtimeOf (value: obj) : ModelRouting.ModelRoutingRuntime = (unbox<RuntimeHandle> value).Runtime

    let private portOf (value: obj) : IOpenCodePort = (unbox<PortHandle> value).Port

    let private modelOf (value: obj) : OpencodeModel option =
        if isNullish value then
            None
        else
            Some
                { providerID = text (field value [ "providerID"; "providerId" ])
                  modelID = text (field value [ "modelID"; "modelId" ])
                  variant = optionalText (field value [ "variant" ]) }

    let private toolsOf (value: obj) : Map<string, bool> option =
        let raw = field value [ "tools"; "Tools" ]

        if isNullish raw then
            None
        else
            let keys: string array = emitJsExpr raw "Object.keys($0)"

            keys
            |> Array.map (fun key -> key, unbox<bool> (property raw key))
            |> Map.ofArray
            |> Some

    let private promptOptionsOf (value: obj) : OpenCodePromptOptions =
        { Model = modelOf (field value [ "model"; "Model" ])
          Agent = optionalText (field value [ "agent"; "Agent" ])
          Directory = optionalText (field value [ "directory"; "Directory" ])
          Metadata = optionalObject (field value [ "metadata"; "Metadata" ])
          Tools = toolsOf value
          DetachedListener = None }

    let private outcomeToJs (outcome: SendOutcome) : obj =
        match outcome with
        | AdmittedWithReceipt receipt ->
            box
                {| kind = "AdmittedWithReceipt"
                   receipt = TransportReceipt.value receipt
                   physical = null
                   error = null |}
        | AdmittedWithPhysicalMessage physical ->
            box
                {| kind = "AdmittedWithPhysicalMessage"
                   receipt = null
                   physical = PhysicalUserMessageId.value physical
                   error = null |}
        | Retryable reason ->
            box
                {| kind = "Retryable"
                   receipt = null
                   physical = null
                   error = reason |}
        | AcceptanceUnknown reason ->
            box
                {| kind = "AcceptanceUnknown"
                   receipt = null
                   physical = null
                   error = reason |}
        | Fatal reason ->
            box
                {| kind = "Fatal"
                   receipt = null
                   physical = null
                   error = reason |}

    /// Initialize the process-shared scheduler runtime used by Host admission.
    let initialize () : Task = ModelRouting.initialize ()

    let acquireSharedExecutionAdmission
        (sessionId: string)
        (physicalUserMessageId: string)
        (role: string)
        (participant: string)
        (lenderSessionId: string)
        (purpose: obj)
        : Task<obj> =
        task {
            let! acquisition =
                ModelRouting.acquireExecutionAdmission
                    (SessionId.create sessionId)
                    (PhysicalUserMessageId.create physicalUserMessageId)
                    (normalizeRoleText (box role))
                    participant
                    (purposeOf purpose)
                    (if
                         isNullish (box lenderSessionId)
                         || System.String.IsNullOrWhiteSpace lenderSessionId
                     then
                         None
                     else
                         Some lenderSessionId)

            let! completed = awaitAdmission acquisition
            return acquireAdmissionObject completed
        }

    let sharedExecutionAdmissionTarget (token: obj) : obj =
        match leaseOf token with
        | None -> null
        | Some lease ->
            match ModelRouting.executionAdmissionTarget lease with
            | Ok target -> targetObject target
            | Error _ -> null

    let sharedCapacitySnapshot () : obj =
        ModelRouting.capacitySnapshot () |> capacitySnapshotObject

    /// Enter a provider step on the process-shared runtime — the same
    /// execution-scoped admission the real transform boundary must satisfy.
    let sharedEnterProviderStep
        (sessionId: string)
        (physicalUserMessageId: string)
        (visibleProviderRuns: string array)
        : Task =
        ModelRouting.enterProviderStep
            (SessionId.create sessionId)
            (PhysicalUserMessageId.create physicalUserMessageId)
            (visibleProviderRuns |> Set.ofArray |> Set.map ProviderRunIdentity.create)
            None

    let commitSharedExecutionAdmission (token: obj) (observed: obj) : obj =
        match leaseOf token with
        | None -> CapacityTransitionOutcome.StaleFence |> transitionOutcomeObject
        | Some lease ->
            ModelRouting.commitExecutionAdmission lease (admissionIdentityOf observed)
            |> transitionOutcomeObject

    let releaseSharedExecutionAdmissionBeforeProvider (token: obj) (observed: obj) : obj =
        match leaseOf token with
        | None -> CapacityTransitionOutcome.StaleFence |> transitionOutcomeObject
        | Some lease ->
            ModelRouting.releaseExecutionAdmissionBeforeProvider lease (admissionIdentityOf observed)
            |> transitionOutcomeObject

    /// The shared-runtime retention wrapper. Production retains the old opaque
    /// lease for an accepted continuation input on the process-shared runtime
    /// (HostSignalBootstrap PublishInput), and clearSession releases against
    /// that same runtime, so this is the shared face of the same operation the
    /// isolated wrapper exposes for an isolated runtime.
    let sharedRetainContinuationInput (token: obj) (physicalUserMessageId: string) : obj =
        match leaseOf token with
        | None -> invalidArg "token" "retention requires an opaque admission lease"
        | Some lease ->
            ModelRouting.retainContinuationInputForPhysical lease (PhysicalUserMessageId.create physicalUserMessageId)
            |> targetObject

    /// Release only the process-shared execution proven to belong to this exact
    /// physical user material. A stale terminal observation for an older turn is
    /// therefore harmless after the SessionId has been reused.
    let releasePhysical (sessionId: string) (physicalUserMessageId: string) : obj =
        ModelRouting.releasePhysicalExecution
            (SessionId.create sessionId)
            (PhysicalUserMessageId.create physicalUserMessageId)
        |> physicalReleaseObject

    /// Load the user-visible scheduler module through the owner boundary. The
    /// returned function is an opaque JS capability and is never introspected by
    /// the semantic caller.
    let bootstrapAndLoadAt (path: string) (template: string) : Task<obj> =
        ModelRouting.bootstrapAndLoadAt path template

    /// Invoke a scheduler with plain JS target observations. `null` means the
    /// scheduler declined the demand; target validation remains owned by routing.
    let invokeScheduler (scheduler: obj) (role: string) (running: obj) (previous: obj) (purpose: obj) : obj =
        ModelRouting.invokeScheduler
            scheduler
            role
            (targetsOf running)
            (if isNullish previous then None else Some(targetOf previous))
            (purposeOf purpose)
        |> Option.map targetObject
        |> Option.defaultValue null

    /// Construct an isolated routing runtime around an injected scheduler. The
    /// handle is opaque; all observations and mutations remain on this surface.
    let createRuntime (scheduler: obj) : obj =
        RuntimeHandle(ModelRouting.ModelRoutingRuntime(scheduler)) :> obj

    /// Read-only Predictor slot existence query, observed from the loaded model
    /// configuration. Tool decoration and readonly-delegate admission share
    /// this one result; capacity and provider health do not change it.
    let predictorConfiguration (scheduler: obj) : obj =
        match ModelRouting.predictorConfiguration scheduler with
        | ModelRouting.PredictorConfiguration.Configured -> box {| kind = "Configured"; reason = null |}
        | ModelRouting.PredictorConfiguration.NotConfigured ->
            box
                {| kind = "NotConfigured"
                   reason = null |}
        | ModelRouting.PredictorConfiguration.ConfigurationInvalid reason ->
            box
                {| kind = "ConfigurationInvalid"
                   reason = reason |}

    /// Same query on the process-shared scheduler loaded during plugin load.
    let sharedPredictorConfiguration () : obj =
        match ModelRouting.sharedPredictorConfiguration () with
        | ModelRouting.PredictorConfiguration.Configured -> box {| kind = "Configured"; reason = null |}
        | ModelRouting.PredictorConfiguration.NotConfigured ->
            box
                {| kind = "NotConfigured"
                   reason = null |}
        | ModelRouting.PredictorConfiguration.ConfigurationInvalid reason ->
            box
                {| kind = "ConfigurationInvalid"
                   reason = reason |}

    let acquireExecutionAdmission
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (role: string)
        (participant: string)
        (lenderSessionId: string)
        (purpose: obj)
        : Task<obj> =
        task {
            let! acquisition =
                (runtimeOf runtime)
                    .AcquireExecutionAdmission(
                        sessionId,
                        physicalUserMessageId,
                        normalizeRoleText (box role),
                        participant,
                        purposeOf purpose,
                        (if
                             isNullish (box lenderSessionId)
                             || System.String.IsNullOrWhiteSpace lenderSessionId
                         then
                             None
                         else
                             Some lenderSessionId)
                    )

            let! completed = awaitAdmission acquisition
            return acquireAdmissionObject completed
        }

    let continueExecutionAdmission (runtime: obj) (token: obj) (physicalUserMessageId: string) : Task<obj> =
        task {
            let previous =
                leaseOf token
                |> Option.defaultWith (fun () -> invalidArg "token" "continuation requires an opaque admission lease")

            let! acquisition = (runtimeOf runtime).ContinueExecutionAdmission(previous, physicalUserMessageId)

            return acquireAdmissionObject acquisition
        }

    let retainContinuationInput (runtime: obj) (token: obj) (physicalUserMessageId: string) : obj =
        let previous =
            leaseOf token
            |> Option.defaultWith (fun () -> invalidArg "token" "input requires an opaque admission lease")

        (runtimeOf runtime).RetainContinuationInput(previous, physicalUserMessageId)
        |> targetObject

    let beginExecutionAdmission
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (role: string)
        (participant: string)
        (lenderSessionId: string)
        (purpose: obj)
        : Task<obj> =
        task {
            let! acquisition =
                (runtimeOf runtime)
                    .AcquireExecutionAdmission(
                        sessionId,
                        physicalUserMessageId,
                        normalizeRoleText (box role),
                        participant,
                        purposeOf purpose,
                        (if
                             isNullish (box lenderSessionId)
                             || System.String.IsNullOrWhiteSpace lenderSessionId
                         then
                             None
                         else
                             Some lenderSessionId)
                    )

            return acquireAdmissionObject acquisition
        }

    let awaitQueuedExecutionAdmission (queueToken: obj) : Task<obj> =
        if hasOpaqueLease queuedAdmissionNodes queueToken then
            task {
                let node = queuedNodeValue queuedAdmissionNodes queueToken
                let! completed = node.Completion.Task
                let! terminal = awaitAdmission completed
                return acquireAdmissionObject terminal
            }
        else
            let rejected =
                TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)

            rejected.SetException(System.InvalidOperationException "execution-model-routing: unknown queue node")

            rejected.Task

    let executionAdmissionTarget (runtime: obj) (token: obj) : obj =
        match leaseOf token with
        | None -> null
        | Some lease ->
            match (runtimeOf runtime).ExecutionAdmissionTarget lease with
            | Ok target -> targetObject target
            | Error _ -> null

    let commitExecutionAdmission (runtime: obj) (token: obj) (observed: obj) : obj =
        match leaseOf token with
        | None -> CapacityTransitionOutcome.StaleFence |> transitionOutcomeObject
        | Some lease ->
            (runtimeOf runtime)
                .CommitExecutionAdmission(lease, admissionIdentityOf observed)
            |> transitionOutcomeObject

    let releaseExecutionAdmissionBeforeProvider (runtime: obj) (token: obj) (observed: obj) : obj =
        match leaseOf token with
        | None -> CapacityTransitionOutcome.StaleFence |> transitionOutcomeObject
        | Some lease ->
            (runtimeOf runtime)
                .ReleaseExecutionAdmissionBeforeProvider(lease, admissionIdentityOf observed)
            |> transitionOutcomeObject

    let executionAdmissionLifecycle (runtime: obj) (token: obj) : obj =
        match leaseOf token with
        | None -> null
        | Some lease ->
            match (runtimeOf runtime).ExecutionAdmissionLifecycle lease with
            | Ok name -> box name
            | Error _ -> null

    let tryReserveManaged
        (runtime: obj)
        (sessionId: string)
        (role: string)
        (lenderSessionId: string)
        (purpose: obj)
        : obj =
        (runtimeOf runtime)
            .TryReserveManaged(
                sessionId,
                normalizeRoleText (box role),
                purposeOf purpose,
                (if
                     isNullish (box lenderSessionId)
                     || System.String.IsNullOrWhiteSpace lenderSessionId
                 then
                     None
                 else
                     Some lenderSessionId)
            )
        |> Option.map targetObject
        |> Option.defaultValue null

    let tryLease
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (role: string)
        (participant: string)
        (lenderSessionId: string)
        (purpose: obj)
        : obj =
        (runtimeOf runtime)
            .TryLease(
                sessionId,
                physicalUserMessageId,
                normalizeRoleText (box role),
                participant,
                purposeOf purpose,
                (if
                     isNullish (box lenderSessionId)
                     || System.String.IsNullOrWhiteSpace lenderSessionId
                 then
                     None
                 else
                     Some lenderSessionId)
            )
        |> Option.map targetObject
        |> Option.defaultValue null

    /// Read-only exact committed lease observation. The opaque token is the same
    /// capability the acquire path handed out; an unregistered lease gets a token
    /// so the caller can still bind it to the owner projection.
    let tryReadExecution (runtime: obj) (sessionId: string) (physicalUserMessageId: string) : obj =
        match (runtimeOf runtime).TryReadExecution(sessionId, physicalUserMessageId) with
        | None -> null
        | Some lease ->
            if hasOpaqueToken executionAdmissionTokens lease then
                opaqueTokenValue executionAdmissionTokens lease
            else
                let created = opaqueLeaseToken ()
                rememberOpaqueToken executionAdmissionTokens lease created
                rememberOpaqueLease executionAdmissionLeases created lease
                created

    /// The exact provider-run → physical-message relation for a run the Host
    /// itself observed. Written only from the authoritative start observation.
    let rememberProviderStepIdentity
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        : unit =
        (runtimeOf runtime)
            .RememberProviderStepIdentity(sessionId, physicalUserMessageId, providerRun)

    /// Read-only exact lookup. A run this process never observed returns null;
    /// the session's current physical message is never substituted.
    let tryProviderStepIdentity (runtime: obj) (providerRun: string) : obj =
        (runtimeOf runtime).TryProviderStepIdentity(providerRun)
        |> Option.map (fun (sessionId, physicalUserMessageId) ->
            box
                {| sessionId = sessionId
                   physicalUserMessageId = physicalUserMessageId |})
        |> Option.defaultValue null

    let bindDevopsTarget (runtime: obj) (sessionId: string) (target: obj) : unit =
        (runtimeOf runtime).BindDevopsTarget(sessionId, targetOf target)

    /// Seed the fixed DevOps target from a durable road's 'provider/model:reasoning'
    /// string. Decoding is fail-closed and the write reuses the same binding path a
    /// Normal admission uses, so this surface is the road-recovery entry point and
    /// the observable form of execution-model-routing-019.
    let seedDevOpsModelTarget (runtime: obj) (sessionId: string) (target: string) : unit =
        (runtimeOf runtime).SeedBoundDevOpsModel(sessionId, target)

    let boundDevopsTarget (runtime: obj) (sessionId: string) : obj =
        (runtimeOf runtime).BoundDevopsTarget(sessionId)
        |> Option.map targetObject
        |> Option.defaultValue null

    let releasePhysicalExecution (runtime: obj) (sessionId: string) (physicalUserMessageId: string) : obj =
        (runtimeOf runtime).ReleasePhysicalExecution(sessionId, physicalUserMessageId)
        |> physicalReleaseObject

    let releaseExecution (runtime: obj) (sessionId: string) : obj =
        (runtimeOf runtime).ReleaseExecution(sessionId) |> transitionOutcomeObject

    let cancelPendingExecution (runtime: obj) (sessionId: string) : obj =
        (runtimeOf runtime).CancelPendingExecution(sessionId) |> transitionOutcomeObject

    let enterProviderStep
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (visibleProviderRuns: string array)
        (requestKey: string option)
        : Task =
        (runtimeOf runtime)
            .EnterProviderStep(
                sessionId,
                physicalUserMessageId,
                visibleProviderRuns |> Set.ofArray,
                ?requestKey = requestKey
            )

    let endProviderStep
        (runtime: obj)
        (sessionId: string)
        (physicalUserMessageId: string)
        (providerRun: string)
        : unit =
        (runtimeOf runtime)
            .EndProviderStep(sessionId, physicalUserMessageId, providerRun)

    let takeProviderRunTarget (runtime: obj) (providerRun: string) : obj =
        (runtimeOf runtime).TakeProviderRunTarget(providerRun)
        |> Option.map targetObject
        |> Option.defaultValue null

    /// provider-attempt-recovery-021: the failed attempt itself was the LWR retry, so its exact
    /// witness target condemns the provider of that target.
    let condemnFailedTarget (runtime: obj) (providerRun: string) : obj =
        (runtimeOf runtime).CondemnFailedTarget(providerRun)
        |> Option.map targetObject
        |> Option.defaultValue null

    /// provider-attempt-recovery-021: the failed attempt carried the original context, so the next
    /// fresh admission of this session is bound to the failed target.
    let retainFailedTargetForRetry (runtime: obj) (sessionId: string) (providerRun: string) : obj =
        (runtimeOf runtime).RetainFailedTargetForRetry(sessionId, providerRun)
        |> Option.map targetObject
        |> Option.defaultValue null

    let suppressProviderStep (runtime: obj) (sessionId: string) (physicalUserMessageId: string) : unit =
        (runtimeOf runtime).SuppressProviderStep(sessionId, physicalUserMessageId)

    let snapshotOccupied (runtime: obj) : obj array =
        (runtimeOf runtime).SnapshotOccupied() |> Array.map targetObject

    let capacitySnapshot (runtime: obj) : obj =
        (runtimeOf runtime).CapacitySnapshot() |> capacitySnapshotObject

    let reconcileCapacityEvidence (evidence: obj) : obj =
        match evidence |> invariantEvidenceOf |> CapacityReconciliation.decide with
        | CapacityReconciliationDecision.NoOp -> box {| kind = "NoOp" |}
        | CapacityReconciliationDecision.FailClosed failures ->
            box
                {| kind = "FailClosed"
                   reasons = failures |> Array.map reconciliationFailureName |}
            |> deepFreeze

    let pendingCount (runtime: obj) : int = (runtimeOf runtime).PendingCount

    /// Observation only: capacity owner truth for this physical execution. It
    /// reports whether an exact committed lease exists; it never repairs,
    /// allocates or releases anything.
    let admissionSnapshot (routingRuntime: obj) (sessionId: string) (physicalUserMessageId: string) : obj =
        let lease =
            (runtimeOf routingRuntime).TryReadExecution(sessionId, physicalUserMessageId)

        box
            {| activeCapacity = snapshotOccupied routingRuntime |> Array.length
               pendingAdmissions = pendingCount routingRuntime
               exactLeaseCommitted = lease |> Option.isSome |}

    let pendingBound (runtime: obj) : int = (runtimeOf runtime).PendingBound

    let pendingContractVersion (runtime: obj) : int =
        (runtimeOf runtime).PendingContractVersion

    /// Create an SDK-backed prompt port without exposing the Fable class. The
    /// port keeps prompt_async enqueue semantics, including fire-and-forget
    /// observation of the Host run promise.
    let createSdkClientPort (client: obj) : obj =
        match OpenCodePortAdapter.create (createObj [ "client", client ]) with
        | Some port -> PortHandle(port) :> obj
        | None -> invalidArg "client" "OpenCode SDK client has no session API"

    let sendPrompt (port: obj) (sessionId: string) (text: string) (options: obj) : Task<obj> =
        task {
            let! outcome = (portOf port).SendPrompt (SessionId.create sessionId) text (promptOptionsOf options)
            return outcomeToJs outcome
        }
