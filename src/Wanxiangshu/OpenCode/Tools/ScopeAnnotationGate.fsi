namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Persistence.Journal

/// Host-side Scope annotation gate (schema injection + per-call validation).
/// Semantics live in wanxiangli/scope-retention and common law «Scope».
module ScopeAnnotationGate =
    val apply:
        toolName: string ->
        args: HostToolArguments ->
        ctx: HostToolContext ->
        epochOf: (HostToolContext -> string) ->
            Result<obj, string>

    val wrap:
        toolName: string ->
        epochOf: (HostToolContext -> string) ->
        journalOpt: AgentJournal option ->
        snapshotPort: ISessionSnapshotPort option ->
        inner: (HostToolArguments -> HostToolContext -> Task<string>) ->
            (HostToolArguments -> HostToolContext -> Task<string>)

    val injectArgument: factory: HostToolFactory -> spec: ToolSpec -> ToolSpec
