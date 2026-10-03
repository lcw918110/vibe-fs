namespace Wanxiangshu.Execution.Delegation

open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority

type HandleCompletion =
    { Kind: HandleCompletionKind
      CompletionRef: BlobRef option
      CompletionDigest: BlobDigest option }

type HandleLifecycle =
    | Active
    | CompletedAwaitingJoin of HandleCompletion
    | Abandoned of HandleAbandonReason
    | Retired

type HandleRecord =
    { Handle: HandleId
      ChildSessionId: SessionId
      TargetAgent: string
      Byname: string
      CanonicalRole: Role
      Ownership: HandleOwnership
      Lifecycle: HandleLifecycle
      CreationOrder: int
      LastCompletion: HandleCompletion option
      Work: HandleWorkId option }

type HandleWorkRecord =
    { Work: HandleWorkId
      LogicalRunId: LogicalRunId
      Lifecycle: HandleLifecycle
      LastCompletion: HandleCompletion option
      ConsumptionId: string option }

type AdmittedWork = private AdmittedWork of HandleWorkId * LogicalRunId

module AdmittedWork =
    val id: AdmittedWork -> HandleWorkId
    val logicalRunId: AdmittedWork -> LogicalRunId

type AgentLinkageProjection =
    { Handles: Map<HandleId, HandleRecord>
      NextCreationOrder: int
      Works: Map<HandleWorkId, HandleWorkRecord>
      LegacyWorkHandles: Set<HandleId> }

type HandleTransitionRejection =
    | UnknownHandle
    | HandleIdentityConflict
    | HandleIsRetired
    | AlreadyCompleted
    | AlreadyAbandoned
    | NotCompleted
    | WorkNotAdmitted
    | WorkStillActive
    | ConsumptionMismatch
    | LegacyWorkAmbiguous

module HandleProjection =
    val empty: AgentLinkageProjection

    val admitWork:
        SessionId ->
        HandleId ->
        PromptAuthority.PromptAuthorityProjection ->
        AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val tryWork: HandleWorkId -> AgentLinkageProjection -> HandleWorkRecord option
    val tryAdmittedWork: HandleWorkId -> AgentLinkageProjection -> Result<AdmittedWork, HandleTransitionRejection>

    val completeWork:
        HandleWorkId ->
        HandleCompletion ->
        AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val abandonWork:
        HandleWorkId ->
        HandleAbandonReason ->
        AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val voidWork: HandleWorkId -> AgentLinkageProjection -> Result<AgentLinkageProjection, HandleTransitionRejection>

    val consumeWork:
        HandleWorkId ->
        string ->
        HandleCompletion ->
        AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val workRecords: AgentLinkageProjection -> HandleRecord list
    val tryBinding: HandleId -> AgentLinkageProjection -> HandleRecord option

    val linkNamed:
        handle: HandleId ->
        childSessionId: SessionId ->
        targetAgent: string ->
        byname: string ->
        role: Role ->
        ownership: HandleOwnership ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val link:
        handle: HandleId ->
        childSessionId: SessionId ->
        targetAgent: string ->
        role: Role ->
        ownership: HandleOwnership ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val replayLink:
        handle: HandleId ->
        childSessionId: SessionId ->
        targetAgent: string ->
        byname: string ->
        role: Role ->
        ownership: HandleOwnership ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val tryFindBindingByByname: byname: string -> AgentLinkageProjection -> HandleRecord option

    val complete:
        handle: HandleId ->
        completion: HandleCompletion ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val abandon:
        handle: HandleId ->
        reason: HandleAbandonReason ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val retire:
        handle: HandleId -> current: AgentLinkageProjection -> Result<AgentLinkageProjection, HandleTransitionRejection>

    val rejectFalseCompletion:
        handle: HandleId ->
        expectedRef: BlobRef ->
        expectedDigest: BlobDigest ->
        current: AgentLinkageProjection ->
            Result<AgentLinkageProjection, HandleTransitionRejection>

    val tryFind: handle: HandleId -> current: AgentLinkageProjection -> HandleRecord option
    val isRetired: handle: HandleId -> current: AgentLinkageProjection -> bool
    val isAbandoned: handle: HandleId -> current: AgentLinkageProjection -> bool
    val tryFindByByname: byname: string -> current: AgentLinkageProjection -> HandleRecord option
    val auditListable: AgentLinkageProjection -> HandleRecord list
    val auditActiveHandles: AgentLinkageProjection -> HandleRecord list
    val listable: current: AgentLinkageProjection -> HandleRecord list
    val horizonVisible: current: AgentLinkageProjection -> HandleRecord list
    val joinable: current: AgentLinkageProjection -> HandleRecord list
    val reportableAbandoned: current: AgentLinkageProjection -> HandleRecord list
    val activeHandles: current: AgentLinkageProjection -> HandleRecord list
    val tryFindByChildSession: childSessionId: SessionId -> current: AgentLinkageProjection -> HandleRecord option
    val lifecycleSealsBlogger: lifecycle: HandleLifecycle -> bool
    val recordSealsBlogger: record: HandleRecord -> bool
    val linkedChildren: current: AgentLinkageProjection -> HandleRecord list
