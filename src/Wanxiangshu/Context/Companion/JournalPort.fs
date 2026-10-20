namespace Wanxiangshu.Context.Companion

open System.Threading.Tasks
open Wanxiangshu.Context.Trace
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation.Identity

type AgentJournalCompanionPort(journal: AgentJournal) =
    let loadSession
        (session: Wanxiangshu.Composition.Durable.SessionAgentProjection)
        : Task<Result<CompanionMemory option, string>> =
        task { return Ok(Some { XTrace = session.XTrace |> Option.defaultValue XTraceProjection.empty }) }

    interface ICompanionDurablePort with
        member _.Load(sessionId: SessionId) : Task<Result<CompanionMemory option, string>> =
            task {
                let projection = AgentJournal.snapshot journal

                match Map.tryFind sessionId projection.AgentProjections.Sessions with
                | None -> return Ok None
                | Some session -> return! loadSession session
            }
