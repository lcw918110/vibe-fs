namespace Wanxiangshu.Verification

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Persistence.Journal

/// EventStore-only temporal proofs for the physical journal writer boundary.
/// Kept separate from TemporalSurface so verification never forms a dual-write bridge.
module EventStoreWriterSurface =

    type private ControlledEventStore(append: EventEnvelope list -> Task<Result<AppendReceipt, AppendError>>) =
        interface IEventStore with
            member _.Append events = append events

            member _.WritePayload _ =
                Task.FromResult<Result<PayloadRef, string>>(Error "payload write unavailable")

            member _.ReadPayload _ =
                Task.FromResult<Result<byte[] option, string>>(Ok None)

            member _.TryCurrent _ = None
            member _.TryEvent _ = None
            member _.TryHeads _ = []
            member _.TryHead _ = None
            member _.AllHeads() = []
            member _.ReloadLocal() = Ok()

    let private writerFact (sessionId: SessionId) =
        Fact.Agent(
            AgentFact.Companion(
                CompanionFactCases.OpeningPromptCaptured
                    {| SessionId = sessionId
                       AssignmentText = "test"
                       AuthoritativeRequirements = []
                       ProviderRun = None |}
            )
        )

    let private commitResultName (result: CommitResult<Envelope>) =
        match result with
        | Committed _ -> "Committed"
        | Rejected _ -> "Rejected"
        | CommitUnknown(_, WriteFailed reason) -> "CommitUnknown:" + reason
        | CommitUnknown(_, FlushFailed reason) -> "CommitUnknownFlush:" + reason
        | CommitUnknown(_, StoreAppendUnknown evidence) -> "CommitUnknown:" + evidence.Primary.Cause.Message
        | CommitResult.NoNewWriteReleaseFailed _ -> "NoNewWriteReleaseFailed"
        | NotAttempted(_, WriterPoisoned failure) ->
            match failure.Error with
            | AppendError.CommitUnknown evidence -> "WriterPoisoned:" + evidence.Primary.Cause.Message
            | error -> "WriterPoisoned:" + AppendError.describe error
        | NotAttempted(_, WriterClosing) -> "WriterClosing"
        | NotAttempted(_, WriterDisposed) -> "WriterDisposed"

    /// PERSIST lifecycle proof: release closes admission but drains every append
    /// admitted while Open; later appends are known-not-attempted.
    let writerReleaseDrainScenario () : Task<obj> =
        task {
            let appendEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let releaseAppend =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            // DSL-MUTABLE: algorithm-scratch — controlled-store append call counter.
            let mutable appendCalls = 0

            let append (_: EventEnvelope list) =
                appendCalls <- appendCalls + 1

                if appendCalls = 1 then
                    task {
                        AsyncSupport.trySetResult appendEntered () |> ignore
                        do! releaseAppend.Task
                        return Ok AppendReceipt.empty
                    }
                else
                    Task.FromResult(Ok AppendReceipt.empty)

            let store = ControlledEventStore(append) :> IEventStore
            let sessionId = SessionId.create "writer-release-drain"

            let! writer, _ =
                EventStoreJournalWriter.create (
                    RuntimeId.create "writer_release_runtime",
                    4242,
                    DateTimeOffset.Parse "2026-08-17T00:00:00Z",
                    store
                )

            let first = writer.Append (StreamId.Session sessionId) None (writerFact sessionId)
            do! appendEntered.Task

            // DSL-MUTABLE: algorithm-scratch — scenario close-completion observation.
            let mutable closeCompleted = false

            let close =
                task {
                    do! writer.ReleaseAsync()
                    closeCompleted <- true
                }

            let closeBlockedOnAcceptedAppend = not closeCompleted
            let closing = writer.Append (StreamId.Session sessionId) None (writerFact sessionId)

            AsyncSupport.trySetResult releaseAppend () |> ignore
            let! firstResult = first
            let! closingResult = closing
            do! close
            let! disposedResult = writer.Append (StreamId.Session sessionId) None (writerFact sessionId)

            return
                box
                    {| acceptedPrefix = commitResultName firstResult
                       duringClose = commitResultName closingResult
                       afterClose = commitResultName disposedResult
                       closeBlockedOnAcceptedAppend = closeBlockedOnAcceptedAppend
                       appendCalls = appendCalls |}
        }

    /// PERSIST forensic proof: a physical first failure poisons the writer once;
    /// later calls never hit storage and preserve the original failure text.
    let writerPoisonPreservesFirstFailureScenario () : Task<obj> =
        task {
            // DSL-MUTABLE: algorithm-scratch — controlled-store append call counter.
            let mutable appendCalls = 0

            let failure = InvalidOperationException "disk exploded"

            let append (events: EventEnvelope list) =
                appendCalls <- appendCalls + 1

                match appendCalls with
                | 1 -> Task.FromResult(Ok AppendReceipt.empty)
                | 2 ->
                    Task.FromResult(
                        Error(
                            AppendError.CommitUnknown
                                { Requested = events
                                  Prepared = { DurableEvents = events; Cuts = [] }
                                  Primary =
                                    { Phase = AppendPhase.PhysicalAppend
                                      Cause = failure }
                                  CleanupFailures = [] }
                        )
                    )
                | _ -> Task.FromResult(Ok AppendReceipt.empty)

            let store = ControlledEventStore(append) :> IEventStore
            let sessionId = SessionId.create "writer-poison-first-failure"

            let! writer, _ =
                EventStoreJournalWriter.create (
                    RuntimeId.create "writer_poison_runtime",
                    4242,
                    DateTimeOffset.Parse "2026-08-17T00:00:00Z",
                    store
                )

            let! first = writer.Append (StreamId.Session sessionId) None (writerFact sessionId)
            let! second = writer.Append (StreamId.Session sessionId) None (writerFact sessionId)
            do! writer.ReleaseAsync()

            return
                box
                    {| first = commitResultName first
                       second = commitResultName second
                       appendCalls = appendCalls |}
        }
