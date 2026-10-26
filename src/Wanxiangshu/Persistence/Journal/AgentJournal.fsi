namespace Wanxiangshu.Persistence.Journal

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

type JournalChange =
    { Revision: JournalRevision
      Envelope: Envelope }

type JournalAppendException =
    inherit Exception
    new: failure: JournalAppendFailure -> JournalAppendException
    member Failure: JournalAppendFailure


type AgentJournal =
    interface IAsyncDisposable
    interface IDisposable

    internal new: writer: IJournalWriter * initialProjection: ProjectionSet -> AgentJournal

    member AppendAgent:
        stream: StreamId ->
        providerRun: ProviderRunIdentity option ->
        fact: AgentFact ->
            Task<Result<ProjectionSet, JournalAppendFailure>>


    member AwaitChangeFrom: fromRevision: JournalRevision -> Task<JournalChange>

    member AwaitChangeFromOrCancel:
        fromRevision: JournalRevision * cancellation: CancellationToken -> Task<JournalChange option>

    member WriteBlob: content: string -> Task<Result<BlobWriteReceipt, string>>
    member IsPoisoned: bool
    member Revision: JournalRevision
    member RuntimeId: RuntimeId
    member Snapshot: ProjectionSet
    member SnapshotWithRevision: ProjectionSet * JournalRevision
    member Writer: IJournalWriter
    member RefreshCurrent: unit -> Result<unit, string>

    /// crash-reconciliation-020: register one restart-recovery body; the first
    /// RunOrAwaitRecovery caller starts it, all callers await the same work.
    member EnqueueRecovery: start: (unit -> Task) -> unit

    member RunOrAwaitRecovery: unit -> Task

module AgentJournal =
    val createFromProjection: writer: IJournalWriter -> projection: ProjectionSet -> Result<AgentJournal, FoldRejection>

    val appendAgent:
        stream: StreamId ->
        providerRun: ProviderRunIdentity option ->
        fact: AgentFact ->
        journal: AgentJournal ->
            Task<Result<ProjectionSet, JournalAppendFailure>>


    val refreshCurrent: journal: AgentJournal -> Result<unit, string>
    val snapshot: journal: AgentJournal -> ProjectionSet
    val revision: journal: AgentJournal -> JournalRevision
    val snapshotWithRevision: journal: AgentJournal -> ProjectionSet * JournalRevision
    val awaitChangeFrom: fromRevision: JournalRevision -> journal: AgentJournal -> Task<JournalChange>

    val awaitChangeFromOrCancel:
        fromRevision: JournalRevision ->
        cancellation: CancellationToken ->
        journal: AgentJournal ->
            Task<JournalChange option>

    val handleProjection: journal: AgentJournal -> sessionId: SessionId -> AgentLinkageProjection
    val runtimeId: journal: AgentJournal -> RuntimeId
    val isPoisoned: journal: AgentJournal -> bool
