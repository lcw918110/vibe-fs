namespace Wanxiangshu.Persistence.Journal

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.EventStore

module JournalOutcome =

    type JournalFailure =
        | WriteFailed of reason: string
        | FlushFailed of reason: string
        | StoreAppendUnknown of AppendCommitUnknownEvidence

    type JournalAppendPoison =
        { FailedEventId: EventId
          Error: AppendError }

    /// A writer that never attempted the caller's event. This is deliberately
    /// distinct from CommitUnknown: lifecycle refusal is known-not-committed.
    type JournalUnavailable =
        | WriterPoisoned of firstFailure: JournalAppendPoison
        /// Pure pre-write refusal: no bytes were written, the writer is not
        /// poisoned, and a fresh caller decision may append again.
        | WriterRefused of reason: string
        | WriterClosing
        | WriterDisposed

    /// The caller's fact and a failed lazy initialization have separate fates.
    /// A durable semantic rejection and a no-new-write release failure also stay explicit.
    type CommitResult<'e> =
        | Committed of 'e
        /// Bytes are durable, but the business rule cut this event and immediately
        /// wrote a durable reset fact. This invocation failed; the writer remains usable.
        | Rejected of EventId * reason: string
        | NotAttempted of EventId * JournalUnavailable
        | CommitUnknown of EventId * JournalFailure
        | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure

    /// Physical fate of a single journal append.
    ///
    /// WriteUnknown = durable-state indeterminate; WriterUnavailable = the writer
    /// refused without attempting (known-not-committed); FactRejected = the line
    /// bytes are durable but the semantic fold cut it — the append boundary treats
    /// it as fatal evidence rather than retryable.
    type JournalAppendFailure =
        | WriteUnknown of EventId * JournalFailure
        | WriterUnavailable of EventId * JournalUnavailable
        | FactRejected of EventId * FoldRejection
        | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure

    module JournalAppendFailure =

        let describe (failure: JournalAppendFailure) : string =
            match failure with
            | WriteUnknown(eventId, WriteFailed reason) ->
                sprintf "append outcome unknown for %s: write failed: %s" (EventId.value eventId) reason
            | WriteUnknown(eventId, FlushFailed reason) ->
                sprintf "append outcome unknown for %s: flush failed: %s" (EventId.value eventId) reason
            | WriteUnknown(eventId, StoreAppendUnknown evidence) ->
                sprintf
                    "append outcome unknown for %s at %A: %s"
                    (EventId.value eventId)
                    evidence.Primary.Phase
                    evidence.Primary.Cause.Message
            | WriterUnavailable(eventId, WriterPoisoned firstFailure) ->
                sprintf
                    "append not attempted for %s: writer poisoned by prior failure: %s"
                    (EventId.value eventId)
                    (sprintf
                        "%s: %s"
                        (EventId.value firstFailure.FailedEventId)
                        (AppendError.describe firstFailure.Error))
            | WriterUnavailable(eventId, WriterRefused reason) ->
                sprintf "append refused for %s before any write: %s" (EventId.value eventId) reason
            | NoNewWriteReleaseFailed(eventId, failure) ->
                sprintf
                    "no new journal write for %s; store release failed: %s"
                    (EventId.value eventId)
                    failure.Cause.Message
            | WriterUnavailable(eventId, WriterClosing) ->
                sprintf "append not attempted for %s: writer is closing" (EventId.value eventId)
            | WriterUnavailable(eventId, WriterDisposed) ->
                sprintf "append not attempted for %s: writer is disposed" (EventId.value eventId)
            | FactRejected(eventId, rejection) ->
                sprintf
                    "journal semantic cut at %s: fact \'%s\' rejected: %s"
                    (EventId.value eventId)
                    rejection.Fact
                    rejection.Reason
