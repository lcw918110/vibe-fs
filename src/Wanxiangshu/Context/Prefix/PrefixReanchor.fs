namespace Wanxiangshu.Context.Prefix

open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Context.Companion
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Persistence.Journal.JournalOutcome

/// Commit the next Prefix candidate from the journal's current projection.
module PrefixReanchor =
    let observed
        (journal: AgentJournal)
        (sessionId: SessionId)
        (observed: ProviderRunIdentity list)
        : Task<Result<ProviderRunIdentity option, string>> =
        task {
            let snapshot = AgentJournal.snapshot journal
            let epoch =
                AgentProjection.tryFind sessionId snapshot.AgentProjections
                |> Option.bind (fun session -> session.PrefixEpoch)
                |> Option.defaultValue PrefixEpochProjection.empty
            match PrefixEpochProjection.proposeCompaction observed epoch with
            | None -> return Ok None
            | Some candidate ->
                let run = candidate.CompactionRun
                let fact =
                    ContextFact.ContextReanchored
                        {| SessionId = sessionId
                           PreviousEpochId = candidate.PreviousEpochId
                           NextEpochId = candidate.NextEpochId
                           ObservedCompactionRun = run |}
                let! appended = AgentJournal.appendAgent (StreamId.Session sessionId) (Some run) fact journal
                return appended |> Result.map (fun _ -> Some run) |> Result.mapError JournalAppendFailure.describe
        }
