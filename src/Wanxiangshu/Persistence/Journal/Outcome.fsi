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

    type JournalUnavailable =
        | WriterPoisoned of firstFailure: JournalAppendPoison
        | WriterRefused of reason: string
        | WriterClosing
        | WriterDisposed

    type CommitResult<'e> =
        | Committed of 'e
        | Rejected of EventId * reason: string
        | NotAttempted of EventId * JournalUnavailable
        | CommitUnknown of EventId * JournalFailure
        | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure

    type JournalAppendFailure =
        | WriteUnknown of EventId * JournalFailure
        | WriterUnavailable of EventId * JournalUnavailable
        | FactRejected of EventId * FoldRejection
        | NoNewWriteReleaseFailed of EventId * AppendNoNewWriteReleaseFailure

    module JournalAppendFailure =
        val describe: failure: JournalAppendFailure -> string
