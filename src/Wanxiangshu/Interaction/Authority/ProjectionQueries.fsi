namespace Wanxiangshu.Interaction.Authority

open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Query layer translating an AgentProjectionSet to PromptAuthority facts with
/// durable-level readonly views. This must be durable-composition because it
/// has complete knowledge of AgentProjectionSet.
module PromptAuthorityProjectionQueries =


    /// The durable PromptAuthority projection of one session.
    val projectionFor:
        sessionId: SessionId -> agentProjections: AgentProjectionSet -> PromptAuthority.PromptAuthorityProjection option

    /// The durable Fission owner of a lane session, when this session is a lane.
    val laneOwnerSessionId: sessionId: SessionId -> agentProjections: AgentProjectionSet -> SessionId option

    /// The physical ids this session accepted as continuations of its own claims.
    val ownContinuationPhysicals:
        sessionId: SessionId -> agentProjections: AgentProjectionSet -> PhysicalUserMessageId list

    /// The owner's active logical run for a lane session (never for an ordinary session).
    val inheritedOwnerProfile:
        sessionId: SessionId -> agentProjections: AgentProjectionSet -> PromptAuthority.AuthorityExecutionProfile option

    /// Returns the authority projection for the fission-owner of this session.
    val activeProfile:
        sessionId: SessionId -> agentProjections: AgentProjectionSet -> PromptAuthority.AuthorityExecutionProfile option

    /// Returns the last known authority projection for the fission-owner of this session.
    val lastAuthorityProfile:
        sessionId: SessionId -> agentProjections: AgentProjectionSet -> PromptAuthority.AuthorityExecutionProfile option

    val acceptedRootClosure:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.AcceptedRootClosure option

    /// interaction-authority-018: the closure already recorded for one logical
    /// run, so a replay can tell a duplicate from a conflict.
    val closedRun:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.AuthorityClosureRecord option

    val terminalSources:
        sessionId: SessionId ->
        logicalRunId: LogicalRunId ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.AuthorityTerminalSource list

    /// Returns the pending claim for the given session and prompt key.
    val pendingClaim:
        sessionId: SessionId ->
        promptKey: PromptKey ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.PromptClaim option

    [<RequireQualifiedAccess>]
    type DispatchStatus =
        | Accepted of evidence: PromptAuthority.AcceptedDispatch
        | Pending
        | Dispatchable

    /// Finds a pending dispatch claim matching the given payload digest.
    val pendingDispatchClaim:
        sessionId: SessionId ->
        payloadDigest: string ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.PromptClaim option

    /// The exact dispatch that landed as this physical message.
    val physicalLanding:
        sessionId: SessionId ->
        physicalUserMessageId: PhysicalUserMessageId ->
        agentProjections: AgentProjectionSet ->
            PromptAuthority.AcceptedDispatch option

    /// Durable status of a dispatch.
    val dispatchStatusFor:
        sessionId: SessionId -> payloadDigest: string -> agentProjections: AgentProjectionSet -> DispatchStatus

    /// Issues an inherited identity seed for a managed child.
    val issueCurrentOwnerIdentitySeed:
        agentProjections: AgentProjectionSet ->
        ownerSessionId: SessionId ->
        childAgent: string ->
            Result<PromptAuthority.IdentitySeed, string>
