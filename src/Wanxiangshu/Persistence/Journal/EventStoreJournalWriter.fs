namespace Wanxiangshu.Persistence.Journal

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Text
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
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
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionAdmitted p)) -> pair p.OwnerViewRef p.OwnerViewDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionLaneMaterialized p)) -> pair p.ViewRef p.ViewDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionCompletionCaptured p)) ->
                pair p.PayloadRef p.PayloadDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionTakeoverClaimed p)) ->
                pair p.AggregateViewRef p.AggregateViewDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionTakeoverStarted p)) ->
                pair p.AggregateViewRef p.AggregateViewDigest
            | Fact.Agent(AgentFact.Fission(FissionFactCases.FissionConverged p)) ->
                pair p.AggregateViewRef p.AggregateViewDigest
            | _ -> []

        PayloadRefs.canonicalize refs

module private JournalWriterDisposal =
#if FABLE_COMPILER
    [<Emit("$0")>]
    let asValueTask (operation: Task) : ValueTask = jsNative
#else
    let asValueTask (operation: Task) = ValueTask(operation)
#endif

module private JournalWriterValues =
    [<Import("JournalWriter", "wanxiangli/journal-session")>]
    let private writerClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (_class: obj) (_options: obj) : obj = jsNative

    let create options = construct writerClass options

    [<Emit("$0.append($1)")>]
    let append (_writer: obj) (_request: obj) : Task<obj> = jsNative

    let cut (value: SemanticCut) =
        createObj
            [ "rule" ==> value.Rule
              "failedEventId" ==> EventId.value value.FailedEventId
              "reason" ==> value.Reason
              "cutEventId" ==> EventId.value value.CutEventId
              "original" ==> value ]

    let errorToNative (error: AppendError) =
        let kind, fields =
            match error with
            | AppendError.StorageInvalid _ -> "StorageInvalid", []
            | AppendError.AppendNotAttempted _ -> "AppendNotAttempted", []
            | AppendError.CommitUnknown _ -> "CommitUnknown", []
            | AppendError.NoNewWriteReleaseFailed _ -> "NoNewWriteReleaseFailed", []
            | AppendError.AppendFailed reason -> "AppendFailed", [ "reason" ==> reason ]
            | AppendError.SemanticCut value -> "SemanticCut", [ "cut" ==> cut value ]

        createObj ([ "kind" ==> kind; "original" ==> error ] @ fields)

    let errorFromNative (value: obj) : AppendError =
        let original: obj = value?original

        if not (isNull original) then
            unbox original
        elif unbox<string> value?kind = "SemanticCut" then
            AppendError.SemanticCut(unbox<SemanticCut> value?cut?original)
        else
            invalidOp "A journal storage failure must retain its original value"

    let receipt (value: AppendReceipt) =
        createObj [ "cuts" ==> (value.Cuts |> List.map cut |> List.toArray) ]

    let unavailable (value: obj) : JournalUnavailable =
        match unbox<string> value?kind with
        | "WriterDisposed" -> WriterDisposed
        | "WriterClosing" -> WriterClosing
        | "WriterRefused" -> WriterRefused(unbox<string> value?reason)
        | "WriterPoisoned" ->
            WriterPoisoned
                { FailedEventId = EventId.create (unbox<string> value?failedEventId)
                  Error = errorFromNative value?error }
        | kind -> invalidOp ("Unknown journal writer availability: " + kind)

    let private unknownOutcome eventId error =
        match errorFromNative error with
        | AppendError.CommitUnknown evidence -> CommitUnknown(eventId, StoreAppendUnknown evidence)
        | _ -> invalidOp "A journal unknown result must retain its storage uncertainty"

    let private releaseOutcome eventId error =
        match errorFromNative error with
        | AppendError.NoNewWriteReleaseFailed evidence -> CommitResult.NoNewWriteReleaseFailed(eventId, evidence)
        | _ -> invalidOp "A journal release result must retain its storage failure"

    let outcome (value: obj) : CommitResult<Envelope> =
        let eventId = EventId.create (unbox<string> value?eventId)

        match unbox<string> value?kind with
        | "Committed" -> Committed(unbox<Envelope> value?envelope)
        | "Rejected" -> Rejected(eventId, unbox<string> value?reason)
        | "NotAttempted" -> NotAttempted(eventId, unavailable value?unavailable)
        | "CommitUnknown" -> unknownOutcome eventId value?error
        | "NoNewWriteReleaseFailed" -> releaseOutcome eventId value?error
        | kind -> invalidOp ("Unknown journal writer result: " + kind)

    let private initializationFailure error =
        match errorFromNative error with
        | AppendError.CommitUnknown evidence when not (List.isEmpty evidence.Prepared.Cuts) ->
            FatalProcess.trip
                "runtime-started-semantic-cut"
                (sprintf
                    "semantic cut append settled unknown at %A: %s"
                    evidence.Primary.Phase
                    evidence.Primary.Cause.Message)
        | _ -> ()

    let onSettlement (notice: obj) =
        match unbox<string> notice?kind with
        | "initialization-cut" ->
            FatalProcess.trip
                "runtime-started-semantic-cut"
                ("RuntimeStarted semantic cut: " + unbox<string> notice?cut?reason)
        | "initialization-error" -> initializationFailure notice?error
        | kind -> invalidOp ("Unknown journal initialization settlement: " + kind)

/// 保留事实、父依赖与 Blob 适配，写入寿命由库持有。
[<Sealed>]
type EventStoreJournalWriter private (runtimeId: RuntimeId, init: Envelope, blobWriter: IBlobWriter, store: IEventStore)
    =
    let writer =
        JournalWriterValues.create (
            createObj
                [ "initialization"
                  ==> createObj [ "eventId" ==> EventId.value init.EventId; "envelope" ==> init ]
                  "newEventId" ==> Func<string>(fun () -> Guid.NewGuid().ToString("N"))
                  "now" ==> Func<obj>(fun () -> box DateTimeOffset.UtcNow)
                  "makeEnvelope"
                  ==> Func<obj, obj>(fun input ->
                      let stream, providerRun, fact =
                          unbox<StreamId * ProviderRunIdentity option * Fact> input?request

                      box
                          { RuntimeId = runtimeId
                            LocalSeq = LocalSeq.create (unbox<int64> input?sequence)
                            ObservedAt = unbox<DateTimeOffset> input?observedAt
                            EventId = EventId.create (unbox<string> input?eventId)
                            Stream = stream
                            ProviderRun = providerRun
                            Fact = fact })
                  "commitEnvelope"
                  ==> Func<obj, Task<obj>>(fun envelope ->
                      task {
                          match! EventStoreJournalWriter.commitEnvelope store (unbox<Envelope> envelope) with
                          | Ok receipt ->
                              return createObj [ "ok" ==> true; "value" ==> JournalWriterValues.receipt receipt ]
                          | Error error ->
                              return createObj [ "ok" ==> false; "error" ==> JournalWriterValues.errorToNative error ]
                      })
                  "onSettlement" ==> Action<obj>(JournalWriterValues.onSettlement) ]
        )

    member _.RuntimeId = runtimeId
    member _.BlobWriter = blobWriter
    member _.LocalSeq: int64 = writer?localSeq ()

    member _.LastCommittedLocalSeq: int64 = writer?lastCommittedLocalSeq ()

    member _.IsPoisoned: bool = writer?isPoisoned ()

    member _.TryCurrent(key: string) = store.TryCurrent key

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

    member _.Append
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: Fact)
        : Task<CommitResult<Envelope>> =
        let running = JournalWriterValues.append writer (box (stream, providerRun, fact))

        task {
            let! value = running
            return JournalWriterValues.outcome value
        }

    member _.Release() : unit = writer?release ()

    member _.ReleaseAsync() =
        JournalWriterDisposal.asValueTask (writer?releaseAsync ())

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
        member this.RefreshCurrent() = store.ReloadLocal()
