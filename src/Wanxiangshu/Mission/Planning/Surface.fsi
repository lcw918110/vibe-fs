namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks

module PlanningSurface =
    val assembleTenureMessages: messages: obj -> tenureObj: obj -> messageCursors: obj -> obj

    val validateWorkId: workId: string -> obj
    val canonicalWorkKey: workId: string -> obj
    val resolvePlanPath: root: string -> workId: string -> obj
    val readPlan: root: string -> workId: string -> obj
    val rewritePlanAtomic: root: string -> workId: string -> content: string -> obj
    val editPlanAtomic: root: string -> workId: string -> patches: obj -> obj

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
    val executeJsPlan: root: string -> workId: string -> action: string -> content: obj -> patches: obj -> obj
    val reviewTargetFor: stage: string -> string

    val executeReview:
        gitCommonDir: string -> workId: string -> incumbencyId: string -> findings: obj -> note: obj -> Task<obj>

    val PlanningSurface: obj
