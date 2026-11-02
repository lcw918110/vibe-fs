namespace Wanxiangshu.Repository.Knowledge.Casebook

open System.Threading.Tasks

module CasebookSurface =

    val contentHash: text: string -> string

    val ofExecCommand: command: string -> obj

    val normalize: observations: obj array -> obj array

    val classifyReplay: stored: obj array -> replayed: obj array -> string

    val fetchCase: store: obj -> capacity: int -> sessionId: string -> Task<obj>

    val fetchCaseByIdentity: store: obj -> identity: string -> Task<obj>

    val refresh: store: obj -> sessionId: string -> q: string -> a: string -> observations: obj array -> Task<obj>

    val refreshWithDiff:
        store: obj -> identity: string -> diff: string -> newStateRef: string -> updates: obj -> Task<obj>

    val needsRefresh: store: obj -> capacity: int -> sessionId: string -> root: string -> Task<obj>

    val touchAccess: store: obj -> sessionId: string -> Task<obj>

    val evictCase: store: obj -> sessionId: string -> Task<obj>

    val featureEnabled: workspaceRoot: string -> bool

    val finalize: store: obj -> case: obj -> Task<obj>

    val archive: store: obj -> case: obj -> Task<obj>

    val archiveCase: store: obj -> case: obj -> Task<obj>

    val finalizeEngineerCase:
        store: obj ->
        identity: string ->
        trace: string ->
        question: string ->
        answer: string ->
        relatedPathsRaw: obj ->
        baselineJson: string ->
            Task<obj>

    val recordSubstantiveAccess: tracker: obj -> toolName: string -> args: obj -> committed: bool -> unit

    val createAccessTracker: unit -> obj

    val isSubstantiveTool: toolName: string -> bool

    val freezeCompletionState: store: obj -> workspaceRoot: string -> pathsRaw: obj -> Task<obj>

    val computeMaintenanceDiff: store: obj -> workspaceRoot: string -> pathsRaw: obj -> baseline: string -> Task<obj>

    val caseIdentityForInvocation: sessionId: string -> invocationId: string -> string

    val mergeFissionSubstantiveAccess: preFission: obj -> laneAccesses: obj -> obj

    val truncateDiffForBudget: diff: string -> budget: int -> obj
