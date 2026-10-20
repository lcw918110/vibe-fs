namespace Wanxiangshu.Change.Host

open System.Threading.Tasks

/// JS-native owner for the OrchestratorHost semantic harness.
///
/// Host runtime state, Relay port, journal projection and typed ports remain
/// opaque. The harness supplies plain JavaScript port observations; this owner
/// translates them once into the real Host contracts.
[<RequireQualifiedAccess>]
module OrchestratorHostSurface =

    /// Build a real OrchestratorHost from plain JavaScript port contracts.
    /// `sessions`, `gitPort`, and `journal` are capabilities owned by the caller;
    /// this function never projects their internal representation.
    val create: options: obj -> obj

    val detachAndDrain: handle: obj -> Task

    /// Read-only probe: the durable loop signal this owner derives for one Manager
    /// session's work, from the same fold the acquisition program reads before it
    /// waits. Never dispatches and never writes.
    val loopSignalOf: workspace: string -> managerSessionId: string -> Task<obj>

    /// change-integration-005/013: the same Work's next tenure after a delivered
    /// manager life, through exactly the hand-off the Host applies when it
    /// continues a delivered loop. `observedCursor` is the head the caller observed
    /// for that session (null/undefined when none); the host itself never guesses
    /// one.
    val handOffDeliveredTenure:
        workspace: string -> managerSessionId: string -> requestId: string -> observedCursor: obj -> Task<obj>

    /// change-integration-005/013: the durable review evidence the publish gate
    /// reads for one Manager road. `Refused` means the gate refuses publishing with
    /// zero writes.
    val publishReviewOf: workspace: string -> managerSessionId: string -> Task<obj>

    val hasChild: handle: obj -> agentId: string -> bool

    val forkManagerJob: handle: obj -> jobId: string -> managerAgent: string -> prompt: string -> Task<obj>

    /// Exercise the production candidate-finalization sequence through a plain
    /// JavaScript command port without exposing Command or Result internals.
    val finalizeWorktree: runner: obj -> managerId: string -> worktree: string -> Task<obj>
