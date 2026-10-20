namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Change
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.Participant.Provider.Attempt.Fallback

type SessionAgentProjection =
    { Companion: CompanionProjection option
      XTrace: XTraceProjectionState option

      PrefixEpoch: ActivePrefixEpoch option
      Handles: AgentLinkageProjection option
      ProviderFailures: ProviderFailureProjection option
      PromptAuthority: PromptAuthority.PromptAuthorityProjection option
      Guidelines: GuidelineProjectionState option
      SessionStartedAt: SessionStartedAtProjectionState option
      DelegatedToolEstimate: DelegatedToolEstimateProjectionState option }

type AgentProjectionSet =
    {
        Sessions: Map<SessionId, SessionAgentProjection>
        Associations: Map<SessionId, SessionAssociation>
        Orchestrator: OrchestratorProjection
        HandleByChildSession: Map<SessionId, HandleRecord>
        Fission: FissionProjectionState
        ChatExecutions: ChatExecutionProjectionState
        /// Successful view declaration checkpoints still on the raw side of the
        /// committed prefix. The retain window (K) is a fixed system parameter.
        ViewCheckpoints: Map<SessionId, PhaseWindow.PhaseCommitWindow>
        RuntimeStartCount: int
    }

module AgentProjection =
    val emptySession: SessionAgentProjection
    val empty: AgentProjectionSet
    val tryFind: sessionId: SessionId -> projection: AgentProjectionSet -> SessionAgentProjection option
    val hasActiveOrchestratorJobs: projection: AgentProjectionSet -> bool
    val activeOrchestratorJobPairs: projection: AgentProjectionSet -> (ManagerJobId * SessionId) list

    val update:
        sessionId: SessionId ->
        apply: (SessionAgentProjection -> SessionAgentProjection) ->
        projection: AgentProjectionSet ->
            AgentProjectionSet

    val tryUpdate:
        sessionId: SessionId ->
        apply: (SessionAgentProjection -> Result<SessionAgentProjection, 'rejection>) ->
        projection: AgentProjectionSet ->
            Result<AgentProjectionSet, 'rejection>
