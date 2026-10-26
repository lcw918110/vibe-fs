namespace Wanxiangshu.Mission.Planning

open System.Threading.Tasks

type DeliverResult =
    { Ok: bool
      Relayed: bool
      Delivered: bool
      RetiredIncumbencyId: string
      RetirementOutcome: string
      Digest: string
      Path: string
      ExecSessionId: string
      NextIncumbencyId: string
      Message: string }

/// work-028: the outcome of one synchronous ask delegation.
type AskResult =
    { Kind: string
      View: string
      Status: string }

type DeliverySpawnOutcome =
    | Started of sessionId: string
    | NotStarted of reason: string
    | Unknown of reason: string

module PlanTools =

    /// The one plan artifact read used by deliver and by the plan surface: the
    /// content, its digest, and the resolved path. Empty or missing is an error.
    val readAndValidatePlan: root: string -> workId: string -> Result<string * string * string, string>

    /// 已退出。review 是唯一生命周期入口。调用零写并返回错误。
    val executeDeliver:
        gitCommonDir: string ->
        root: string ->
        workId: string ->
        incumbencyId: string ->
        providerRunId: string ->
        toolCallId: string ->
        retirementBlockers: (unit -> string array) ->
        createSession: (string -> string -> Task<Result<string, string>>) ->
        promptSession: (string -> string -> string -> Task<DeliverySpawnOutcome>) ->
        discardSession: (string -> Task<Result<unit, string>>) ->
        retirementCursor: int64 ->
            Task<Result<DeliverResult, string>>

    val enterPostS3OrRelay:
        gitCommonDir: string ->
        root: string ->
        workId: string ->
        incumbencyId: string ->
        providerRunId: string ->
        toolCallId: string ->
        retirementBlockers: (unit -> string array) ->
        retirementCursor: int64 ->
            Task<Result<DeliverResult, string>>

    val startPostS3Execution:
        gitCommonDir: string ->
        root: string ->
        workId: string ->
        providerRunId: string ->
        toolCallId: string ->
        retirementBlockers: (unit -> string array) ->
        createSession: (string -> string -> Task<Result<string, string>>) ->
        promptSession: (string -> string -> string -> Task<DeliverySpawnOutcome>) ->
        discardSession: (string -> Task<Result<unit, string>>) ->
        retirementCursor: int64 ->
            Task<Result<DeliverResult, string>>

    /// 已退出。恢复不得结算旧交付。调用零写并返回错误。
    val reconcileBoundExecution:
        gitCommonDir: string ->
        workId: string ->
        execSessionId: string ->
        observedPrompt: string ->
        blockers: string array ->
            Result<DeliverResult, string>

    val bindObservedExecutionSession:
        gitCommonDir: string -> workId: string -> operationId: string -> execSessionId: string -> Result<unit, string>

    /// work-028: delegate the intent synchronously to the bound fixed
    /// DevOps and return its complete delivered view verbatim. `delegateTo`
    /// carries the real delegation capability (injected by the tool surface);
    /// at most one pending delegation per work, tracked in process memory.
    val executeAsk:
        gitCommonDir: string ->
        workId: string ->
        intent: string ->
        delegateTo: (string -> Task<string>) ->
            Task<Result<AskResult, string>>
