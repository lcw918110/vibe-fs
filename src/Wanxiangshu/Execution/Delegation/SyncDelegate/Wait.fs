namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session.Wait

/// EXEC-026 / EXEC-031: wait descriptors for SyncDelegate CE
/// (Acquire → GetOrCreate → Send → await ordinary Completion / delivered view).
type SyncDelegateWait =
    | DelegateCompletion of owner: SessionId * delegateSession: SessionId * role: SyncDelegateRole
    | InvocationJoin of owner: SessionId * role: SyncDelegateRole

module SyncDelegateWait =

    /// Diagnostic wait descriptor for the SyncDelegate CE await point.
    let describe (wait: SyncDelegateWait) : DiagnosticWait =
        let roleLabel = SyncDelegate.roleLabel

        let toolOwner (owner: SessionId) =
            CausalOwner.create "sync-delegate-tool" [ "session", SessionId.value owner ]

        let delegateProducer (delegateSession: SessionId) =
            WorkflowProducer(
                CausalOwner.create "sync-delegate-session-workflow" [ "session", SessionId.value delegateSession ]
            )

        let cancelEscape (owner: SessionId) =
            WaitEscape.CancelledBy(CausalOwner.create "owner-session" [ "session", SessionId.value owner ])

        match wait with
        | DelegateCompletion(owner, delegateSession, role) ->
            DiagnosticWait.create
                "sync-delegate-completion"
                (toolOwner owner)
                [ "owner", SessionId.value owner
                  "delegate", SessionId.value delegateSession
                  "role", roleLabel role ]
                (delegateProducer delegateSession)
                [ cancelEscape owner; WaitEscape.SessionLifetime ]
                "SyncDelegateRuntime.Invoke"
        | InvocationJoin(owner, role) ->
            DiagnosticWait.create
                "sync-delegate-invocation"
                (toolOwner owner)
                [ "owner", SessionId.value owner; "role", roleLabel role ]
                (WorkflowProducer(CausalOwner.create "sync-delegate-invoke" [ "session", SessionId.value owner ]))
                [ cancelEscape owner; WaitEscape.SessionLifetime ]
                "SyncDelegateWorkflow.invoke"
