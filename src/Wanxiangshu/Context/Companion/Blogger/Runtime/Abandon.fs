namespace Wanxiangshu.Context.Companion.Blogger.Runtime

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// context-compression-024 single writer for BloggerRequestAbandoned (protocol fail + send-fail + crash-A).
/// Coordinator / EnforcerHost call here; they do not construct the fact.
module BloggerAbandon =

    /// Abandon by explicit RequestId (crash-window A / supersede known open).
    let byRequestId
        (journal: AgentJournal)
        (requestId: BloggerRequestId)
        (mainSessionId: SessionId)
        (bloggerSessionId: SessionId)
        (reason: string)
        : Task =
        task {
            let fact =
                ContextFact.BloggerRequestAbandoned
                    {| RequestId = requestId
                       MainSessionId = mainSessionId
                       BloggerSessionId = bloggerSessionId
                       Reason = reason |}

            match! AgentJournal.appendAgent (StreamId.Session mainSessionId) None fact journal with
            | Ok _ -> return ()
            | Error failure -> return raise (JournalAppendException failure)
        }

    /// Prefer typed context RequestId; else abandon the open materialization for this Blogger.
    let openRequest
        (journal: AgentJournal)
        (mainSessionId: SessionId)
        (bloggerSessionId: SessionId)
        (preferred: BloggerRequestContext option)
        (reason: string)
        : Task =
        let requestId =
            match preferred with
            | Some ctx -> Some(BloggerRequestContext.requestId ctx)
            | None ->
                (AgentJournal.snapshot journal).AgentProjections.Sessions
                |> Map.tryFind mainSessionId
                |> Option.bind (fun session -> session.BloggerCycles)
                |> Option.bind (fun cycles -> BloggerCycleProjection.tryOpenByBlogger bloggerSessionId cycles)
                |> Option.map (fun openReq -> openReq.RequestId)

        match requestId with
        | None -> Task.FromResult(()) :> Task
        | Some rid -> byRequestId journal rid mainSessionId bloggerSessionId reason

    /// crash-reconciliation-020 / context-compression-024: the Blog materializations a
    /// dead runtime left open.
    ///
    /// Every request still open at load belongs to a runtime that is gone: no live
    /// execution can own it, and its producer (the parked Blogger transform) died with
    /// it. Left open it blocks the catch-up forever — the coordinator only stages new
    /// material for a producer that no longer exists and never materializes a fresh
    /// request, so the Blogger never ingests the raw tail again (observed live: coverage
    /// frozen at the last fold while the main loop kept growing).
    ///
    /// `liveFlight` answers "does this process still own that exact request". Another
    /// plugin instance in this process shares the journal and the flight registry, so a
    /// request it is actively producing is not stale and must stay open.
    let staleOpenRequests
        (liveFlight: SessionId -> BloggerRequestId -> bool)
        (projections: AgentProjectionSet)
        : (SessionId * OpenBloggerRequest) list =
        projections.Sessions
        |> Map.toList
        |> List.collect (fun (mainSessionId, session) ->
            session.BloggerCycles
            |> Option.map (fun cycles ->
                cycles.OpenByRequestId
                |> Map.toList
                |> List.map (fun (_, openReq) -> mainSessionId, openReq))
            |> Option.defaultValue [])
        |> List.filter (fun (_, openReq) -> not (liveFlight openReq.BloggerSessionId openReq.RequestId))

    /// Settle those requests once, at load, before any session runs. Abandoning is the
    /// whole settlement: the interrupted cycle produced nothing, so it owes nothing.
    /// Returns the Blogger session ids whose open request was abandoned here, so the
    /// load phase can decide their same-source accepted executions
    /// (provider-attempt-recovery-024).
    let settleStaleOpenAtLoad
        (liveFlight: SessionId -> BloggerRequestId -> bool)
        (journal: AgentJournal)
        : Task<SessionId list> =
        task {
            let projections = (AgentJournal.snapshot journal).AgentProjections
            let stale = staleOpenRequests liveFlight projections

            for mainSessionId, openReq in stale do
                do! byRequestId journal openReq.RequestId mainSessionId openReq.BloggerSessionId "stale-open-at-load"

            return
                stale
                |> List.map (fun (_, openReq) -> openReq.BloggerSessionId)
                |> List.distinct
        }
