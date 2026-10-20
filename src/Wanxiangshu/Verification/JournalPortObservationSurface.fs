namespace Wanxiangshu.Verification

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.OpenCode
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

/// Observation-timing proofs for AgentJournalPortAdapter. Every scenario builds
/// the production adapter over a real AgentJournal whose writer commits into a
/// real filesystem EventStore, so a frozen-at-construction member reads through
/// the same canonical projection the store publishes.
module JournalPortObservationSurface =

    /// Armed after real authority admission, this parks the next business append.
    /// TryCurrent and the canonical integrator pass through untouched.
    type private GatedAppendStore
        (inner: IEventStore, entered: TaskCompletionSource<unit>, release: TaskCompletionSource<unit>) =
        // DSL-MUTABLE: resource — one-shot ownership of the parked append
        let mutable parkNext = false

        member _.ParkNextAppend() = parkNext <- true

        interface IEventStore with
            member _.Append events =
                if parkNext then
                    parkNext <- false

                    task {
                        AsyncSupport.trySetResult entered () |> ignore
                        do! release.Task
                        return! inner.Append events
                    }
                else
                    inner.Append events

            member _.WritePayload content = inner.WritePayload content
            member _.ReadPayload payloadRef = inner.ReadPayload payloadRef
            member _.TryCurrent key = inner.TryCurrent key
            member _.TryEvent eventId = inner.TryEvent eventId
            member _.TryHeads streamId = inner.TryHeads streamId
            member _.TryHead streamId = inner.TryHead streamId
            member _.AllHeads() = inner.AllHeads()
            member _.ReloadLocal() = inner.ReloadLocal()

    let private openStore (commonDir: string) (writerTag: string) : IEventStore =
        EventStore.createLocal
            commonDir
            ("writer-" + writerTag)
            (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

    let private openJournal (store: IEventStore) (tag: string) : Task<AgentJournal> =
        task {
            match!
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create ("port_obs_" + tag),
                    4242,
                    DateTimeOffset.Parse "2026-09-12T00:00:00Z",
                    store
                )
            with
            | Error rejection -> return failwith $"resumeOrCreate rejected: {rejection.Fact}: {rejection.Reason}"
            | Ok(writer, _, projection) ->
                match AgentJournal.createFromProjection writer projection with
                | Error rejection -> return failwith $"journal attach rejected: {rejection.Fact}: {rejection.Reason}"
                | Ok journal -> return journal
        }

    let private appendOutcomeName (result: Result<ProjectionSet, JournalAppendFailure>) =
        match result with
        | Ok _ -> "Ok"
        | Error(WriteUnknown(_, WriteFailed reason)) -> "Unknown:" + reason
        | Error(WriteUnknown(_, FlushFailed reason)) -> "UnknownFlush:" + reason
        | Error(WriteUnknown(_, StoreAppendUnknown evidence)) -> "Unknown:" + evidence.Primary.Cause.Message
        | Error(WriterUnavailable(_, WriterPoisoned first)) -> "Poisoned:" + AppendError.describe first.Error
        | Error(JournalAppendFailure.NoNewWriteReleaseFailed _) -> "NoNewWriteReleaseFailed"
        | Error(WriterUnavailable(_, WriterClosing)) -> "Closing"
        | Error(WriterUnavailable(_, WriterDisposed)) -> "Disposed"
        | Error(FactRejected _) -> "Rejected"

    let private handleLinkedFact (parent: SessionId) (child: SessionId) : AgentFact =
        AgentFact.Execution(
            ExecutionFactCases.HandleLinked
                {| ParentSessionId = parent
                   ChildSessionId = child
                   Handle = HandleId.Agent(AgentHandleId.create "hdl-port-obs")
                   TargetAgent = "engineer"
                   Byname = "engineer-port-obs"
                   CanonicalRole = Role.Engineer
                   Ownership = HandleOwnership.DurableParentHandle |}
        )

    let private acceptChildWork (journal: AgentJournal) (parent: SessionId) (child: SessionId) : Task<unit> =
        task {
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create journal)

            let identity =
                ParticipantIdentity.resolveAtRoot "manager"
                |> Result.defaultWith (fun error -> failwithf "parent identity rejected: %A" error)

            let! acceptedParent =
                dispatcher.AcceptHumanRoot
                    parent
                    (PhysicalUserMessageId.create ("msg-" + SessionId.value parent))
                    (Some(PromptIdentitySeed.RootSelection identity))

            let owner =
                acceptedParent
                |> Result.defaultWith (fun error -> failwithf "parent admission rejected: %A" error)

            let seed =
                PromptAuthority.issueInheritedIdentitySeed "engineer" owner
                |> Result.defaultWith (fun error -> failwithf "child identity rejected: %A" error)

            let port =
                { new IDispatchSessionPort with
                    member _.SendPrompt(_, _, _) =
                        Task.FromResult(SendOutcome.AdmittedWithReceipt(TransportReceipt.create "receipt-port-obs"))

                    member _.SubscribeFutureTerminal(_, _) =
                        { new IDisposable with
                            member _.Dispose() = () }

                    member _.SubscribeTerminal(_, _) =
                        { new IDisposable with
                            member _.Dispose() = () } }

            let! sent =
                dispatcher.SendAgentOwnerRoot
                    port
                    child
                    "port-observation work"
                    seed
                    None
                    PromptDispatcher.AwaitMode.Await
                    None

            let key =
                sent
                |> Result.defaultWith (fun error -> failwithf "child dispatch rejected: %s" error)

            let! acceptedChild =
                dispatcher.AcceptAgentOwnerRoot
                    key
                    child
                    (PhysicalUserMessageId.create ("msg-" + SessionId.value child))

            acceptedChild
            |> Result.defaultWith (fun error -> failwithf "child admission rejected: %s" error)
            |> ignore
        }

    let private hasCommittedHandle (journal: AgentJournal) (parent: SessionId) : bool =
        AgentProjection.tryFind parent (AgentJournal.snapshot journal).AgentProjections
        |> Option.bind _.Handles
        |> Option.exists (fun handles ->
            HandleProjection.tryFind (HandleId.Agent(AgentHandleId.create "hdl-port-obs")) handles
            |> Option.isSome)

    let private estimateFact (sessionId: SessionId) : AgentFact =
        AgentFact.Delegation(
            DelegationFactCases.DelegatedToolEstimateReplaced
                {| SessionId = sessionId
                   ExpectedToolCalls = 1 |}
        )

    let private missingPayloadFact (sessionId: SessionId) : AgentFact =
        AgentFact.Companion(
            CompanionFactCases.TerminalOutputCaptured
                {| SessionId = sessionId
                   TextRef = BlobRef.create ("blobs/" + String.replicate 64 "f")
                   TextDigest = BlobDigest.create (String.replicate 64 "f")
                   ProviderRun = ProviderRunIdentity.create "run-port-obs" |}
        )

    /// XTrace-shaping fact with no payload refs: the only session-visible field a
    /// Wire read can observe without a blob store write.
    let private openingFact (sessionId: SessionId) : AgentFact =
        AgentFact.Companion(
            CompanionFactCases.OpeningPromptCaptured
                {| SessionId = sessionId
                   AssignmentText = "port-observation opening"
                   AuthoritativeRequirements = []
                   ProviderRun = None |}
        )

    /// Builds every port relevant to a scenario against one journal. Constructing
    /// all ports up front is intentional: the proofs say each member reads the
    /// journal when the member is invoked, not when the port is assembled.
    let private portsOf (journal: AgentJournal) : DelegatedToolEstimatePort * TerminalPolicyPort * WireJournalPort =
        AgentJournalPortAdapter.forDelegatedToolEstimate journal,
        AgentJournalPortAdapter.forTerminalPolicy journal,
        AgentJournalPortAdapter.forWire journal

    let liveReadScenario (commonDir: string) (writerTag: string) : Task<obj> =
        task {
            let store = openStore commonDir writerTag
            use! journal = openJournal store writerTag
            let session = SessionId.create "ses-port-obs"

            let estimate, terminal, wire = portsOf journal

            let pendingBefore =
                estimate.TryState session
                |> Option.map DelegatedToolEstimateProjection.remaining
                |> Option.defaultValue 0

            let poisonedBefore = terminal.IsPoisoned()
            let viewBefore = wire.ReadView session
            let stateBefore = Option.isSome viewBefore.State

            let! appended = journal.AppendAgent (StreamId.Session session) None (estimateFact session)

            let folded =
                match appended with
                | Ok _ -> true
                | Error _ -> false

            let pendingAfter =
                estimate.TryState session
                |> Option.map DelegatedToolEstimateProjection.remaining
                |> Option.defaultValue 0

            let freshAfter =
                (AgentJournalPortAdapter.forDelegatedToolEstimate journal).TryState session
                |> Option.map DelegatedToolEstimateProjection.remaining
                |> Option.defaultValue 0

            let poisonedAfter = terminal.IsPoisoned()

            let! opened = journal.AppendAgent (StreamId.Session session) None (openingFact session)

            let openedOk =
                match opened with
                | Ok _ -> true
                | Error _ -> false

            let viewAfter = wire.ReadView session

            let stateAfter =
                viewAfter.State |> Option.bind (fun state -> state.XTrace) |> Option.isSome

            return
                box
                    {| folded = folded
                       openedOk = openedOk
                       pendingBefore = pendingBefore
                       pendingAfter = pendingAfter
                       freshAfter = freshAfter
                       poisonedBefore = poisonedBefore
                       poisonedAfter = poisonedAfter
                       stateBefore = stateBefore
                       stateAfter = stateAfter |}
        }

    let sameCommitViewScenario (commonDir: string) (writerTag: string) : Task<obj> =
        task {
            let entered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let inner = openStore commonDir writerTag
            let store = GatedAppendStore(inner, entered, release)
            use! journal = openJournal (store :> IEventStore) writerTag

            let _, terminal, wire = portsOf journal
            let parent = SessionId.create "ses-port-parent"
            let child = SessionId.create "ses-port-child"
            do! acceptChildWork journal parent child
            let fromRevision = journal.Revision

            let preMember = terminal.HasListableHandles parent
            let preLinked = terminal.IsLinkedChild child
            let preState = hasCommittedHandle journal parent

            store.ParkNextAppend()

            let commitTask =
                journal.AppendAgent (StreamId.Session parent) None (handleLinkedFact parent child)

            do! entered.Task

            let midMember = terminal.HasListableHandles parent
            let midLinked = terminal.IsLinkedChild child
            let midView = wire.ReadView parent
            let midState = hasCommittedHandle journal parent
            let midCompanion = midView.IsCompanion

            AsyncSupport.trySetResult release () |> ignore
            let! committed = commitTask

            let postMember = terminal.HasListableHandles parent
            let postLinked = terminal.IsLinkedChild child
            let postState = hasCommittedHandle journal parent

            let advancedOnce =
                JournalRevision.value journal.Revision = JournalRevision.value fromRevision + 1L

            return
                box
                    {| outcome = appendOutcomeName committed
                       preMember = preMember
                       preLinked = preLinked
                       preState = preState
                       midMember = midMember
                       midLinked = midLinked
                       midState = midState
                       midCompanion = midCompanion
                       postMember = postMember
                       postLinked = postLinked
                       postState = postState
                       advancedOnce = advancedOnce |}
        }

    let revisionWaitScenario (commonDir: string) (writerTag: string) : Task<obj> =
        task {
            let store = openStore commonDir writerTag
            use! journal = openJournal store writerTag
            let _, terminal, _ = portsOf journal
            let parent = SessionId.create "ses-port-wait"
            let child = SessionId.create "ses-port-waited"
            do! acceptChildWork journal parent child

            let fromRevision = journal.Revision
            let waiter = journal.AwaitChangeFrom fromRevision
            let! appended = journal.AppendAgent (StreamId.Session parent) None (handleLinkedFact parent child)

            // The caller-side Promise.race timeout in the test bounds the wait;
            // this task resolves only when the journal publishes a real change.
            let! outcome = waiter

            let resolved = true
            let changeRevision = int64 (JournalRevision.value outcome.Revision)

            return
                box
                    {| committed = appendOutcomeName appended
                       resolved = resolved
                       changeRevision = changeRevision
                       currentRevision = int64 (JournalRevision.value journal.Revision)
                       observedHandle = terminal.HasListableHandles parent
                       advancedOnce = JournalRevision.value outcome.Revision = JournalRevision.value fromRevision + 1L |}
        }

    let cancelWaiterScenario (commonDir: string) (writerTag: string) : Task<obj> =
        task {
            let store = openStore commonDir writerTag
            use! journal = openJournal store writerTag
            let parent = SessionId.create "ses-port-cancel"
            let child = SessionId.create "ses-port-cancelled"

            let fromRevision = journal.Revision
            use cancellation = new CancellationTokenSource()
            let waiter = journal.AwaitChangeFromOrCancel(fromRevision, cancellation.Token)
            cancellation.Cancel()
            let! cancelled = waiter
            let cancelledToNone = Option.isNone cancelled

            let! appended = journal.AppendAgent (StreamId.Session parent) None (handleLinkedFact parent child)
            let revisionAdvanced = JournalRevision.isAfter journal.Revision fromRevision

            return
                box
                    {| cancelledToNone = cancelledToNone
                       committed = appendOutcomeName appended
                       revisionAdvanced = revisionAdvanced |}
        }

    let rejectedPayloadPoisonsWriterScenario (commonDir: string) (writerTag: string) : Task<obj> =
        task {
            let store = openStore commonDir writerTag
            use! journal = openJournal store writerTag
            let _, terminal, _ = portsOf journal
            let session = SessionId.create "ses-port-poison"

            let! seeded = journal.AppendAgent (StreamId.Session session) None (estimateFact session)

            let seededOk =
                match seeded with
                | Ok _ -> true
                | Error _ -> false

            let revisionBefore = string (JournalRevision.value journal.Revision)
            let! failed = journal.AppendAgent (StreamId.Session session) None (missingPayloadFact session)
            let failedOutcome = appendOutcomeName failed
            let poisoned = terminal.IsPoisoned()
            let revisionAfterFailure = string (JournalRevision.value journal.Revision)

            let missingPayloadRef =
                match failed with
                | Error(WriterUnavailable(_,
                                          WriterPoisoned { Error = AppendError.StorageInvalid(StorageInvalid.MissingPayload payloadRef) })) ->
                    PayloadRef.value payloadRef
                | _ -> ""

            let absentNotAttempted result =
                match result with
                | Error(WriterUnavailable(eventId, WriterPoisoned _)) -> store.TryEvent eventId |> Option.isNone
                | _ -> false

            let! after = journal.AppendAgent (StreamId.Session session) None (estimateFact session)
            let afterOutcome = appendOutcomeName after
            let revisionAfterPoisoned = string (JournalRevision.value journal.Revision)

            let originalPoisonPreserved =
                match failed, after with
                | Error(WriterUnavailable(eventId, WriterPoisoned first)),
                  Error(WriterUnavailable(_, WriterPoisoned later)) ->
                    first.FailedEventId = eventId
                    && later.FailedEventId = first.FailedEventId
                    && obj.ReferenceEquals(first.Error, later.Error)
                | _ -> false

            return
                box
                    {| seededOk = seededOk
                       failedOutcome = failedOutcome
                       poisoned = poisoned
                       afterOutcome = afterOutcome
                       missingPayloadRef = missingPayloadRef
                       failedFactAbsent = absentNotAttempted failed
                       laterFactAbsent = absentNotAttempted after
                       originalPoisonPreserved = originalPoisonPreserved
                       revisionBefore = revisionBefore
                       revisionAfterFailure = revisionAfterFailure
                       revisionAfterPoisoned = revisionAfterPoisoned |}
        }

    type private ActualJournalHandle(store: IEventStore, journal: AgentJournal, init: Envelope) =
        let cancellation = new CancellationTokenSource()
        // DSL-MUTABLE: resource — observes completion of the actual registered waiter.
        let mutable waitCompleted = false

        let waiter =
            task {
                let! change =
                    AgentJournal.awaitChangeFromOrCancel (AgentJournal.revision journal) cancellation.Token journal

                waitCompleted <- true
                return change
            }

        member _.Store = store
        member _.Journal = journal
        member _.Init = init
        member _.WaitCompleted = waitCompleted

        member _.Dispose() =
            task {
                cancellation.Cancel()
                let! _ = waiter
                do! (journal :> IAsyncDisposable).DisposeAsync()
                cancellation.Dispose()
            }

    let private openActualJournalOnStore store writerId (startedAt: string) : Task<obj> =
        task {
            let! opened =
                EventStoreJournalWriter.resumeOrCreate (
                    RuntimeId.create ("native-" + writerId),
                    4242,
                    (DateTimeOffset.Parse startedAt).ToOffset TimeSpan.Zero,
                    store
                )

            match opened with
            | Error error -> return failwithf "native journal open rejected: %A" error
            | Ok(writer, init, projection) ->
                match AgentJournal.createFromProjection writer projection with
                | Error error -> return failwithf "native journal attach rejected: %A" error
                | Ok journal -> return box (ActualJournalHandle(store, journal, init))
        }

    let openActualJournal commonDir writerId startedAt : Task<obj> =
        let store =
            EventStore.createLocal
                commonDir
                writerId
                (CanonicalIntegrator.createWithRules CanonicalIntegrator.baseRules AuthoritativeEventTypes.isKnown)

        openActualJournalOnStore store writerId startedAt

    let openActualJournalWithStore (store: obj) writerId startedAt : Task<obj> =
        openActualJournalOnStore (unbox<EventStoreHandle> store).Store writerId startedAt

    let observeActualJournalProjection (value: obj) : obj =
        let current = AgentJournal.snapshot (unbox<ActualJournalHandle> value).Journal

        box
            {| runtimeId = current.RuntimeId |> Option.map RuntimeId.value |> Option.toObj
               runtimeStartCount = current.AgentProjections.RuntimeStartCount
               sessionCount = Map.count current.AgentProjections.Sessions
               hasNativeSession =
                Map.containsKey (SessionId.create "native-settlement") current.AgentProjections.Sessions |}

    let observeActualJournal (value: obj) : obj =
        let handle = unbox<ActualJournalHandle> value

        box
            {| initEventId = EventId.value handle.Init.EventId
               revision = JournalRevision.value (AgentJournal.revision handle.Journal)
               localSeq = handle.Journal.Writer.LocalSeq
               lastCommittedLocalSeq = handle.Journal.Writer.LastCommittedLocalSeq
               poisoned = handle.Journal.IsPoisoned
               waitCompleted = handle.WaitCompleted |}

    let containsActualJournalEvent (value: obj) eventId : bool =
        (unbox<ActualJournalHandle> value).Store.TryEvent(EventId.create eventId)
        |> Option.isSome

    let private faultView (eventId: EventId) (failedEventId: EventId) (error: AppendError) =
        let code, phase, cause, cleanup, requested, prepared, cuts =
            match error with
            | AppendError.CommitUnknown evidence ->
                "CommitUnknown",
                string evidence.Primary.Phase,
                box evidence.Primary.Cause,
                evidence.CleanupFailures,
                evidence.Requested,
                evidence.Prepared.DurableEvents,
                evidence.Prepared.Cuts
            | AppendError.AppendNotAttempted evidence ->
                let prepared =
                    evidence.Prepared |> Option.map _.DurableEvents |> Option.defaultValue []

                let cuts = evidence.Prepared |> Option.map _.Cuts |> Option.defaultValue []

                "AppendNotAttempted",
                string evidence.Primary.Phase,
                box evidence.Primary.Cause,
                evidence.CleanupFailures,
                evidence.Requested,
                prepared,
                cuts
            | AppendError.NoNewWriteReleaseFailed evidence ->
                let prepared =
                    evidence.Prepared |> Option.map _.DurableEvents |> Option.defaultValue []

                let cuts = evidence.Prepared |> Option.map _.Cuts |> Option.defaultValue []
                "NoNewWriteReleaseFailed", "StoreRelease", box evidence.Cause, [], evidence.Requested, prepared, cuts
            | AppendError.StorageInvalid _ -> "StorageInvalid", "", null, [], [], [], []
            | AppendError.SemanticCut _ -> "SemanticCut", "", null, [], [], [], []
            | AppendError.AppendFailed _ -> "AppendFailed", "", null, [], [], [], []

        box
            {| eventId = EventId.value eventId
               failedEventId = EventId.value failedEventId
               code = code
               phase = phase
               cause = cause
               cleanupFailures =
                cleanup
                |> List.map (fun fault ->
                    box
                        {| phase = string fault.Phase
                           cause = fault.Cause |})
                |> List.toArray
               requestedIds = requested |> List.map (fun event -> EventId.value event.EventId) |> List.toArray
               preparedIds = prepared |> List.map (fun event -> EventId.value event.EventId) |> List.toArray
               cutIds = cuts |> List.map (fun cut -> EventId.value cut.CutEventId) |> List.toArray |}

    let appendActualJournal (value: obj) : Task<obj> =
        task {
            let handle = unbox<ActualJournalHandle> value

            let! result =
                AgentJournal.appendAgent
                    (StreamId.Session(SessionId.create "native-settlement"))
                    None
                    (CompanionFact.OpeningPromptCaptured
                        {| SessionId = SessionId.create "native-settlement"
                           AssignmentText = "native settlement"
                           AuthoritativeRequirements = []
                           ProviderRun = None |})
                    handle.Journal

            match result with
            | Ok _ -> return box {| kind = "Committed"; error = null |}
            | Error(WriterUnavailable(eventId, WriterPoisoned failure)) ->
                return
                    box
                        {| kind = "NotAttempted"
                           error = faultView eventId failure.FailedEventId failure.Error |}
            | Error(WriteUnknown(eventId, StoreAppendUnknown evidence)) ->
                return
                    box
                        {| kind = "CommitUnknown"
                           error = faultView eventId eventId (AppendError.CommitUnknown evidence) |}
            | Error(JournalAppendFailure.NoNewWriteReleaseFailed(eventId, failure)) ->
                return
                    box
                        {| kind = "NoNewWriteReleaseFailed"
                           error = faultView eventId eventId (AppendError.NoNewWriteReleaseFailed failure) |}
            | Error failure -> return failwithf "unexpected native journal failure: %A" failure
        }

    let disposeActualJournal (value: obj) : Task<unit> =
        (unbox<ActualJournalHandle> value).Dispose()
