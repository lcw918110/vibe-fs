namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Foundation.Identity

/// Per-instance session registry state for one plugin instance (HOST-012):
/// owned sessions, companions, verdicts, nudges,
/// quiescence permits and join interrupts. Shared cross-worktree state stays
/// in SharedState; everything here is per-instance and dies with the scope.
type PluginSessionScope =
    new:
        journal: Wanxiangshu.Persistence.Journal.AgentJournal option *
        isModelLeaseExternallyOwned: (SessionId -> bool) *
        jsLifecyclePersistence: Wanxiangshu.Repository.Programming.Js.IJsLifecyclePersistence option ->
            PluginSessionScope

    /// Cross-instance session directory map alias.
    member SessionDirectories: Dictionary<string, string>

    /// Per-instance owned session set.
    member OwnedSessions: HashSet<string>

    /// Per-plugin-instance routing demands.
    member ModelRoutingSessions: HashSet<string>

    /// Cross-instance session parent map alias.
    member SessionParents: Dictionary<string, string>

    /// Per-instance companion registry.
    member Companions: Dictionary<string, CompanionHost>

    /// Lock gate object for companion operations.
    member CompanionGate: obj

    /// Cross-instance verdict session set alias.
    /// Per-instance nudge sent set.
    member NudgeSent: HashSet<string>

    /// Per-instance join guard nudge set.
    member JoinGuardNudges: HashSet<string>

    /// Per-instance quiescence gate instance.
    member Quiescence: SessionQuiescenceGate

    /// Per-instance join interrupt registry.
    member JoinInterrupts: IJoinAttemptRegistry


    /// Drops the provider-language identity for this session idempotently.
    member DropSessionIdentity: sessionId: string -> unit

    /// Session deletion drops every per-instance registry entry for this session
    /// and awaits this scope's exact execution settlement before returning:
    /// managed-chat-execution-010 requires session delete to finish durable terminal
    /// plus exact capacity release for every admitted execution in the scope before
    /// the lifecycle may be declared drained.
    member ClearSession: sessionId: string -> Task

    /// Plugin dispose releases companions and instance-owned model leases.
    member Dispose: unit -> unit
