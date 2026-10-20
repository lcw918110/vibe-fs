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

/// Bounded projections one session owns (PERSIST-008).
///
/// Each is `option` because a session acquires state only when a fact concerning
/// it arrives. "No fact yet" and "an empty projection" are different claims and
/// must stay distinguishable — collapsing them is how a missing fact starts
/// looking like a satisfied precondition.
/// DSL-state-combination: domain — this aggregate is the durable per-session
/// projection of independently-owned fact families; each optional field is a
/// bounded-context projection, not an execution cursor.
type SessionAgentProjection =
    {
        /// The durable Companion link for this session. The Work ↔ Companion
        /// relation itself lives in `AgentProjectionSet.Associations`; the raw
        /// semantic history lives in the XTrace below.
        Companion: CompanionProjection option
        /// COMPANION-003 / HOST-005: the XTrace — the session's unique raw
        /// semantic trajectory. It is the raw record itself, not a compressed
        /// view of it: what the provider is shown is decided by the model's own
        /// ANCHOR `view` declaration (prefixUntil / thenRemove), and the
        /// compressed result handed back to a caller is the delivered
        /// `js.deliver` view.
        XTrace: XTraceProjectionState option

        /// COMPANION-009: which provider-visible prefix generation is in force.
        /// Not folded into the prefix: a reanchor retires the prefix and
        /// advances this epoch, so one record for both would make each change
        /// look like it moved the other.
        PrefixEpoch: ActivePrefixEpoch option
        Handles: AgentLinkageProjection option
        ProviderFailures: ProviderFailureProjection option
        PromptAuthority: PromptAuthority.PromptAuthorityProjection option
        /// HOST-013: permanent auto-injected pairs for this transcript.
        Guidelines: GuidelineProjectionState option
        SessionStartedAt: SessionStartedAtProjectionState option
        DelegatedToolEstimate: DelegatedToolEstimateProjectionState option
    }

type AgentProjectionSet =
    {
        Sessions: Map<SessionId, SessionAgentProjection>
        /// HOST-008: the Work ↔ Companion relation.
        ///
        /// Workspace-scoped rather than per-session, because the relation spans two
        /// sessions and both directions must be answerable from one keyed lookup
        /// (PERSIST-008). Held per-session, "is this id somebody's Companion" would require
        /// scanning every session.
        Associations: Map<SessionId, SessionAssociation>
        Orchestrator: OrchestratorProjection
        /// PERSIST-008: child session → handle record, across ALL parents.
        ///
        /// Child-link queries answer "is this session somebody's child" from
        /// this one keyed lookup; scanning every parent's handle map instead is
        /// the scan PERSIST-008 forbids. Fold keeps it in step with the
        /// three handle facts (link / complete / retire), and retired records stay
        /// — the tombstone is permanent (EXEC-009).
        HandleByChildSession: Map<SessionId, HandleRecord>
        /// Same-participant multi-present groups and physical lane aliases.
        Fission: FissionProjectionState
        /// Exact durable managed-chat executions, keyed by physical user message.
        ChatExecutions: ChatExecutionProjectionState
        /// context-compression-028: successful view declaration checkpoints that
        /// are still on the raw side of the committed prefix. Each checkpoint
        /// carries the K chosen on that call; a committed rebase prunes only
        /// checkpoints it has actually crossed.
        ViewCheckpoints: Map<SessionId, PhaseWindow.PhaseCommitWindow>

        /// Historical count of folded `RuntimeStarted` envelopes. Retained for
        /// audit/backward-compatible projections; it no longer drives recovery.
        RuntimeStartCount: int
    }

/// Composition of bounded session projections. Fact routing lives in Fold.fs.
module AgentProjection =

    let emptySession =
        { Companion = None
          XTrace = None
          PrefixEpoch = None
          Handles = None
          ProviderFailures = None
          PromptAuthority = None
          Guidelines = None
          SessionStartedAt = None
          DelegatedToolEstimate = None }

    let empty =
        { Sessions = Map.empty
          Associations = SessionAssociationProjection.empty
          Orchestrator = OrchestratorProjection.empty
          HandleByChildSession = Map.empty
          Fission = FissionProjection.empty
          ChatExecutions = ChatExecutionProjection.empty
          ViewCheckpoints = Map.empty
          RuntimeStartCount = 0 }

    let tryFind (sessionId: SessionId) (projection: AgentProjectionSet) =
        Map.tryFind sessionId projection.Sessions

    let hasActiveOrchestratorJobs (projection: AgentProjectionSet) : bool =
        OrchestratorProjection.activeJobs projection.Orchestrator |> List.isEmpty |> not

    let activeOrchestratorJobPairs (projection: AgentProjectionSet) : (ManagerJobId * SessionId) list =
        OrchestratorProjection.activeJobs projection.Orchestrator
        |> List.map (fun job -> job.ManagerJobId, job.ManagerSessionId)

    let private sessionOrEmpty sessionId projection =
        Map.tryFind sessionId projection.Sessions |> Option.defaultValue emptySession

    let update sessionId apply projection =
        { projection with
            Sessions = Map.add sessionId (apply (sessionOrEmpty sessionId projection)) projection.Sessions }

    /// Update one session when the change itself may be refused.
    ///
    /// Threads the rejection out rather than swallowing it. A projection that
    /// silently ignores an invalid fact cannot fail closed.
    let tryUpdate
        (sessionId: SessionId)
        (apply: SessionAgentProjection -> Result<SessionAgentProjection, 'rejection>)
        (projection: AgentProjectionSet)
        : Result<AgentProjectionSet, 'rejection> =
        sessionOrEmpty sessionId projection
        |> apply
        |> Result.map (fun session ->
            { projection with
                Sessions = Map.add sessionId session projection.Sessions })
