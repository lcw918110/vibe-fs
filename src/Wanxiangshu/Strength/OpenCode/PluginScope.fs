namespace Wanxiangshu.Strength.OpenCode

open Fable.Core
open System.Collections.Generic
open Fable.Core
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Strength
open Wanxiangshu.Strength.Replica

/// STRENGTH-*: decision-local replica ownership/capability registry plus the
/// process-lifetime fuse for one plugin instance. Durable causality stays in
/// EventStore; this is only live physical-session state (STRENGTH-014).
/// When acquired for a git common-dir runtime key, multiple plugin instances (e.g.
/// root workspace and manager worktrees) share the same live registry and attached
/// replica coordinator, refcounted across plugin instance lifecycles.
[<AttachMembers>]
type PluginStrengthScope(sharedKey: string option) =
    // STRENGTH-014: decision-local replica ownership/capability registry. Durable
    // causality remains in EventStore; this is only live physical-session state.
    let runtimeRegistry = StrengthRuntime()
    // STRENGTH-004: physical coordinator is attached after Host ports are wired.
    // The Session StrengthRuntime above remains the sole live ownership/capability registry.

    // DSL-MUTABLE: resource — attached process-local Replica coordinator
    let mutable strengthReplicaRuntime: StrengthReplicaRuntime option = None

    /// speculative-investigation-011: Fuse is a process-wide monotonic Result error latch owned by PluginStrengthScope
    /// (single safety owner). Ok() = operational; Error reason = permanently tripped (delegation fail-closed).
    /// One-shot idempotent: once tripped, it can never be cleared by session cleanup, turn reconciliation,
    /// or caller reset. The fuse freezes new delegations for the remainder of the process.
    // DSL-MUTABLE: resource — strength fuse latch (Ok=operational, Error=tripped)
    let mutable strengthFuse: Result<unit, string> = Ok()

    member _.SharedKey = sharedKey
    member _.StrengthRuntime = runtimeRegistry
    member _.AttachStrengthReplicaRuntime(runtime: StrengthReplicaRuntime) = strengthReplicaRuntime <- Some runtime
    member _.StrengthReplicaRuntime = strengthReplicaRuntime

    member _.TripStrengthFuse(reason: string) =
        match strengthFuse with
        | Ok() -> strengthFuse <- Error reason
        | Error _ -> ()

    member _.StrengthFuseReason =
        match strengthFuse with
        | Ok() -> None
        | Error reason -> Some reason

    member _.StrengthFuse = strengthFuse

    /// Session deletion drops the decision-local Strength state for that session
    /// (mirror of DisposeSession's per-session cleanup in PluginRuntimeScope).
    member private _.RetireOrphanSessionBinding(replicaId: SessionId) =
        match runtimeRegistry.TryFindByReplica replicaId with
        | Some _ -> runtimeRegistry.Retire replicaId |> ignore
        | None ->
            runtimeRegistry.TryFindByOwner replicaId
            |> Option.iter (fun binding -> runtimeRegistry.Retire binding.ReplicaSessionId |> ignore)

    member this.ClearSession(sessionId: string) =
        // The strength fuse is a process-lifetime latch and is never cleared here.
        // Retire this session's live-registry side (as replica, else as owner)
        // so no orphan binding survives the session. Model-lease release stays
        // with the attached StrengthReplicaRuntime, which shares this registry.
        let replicaId = SessionId.create sessionId

        match strengthReplicaRuntime with
        | Some runtime -> runtime.HandleSessionDeleted replicaId
        | None -> this.RetireOrphanSessionBinding replicaId

    member _.Dispose() =
        strengthReplicaRuntime |> Option.iter (fun runtime -> runtime.Dispose())
        strengthReplicaRuntime <- None
        // Drop the live ownership registry. The strength fuse is a
        // process-lifetime latch and is never cleared here.
        runtimeRegistry.Clear()

module SharedPredictorScope =

    /// DSL-state-combination: physical — shared predictor scope refcount resource
    type private SharedEntry =
        { Scope: PluginStrengthScope
          mutable RefCount: int }

    let private gate = obj ()
    // DSL-MUTABLE: resource — shared predictor scope registry by runtime directory
    let private shared = Dictionary<string, SharedEntry>()

    let acquire (key: string) : PluginStrengthScope =
        lock gate (fun () ->
            match shared.TryGetValue key with
            | true, entry ->
                entry.RefCount <- entry.RefCount + 1
                entry.Scope
            | false, _ ->
                let scope = PluginStrengthScope(Some key)
                shared.[key] <- { Scope = scope; RefCount = 1 }
                scope)

    let private updateRelease key (entry: SharedEntry) =
        let remaining = entry.RefCount - 1

        if remaining <= 0 then
            shared.Remove key |> ignore
            entry.Scope.Dispose()
        else
            entry.RefCount <- remaining

    let private releaseShared key scope =
        lock gate (fun () ->
            match shared.TryGetValue key with
            | true, entry when obj.ReferenceEquals(entry.Scope, scope) -> updateRelease key entry
            | _ -> ())

    let release (scope: PluginStrengthScope) =
        match scope.SharedKey with
        | None -> scope.Dispose()
        | Some key -> releaseShared key scope

    let tryAcquireForRuntime (key: string option) : PluginStrengthScope option = key |> Option.map acquire
