namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Host.Domain

/// Host-neutral sync-delegate batching rules: which sibling calls one provider
/// turn batched together, and the deferred-inspection ledger whose settled
/// results replace the placeholder tool outputs. Every reply is an instruction
/// line; a host adapter chooses the wire encoding and the message mutation.
[<RequireQualifiedAccess>]
module SyncDelegateBatchRules =

    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val MergedReference: string = "tool/sync-delegate/merged-reference"

    /// The more complete of the runtime-observed batch and the batch read back
    /// from the caller's provider-run message; None unless the caller names a
    /// session, provider run and tool call.
    val resolve:
        runtime: SyncDelegateRuntime ->
        snapshot: ISessionSnapshotPort option ->
        role: SyncDelegateRole ->
        caller: DelegationCaller ->
            Task<SyncDelegateBatch option>

    /// Stages one engineer charge for batch execution; returns the acceptance instruction.
    val stageDeferredInspection:
        sessionId: string -> callId: ToolCallId -> charge: string -> keywords: string -> estimate: int option -> string

    /// Runs every charge staged for the session as one batch and records each
    /// call's result instruction as its replacement.
    val settleDeferredInspections: runtime: SyncDelegateRuntime -> sessionId: string -> Task<unit>

    val hasReplacedResults: unit -> bool

    /// The settled result instruction that replaces the tool output of `callId`.
    val tryReplacedResult: callId: string -> string option

    /// Cleans up pending deferred calls and settled replacements for one session.
    val clearSession: sessionId: string -> unit

    /// Cleans up all pending deferred calls and settled replacements across all sessions.
    val clearAll: unit -> unit
