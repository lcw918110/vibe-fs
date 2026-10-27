namespace Wanxiangshu.Composition.Turn

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Persona
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Execution.Failure

/// DSL-state-combination: domain — this is a single reconciled provider-turn
/// observation; optional metadata fields preserve evidence absence and never
/// encode a next-step cursor.
type ReconciledTurn =
    {
        SessionId: SessionId
        /// Physical user message that caused this provider run.
        PhysicalUserMessageId: PhysicalUserMessageId
        /// Semantic authority root; continuations never replace this identity.
        AuthorityRootUserMessageId: AuthorityRootUserMessageId
        /// HOST-010/HOST-011: one assistant message is one provider request is
        /// one turn, so the run identity IS the assistant message id. Naming the
        /// field `AssistantMessageId` invited a second identity for the same
        /// thing — and provider-attempt-recovery-003 deduplicates failed attempts by this value.
        ProviderRun: ProviderRunIdentity
        Role: Role option
        /// The worktree this run executed in, when it has one. A Manager child runs
        /// in its own worktree; a top-level session has none.
        Directory: string option
        Parts: MessagePart array
        Finish: string option
        ErrorName: string option
        Model: HostModel option
        /// Publishable provider-turn classification (HOST-004). Never TurnUnknown.
        Outcome: ReconcileProgram.TurnOutcome
        /// Reconciliation-private finish=None observation. When Some, evidence and
        /// missing-final-report repair consult this — not Outcome.
        Observation: ReconcileProgram.SnapshotObservation option
    }

/// A reconciled fact is delivered once. A later fresh idle observation may revisit
/// the same fact only to evaluate idle-derived work; it must not replay terminal
/// plumbing, judgment, or other first-delivery effects (HOST-004).
[<RequireQualifiedAccess>]
type ReconciledTurnDelivery =
    | Observation
    | IdleRevisit

/// The reconciled turn plus the quiescence evidence of the pass that delivered it.
/// An idle wake carries `Some permit`, including one merged with failure. The
/// side-effect boundary re-checks the permit immediately before any physical
/// idle-derived send.
type ReconciledTurnContext =
    { Turn: ReconciledTurn
      Failure: ExecutionFailure option
      Quiescence: QuiescencePermit option
      Delivery: ReconciledTurnDelivery }

/// DSL-state-combination: physical — this process-local binding snapshots
/// observed run/message identities and optional host metadata; it is not durable
/// workflow state or a continuation program counter.
type ActiveRunBinding =
    {
        SessionId: SessionId
        RunId: string option
        AuthorityRootUserMessageId: AuthorityRootUserMessageId option
        /// Latest physical user message for the active logical run.
        PhysicalUserMessageId: PhysicalUserMessageId option
        ContinuationMessageIds: Set<string>
        Role: Role option
        Directory: string option
        /// The Fission owner whose authority this session inherits.
        ///
        /// A Fission lane never owns an authority root: it continues the
        /// owner's logical run. This marker says the bound root belongs to
        /// another session, so reconcile may accept a root that is not in this
        /// session's transcript. `None` for every ordinary session.
        InheritedAuthorityOwner: SessionId option
    }
