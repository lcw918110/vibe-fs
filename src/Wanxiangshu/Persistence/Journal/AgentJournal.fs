namespace Wanxiangshu.Persistence.Journal

open System
open System.Threading
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Ablation

type JournalChange =
    { Revision: JournalRevision
      Envelope: Envelope }

type JournalAppendException(failure: JournalAppendFailure) =
    inherit Exception(JournalAppendFailure.describe failure)
    member _.Failure = failure

module private AgentJournalInternals =
    [<Import("JournalSession", "wanxiangli/journal-session")>]
    let private sessionClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private construct (_class: obj) (_options: obj) : obj = jsNative

    let create options = construct sessionClass options

    [<Emit("$0.append($1)")>]
    let append (_session: obj) (_request: obj) : Task<obj> = jsNative

#if FABLE_COMPILER
    [<Emit("$0")>]
    let asTask (operation: ValueTask) : Task = jsNative

    [<Emit("$0")>]
    let asValueTask (operation: Task) : ValueTask = jsNative
#else
    let asTask (operation: ValueTask) : Task = operation.AsTask()
    let asValueTask (operation: Task) = ValueTask(operation)
#endif

    let outcome (value: CommitResult<Envelope>) =
        match value with
        | Committed envelope ->
            createObj
                [ "kind" ==> "Committed"
                  "eventId" ==> EventId.value envelope.EventId
                  "sequence" ==> LocalSeq.value envelope.LocalSeq
                  "envelope" ==> envelope ]
        | Rejected(eventId, reason) ->
            createObj
                [ "kind" ==> "Rejected"
                  "eventId" ==> EventId.value eventId
                  "reason" ==> reason ]
        | CommitUnknown(eventId, failure) ->
            createObj
                [ "kind" ==> "CommitUnknown"
                  "eventId" ==> EventId.value eventId
                  "error" ==> failure ]
        | CommitResult.NoNewWriteReleaseFailed(eventId, failure) ->
            createObj
                [ "kind" ==> "NoNewWriteReleaseFailed"
                  "eventId" ==> EventId.value eventId
                  "error" ==> failure ]
        | NotAttempted(eventId, unavailable) ->
            createObj
                [ "kind" ==> "NotAttempted"
                  "eventId" ==> EventId.value eventId
                  "unavailable" ==> unavailable ]

    let failure (value: obj) : JournalAppendFailure =
        let eventId = EventId.create (unbox<string> value?eventId)

        match unbox<string> value?kind with
        | "CommitUnknown" -> WriteUnknown(eventId, unbox<JournalFailure> value?error)
        | "NoNewWriteReleaseFailed" -> JournalAppendFailure.NoNewWriteReleaseFailed(eventId, unbox value?error)
        | "NotAttempted" -> WriterUnavailable(eventId, unbox<JournalUnavailable> value?unavailable)
        | "Rejected" ->
            FactRejected(
                eventId,
                { Fact = "semantic-cut"
                  Reason = unbox<string> value?reason }
            )
        | kind -> invalidOp ("Unknown journal session failure: " + kind)

    let private onUnknown (failure: JournalFailure) =
        match failure with
        | StoreAppendUnknown evidence when not (List.isEmpty evidence.Prepared.Cuts) ->
            FatalProcess.trip
                "journal-semantic-cut"
                (sprintf
                    "semantic cut append settled unknown at %A: %s"
                    evidence.Primary.Phase
                    evidence.Primary.Cause.Message)
        | _ -> ()

    let onSettlement (value: obj) =
        match unbox<string> value?kind with
        | "CommitUnknown" -> onUnknown (unbox<JournalFailure> value?error)
        | "Rejected" -> FatalProcess.trip "journal-semantic-cut" (JournalAppendFailure.describe (failure value))
        | _ -> ()

    let change (value: obj) : JournalChange =
        { Revision = JournalRevision.create (unbox<int64> value?revision)
          Envelope = unbox<Envelope> value?envelope }

    let agentFactTag (fact: AgentFact) =
        match fact with
        | AgentFact.Prompt _ -> "AgentFact.Prompt"
        | AgentFact.ProviderFailure _ -> "AgentFact.ProviderFailure"
        | AgentFact.Execution _ -> "AgentFact.Execution"
        | AgentFact.Orchestrator _ -> "AgentFact.Orchestrator"
        | AgentFact.Companion _ -> "AgentFact.Companion"
        | AgentFact.Context _ -> "AgentFact.Context"
        | AgentFact.Host _ -> "AgentFact.Host"
        | AgentFact.Fission _ -> "AgentFact.Fission"
        | AgentFact.Delegation _ -> "AgentFact.Delegation"
        | AgentFact.ChatExecution _ -> "AgentFact.ChatExecution"
        | AgentFact.Cognition _ -> "AgentFact.Cognition"

    let rejectAblation (factTag: string) =
        FactRejected(
            EventId.create "ablation-denied",
            { Fact = factTag
              Reason = AblationGate.deniedFactPath }
        )

    let appendResult (value: obj) =
        if unbox<string> value?kind = "Committed" then
            Ok(unbox<ProjectionSet> value?current)
        else
            Error(failure value)

open AgentJournalInternals

/// 领域投影仍来自唯一的 Current，通知与恢复由库会话持有。
type AgentJournal internal (writer: IJournalWriter, initialProjection: ProjectionSet) =
    let session =
        create (
            createObj
                [ "initialRevision" ==> writer.LastCommittedLocalSeq
                  "writer"
                  ==> createObj
                          [ "append"
                            ==> Func<obj, Task<obj>>(fun request ->
                                let stream, providerRun, fact =
                                    unbox<StreamId * ProviderRunIdentity option * Fact> request

                                task {
                                    let! committed = writer.Append stream providerRun fact
                                    return outcome committed
                                })
                            "isPoisoned" ==> Func<bool>(fun () -> writer.IsPoisoned)
                            "release" ==> Action(writer.Release)
                            "releaseAsync" ==> Func<Task>(fun () -> writer.ReleaseAsync() |> asTask) ]
                  "readCurrent"
                  ==> Func<obj>(fun () ->
                      match writer.TryCurrent "Journal" with
                      | Some current -> current
                      | None -> box initialProjection)
                  "refreshCurrent" ==> Func<obj>(fun () -> box (writer.RefreshCurrent()))
                  "onSettlement" ==> Action<obj>(onSettlement) ]
        )

    member _.Writer = writer
    member _.RuntimeId = writer.RuntimeId
    member _.WriteBlob(content: string) : Task<Result<BlobWriteReceipt, string>> = writer.BlobWriter.Write content
    member _.RefreshCurrent() : Result<unit, string> = session?refreshCurrent ()
    member _.IsPoisoned: bool = session?isPoisoned ()
    member _.Snapshot: ProjectionSet = session?snapshot ()

    member _.Revision: JournalRevision =
        JournalRevision.create (unbox<int64> (session?revision ()))

    member _.SnapshotWithRevision: ProjectionSet * JournalRevision =
        let value: obj = session?snapshotWithRevision ()
        unbox<ProjectionSet> value?current, JournalRevision.create (unbox<int64> value?revision)

    member _.EnqueueRecovery(start: unit -> Task) : unit =
        session?enqueueRecovery (Func<Task>(start))

    member _.RunOrAwaitRecovery() : Task = session?runOrAwaitRecovery ()

    member _.AwaitChangeFromOrCancel
        (fromRevision: JournalRevision, cancellation: CancellationToken)
        : Task<JournalChange option> =
        task {
            let registerCancel =
                Func<Action, Action>(fun cancel ->
                    let registration = cancellation.Register(fun () -> cancel.Invoke()) :> IDisposable
                    Action(fun () -> registration.Dispose()))

            let! value = (session?awaitChange (JournalRevision.value fromRevision, registerCancel): Task<obj>)
            return if isNull value then None else Some(change value)
        }

    member this.AwaitChangeFrom(fromRevision: JournalRevision) : Task<JournalChange> =
        task {
            let! changed = this.AwaitChangeFromOrCancel(fromRevision, CancellationToken.None)

            match changed with
            | Some committed -> return committed
            | None -> return failwith "unreachable: uncancelled journal wait"
        }

    member _.AppendAgent
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: AgentFact)
        : Task<Result<ProjectionSet, JournalAppendFailure>> =
        task {
            let tag = agentFactTag fact

            if AblationGate.factDenied (AblationGate.registry ()) tag then
                return Error(rejectAblation tag)
            else
                let! value = append session (box (stream, providerRun, Fact.Agent fact))

                return appendResult value
        }

    interface IDisposable with
        member _.Dispose() : unit = session?release ()

    interface IAsyncDisposable with
        member _.DisposeAsync() = asValueTask (session?releaseAsync ())

module AgentJournal =
    let createFromProjection
        (writer: IJournalWriter)
        (projection: ProjectionSet)
        : Result<AgentJournal, FoldRejection> =
        Ok(new AgentJournal(writer, projection))

    let appendAgent
        (stream: StreamId)
        (providerRun: ProviderRunIdentity option)
        (fact: AgentFact)
        (journal: AgentJournal)
        : Task<Result<ProjectionSet, JournalAppendFailure>> =
        journal.AppendAgent stream providerRun fact

    let snapshot (journal: AgentJournal) : ProjectionSet = journal.Snapshot
    let revision (journal: AgentJournal) : JournalRevision = journal.Revision
    let snapshotWithRevision (journal: AgentJournal) : ProjectionSet * JournalRevision = journal.SnapshotWithRevision

    let awaitChangeFrom (fromRevision: JournalRevision) (journal: AgentJournal) : Task<JournalChange> =
        journal.AwaitChangeFrom fromRevision

    let awaitChangeFromOrCancel
        (fromRevision: JournalRevision)
        (cancellation: CancellationToken)
        (journal: AgentJournal)
        : Task<JournalChange option> =
        journal.AwaitChangeFromOrCancel(fromRevision, cancellation)

    let refreshCurrent (journal: AgentJournal) : Result<unit, string> = journal.RefreshCurrent()

    let handleProjection (journal: AgentJournal) (sessionId: SessionId) : AgentLinkageProjection =
        AgentProjection.tryFind sessionId (snapshot journal).AgentProjections
        |> Option.bind (fun session -> session.Handles)
        |> Option.defaultValue HandleProjection.empty

    let runtimeId (journal: AgentJournal) : RuntimeId = journal.RuntimeId
    let isPoisoned (journal: AgentJournal) : bool = journal.IsPoisoned
