namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation.HostSession

module ScopeReset =
    val alreadyCommitted: journalOpt: AgentJournal option -> ctx: HostToolContext -> bool
    val commit:
        journalOpt: AgentJournal option ->
        snapshotPort: ISessionSnapshotPort option ->
        ctx: HostToolContext ->
        objective: string ->
        oldVerbatim: string ->
        retentionBudget: int ->
            Task<Result<string, string>>
