namespace Wanxiangshu.Verification

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Process
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.OpenCode

/// Narrow JS-native owner for deterministic temporal proofs.
///
/// Timer/clock capabilities and journal/fold translation stay here. F# ids,
/// unions, maps, records, and EventStore plumbing never cross this boundary;
/// callers receive plain values or opaque handles only.
module TemporalSurface =

    type private TimerHandle(port: VirtualTimerPort) =
        member _.Port = port

    type private DeadlineHandle(handle: IDeadlineHandle) =
        member _.Handle = handle

    type private ClockHandle(port: VirtualClockPort) =
        member _.Port = port

    type private JournalHandle(commonDir: string, journal: AgentJournal) =
        // DSL-MUTABLE: resource — one-shot temporal journal disposal latch
        let mutable disposed = false

        member _.CommonDir = commonDir

        member _.Journal =
            if disposed then
                invalidOp "Temporal journal handle is disposed"

            journal

        member _.Dispose() =
            if not disposed then
                disposed <- true
                (journal :> IDisposable).Dispose()

    type private ProviderFailureHandle(state: ProviderFailureProjection) =
        member _.State = state

    [<Emit("$0==null")>]
    let private isNullish (value: obj) : bool = jsNative

    let private text (value: obj) =
        if isNullish value then "" else string value

    let private intValue (value: obj) =
        if isNullish value then 0 else int (text value)

    let private int64Value (value: obj) =
        if isNullish value then 0L else int64 (text value)

    let private sessionIdOf (value: obj) = SessionId.create (text value)
    let private logicalRunOf (value: obj) = LogicalRunId.create (text value)

    let private authorityRootOf (value: obj) =
        AuthorityRootUserMessageId.create (text value)

    let private providerRunOf (value: obj) = ProviderRunIdentity.create (text value)

    let private optionalProviderRun (value: obj) =
        if isNullish value then None else Some(providerRunOf value)

    let private participantIdentityOfJs (value: obj) : ParticipantIdentityEvidence =
        let roleText =
            if not (isNullish value?role) then text value?role
            elif not (isNullish value?Role) then text value?Role
            else ""

        let role =
            if String.IsNullOrEmpty roleText then
                None
            else
                Roles.tryParseRole roleText
                |> Option.defaultWith (fun () -> failwith $"TemporalSurface: unknown participant role '{roleText}'")
                |> Some

        let originLabel = text (value?Origin)

        let origin =
            match originLabel with
            | "ResolvedAtRoot" -> PersonaOrigin.ResolvedAtRoot
            | "InheritedFromOwner" -> PersonaOrigin.InheritedFromOwner
            | _ -> failwith $"TemporalSurface: unknown participant origin '{originLabel}'"

        let selectedAgent =
            if not (isNullish value?participant) then
                text value?participant
            elif not (isNullish value?SelectedAgent) then
                text value?SelectedAgent
            else
                ""

        { SelectedAgent = selectedAgent
          Role = role
          Persona = text (value?Persona)
          PersonaCatalogVersion = intValue (value?PersonaCatalogVersion)
          Origin = origin }
        |> ParticipantIdentity.fromInput
        |> Result.defaultWith (fun error -> failwith $"TemporalSurface: invalid participant identity: {error}")

    let private participantIdentityToJs (evidence: ParticipantIdentityEvidence) : obj =
        let identity = ParticipantIdentity.toInput evidence

        box
            {| participant = identity.SelectedAgent
               role = identity.Role |> Option.map Roles.roleLabel |> Option.toObj
               Persona = identity.Persona
               PersonaCatalogVersion = identity.PersonaCatalogVersion
               Origin =
                match identity.Origin with
                | PersonaOrigin.ResolvedAtRoot -> "ResolvedAtRoot"
                | PersonaOrigin.InheritedFromOwner -> "InheritedFromOwner" |}

    let private identitySeedOfJs (value: obj) : PromptAuthority.IdentitySeed =
        let participantIdentity = participantIdentityOfJs (value?ParticipantIdentity)
        let identityInput = ParticipantIdentity.toInput participantIdentity

        let seedInput =
            match text (value?Kind) with
            | "RootSelection" -> PromptAuthority.IdentitySeedInput.RootSelectionInput identityInput
            | "InheritedFromOwner" ->
                PromptAuthority.IdentitySeedInput.InheritedFromOwnerInput
                    { OwnerSessionId = sessionIdOf (value?OwnerSessionId)
                      OwnerLogicalRunId = logicalRunOf (value?OwnerLogicalRunId)
                      OwnerAuthorityRootUserMessageId = authorityRootOf (value?OwnerAuthorityRootUserMessageId)
                      ParticipantIdentity = identityInput }
            | kind -> failwith $"TemporalSurface: unknown identity seed kind '{kind}'"

        PromptIdentitySeed.rehydrate seedInput
        |> Result.defaultWith (fun error -> failwith $"TemporalSurface: invalid identity seed: {error}")

    let private identitySeedToJs (seed: PromptAuthority.IdentitySeed) : obj =
        let participantIdentity =
            PromptAuthority.identitySeedParticipantIdentity seed |> participantIdentityToJs

        match PromptAuthority.identitySeedOwner seed with
        | None ->
            box
                {| Kind = "RootSelection"
                   OwnerSessionId = null
                   OwnerLogicalRunId = null
                   OwnerAuthorityRootUserMessageId = null
                   ParticipantIdentity = participantIdentity |}
        | Some(ownerSessionId, ownerLogicalRunId, ownerAuthorityRootUserMessageId) ->
            box
                {| Kind = "InheritedFromOwner"
                   OwnerSessionId = SessionId.value ownerSessionId
                   OwnerLogicalRunId = LogicalRunId.value ownerLogicalRunId
                   OwnerAuthorityRootUserMessageId = AuthorityRootUserMessageId.value ownerAuthorityRootUserMessageId
                   ParticipantIdentity = participantIdentity |}

    let private streamOfJs (value: obj) : StreamId =
        match text (value?kind) with
        | "Session" -> StreamId.Session(sessionIdOf (value?session))
        | "Workspace" -> StreamId.Workspace
        | "Child" -> StreamId.Child(ChildId.create (text (value?child)))
        | "Process" -> StreamId.Process(ProcessId.create (text (value?processId)))
        | other -> failwith $"TemporalSurface: unknown stream kind '{other}'"

    let private streamToJs stream =
        match stream with
        | StreamId.Workspace -> box {| kind = "Workspace" |}
        | StreamId.Session session ->
            box
                {| kind = "Session"
                   session = SessionId.value session |}
        | StreamId.Child child ->
            box
                {| kind = "Child"
                   child = ChildId.value child |}
        | StreamId.Process processId ->
            box
                {| kind = "Process"
                   ``process`` = ProcessId.value processId |}

    let private agentFactOfJs (value: obj) : AgentFact =
        let family = text (value?family)
        let caseName = text (value?case)
        let payload = unbox<obj> (value?payload)

        match family, caseName with
        | "Prompt", "AuthorityRootAccepted" ->
            AgentFact.Prompt(
                PromptFactCases.AuthorityRootAccepted
                    { SchemaVersion = 2
                      SessionId = sessionIdOf (payload?SessionId)
                      LogicalRunId = logicalRunOf (payload?LogicalRunId)
                      AuthorityRootUserMessageId = authorityRootOf (payload?AuthorityRootUserMessageId)
                      AuthorityKind = text (payload?AuthorityKind)
                      IdentitySeed = identitySeedOfJs (payload?IdentitySeed) }
            )
        | "ProviderFailure", "FailureRecorded" ->
            AgentFact.ProviderFailure(
                ProviderFailureFactCases.FailureRecorded
                    {| SessionId = sessionIdOf (payload?SessionId)
                       LogicalRunId = logicalRunOf (payload?LogicalRunId)
                       AuthorityRootUserMessageId = authorityRootOf (payload?AuthorityRootUserMessageId)
                       ProviderRun = providerRunOf (payload?ProviderRun)
                       ConsecutiveFailureCount = intValue (payload?ConsecutiveFailureCount)
                       Reason = text (payload?Reason) |}
            )
        | "ProviderFailure", "RetryExhausted" ->
            AgentFact.ProviderFailure(
                ProviderFailureFactCases.RetryExhausted
                    {| SessionId = sessionIdOf (payload?SessionId)
                       LogicalRunId = logicalRunOf (payload?LogicalRunId)
                       AuthorityRootUserMessageId = authorityRootOf (payload?AuthorityRootUserMessageId)
                       FinalConsecutiveFailureCount = intValue (payload?FinalConsecutiveFailureCount) |}
            )
        | "ProviderFailure", "SuccessRecorded" ->
            AgentFact.ProviderFailure(
                ProviderFailureFactCases.SuccessRecorded
                    {| SessionId = sessionIdOf (payload?SessionId)
                       LogicalRunId = logicalRunOf (payload?LogicalRunId)
                       AuthorityRootUserMessageId = authorityRootOf (payload?AuthorityRootUserMessageId)
                       ProviderRun = providerRunOf (payload?ProviderRun) |}
            )
        | "Companion", "OpeningPromptCaptured" ->
            AgentFact.Companion(
                CompanionFactCases.OpeningPromptCaptured
                    {| SessionId = sessionIdOf (payload?SessionId)
                       AssignmentText = text (payload?AssignmentText)
                       AuthoritativeRequirements = []
                       ProviderRun = None |}
            )
        | other -> failwith $"TemporalSurface: unknown AgentFact {family}.{caseName}"

    let private factOfJs (value: obj) : Fact =
        let family = text (value?family)

        match family with
        | "Runtime" ->
            let payload = unbox<obj> (value?payload)

            Fact.Runtime(
                RuntimeFact.RuntimeStarted
                    {| RuntimeId = RuntimeId.create (text (payload?RuntimeId))
                       ProcessId = intValue (payload?ProcessId)
                       StartedAt = DateTimeOffset.Parse(text (payload?StartedAt)) |}
            )
        | _ -> Fact.Agent(agentFactOfJs value)

    let private envelopeOfJs (value: obj) : Envelope =
        let stream = streamOfJs (value?stream)
        let fact = factOfJs (value?fact)

        { RuntimeId =
            RuntimeId.create (
                if isNullish (value?runtime) then
                    "rt_temporal"
                else
                    text (value?runtime)
            )
          LocalSeq = LocalSeq.create (if isNullish (value?seq) then 1L else int64Value (value?seq))
          ObservedAt =
            DateTimeOffset.Parse(
                if isNullish (value?observedAt) then
                    "2026-01-01T00:00:00Z"
                else
                    text (value?observedAt)
            )
          EventId = EventId.create (if isNullish (value?id) then "e1" else text (value?id))
          Stream = stream
          ProviderRun = optionalProviderRun (value?run)
          Fact = fact }

    let private providerFailureToJs (state: ProviderFailureProjection) : obj =
        box
            {| logicalRun = LogicalRunId.value state.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value state.AuthorityRootUserMessageId
               failures = state.Budget.ConsecutiveFailureCount
               dedupeKeys = state.RecentFailureKeys.Length
               exhausted = state.Exhausted |}

    let private authorityProfileToJs (profile: PromptAuthority.AuthorityExecutionProfile) : obj =
        box
            {| session = SessionId.value profile.SessionId
               logicalRun = LogicalRunId.value profile.LogicalRunId
               authorityRoot = AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId
               authorityKind =
                match profile.AuthorityKind with
                | PromptAuthority.RootAuthorityKind.HumanRoot -> "HumanRoot"
                | PromptAuthority.RootAuthorityKind.AgentOwnerRoot -> "AgentOwnerRoot"
               identitySeed = identitySeedToJs profile.IdentitySeed
               participantIdentity = participantIdentityToJs profile.ParticipantIdentity |}

    let private sessionToJs (session: SessionAgentProjection) : obj =
        box
            {| providerFailures =
                match session.ProviderFailures with
                | None -> null
                | Some value -> providerFailureToJs value
               activeLogicalRun =
                session.PromptAuthority
                |> Option.bind (fun authority -> authority.ActiveLogicalRun)
                |> Option.map authorityProfileToJs
                |> Option.defaultValue null
               lastAuthorityProfile =
                session.PromptAuthority
                |> Option.bind (fun authority -> authority.LastAuthorityProfile)
                |> Option.map authorityProfileToJs
                |> Option.defaultValue null |}

    let private projectionToJs (projection: ProjectionSet) : obj =
        let sessions =
            projection.AgentProjections.Sessions
            |> Map.toList
            |> List.map (fun (sessionId, session) -> SessionId.value sessionId ==> sessionToJs session)

        box {| sessions = createObj sessions |}

    let private rejectionToJs (rejection: FoldRejection) : obj =
        box
            {| Fact = rejection.Fact
               Reason = rejection.Reason |}

    let private factToJs (fact: Fact) : obj =
        match fact with
        | Fact.Runtime(RuntimeFact.RuntimeStarted payload) ->
            box
                {| family = "Runtime"
                   case = "RuntimeStarted"
                   payload =
                    box
                        {| RuntimeId = RuntimeId.value payload.RuntimeId
                           ProcessId = payload.ProcessId
                           StartedAt = payload.StartedAt.ToString("o") |} |}
        | Fact.Agent(AgentFact.Prompt(PromptFactCases.AuthorityRootAccepted payload)) ->
            box
                {| family = "Prompt"
                   case = "AuthorityRootAccepted"
                   payload =
                    box
                        {| SchemaVersion = 2
                           SessionId = SessionId.value payload.SessionId
                           LogicalRunId = LogicalRunId.value payload.LogicalRunId
                           AuthorityRootUserMessageId =
                            AuthorityRootUserMessageId.value payload.AuthorityRootUserMessageId
                           AuthorityKind = payload.AuthorityKind
                           IdentitySeed = identitySeedToJs payload.IdentitySeed |} |}
        | Fact.Agent(AgentFact.ProviderFailure(ProviderFailureFactCases.FailureRecorded payload)) ->
            box
                {| family = "ProviderFailure"
                   case = "FailureRecorded"
                   payload =
                    box
                        {| SessionId = SessionId.value payload.SessionId
                           LogicalRunId = LogicalRunId.value payload.LogicalRunId
                           AuthorityRootUserMessageId =
                            AuthorityRootUserMessageId.value payload.AuthorityRootUserMessageId
                           ProviderRun = ProviderRunIdentity.value payload.ProviderRun
                           ConsecutiveFailureCount = payload.ConsecutiveFailureCount
                           Reason = payload.Reason |} |}
        | Fact.Agent(AgentFact.ProviderFailure(ProviderFailureFactCases.RetryExhausted payload)) ->
            box
                {| family = "ProviderFailure"
                   case = "RetryExhausted"
                   payload =
                    box
                        {| SessionId = SessionId.value payload.SessionId
                           LogicalRunId = LogicalRunId.value payload.LogicalRunId
                           AuthorityRootUserMessageId =
                            AuthorityRootUserMessageId.value payload.AuthorityRootUserMessageId
                           FinalConsecutiveFailureCount = payload.FinalConsecutiveFailureCount |} |}
        | Fact.Agent(AgentFact.ProviderFailure(ProviderFailureFactCases.SuccessRecorded payload)) ->
            box
                {| family = "ProviderFailure"
                   case = "SuccessRecorded"
                   payload =
                    box
                        {| SessionId = SessionId.value payload.SessionId
                           LogicalRunId = LogicalRunId.value payload.LogicalRunId
                           AuthorityRootUserMessageId =
                            AuthorityRootUserMessageId.value payload.AuthorityRootUserMessageId
                           ProviderRun = ProviderRunIdentity.value payload.ProviderRun |} |}
        | Fact.Agent(AgentFact.Companion(CompanionFactCases.OpeningPromptCaptured payload)) ->
            box
                {| family = "Companion"
                   case = "OpeningPromptCaptured"
                   payload =
                    box
                        {| SessionId = SessionId.value payload.SessionId
                           AssignmentText = payload.AssignmentText |} |}
        | _ -> failwith "TemporalSurface: persisted fact is outside the temporal trace contract"

    let private envelopeToJs (envelope: Envelope) : obj =
        box
            {| runtime = RuntimeId.value envelope.RuntimeId
               seq = LocalSeq.value envelope.LocalSeq
               observedAt = envelope.ObservedAt.ToString("o")
               id = EventId.value envelope.EventId
               stream = streamToJs envelope.Stream
               run =
                match envelope.ProviderRun with
                | None -> null
                | Some value -> ProviderRunIdentity.value value
               fact = factToJs envelope.Fact |}

    let private decodePersisted (events: EventEnvelope list) : Envelope list =
        let rec loop remaining acc =
            match remaining with
            | [] -> List.rev acc
            | event :: tail ->
                match EventStoreJournalCodec.tryDecode event with
                | Ok envelope -> loop tail (envelope :: acc)
                | Error error -> failwith $"TemporalSurface: persisted journal decode failed: {error}"

        loop events []

    let private persistedEnvelopes (commonDir: string) : obj array =
        match ProcessEventLog.readStreams commonDir with
        | Error error -> failwith $"TemporalSurface: persisted journal read failed: {error}"
        | Ok streams ->
            streams
            |> List.collect snd
            |> decodePersisted
            |> List.map envelopeToJs
            |> List.toArray

    let private journalResult commonDir writer projection =
        match AgentJournal.createFromProjection writer projection with
        | Error error ->
            writer.Release()

            box
                {| ok = false
                   error = $"{error.Fact}: {error.Reason}" |}
        | Ok journal ->
            box
                {| ok = true
                   journal = JournalHandle(commonDir, journal) |}

    // ── deterministic timer/clock capabilities ─────────────────────────────

    let createVirtualTimer () : obj =
        TimerHandle(VirtualTiming.createVirtualTimerPort ()) :> obj

    let timerDelay (timer: obj) (milliseconds: int) : obj =
        let handle = (timer :?> TimerHandle).Port.Port.Delay milliseconds
        DeadlineHandle(handle) :> obj

    let timerAwait (handle: obj) : Task<unit> =
        (handle :?> DeadlineHandle).Handle.Delay

    let timerCancel (handle: obj) : unit =
        (handle :?> DeadlineHandle).Handle.Cancel()

    let timerAdvance (timer: obj) (milliseconds: int) : unit =
        (timer :?> TimerHandle).Port.Advance milliseconds

    let timerNowMs (timer: obj) : int = (timer :?> TimerHandle).Port.NowMs()

    let timerDispose (timer: obj) : unit =
        (timer :?> TimerHandle).Port.Port.Dispose()

    let createVirtualClock () : obj =
        ClockHandle(VirtualTiming.createVirtualClockPort ()) :> obj

    let clockNowIso (clock: obj) : string =
        (clock :?> ClockHandle).Port.Port.UtcNow().ToString("o")

    let clockNowMs (clock: obj) : int64 =
        (clock :?> ClockHandle).Port.Port.UtcNow().ToUnixTimeMilliseconds()

    let clockAdvanceMs (clock: obj) (milliseconds: int) : unit =
        (clock :?> ClockHandle).Port.AdvanceMs milliseconds

    let clockSet (clock: obj) (iso: string) : unit =
        (clock :?> ClockHandle).Port.Set(DateTimeOffset.Parse iso)

    // ── durable temporal world ──────────────────────────────────────────────

    let openJournal (commonDir: string) (runtimeId: string) (processId: int) (startedAt: string) : Task<obj> =
        task {
            let store =
                EventStore.createLocal
                    commonDir
                    (Guid.NewGuid().ToString("N"))
                    (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

            let! writer, _init =
                EventStoreJournalWriter.create (
                    RuntimeId.create runtimeId,
                    processId,
                    DateTimeOffset.Parse startedAt,
                    store
                )

            return journalResult commonDir writer Fold.empty
        }

    let resumeJournal (commonDir: string) (runtimeId: string) (processId: int) (startedAt: string) : Task<obj> =
        task {
            let store =
                EventStore.createLocal
                    commonDir
                    (Guid.NewGuid().ToString("N"))
                    (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

            let! result =
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create runtimeId,
                    processId,
                    DateTimeOffset.Parse startedAt,
                    store
                )

            match result with
            | Error error ->
                return
                    box
                        {| ok = false
                           error = $"{error.Fact}: {error.Reason}" |}
            | Ok(writer, _init, projection) -> return journalResult commonDir writer projection
        }

    let journalDispose (handle: obj) : unit = (handle :?> JournalHandle).Dispose()

    let journalAppendAgent (handle: obj) (stream: obj) (run: obj) (fact: obj) : Task<obj> =
        task {
            let journal = (handle :?> JournalHandle).Journal

            let! result =
                AgentJournal.appendAgent (streamOfJs stream) (optionalProviderRun run) (agentFactOfJs fact) journal

            match result with
            | Ok projection ->
                return
                    box
                        {| ok = true
                           projection = projectionToJs projection |}
            | Error error ->
                return
                    box
                        {| ok = false
                           error = JournalAppendFailure.describe error |}
        }

    let journalSnapshot (handle: obj) : obj =
        (handle :?> JournalHandle).Journal |> AgentJournal.snapshot |> projectionToJs

    let journalPersistedEnvelopes (handle: obj) : obj array =
        let commonDir = (handle :?> JournalHandle).CommonDir
        persistedEnvelopes commonDir

    let writerReleaseDrainScenario () : Task<obj> =
        EventStoreWriterSurface.writerReleaseDrainScenario ()

    let writerPoisonPreservesFirstFailureScenario () : Task<obj> =
        EventStoreWriterSurface.writerPoisonPreservesFirstFailureScenario ()

    /// Host lifecycle proof: scheduler shutdown closes admission immediately,
    /// waits for the already-started pass, and refuses later kicks.
    let reconcileSchedulerStopDrainScenario () : Task<obj> =
        task {
            let entered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — scenario snapshot-read counter.
            let mutable snapshotReads = 0
            // DSL-MUTABLE: algorithm-scratch — scenario scheduler-drain completion observation.
            let mutable drainCompleted = false

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1
                        Task.FromResult(Ok([]: SessionMessage list)) }

            let observeSnapshot (_: SessionId) (_: SessionMessage list) : Task =
                task {
                    AsyncSupport.trySetResult entered () |> ignore
                    do! release.Task
                }
                :> Task

            let scheduler =
                Reconciler.Scheduler(
                    snapshot,
                    TurnBinding.Store(),
                    (fun (_: ReconciledTurnContext) -> Task.FromResult(()) :> Task),
                    ?onSnapshot = Some observeSnapshot
                )

            scheduler.Kick(SessionId.create "scheduler-drain-running", ReconcileProgram.ReconcileWake.RetryWake)
            do! entered.Task

            let drain =
                task {
                    do! scheduler.StopAndDrain()
                    drainCompleted <- true
                }

            let blockedOnRunningPass = not drainCompleted
            let readsBeforeRejectedKick = snapshotReads
            scheduler.Kick(SessionId.create "scheduler-drain-rejected", ReconcileProgram.ReconcileWake.RetryWake)
            let rejectedKickDidNotRun = snapshotReads = readsBeforeRejectedKick

            AsyncSupport.trySetResult release () |> ignore
            do! drain

            return
                box
                    {| blockedOnRunningPass = blockedOnRunningPass
                       rejectedKickDidNotRun = rejectedKickDidNotRun
                       drained = drainCompleted
                       snapshotReads = snapshotReads |}
        }

    /// Poison proof: once the durable substrate becomes unavailable, a new wake
    /// cannot start another reconcile pass even while the first pass is blocked.
    let reconcileSchedulerDurableUnavailableScenario () : Task<obj> =
        task {
            let entered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — scenario snapshot-read counter.
            let mutable snapshotReads = 0
            // DSL-MUTABLE: algorithm-scratch — scenario durable availability switch.
            let mutable unavailable = false

            let snapshot =
                { new ISessionSnapshotPort with
                    member _.GetMessages _ =
                        snapshotReads <- snapshotReads + 1
                        Task.FromResult(Ok([]: SessionMessage list)) }

            let observeSnapshot (_: SessionId) (_: SessionMessage list) : Task =
                task {
                    AsyncSupport.trySetResult entered () |> ignore
                    do! release.Task
                }
                :> Task

            let scheduler =
                Reconciler.Scheduler(
                    snapshot,
                    TurnBinding.Store(),
                    (fun (_: ReconciledTurnContext) -> Task.FromResult(()) :> Task),
                    ?onSnapshot = Some observeSnapshot,
                    ?durableUnavailable = Some(fun () -> unavailable)
                )

            scheduler.Kick(SessionId.create "scheduler-poison-running", ReconcileProgram.ReconcileWake.RetryWake)
            do! entered.Task

            unavailable <- true
            let readsBeforeRejectedKick = snapshotReads
            scheduler.Kick(SessionId.create "scheduler-poison-rejected", ReconcileProgram.ReconcileWake.RetryWake)
            let rejectedWhileFirstPassBlocked = snapshotReads = readsBeforeRejectedKick

            AsyncSupport.trySetResult release () |> ignore
            do! scheduler.StopAndDrain()

            return
                box
                    {| rejectedWhileFirstPassBlocked = rejectedWhileFirstPassBlocked
                       snapshotReads = snapshotReads |}
        }

    /// Plugin owner proof: disposal closes Host-work admission and awaits
    /// reconcile plus already-admitted foreground/background work before returning.
    let pluginScopeStopDrainScenario () : Task<obj> =
        task {
            let reconcileEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let releaseReconcile =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let backgroundEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let releaseBackground =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let ownedEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let releaseOwned =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — scenario scope-disposal observation.
            let mutable disposed = false
            // DSL-MUTABLE: algorithm-scratch — scenario rejected-background observation.
            let mutable lateBackgroundStarted = false
            // DSL-MUTABLE: algorithm-scratch — scenario rejected-owned-work observation.
            let mutable lateOwnedStarted = false
            let scope = new PluginRuntimeScope(None, (fun _ -> false), None)

            scope.TrackReconcileShutdown(fun () ->
                task {
                    AsyncSupport.trySetResult reconcileEntered () |> ignore
                    do! releaseReconcile.Task
                }
                :> Task)

            scope.RunBackground(fun () ->
                task {
                    AsyncSupport.trySetResult backgroundEntered () |> ignore
                    do! releaseBackground.Task
                }
                :> Task)

            let owned =
                scope.RunOwnedWork(fun () ->
                    task {
                        AsyncSupport.trySetResult ownedEntered () |> ignore
                        do! releaseOwned.Task
                    }
                    :> Task)

            do! backgroundEntered.Task
            do! ownedEntered.Task

            let disposing =
                task {
                    do! scope.DisposeAsync()
                    disposed <- true
                }

            do! reconcileEntered.Task

            scope.RunBackground(fun () ->
                lateBackgroundStarted <- true
                Task.FromResult(()) :> Task)

            do!
                scope.RunOwnedWork(fun () ->
                    lateOwnedStarted <- true
                    Task.FromResult(()) :> Task)

            let blockedBeforeRelease = not disposed
            AsyncSupport.trySetResult releaseBackground () |> ignore
            let stillWaitingForReconcile = not disposed
            AsyncSupport.trySetResult releaseReconcile () |> ignore
            let stillWaitingForOwnedWork = not disposed
            AsyncSupport.trySetResult releaseOwned () |> ignore
            do! owned
            do! disposing

            return
                box
                    {| blockedBeforeRelease = blockedBeforeRelease
                       stillWaitingForReconcile = stillWaitingForReconcile
                       stillWaitingForOwnedWork = stillWaitingForOwnedWork
                       lateBackgroundRejected = not lateBackgroundStarted
                       lateOwnedRejected = not lateOwnedStarted
                       disposed = disposed |}
        }

    /// Plugin owner proof: detached Host failures are not swallowed. Shutdown
    /// drains the task, closes further admission, then returns the original error.
    let pluginScopeBackgroundFailureScenario () : Task<obj> =
        task {
            let entered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — scenario rejected-background observation.
            let mutable lateBackgroundStarted = false
            let scope = new PluginRuntimeScope(None, (fun _ -> false), None)

            scope.RunBackground(fun () ->
                task {
                    AsyncSupport.trySetResult entered () |> ignore
                    do! release.Task
                    return raise (InvalidOperationException "background exploded")
                }
                :> Task)

            do! entered.Task
            let disposing = scope.DisposeAsync()
            AsyncSupport.trySetResult release () |> ignore

            // DSL-MUTABLE: algorithm-scratch — captured scenario failure text.
            let mutable error = ""

            try
                do! disposing
            with ex ->
                error <- ex.Message

            scope.RunBackground(fun () ->
                lateBackgroundStarted <- true
                Task.FromResult(()) :> Task)

            return
                box
                    {| error = error
                       lateBackgroundRejected = not lateBackgroundStarted |}
        }

    // ── pure durable fold ───────────────────────────────────────────────────

    let fold (envelopes: obj array) : obj =
        let rec loop current remaining =
            match remaining with
            | [] ->
                box
                    {| ok = true
                       value = projectionToJs current |}
            | value :: tail ->
                match Fold.foldEnvelope current (envelopeOfJs value) with
                | Ok updated -> loop updated tail
                | Error rejection ->
                    box
                        {| ok = false
                           error = rejectionToJs rejection |}

        loop Fold.empty (envelopes |> Array.toList)

    // ── ProviderFailureProjection's typed transition, exposed as opaque state ─

    let private providerFailureIdentity (value: obj) : FailedProviderAttemptIdentity =
        { SessionId = sessionIdOf (value?session)
          LogicalRunId = logicalRunOf (value?logicalRun)
          AuthorityRootUserMessageId = authorityRootOf (value?authorityRoot)
          ProviderRun = providerRunOf (value?providerRun) }

    let private providerFailureError (error: ProviderFailureAdvanceRejection) =
        match error with
        | ProviderFailureAdvanceRejection.AlreadyObserved -> "AlreadyObserved"
        | ProviderFailureAdvanceRejection.AlreadyExhausted -> "AlreadyExhausted"
        | ProviderFailureAdvanceRejection.DifferentRun -> "DifferentRun"
        | ProviderFailureAdvanceRejection.NoActiveBudget -> "NoActiveBudget"
        | ProviderFailureAdvanceRejection.InvalidTransition -> "InvalidTransition"

    let providerFailureForAuthority (logicalRun: string) (authorityRoot: string) : obj =
        ProviderFailureHandle(
            ProviderFailureProjection.forAuthority (logicalRunOf logicalRun) (authorityRootOf authorityRoot)
        )
        :> obj

    let applyFailure (identity: obj) (count: int) (current: obj) : obj =
        let state = (current :?> ProviderFailureHandle).State

        match ProviderFailureProjection.applyFailure (providerFailureIdentity identity) count state with
        | Ok updated ->
            box
                {| ok = true
                   value = ProviderFailureHandle updated |}
        | Error error ->
            box
                {| ok = false
                   error = providerFailureError error |}

    let providerFailureRead (current: obj) : obj =
        (current :?> ProviderFailureHandle).State |> providerFailureToJs

    let sessionReuseIdentityScenario (firstAccepted: obj) (secondAccepted: obj) : obj =
        let accept (current: ProjectionSet) (fact: obj) = Fold.foldFact current (factOfJs fact)

        let closeFirst (current: ProjectionSet) (fact: obj) =
            let sessionId = sessionIdOf (fact?payload?SessionId)

            { current with
                AgentProjections =
                    match
                        AgentProjection.tryUpdate
                            sessionId
                            (fun session ->
                                Ok
                                    { session with
                                        PromptAuthority =
                                            session.PromptAuthority
                                            |> Option.map PromptAuthorityLedger.closeCompletedHumanRootManager })
                            current.AgentProjections
                    with
                    | Ok updated -> updated
                    | Error _ -> current.AgentProjections }

        let refused reason =
            box
                {| ok = false
                   error =
                    {| Fact = "AuthorityRootAccepted"
                       Reason = reason |} |}

        match accept Fold.empty firstAccepted with
        | Error rejection -> refused rejection.Reason
        | Ok afterFirst ->
            match accept afterFirst secondAccepted with
            | Ok _ -> refused "second root was admitted before the first run closed"
            | Error rejection ->
                let afterLife = closeFirst afterFirst firstAccepted

                match accept afterLife secondAccepted with
                | Error secondRejection -> refused secondRejection.Reason
                | Ok online ->
                    match accept Fold.empty firstAccepted with
                    | Error replayRejection -> refused replayRejection.Reason
                    | Ok replayedFirst ->
                        let replayedLife = closeFirst replayedFirst firstAccepted

                        match accept replayedLife secondAccepted with
                        | Error replayedRejection -> refused replayedRejection.Reason
                        | Ok replayed ->
                            box
                                {| preCloseSecond =
                                    box
                                        {| ok = false
                                           error = rejectionToJs rejection |}
                                   afterFirst = projectionToJs afterFirst
                                   afterLife = projectionToJs afterLife
                                   online = projectionToJs online
                                   replayed = projectionToJs replayed |}
