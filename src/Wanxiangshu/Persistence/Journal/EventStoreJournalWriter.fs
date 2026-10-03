namespace Wanxiangshu.Persistence.Journal

open System
open System.Text
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Host
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Interaction.Authority

/// Local payload writer for EventStore journals.
/// `BlobRef` keeps the long-standing `blobs/<handle>` application shape, while
/// `<handle>` is now the local content-addressed PayloadRef. Git OIDs do not
/// participate in runtime durability.
[<Sealed>]
type EventStoreBlobWriter private (store: IEventStore) =
    let writePayload (content: string) =
        let digest = BlobDigest.create (HostDigest.sha256Hex content)
        let bytes = Encoding.UTF8.GetBytes content

        task {
            match! store.WritePayload bytes with
            | Error error -> return Error error
            | Ok payloadRef ->
                return
                    Ok
                        { BlobRef = BlobRef.create ("blobs/" + PayloadRef.value payloadRef)
                          BlobDigest = digest }
        }

    let handleOf (relative: string) =
        let prefix = "blobs/"

        if not (relative.StartsWith(prefix, StringComparison.Ordinal)) then
            None
        else
            Some(relative.Substring(prefix.Length))

    let decodeBlobHandle blobRef =
        let relative = BlobRef.value blobRef

        match handleOf relative with
        | None -> Error(sprintf "invalid blob reference: %s" relative)
        | Some handle when String.IsNullOrWhiteSpace handle || handle.Contains "/" ->
            Error(sprintf "invalid blob reference: %s" relative)
        | Some handle -> Ok handle

    let decodeUtf8 (bytes: byte[]) =
        try
            Ok(Encoding.UTF8.GetString bytes)
        with ex ->
            Error(sprintf "event-store payload read failed: %s" ex.Message)

    let readPayload handle =
        task {
            match! store.ReadPayload(PayloadRef.create handle) with
            | Error error -> return Error error
            | Ok None -> return Error(sprintf "event-store payload missing: %s" handle)
            | Ok(Some bytes) -> return decodeUtf8 bytes
        }

    member _.Write(content: string) : Task<Result<BlobWriteReceipt, string>> =
        task {
            try
                return! writePayload content
            with ex ->
                return Error(sprintf "event-store payload write failed: %s" ex.Message)
        }

    member _.Read(blobRef: BlobRef) : Task<Result<string, string>> =
        match decodeBlobHandle blobRef with
        | Error error -> Task.FromResult(Error error)
        | Ok handle -> readPayload handle

    interface IBlobWriter with
        member this.Write(content) = this.Write content
        member this.Read(blobRef) = this.Read blobRef

    static member Create(store: IEventStore) : IBlobWriter =
        EventStoreBlobWriter(store) :> IBlobWriter

/// Unified payload closure for one Journal fact (durable-events-012): every
/// EventStore payload the fact references. This is the single mapping that makes
/// `EventEnvelope.PayloadRefs` authoritative instead of always empty. Journal
/// facts carry blob handles inline in the domain payload, so the closure is
/// derived at the append boundary rather than tracked twice.
[<RequireQualifiedAccess>]
module JournalPayloadClosure =

    let ofFact (fact: Fact) : PayloadRef list =
        /// A blob handle is an EventStore payload reference only when it is the
        /// content address (64 lowercase sha256 hex chars) the store names files by.
        /// Anything else — a test placeholder or malformed data — is not a payload
        /// dependency, and claiming it would make every append fail the closure.
        let payloadRefOfContentAddress (value: string) : PayloadRef option =
            let isHex c =
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

            if value.Length = 64 && value |> Seq.forall isHex then
                Some(PayloadRef.create value)
            else
                None

        let payloadRefOfBlobRef (ref: BlobRef) : PayloadRef option =
            let value = BlobRef.value ref
            let prefix = "blobs/"

            if value.StartsWith(prefix, StringComparison.Ordinal) then
                payloadRefOfContentAddress (value.Substring(prefix.Length))
            else
                None

        let payloadRefOfBlobDigest (digest: BlobDigest) : PayloadRef option =
            payloadRefOfContentAddress (BlobDigest.value digest)

        let refOf (ref: BlobRef) =
            payloadRefOfBlobRef ref |> Option.toList

        let digestOf (digest: BlobDigest) =
            payloadRefOfBlobDigest digest |> Option.toList

        let pair (ref: BlobRef) (digest: BlobDigest) = refOf ref @ digestOf digest

        let refs =
            match fact with
            | Fact.Runtime _ -> []
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleWorkCompleted p)) ->
                (p.CompletionRef |> Option.toList |> List.choose payloadRefOfBlobRef)
                @ (p.CompletionDigest |> Option.toList |> List.choose payloadRefOfBlobDigest)
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleWorkConsumed p)) ->
                (p.CompletionRef |> Option.toList |> List.choose payloadRefOfBlobRef)
                @ (p.CompletionDigest |> Option.toList |> List.choose payloadRefOfBlobDigest)
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleCompleted p)) ->
                (p.CompletionRef |> Option.toList |> List.choose payloadRefOfBlobRef)
                @ (p.CompletionDigest |> Option.toList |> List.choose payloadRefOfBlobDigest)
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleFalseCompletionRejected p)) ->
                pair p.ExpectedCompletionRef p.ExpectedCompletionDigest
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleFalseTerminalReported p)) ->
                pair p.BadCompletionRef p.BadCompletionDigest
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.ParentJoinCorrectionRequested p)) ->
                digestOf p.BadCompletionDigest
            | Fact.Agent(AgentFact.Companion(CompanionFactCases.XTracePartAppended p)) -> pair p.TextRef p.TextDigest
            | Fact.Agent(AgentFact.Companion(CompanionFactCases.TerminalOutputCaptured p)) ->
                pair p.TextRef p.TextDigest
            | Fact.Agent(AgentFact.Context(ContextFactCases.BlogObservationCommitted p)) ->
                pair p.TextRef p.TextDigest
                @ (p.EvidenceRef |> Option.toList |> List.choose payloadRefOfBlobRef)
            | Fact.Agent(AgentFact.Context(ContextFactCases.BlogObservationsSquashed p)) -> pair p.TextRef p.TextDigest
            | Fact.Agent(AgentFact.Context(ContextFactCases.BloggerRequestMaterialized p)) ->
                pair p.ContextRef p.ContextDigest
                @ (p.SelectedFrameDigests |> List.choose payloadRefOfBlobDigest)
            | Fact.Agent(AgentFact.Context(ContextFactCases.PrefixRebaseCommitted p)) ->
                pair p.FrozenRecordPrefixRef p.FrozenRecordPrefixDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionAdmitted p)) ->
                pair p.OwnerWorkRecordRef p.OwnerWorkRecordDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionLaneMaterialized p)) ->
                pair p.WorkRecordRef p.WorkRecordDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionCompletionCaptured p)) ->
                pair p.PayloadRef p.PayloadDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionTakeoverClaimed p)) ->
                pair p.AggregateWorkRecordRef p.AggregateWorkRecordDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionTakeoverStarted p)) ->
                pair p.AggregateWorkRecordRef p.AggregateWorkRecordDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionConverged p)) ->
                pair p.AggregateWorkRecordRef p.AggregateWorkRecordDigest
            | _ -> []

        PayloadRefs.canonicalize refs

module private JournalWriterDisposal =
#if FABLE_COMPILER
    [<Emit("$0")>]
    let asValueTask (operation: Task) : ValueTask = jsNative
#else
    let asValueTask (operation: Task) = ValueTask(operation)
#endif

/// Journal writer backed by the local process EventStore.
/// It never enumerates history and never folds facts itself. CanonicalIntegrator
/// owns both boot replay and live integration; this type only assigns journal
/// envelope identity/sequence and appends one universal EventEnvelope.
[<Sealed>]
type EventStoreJournalWriter private (runtimeId: RuntimeId, init: Envelope, blobWriter: IBlobWriter, store: IEventStore)
    =
    let gate = obj ()
    // DSL-MUTABLE: resource — RuntimeStarted is lazy; load alone writes nothing.
    let mutable runtimeStartedCommitted = false
    // DSL-MUTABLE: resource — business facts start at LocalSeq 2 after lazy RuntimeStarted.
    let mutable currentSeq = 2L
    // DSL-MUTABLE: resource — poison latch: first append failure short-circuits
    // subsequent appends as Result error. None = healthy; set once, never cleared.
    let mutable firstFailure: string option = None
    // DSL-MUTABLE: resource — terminal close latch: drain completed, writer disposed.
    let mutable closed = false
    // DSL-MUTABLE: resource — serialized process writer operations.
    let mutable serial = Task.FromResult(())
    // DSL-MUTABLE: resource — one close Task drains the accepted append prefix.
    let mutable closeTask: Task option = None

    member _.RuntimeId = runtimeId
    member _.BlobWriter = blobWriter
    member _.LocalSeq = lock gate (fun () -> currentSeq)

    member _.LastCommittedLocalSeq =
        lock gate (fun () -> if runtimeStartedCommitted then currentSeq - 1L else 0L)

    member _.IsPoisoned = lock gate (fun () -> firstFailure.IsSome)

    member _.TryCurrent(key: string) = store.TryCurrent key

    member private _.Poison(firstFailureReason: string) =
        lock gate (fun () ->
            if firstFailure.IsNone && not closed then
                firstFailure <- Some firstFailureReason)

    member private _.PriorPoison(eventId: EventId) : CommitResult<Envelope> option =
        lock gate (fun () -> firstFailure |> Option.map (fun f -> NotAttempted(eventId, WriterPoisoned f)))

    static member private formatAppendError(error: AppendError) : string =
        match error with
        | AppendError.StorageInvalid detail -> sprintf "storage invalid: %A" detail
        | AppendError.SemanticCut cut -> sprintf "semantic cut %s: %s" cut.Rule cut.Reason
        | AppendError.AppendFailed reason -> "append failed: " + reason

    static member private commitEnvelope
        (store: IEventStore)
        (envelope: Envelope)
        : Task<Result<AppendReceipt, AppendError>> =
        let streamId = EventStoreJournalCodec.encodeStreamId envelope.Stream

        let parents =
            match envelope.Fact with
            | Fact.Runtime(RuntimeStarted _) ->
                let heads = store.AllHeads()
                if List.isEmpty heads then [] else heads
            | _ -> store.TryHead streamId |> Option.toList

        let dependencySession =
            match envelope.Fact with
            | Fact.Agent(AgentFact.Prompt(Wanxiangshu.Interaction.Authority.PromptFactCases.AuthorityRootAccepted p)) ->
                PromptIdentitySeed.owner p.IdentitySeed
                |> Option.map (fun (owner, _, _) -> owner)
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleLinked p)) -> Some p.ChildSessionId
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleWorkCompleted p)) -> Some p.Work.ChildSessionId
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleWorkConsumed p)) -> Some p.Work.ChildSessionId
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.HandleWorkAbandoned p)) -> Some p.Work.ChildSessionId
            | Fact.Agent(AgentFact.Execution(ExecutionFactCases.ChildWorkVoided p)) -> Some p.Work.ChildSessionId
            | _ -> None

        let parents =
            parents
            @ (dependencySession
               |> Option.bind (fun session ->
                   store.TryHead(EventStoreJournalCodec.encodeStreamId (StreamId.Session session)))
               |> Option.toList)

        let encoded =
            EventStoreJournalCodec.encode parents (JournalPayloadClosure.ofFact envelope.Fact) envelope

        store.Append [ encoded ]

    static member private initEnvelope (runtimeId: RuntimeId) (processId: int) (startedAt: DateTimeOffset) : Envelope =
        { RuntimeId = runtimeId
          LocalSeq = LocalSeq.create 1L
          ObservedAt = startedAt
          EventId = EventId.create (Guid.NewGuid().ToString("N"))
          Stream = StreamId.Workspace
          ProviderRun = None
          Fact =
            Fact.Runtime(
                RuntimeStarted
                    {| RuntimeId = runtimeId
                       ProcessId = processId
                       StartedAt = startedAt |}
            ) }

    /// Fresh runtime allocation is read-only. RuntimeStarted is appended lazily
    /// immediately before the first business fact.
    static member create
        (runtimeId: RuntimeId, processId: int, startedAt: DateTimeOffset, store: IEventStore)
        : Task<IJournalWriter * Envelope> =
        task {
            let init = EventStoreJournalWriter.initEnvelope runtimeId processId startedAt

            let writer =
                EventStoreJournalWriter(runtimeId, init, EventStoreBlobWriter.Create store, store)

            return writer :> IJournalWriter, init
        }

    /// Opening an existing workspace is read-only and does not force Current.
    /// WorkspaceEventStore may be deferred during plugin load; AgentJournal reads
    /// the canonical Current on first semantic consumption through TryCurrent.
    static member resumeOrCreate
        (runtimeId: RuntimeId, processId: int, startedAt: DateTimeOffset, store: IEventStore)
        : Task<Result<IJournalWriter * Envelope * ProjectionSet, FoldRejection>> =
        task {
            let init = EventStoreJournalWriter.initEnvelope runtimeId processId startedAt

            let writer =
                EventStoreJournalWriter(runtimeId, init, EventStoreBlobWriter.Create store, store)

            return Ok(writer :> IJournalWriter, init, Fold.empty)
        }

    member private this.CommitRuntimeStartedLocked() : Task<Result<unit, string>> =
        task {
            match! EventStoreJournalWriter.commitEnvelope store init with
            | Ok receipt when AppendReceipt.cutFor init.EventId receipt |> Option.isSome ->
                let cut = AppendReceipt.cutFor init.EventId receipt |> Option.get
                let reason = "RuntimeStarted semantic cut: " + cut.Reason
                FatalProcess.trip "runtime-started-semantic-cut" reason
                return Error reason
            | Ok _ ->
                runtimeStartedCommitted <- true
                return Ok()
            | Error error ->
                let reason = EventStoreJournalWriter.formatAppendError error
                this.Poison reason
                return Error reason
        }

    member private this.EnsureRuntimeStartedLocked() : Task<Result<unit, string>> =
        if runtimeStartedCommitted then
            Task.FromResult(Ok())
        else
            this.CommitRuntimeStartedLocked()

    static member private businessCommitResult (eventId: EventId) (envelope: Envelope) (receipt: AppendReceipt) =
        match AppendReceipt.cutFor envelope.EventId receipt with
        | Some cut -> Rejected(eventId, cut.Reason)
        | None -> Committed envelope

    member private this.CommitBusinessEnvelopeLocked
        (eventId: EventId, envelope: Envelope)
        : Task<CommitResult<Envelope>> =
        task {
            match! EventStoreJournalWriter.commitEnvelope store envelope with
            | Ok receipt ->
                currentSeq <- currentSeq + 1L
                return EventStoreJournalWriter.businessCommitResult eventId envelope receipt
            | Error error ->
                let reason = EventStoreJournalWriter.formatAppendError error
                this.Poison reason
                return CommitUnknown(eventId, WriteFailed reason)
        }

    member private this.AppendHealthyLocked
        (eventId: EventId)
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: Fact)
        : Task<CommitResult<Envelope>> =
        task {
            match! this.EnsureRuntimeStartedLocked() with
            | Error error -> return NotAttempted(eventId, WriterPoisoned("RuntimeStarted append failed: " + error))
            | Ok() ->
                let envelope: Envelope =
                    { RuntimeId = runtimeId
                      LocalSeq = LocalSeq.create currentSeq
                      ObservedAt = DateTimeOffset.UtcNow
                      EventId = eventId
                      Stream = stream
                      ProviderRun = providerRun
                      Fact = fact }

                return! this.CommitBusinessEnvelopeLocked(eventId, envelope)
        }

    member private this.RunAcceptedAppend
        (previous: Task)
        (eventId: EventId)
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: Fact)
        : Task<CommitResult<Envelope>> =
        task {
            do! previous

            // Admission happened while the writer was Open. A later Release may
            // close admission, but it must drain this accepted prefix. Only a
            // physical failure from an earlier accepted append can invalidate it.
            match this.PriorPoison eventId with
            | Some unavailable -> return unavailable
            | None -> return! this.AppendHealthyLocked eventId stream providerRun fact
        }

    member this.Append
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: Fact)
        : Task<CommitResult<Envelope>> =
        let eventId = EventId.create (Guid.NewGuid().ToString("N"))

        lock gate (fun () ->
            if closed then
                Task.FromResult(NotAttempted(eventId, WriterDisposed))
            elif closeTask.IsSome then
                Task.FromResult(NotAttempted(eventId, WriterClosing))
            elif firstFailure.IsSome then
                Task.FromResult(NotAttempted(eventId, WriterPoisoned firstFailure.Value))
            else
                let previous = serial
                let running = this.RunAcceptedAppend previous eventId stream providerRun fact

                serial <-
                    task {
                        let! _ = running
                        return ()
                    }

                running)

    member private _.FinishClose() = lock gate (fun () -> closed <- true)

    member private this.DrainAcceptedPrefix(acceptedPrefix: Task) : Task =
        task {
            try
                do! acceptedPrefix
            finally
                this.FinishClose()
        }
        :> Task

    member private this.StartCloseLocked() : Task =
        if closed || closeTask.IsSome then
            // Already closed or closing: nothing new to admit.
            Task.FromResult(()) :> Task
        else
            // Open or Poisoned: begin drain of the accepted append prefix.
            let running = this.DrainAcceptedPrefix serial
            closeTask <- Some running
            running

    member private this.BeginRelease() : Task =
        lock gate (fun () -> closeTask |> Option.defaultWith this.StartCloseLocked)

    member this.Release() = this.BeginRelease() |> ignore

    member this.ReleaseAsync() =
        JournalWriterDisposal.asValueTask (this.BeginRelease())

    interface IJournalWriter with
        member this.RuntimeId = this.RuntimeId
        member this.BlobWriter = this.BlobWriter
        member this.LocalSeq = this.LocalSeq
        member this.LastCommittedLocalSeq = this.LastCommittedLocalSeq
        member this.IsPoisoned = this.IsPoisoned
        member this.TryCurrent(key) = this.TryCurrent key
        member this.Append stream providerRun fact = this.Append stream providerRun fact
        member this.Release() = this.Release()
        member this.ReleaseAsync() = this.ReleaseAsync()
