namespace Wanxiangshu.Execution.Failure

module Surface =
    val internal inputOf: value: obj -> ExecutionFailureInput
    val recoveryStep: priorSameProviderRetry: bool -> string
    val decide: value: obj -> obj
