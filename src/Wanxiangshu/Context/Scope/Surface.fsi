namespace Wanxiangshu.Context.Scope

module Surface =
    [<Literal>]
    val ResetScopeTool: string = "resetScope"

    [<Literal>]
    val SuffixSeparator: string = "\u0000\uFEFF"

    val toObjArray: messages: obj -> obj array
    val isUnchanged: first: obj array -> second: obj array -> bool
    val budgetSuffix: budget: int -> string
    val adjustBudgetForKpi: estimate: int -> completion: string -> int
    val turnBudget: sessionId: string -> int
    val isScopeInitialized: sessionId: string -> bool
    val onTurnStart: sessionId: string -> unit
    val resetScopeState: sessionId: string -> budget: int -> unit
    val clearSessionState: sessionId: string -> unit
    val planRequest: sessionId: string -> messages: obj -> obj
    val bindDelegation: recipient: string -> delegator: string -> unit
    val askNegotiation: recipient: string -> questions: string -> obj
    val prepareNegotiationReply: receipt: string -> answer: string -> obj
    val completeNegotiationReply: receipt: string -> bool
    val confirmNegotiationSteer: receipt: string -> recipient: string -> bool
    val negotiationRequired: recipient: string -> bool
    val planFilter: messages: obj -> obj array
    val appendTailOnce: content: obj -> suffix: string -> obj
    val writeUserHint: message: obj -> texts: string array -> obj
    val writeToolResultHint: message: obj -> texts: string array -> obj
    val stripKnownHints: message: obj -> texts: string array -> obj
    val userCanCarryHint: message: obj -> bool
    val toolCanCarryHint: message: obj -> bool
    val rememberSystem: sessionId: string -> system: string array -> unit
    val rememberMessages: sessionId: string -> messages: obj array -> unit
    val rememberTransform: sessionId: string -> input: obj array -> output: obj array -> unit
    val latestPrevious: sessionId: string -> obj
    val rememberModel: sessionId: string -> model: obj -> unit
    val modelFor: sessionId: string -> obj
    val startTemporary: tempId: string -> delegatorId: string -> obj
    val startTemporaryPrevious: tempId: string -> sourceSessionId: string -> metadata: obj -> obj
    val modelForTemporary: tempId: string -> obj
    val isTemporary: sessionId: string -> bool
    val finishTemporary: tempId: string -> unit
    val recordToolViolation: tempId: string -> unit
    val toolViolationCount: tempId: string -> int
    val forgetSession: sessionId: string -> unit
    val projectSystem: tempId: string -> current: string array -> string array option
    val projectPreviousMessages: messages: obj array -> obj
    val projectMessages: tempId: string -> question: obj -> obj array option
    val pendingKind: sessionId: string -> string
    val isPreviousScopePending: sessionId: string -> bool
    val hasPreviousScope: sessionId: string -> bool
    val setHasPreviousScope: sessionId: string -> value: bool -> unit
    val beginAskPreviousScope: sessionId: string -> unit
    val askPreviousScope: sessionId: string -> questions: string -> receipt: string option -> obj
    val preparePreviousScopeReply: receipt: string -> sessionId: string -> answer: string -> obj
    val confirmPreviousScopeSteer: receipt: string -> sessionId: string -> bool
