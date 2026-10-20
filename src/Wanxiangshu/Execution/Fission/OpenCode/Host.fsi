namespace Wanxiangshu.Execution.Fission.OpenCode

open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal

[<RequireQualifiedAccess>]
module FissionHostRequestProjection =

    val projectExternalManaged:
        hasPhysicalParent: (SessionId -> bool) -> intent: ChatAdmissionIntent.Decision -> output: obj -> unit

    val projectPendingManaged:
        hasPhysicalParent: (SessionId -> bool) -> intent: ChatAdmissionIntent.Decision -> output: obj -> unit

/// Host-side terminal bridge for same-participant Fission lanes. A physical lane
/// terminal is not a parent-visible agent completion. It materializes one keyed
/// lane view, and only the durable group convergence publishes a terminal on the
/// old logical owner's SessionId/completion cell.
module FissionHost =

    /// A Fission owner replacement physically aborts the old present without
    /// cancelling logical-owner resources. Fission owns that distinction; the
    /// Host root supplies the two published continuations only.
    val routeAttemptAborted:
        sessionId: SessionId -> onSilentReplacement: (unit -> unit) -> onOrdinaryAbort: (unit -> unit) -> unit

    /// intra-participant-parallelism-009: an exact physical terminal is only
    /// a reconciliation occasion for the durable Fission lane that still owns
    /// that exact current physical material. The Host root supplies observation
    /// and wake capabilities; Fission owns the membership/currentness decision.
    val observePhysicalExecutionEnd:
        tryCurrentPhysical: (SessionId -> PhysicalUserMessageId option) ->
        durable: AgentJournal option ->
        kick: (SessionId -> unit) ->
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
            unit

    /// Once every lane record and shared completion is accounted for, route the
    /// complete ring bundle back into the final physical present. The logical
    /// owner remains open until that continuation itself reaches an ordinary
    /// terminal turn.
    val tryConverge:
        sessionPort: ISessionHostPort ->
        durable: AgentJournal ->
        directoryFor: (SessionId -> string option) ->
        owner: SessionId ->
            Task<bool>

    /// Returns true when this turn belongs to Fission and its terminal semantics
    /// were consumed here. Retired owner sessions are absorbed; non-terminal lane
    /// turns return false so ordinary repair/recovery behavior still runs.
    val observeLaneTurn:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        joinGuardNudges: HashSet<string> ->
        quiescence: ISessionQuiescenceGate ->
        permit: QuiescencePermit option ->
        abortCause: AbortCause ->
        turn: ReconciledTurn ->
            Task<bool>
