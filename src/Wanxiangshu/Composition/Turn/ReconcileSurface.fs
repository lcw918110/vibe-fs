namespace Wanxiangshu.Composition.Turn

open Wanxiangshu.Execution.Failure

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.HostSession

/// JS boundary for reconciliation's classifiers, evidence, wakes and publish
/// seals. Maps and idle permits remain opaque handles; decisions and
/// observations cross as plain objects or stable strings.
module ReconcileSurface =

    type private PublishMapsHandle(maps: ReconcileProgram.PublishMaps) =
        member _.Maps = maps

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    let private stringOf (value: obj) =
        if isNullish value then "" else string value

    let private mapsOf (value: obj) = (value :?> PublishMapsHandle).Maps

    let private outcomeOf (value: obj) : ReconcileProgram.TurnOutcome =
        ReconcileProgram.outcomeOf (stringOf value)

    let private wakeOf (value: obj) : ReconcileProgram.ReconcileWake =
        match stringOf (property value "kind") with
        | "IdleWake" ->
            property value "permit"
            |> unbox<QuiescencePermit>
            |> ReconcileProgram.ReconcileWake.IdleWake
        | "RetryWake" -> ReconcileProgram.ReconcileWake.RetryWake
        | "FailureWake" ->
            let physical = stringOf (property value "physical")
            let reason = stringOf (property value "reason")

            ReconcileProgram.ReconcileWake.FailureWake(
                (if System.String.IsNullOrWhiteSpace physical then
                     None
                 else
                     Some(PhysicalUserMessageId.create physical)),
                ExecutionFailure.ProviderTransient,
                (if System.String.IsNullOrWhiteSpace reason then
                     "provider failure"
                 else
                     reason),
                (if stringOf (property value "source") = "ExactAssistantProjection" then
                     ReconcileProgram.FailureWakeSource.ExactAssistantProjection
                 else
                     ReconcileProgram.FailureWakeSource.CoarseHostSignal)
            )
        | "AbortWake" -> ReconcileProgram.ReconcileWake.AbortWake
        | other -> invalidArg "wake" (sprintf "unknown reconcile wake: %s" other)

    let private evidenceOf (value: obj) : ReconcileProgram.ReconcileEvidence =
        match stringOf (property value "kind") with
        | "SnapshotError" -> ReconcileProgram.evidenceSnapshotError (stringOf (property value "reason"))
        | "NoTurn" -> ReconcileProgram.evidenceNoTurn ()
        | "Provisional" -> ReconcileProgram.evidenceProvisional (outcomeOf (property value "outcome"))
        | "Unknown" -> ReconcileProgram.evidenceUnknown ()
        | "Terminal" ->
            let physical = stringOf (property value "physical")
            let outcome = outcomeOf (property value "outcome")

            if System.String.IsNullOrWhiteSpace physical then
                ReconcileProgram.evidenceTerminal outcome
            else
                ReconcileProgram.turnFixture "terminal-session" physical "terminal-provider-run" outcome
                |> ReconcileProgram.observedTurn
                |> ReconcileProgram.ReconcileEvidence.Terminal
        | "SessionCleared" -> ReconcileProgram.evidenceSessionCleared ()
        | other -> invalidArg "evidence" (sprintf "unknown reconcile evidence: %s" other)

    let private decisionToJs (decision: ReconcileProgram.ReconcileDecision) : obj =
        box {| name = ReconcileProgram.decisionName decision |}

    let private turnOf (value: obj) : ReconcileProgram.PublishTurn =
        ReconcileProgram.turnFixture
            (stringOf (property value "session"))
            (stringOf (property value "physical"))
            (stringOf (property value "providerRun"))
            (ReconcileProgram.outcomeOf (stringOf (property value "outcome")))

    let private turnObject (session: string) (physical: string) (providerRun: string) (outcome: string) : obj =
        box
            {| session = session
               physical = physical
               providerRun = providerRun
               outcome = outcome |}

    let empty () : obj =
        PublishMapsHandle(ReconcileProgram.publishMapsEmpty ()) :> obj

    let turnFixture (value: obj) : obj =
        turnObject
            (stringOf (property value "session"))
            (stringOf (property value "physical"))
            (stringOf (property value "providerRun"))
            (stringOf (property value "outcome"))

    /// Stable JS contract for the fields accepted by `turnFixture` and the
    /// publish seal operations. This is an owner-defined vocabulary view, not
    /// Fable reflection metadata.
    let acceptedTurnFields () : string array =
        [| "session"; "physical"; "providerRun"; "outcome" |]

    /// Classify a publishable turn from its JS outcome name. `TurnUnknown` is
    /// deliberately rejected by the domain classifier: it is a snapshot
    /// observation, never a business turn.
    let classifyTurn (outcomeName: string) : obj =
        let outcome = ReconcileProgram.outcomeOf outcomeName
        let terminal = ReconcileProgram.isTerminalOutcome outcome

        box
            {| outcome = outcomeName
               state = if terminal then "terminal" else "provisional"
               isTerminal = terminal |}

    let isTerminalOutcome (outcomeName: string) : bool =
        ReconcileProgram.outcomeOf outcomeName |> ReconcileProgram.isTerminalOutcome

    /// These predicates keep structural clean-break checks at the owner boundary
    /// without exporting the underlying union constructors or case metadata.
    let isPublishableOutcome (outcomeName: string) : bool =
        outcomeName <> "TurnUnknown" && outcomeName <> "AbortWake"

    let isSnapshotObservation (observationName: string) : bool = observationName = "TurnUnknown"

    let private resolveCanonicalName (outcomeName: string) (outcome: ReconcileProgram.TurnOutcome) =
        match outcomeName with
        | "TurnInProgress"
        | "TurnNeedsContinuation"
        | "TurnCompleted"
        | "TurnAborted"
        | "TurnFailed" -> outcomeName
        | _ when ReconcileProgram.isTerminalOutcome outcome -> "TurnFailed"
        | _ -> "TurnInProgress"

    let tryOutcome (outcomeName: string) : obj =
        try
            let outcome = ReconcileProgram.outcomeOf outcomeName
            let canonicalName = resolveCanonicalName outcomeName outcome

            box
                {| accepted = true
                   name = canonicalName |}
        with error ->
            box
                {| accepted = false
                   error = error.Message |}

    // ── wake and evidence observations ───────────────────────────────────────

    let idleWake (session: string) : obj =
        let sessionId = SessionId.create session
        let gate = SessionQuiescenceGate()
        gate.BeginProviderAttempt sessionId
        let permit = gate.ObserveIdle sessionId

        box
            {| kind = "IdleWake"
               hasQuiescence = true
               permit = permit |}

    let retryWake () : obj =
        box
            {| kind = "RetryWake"
               hasQuiescence = false |}

    let failureWake () : obj =
        box
            {| kind = "FailureWake"
               physical = ""
               reason = "provider failure"
               source = "CoarseHostSignal"
               hasQuiescence = false |}

    let failureWakeFor (physical: string) : obj =
        box
            {| kind = "FailureWake"
               physical = physical
               reason = "provider failure"
               source = "ExactAssistantProjection"
               hasQuiescence = false |}

    let abortWake () : obj =
        box
            {| kind = "AbortWake"
               hasQuiescence = false |}

    let mergeWakeKind (currentPhysical: string) (previous: obj) (incoming: obj) =
        ReconcileProgram.mergeWake
            (if System.String.IsNullOrWhiteSpace currentPhysical then
                 None
             else
                 Some(PhysicalUserMessageId.create currentPhysical))
            (wakeOf previous)
            (wakeOf incoming)
        |> function
            | ReconcileProgram.ReconcileWake.IdleWake _ -> "IdleWake"
            | ReconcileProgram.ReconcileWake.IdleFailureWake _ -> "IdleFailureWake"
            | ReconcileProgram.ReconcileWake.RetryWake -> "RetryWake"
            | ReconcileProgram.ReconcileWake.FailureWake _ -> "FailureWake"
            | ReconcileProgram.ReconcileWake.AbortWake -> "AbortWake"

    let evidenceSnapshotError (reason: string) : obj =
        box
            {| kind = "SnapshotError"
               reason = reason |}

    let evidenceNoTurn () : obj = box {| kind = "NoTurn" |}

    let evidenceProvisional (outcomeName: string) : obj =
        box
            {| kind = "Provisional"
               outcome = outcomeName |}

    let evidenceUnknown () : obj = box {| kind = "Unknown" |}

    let evidenceTerminal (outcomeName: string) : obj =
        box
            {| kind = "Terminal"
               physical = ""
               outcome = outcomeName |}

    let evidenceTerminalFor (physical: string) (outcomeName: string) : obj =
        box
            {| kind = "Terminal"
               physical = physical
               outcome = outcomeName |}

    let evidenceSessionCleared () : obj = box {| kind = "SessionCleared" |}

    let decideStep (wake: obj) (evidence: obj) : obj =
        ReconcileProgram.decideStep (wakeOf wake) (evidenceOf evidence) |> decisionToJs

    let decisionName (decision: obj) : string = stringOf (property decision "name")

    let consumeKey (turn: obj) : string =
        ReconcileProgram.consumeKey (turnOf turn)

    let private failureOfLabel (label: string) : ExecutionFailure =
        match label with
        | "ProviderTransient" -> ExecutionFailure.ProviderTransient
        | "ProviderPermanent" -> ExecutionFailure.ProviderPermanent
        | "LocalInvariant" -> ExecutionFailure.LocalInvariant
        | "ProtocolRejection" -> ExecutionFailure.ProtocolRejection
        | "AuthorizationDenied" -> ExecutionFailure.AuthorizationDenied
        | "UserCancelled" -> ExecutionFailure.UserCancelled
        | "Superseded" -> ExecutionFailure.Superseded
        | "CapacityQueueFull" -> ExecutionFailure.CapacityQueueFull
        | "AcceptanceUnknown" -> ExecutionFailure.AcceptanceUnknown
        | "StreamInterruptedAfterFirstToken" -> ExecutionFailure.StreamInterruptedAfterFirstToken
        | other -> invalidArg "failure" (sprintf "unknown failure label: %s" other)

    /// provider-attempt-recovery-008: a failure witness mints a provider-failure terminal only for a
    /// confirmed provider class, or when the attempt's formal content is usable.
    let failureWitnessMintsTerminal (failure: string) (contentUsable: bool) : bool =
        ReconcileProgram.failureWitnessMintsTerminal (failureOfLabel failure) contentUsable

    let provisionalHas (maps: obj) (turn: obj) : bool =
        (mapsOf maps).provisionalHas (turnOf turn)

    let consumedHas (maps: obj) (turn: obj) : bool = (mapsOf maps).consumedHas (turnOf turn)

    let publishDecision (maps: obj) (turn: obj) : obj =
        let result = ReconcileProgram.publishDecision (mapsOf maps) (turnOf turn)

        box
            {| shouldPublish = result.shouldPublish
               maps = PublishMapsHandle(result.maps) :> obj |}

    let clearProvisional (maps: obj) (session: string) : obj =
        PublishMapsHandle(ReconcileProgram.clearProvisional (mapsOf maps) session) :> obj

    let private schedulerMessage
        (id: string)
        (role: string)
        (parentId: string option)
        (finish: string option)
        (errorName: string option)
        (completed: bool)
        (parts: MessagePart array)
        : SessionMessage =
        { Id = id
          Role = role
          Agent = None
          Finish = finish
          ErrorName = errorName
          Model = None
          ParentId = parentId
          CreatedAt = None
          Completed = completed
          IsCompaction = false
          PromptKey = None
          Parts = parts
          PartIds = Array.create parts.Length None
          ToolParts = [||] }

    let unboundFailureScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "unbound-failure-session"
            let store = TurnBinding.Store()
            // DSL-MUTABLE: algorithm-scratch — proves an unbound coarse failure performs zero reads.
            let mutable snapshotReads = 0

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1
                        Task.FromResult(Ok []) }

            let scheduler =
                Reconciler.Scheduler(snapshot, store, (fun _ -> Task.FromResult(()) :> Task))

            scheduler.Signal(
                ProviderFailure
                    { SessionId = sessionId
                      Failure = ExecutionFailure.ProviderPermanent
                      Diagnostic = "APIError" }
            )

            do! scheduler.StopAndDrain()
            return box {| snapshotReads = snapshotReads |}
        }

    let idleProvisionalWithoutProjectionEdgeScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "single-edge-session"
            let physical = PhysicalUserMessageId.create "single-edge-user"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, physical)

            // DSL-MUTABLE: algorithm-scratch — controlled port observation.
            let mutable snapshotReads = 0
            // DSL-MUTABLE: algorithm-scratch — controlled callback capture.
            let mutable observed: ReconciledTurnContext option = None

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1

                        Task.FromResult(
                            Ok
                                [ schedulerMessage "single-edge-user" "user" None None None false [||]
                                  schedulerMessage
                                      "single-edge-provider-run"
                                      "assistant"
                                      (Some "single-edge-user")
                                      (Some "tool-calls")
                                      None
                                      false
                                      [||] ]
                        ) }

            let onTurn (context: ReconciledTurnContext) : Task =
                observed <- Some context
                Task.FromResult(()) :> Task

            let scheduler = Reconciler.Scheduler(snapshot, store, onTurn)
            let quiescence = SessionQuiescenceGate()
            quiescence.BeginProviderAttempt sessionId
            scheduler.SignalIdle(sessionId, quiescence.ObserveIdle sessionId)
            do! scheduler.StopAndDrain()

            return
                box
                    {| snapshotReads = snapshotReads
                       observed = Option.isSome observed
                       outcome =
                        match observed |> Option.map (fun context -> context.Turn.Outcome) with
                        | Some ReconcileProgram.TurnInProgress -> "TurnInProgress"
                        | Some(ReconcileProgram.TurnNeedsContinuation _) -> "TurnNeedsContinuation"
                        | Some ReconcileProgram.TurnCompleted -> "TurnCompleted"
                        | Some(ReconcileProgram.TurnAborted _) -> "TurnAborted"
                        | Some(ReconcileProgram.TurnFailed _) -> "TurnFailed"
                        | None -> "" |}
        }

    let private projectionEdgeScenario (failureWake: bool) : Task<obj> =
        task {
            let sessionId = SessionId.create "projection-edge-session"
            let rootPhysical = PhysicalUserMessageId.create "projection-edge-root"
            let currentPhysical = PhysicalUserMessageId.create "projection-edge-current"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, rootPhysical)
            store.BindContinuationUserMessage(sessionId, currentPhysical)

            // DSL-MUTABLE: algorithm-scratch — fake Host projection visibility.
            let mutable currentVisible = false
            // DSL-MUTABLE: algorithm-scratch — proves one read per causal edge.
            let mutable snapshotReads = 0
            let firstSnapshotObserved = TaskCompletionSource<unit>()
            let currentTurnObserved = TaskCompletionSource<ReconciledTurnContext>()

            let user id =
                schedulerMessage id "user" None None None false [||]

            let oldAssistant =
                schedulerMessage
                    "projection-edge-old-run"
                    "assistant"
                    (Some "projection-edge-root")
                    (Some "stop")
                    None
                    true
                    [| MessagePart.Text "old terminal" |]

            let currentAssistant =
                if failureWake then
                    schedulerMessage
                        "projection-edge-current-run"
                        "assistant"
                        (Some "projection-edge-current")
                        None
                        (Some "APIError")
                        true
                        [||]
                else
                    schedulerMessage
                        "projection-edge-current-run"
                        "assistant"
                        (Some "projection-edge-current")
                        (Some "stop")
                        None
                        true
                        [| MessagePart.Text "current terminal" |]

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1

                        let messages =
                            [ user "projection-edge-root"; oldAssistant; user "projection-edge-current" ]
                            |> fun prefix ->
                                if currentVisible then
                                    prefix @ [ currentAssistant ]
                                else
                                    prefix

                        Task.FromResult(Ok messages) }

            let observeSnapshot (_: SessionId) (_: SessionMessage list) : Task =
                AsyncSupport.trySetResult firstSnapshotObserved () |> ignore
                Task.FromResult(()) :> Task

            let onTurn (context: ReconciledTurnContext) : Task =
                if ProviderRunIdentity.value context.Turn.ProviderRun = "projection-edge-current-run" then
                    AsyncSupport.trySetResult currentTurnObserved context |> ignore

                Task.FromResult(()) :> Task

            let scheduler =
                Reconciler.Scheduler(snapshot, store, onTurn, ?onSnapshot = Some observeSnapshot)

            if failureWake then
                scheduler.Signal(
                    ProviderFailure
                        { SessionId = sessionId
                          Failure = ExecutionFailure.ProviderTransient
                          Diagnostic = "APIError" }
                )
            else
                let quiescence = SessionQuiescenceGate()
                quiescence.BeginProviderAttempt sessionId
                scheduler.SignalIdle(sessionId, quiescence.ObserveIdle sessionId)

            do! firstSnapshotObserved.Task
            currentVisible <- true
            scheduler.NotifyProjectionChanged(sessionId, currentPhysical)

            let! observed = currentTurnObserved.Task
            do! scheduler.StopAndDrain()

            return
                box
                    {| snapshotReads = snapshotReads
                       providerRun = ProviderRunIdentity.value observed.Turn.ProviderRun
                       outcome =
                        match observed.Turn.Outcome with
                        | ReconcileProgram.TurnCompleted -> "TurnCompleted"
                        | ReconcileProgram.TurnFailed _ -> "TurnFailed"
                        | ReconcileProgram.TurnAborted _ -> "TurnAborted"
                        | ReconcileProgram.TurnInProgress -> "TurnInProgress"
                        | ReconcileProgram.TurnNeedsContinuation _ -> "TurnNeedsContinuation"
                       hasQuiescence = Option.isSome observed.Quiescence |}
        }

    let idleProjectionEdgeScenario () : Task<obj> = projectionEdgeScenario false

    let failureProjectionEdgeScenario () : Task<obj> = projectionEdgeScenario true

    let private outcomeAndReason (context: ReconciledTurnContext) =
        match context.Turn.Outcome with
        | ReconcileProgram.TurnFailed reason -> "TurnFailed", reason
        | ReconcileProgram.TurnCompleted -> "TurnCompleted", ""
        | ReconcileProgram.TurnAborted reason -> "TurnAborted", reason
        | ReconcileProgram.TurnInProgress -> "TurnInProgress", ""
        | ReconcileProgram.TurnNeedsContinuation reason -> "TurnNeedsContinuation", reason

    let physicalIngressDuringSnapshotScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "held-snapshot-session"
            let oldPhysical = PhysicalUserMessageId.create "held-snapshot-old"
            let humanPhysical = PhysicalUserMessageId.create "held-snapshot-human"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, oldPhysical)
            let oldReadEntered = TaskCompletionSource<unit>()
            let releaseOldRead = TaskCompletionSource<unit>()
            let humanReadEntered = TaskCompletionSource<unit>()
            let releaseHumanRead = TaskCompletionSource<unit>()
            // DSL-MUTABLE: algorithm-scratch — observations of actual Scheduler callbacks.
            let published = ResizeArray<ReconciledTurnContext>()
            // DSL-MUTABLE: algorithm-scratch — complete snapshots delivered by actual Scheduler callbacks.
            let observedSnapshots = ResizeArray<string array>()
            // DSL-MUTABLE: algorithm-scratch — counts actual snapshot reads.
            let mutable snapshotReads = 0
            // DSL-MUTABLE: algorithm-scratch — publication count when the next actual snapshot starts.
            let mutable publishedBeforeHumanRead = 0

            let completedMessages physical provider =
                [ schedulerMessage physical "user" None None None false [||]
                  schedulerMessage
                      provider
                      "assistant"
                      (Some physical)
                      (Some "stop")
                      None
                      true
                      [| MessagePart.Text "completed response" |] ]

            let oldMessages = completedMessages "held-snapshot-old" "held-snapshot-old-run"

            let humanMessages =
                oldMessages @ completedMessages "held-snapshot-human" "held-snapshot-human-run"

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        task {
                            snapshotReads <- snapshotReads + 1

                            if snapshotReads = 1 then
                                AsyncSupport.trySetResult oldReadEntered () |> ignore
                                do! releaseOldRead.Task
                                return Ok oldMessages
                            else
                                publishedBeforeHumanRead <- published.Count
                                AsyncSupport.trySetResult humanReadEntered () |> ignore
                                do! releaseHumanRead.Task
                                return Ok humanMessages
                        } }

            let onTurn (context: ReconciledTurnContext) : Task =
                published.Add context
                Task.FromResult(()) :> Task

            let observeSnapshot (_: SessionId) (messages: SessionMessage list) : Task =
                observedSnapshots.Add(messages |> List.map (fun message -> message.Id) |> List.toArray)
                Task.FromResult(()) :> Task

            let scheduler =
                Reconciler.Scheduler(snapshot, store, onTurn, ?onSnapshot = Some observeSnapshot)

            let quiescence = SessionQuiescenceGate()
            quiescence.BeginProviderAttempt sessionId
            scheduler.SignalIdle(sessionId, quiescence.ObserveIdle sessionId)
            do! oldReadEntered.Task
            scheduler.BindPhysicalUserMaterial(sessionId, humanPhysical)
            quiescence.ObservePhysicalUserMessage(sessionId, humanPhysical)
            quiescence.BeginProviderAttempt sessionId
            scheduler.SignalIdle(sessionId, quiescence.ObserveIdle sessionId)
            AsyncSupport.trySetResult releaseOldRead () |> ignore
            do! humanReadEntered.Task
            AsyncSupport.trySetResult releaseHumanRead () |> ignore
            do! scheduler.StopAndDrain()

            return
                box
                    {| snapshotReads = snapshotReads
                       publishedBeforeHumanRead = publishedBeforeHumanRead
                       observedSnapshots = observedSnapshots.ToArray()
                       published =
                        published
                        |> Seq.map (fun context ->
                            {| physical = PhysicalUserMessageId.value context.Turn.PhysicalUserMessageId
                               providerRun = ProviderRunIdentity.value context.Turn.ProviderRun
                               outcome = fst (outcomeAndReason context)
                               hasQuiescence = Option.isSome context.Quiescence |})
                        |> Seq.toArray |}
        }

    let private formatObserved (snapshotReads: int) (observed: ReconciledTurnContext option) : obj =
        match observed with
        | None ->
            box
                {| snapshotReads = snapshotReads
                   observed = false
                   providerRun = ""
                   outcome = ""
                   reason = ""
                   hasQuiescence = false |}
        | Some context ->
            let outcome, reason = outcomeAndReason context

            box
                {| snapshotReads = snapshotReads
                   observed = true
                   providerRun = ProviderRunIdentity.value context.Turn.ProviderRun
                   outcome = outcome
                   reason = reason
                   hasQuiescence = Option.isSome context.Quiescence |}

    let failureWitnessCurrentAssistantScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "failure-witness-session"
            let rootPhysical = PhysicalUserMessageId.create "failure-witness-root"
            let currentPhysical = PhysicalUserMessageId.create "failure-witness-current"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, rootPhysical)
            store.BindContinuationUserMessage(sessionId, currentPhysical)

            // DSL-MUTABLE: algorithm-scratch — test observation capture.
            let mutable snapshotReads = 0
            let mutable observed: ReconciledTurnContext option = None

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1

                        Task.FromResult(
                            Ok
                                [ schedulerMessage "failure-witness-root" "user" None None None false [||]
                                  schedulerMessage "failure-witness-current" "user" None None None false [||]
                                  schedulerMessage
                                      "failure-witness-current-run"
                                      "assistant"
                                      (Some "failure-witness-current")
                                      None
                                      None
                                      false
                                      [||] ]
                        ) }

            let onTurn (context: ReconciledTurnContext) : Task =
                observed <- Some context
                Task.FromResult(()) :> Task

            let scheduler = Reconciler.Scheduler(snapshot, store, onTurn)

            scheduler.Signal(
                ProviderFailure
                    { SessionId = sessionId
                      Failure = ExecutionFailure.ProviderPermanent
                      Diagnostic = "Bad Request: input_invalid" }
            )

            do! scheduler.StopAndDrain()

            return formatObserved snapshotReads observed
        }

    let providerErrorIdleRaceScenario (rawMessage: obj) : Task<obj> =
        task {
            let terminalEvent =
                box
                    {| ``type`` = "message.updated"
                       properties = {| info = rawMessage?info |} |}

            let terminal =
                HostEventCodec.tryDecodeExactProviderTerminal terminalEvent
                |> Option.defaultWith (fun () -> invalidArg "rawMessage" "exact provider terminal required")

            let message =
                SessionSnapshotPort.projectMessage rawMessage
                |> Option.defaultWith (fun () -> invalidArg "rawMessage" "Host message required")

            let physical = terminal.PhysicalUserMessageId
            let session = terminal.SessionId
            let store = TurnBinding.Store()
            store.BindUserMessage(session, physical)

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        Task.FromResult(
                            Ok
                                [ schedulerMessage
                                      (PhysicalUserMessageId.value physical)
                                      "user"
                                      None
                                      None
                                      None
                                      false
                                      [||]
                                  message ]
                        ) }

            // DSL-MUTABLE: algorithm-scratch — capture actual pass deliveries and seals.
            let deliveries = ResizeArray<ReconciledTurnContext>()
            let mutable maps = ReconcileProgram.publishMapsEmpty ()
            let quiescence = SessionQuiescenceGate()
            quiescence.BeginProviderAttempt session

            let run wake =
                ReconcilePass.run
                    snapshot
                    (fun _ _ -> true)
                    (fun _ -> false)
                    (fun _ -> maps)
                    (fun _ updated -> maps <- updated)
                    wake
                    (fun _ _ -> Task.FromResult(()) :> Task)
                    (fun context ->
                        deliveries.Add context
                        Task.FromResult(()) :> Task)
                    (store.ActiveRunBinding session)
                    session
                    0

            do! run (ReconcileProgram.ReconcileWake.IdleWake(quiescence.ObserveIdle session))
            let idleDeliveries = deliveries.Count

            let failure =
                match terminal.Outcome with
                | HostProviderTerminalOutcome.ProviderFailure failure -> failure
                | _ -> invalidArg "rawMessage" "provider failure required"

            do!
                run (
                    ReconcileProgram.ReconcileWake.FailureWake(
                        Some physical,
                        failure,
                        "exact-provider-terminal",
                        ReconcileProgram.FailureWakeSource.ExactAssistantProjection
                    )
                )

            return
                box
                    {| idleDeliveries = idleDeliveries
                       deliveries = deliveries.Count
                       terminal = formatObserved 0 (deliveries |> Seq.tryLast)
                       failure =
                        deliveries
                        |> Seq.tryLast
                        |> Option.bind _.Failure
                        |> Option.map string
                        |> Option.defaultValue "" |}
        }

    /// R17 dist-backed resource bound: same-session burst against the real
    /// Scheduler. First snapshot read blocks on a manual gate while thousands
    /// of same-session kicks/projection edges arrive, StopAndDrain is issued
    /// against the live pass, then ClearSession invalidates the queued burst
    /// and the gate releases. Post-stop kicks must be rejected. No timers.
    let schedulerBurstBoundScenario () : Task<obj> =
        task {
            let sessionId = SessionId.create "scheduler-burst-session"
            let physical = PhysicalUserMessageId.create "scheduler-burst-user"
            let store = TurnBinding.Store()
            store.BindUserMessage(sessionId, physical)

            // DSL-MUTABLE: algorithm-scratch — exact production snapshot read count.
            let mutable snapshotReads = 0
            // DSL-MUTABLE: algorithm-scratch — exact production turn delivery count.
            let mutable delivered = 0
            // DSL-MUTABLE: algorithm-scratch — first-read gate flag, not workflow position.
            let mutable firstRead = true
            // DSL-MUTABLE: algorithm-scratch — stop-completion witness, not workflow position.
            let mutable stopNotified = false
            let blockPass = TaskCompletionSource<unit>()
            let passEntered = TaskCompletionSource<unit>()

            let messages =
                [ schedulerMessage "scheduler-burst-user" "user" None None None false [||]
                  schedulerMessage
                      "scheduler-burst-run"
                      "assistant"
                      (Some "scheduler-burst-user")
                      (Some "tool-calls")
                      None
                      false
                      [||] ]

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        task {
                            snapshotReads <- snapshotReads + 1

                            if firstRead then
                                firstRead <- false
                                AsyncSupport.trySetResult passEntered () |> ignore
                                do! blockPass.Task

                            return Ok messages
                        } }

            let onTurn (_: ReconciledTurnContext) : Task =
                delivered <- delivered + 1
                Task.FromResult(()) :> Task

            let scheduler = Reconciler.Scheduler(snapshot, store, onTurn)

            let retrySignal =
                ProviderRetry
                    { SessionId = sessionId
                      Attempt = "burst-attempt"
                      Failure = ExecutionFailure.ProviderTransient
                      Diagnostic = "burst retry" }

            scheduler.Signal retrySignal
            do! passEntered.Task

            let kickCount = 3000
            let edgeCount = 3000

            for _ = 1 to kickCount do
                scheduler.Signal retrySignal

            for _ = 1 to edgeCount do
                scheduler.NotifyProjectionChanged(sessionId, physical)

            let stopTask = scheduler.StopAndDrain()
            // Attach before yielding: an already-completed stop notifies within
            // microtasks, while a stop owned by the live pass stays pending.
            let _watcher =
                task {
                    do! stopTask
                    stopNotified <- true
                }

            do! Task.FromResult(())
            let stopWaited = not stopNotified

            scheduler.ClearSession(sessionId)
            AsyncSupport.trySetResult blockPass () |> ignore
            do! stopTask

            let readsAfterStop = snapshotReads
            let deliveredAfterStop = delivered

            scheduler.Signal retrySignal
            scheduler.NotifyProjectionChanged(sessionId, physical)
            scheduler.Kick(sessionId, ReconcileProgram.ReconcileWake.RetryWake)
            do! scheduler.StopAndDrain()
            do! Task.FromResult(())

            return
                box
                    {| snapshotReads = snapshotReads
                       kickCount = kickCount
                       edgeCount = edgeCount
                       delivered = delivered
                       stopWaited = stopWaited
                       postStopRejected = ((snapshotReads = readsAfterStop) && (delivered = deliveredAfterStop)) |}
        }
