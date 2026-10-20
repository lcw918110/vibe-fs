namespace Wanxiangshu.Interaction.Repair

module CompletedTurnSurface =
    val partsText: parts: obj -> string


    val partsSessionText: parts: obj -> string
    val hasToolCallPart: parts: obj -> bool
    val isAbortErrorName: name: string -> bool
    val formalContentUnusable: parts: obj -> bool
    val classifyOutcome: completed: bool -> finish: string -> errorName: string -> parts: obj -> obj
    val needsInteractionRepair: role: string -> completed: bool -> finish: string -> parts: obj -> bool
    val repairDefectDecision: currentAttemptIsRepair: bool -> completed: bool -> finish: string -> parts: obj -> string

    val repairSuppressionHolds:
        isRetryContinuation: bool -> hasDurableTerminal: bool -> completed: bool -> finish: string -> parts: obj -> bool

    val roleOfAgent: agent: string -> fallback: string -> string

    val buildTurn:
        session: string ->
        physical: string ->
        authorityRoot: string ->
        message: obj ->
        roleFallback: string ->
        directory: string ->
            obj
