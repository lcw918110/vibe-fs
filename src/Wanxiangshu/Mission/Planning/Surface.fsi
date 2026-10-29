namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks

module PlanningSurface =
    val assembleTenureMessages: messages: obj -> tenureObj: obj -> messageCursors: obj -> obj

    val validateWorkId: workId: string -> obj
    val canonicalWorkKey: workId: string -> obj
    val resolvePlanPath: root: string -> workId: string -> obj
    val readAndValidatePlan: root: string -> workId: string -> obj

    val ensurePlanWorkOpened:
        gitCommonDir: string -> root: string -> sessionId: string -> observedHead: obj -> Task<obj>

    val executeDeliver:
        gitCommonDir: string ->
        root: string ->
        workId: string ->
        incumbencyId: string ->
        providerRunId: string ->
        toolCallId: string ->
        hasBlocker: obj ->
        execution: obj ->
        retirementCursor: obj ->
            Task<obj>

    val startPostS3Execution:
        gitCommonDir: string ->
        root: string ->
        workId: string ->
        providerRunId: string ->
        toolCallId: string ->
        hasBlocker: obj ->
        execution: obj ->
        retirementCursor: obj ->
            Task<obj>

    val reconcileBoundExecution:
        gitCommonDir: string ->
        workId: string ->
        execSessionId: string ->
        observedPrompt: string ->
        blockers: string array ->
            obj

    /// work-028: `delegateTo` is a plain JavaScript function
    /// `(intent) => Promise<string>` returning the DevOps delivered view.
    val executeAsk: gitCommonDir: string -> workId: string -> intent: string -> delegateTo: obj -> Task<obj>

    val executeReview:
        gitCommonDir: string ->
        workId: string ->
        incumbencyId: string ->
        requestId: string ->
        findings: obj ->
        note: obj ->
            Task<obj>

    /// reviewFinishedOnly: true once the named incumbency has finished its
    /// first review, which unlocks draft writes for that tenure.
    val isEligible: gitCommonDir: string -> workId: string -> incumbencyId: string -> bool
    val isReviewFinished: gitCommonDir: string -> workId: string -> incumbencyId: string -> bool

    val PlanningSurface: obj
