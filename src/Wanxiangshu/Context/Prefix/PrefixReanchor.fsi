namespace Wanxiangshu.Context.Prefix

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal

module PrefixReanchor =
    val observed:
        journal: AgentJournal ->
        sessionId: SessionId ->
        observed: ProviderRunIdentity list ->
            Task<Result<ProviderRunIdentity option, string>>
