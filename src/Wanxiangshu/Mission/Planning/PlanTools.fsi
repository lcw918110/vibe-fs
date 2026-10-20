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

    /// work-025 / work-026: an authorized delivery first creates a
    /// parentless execution container, durably binds its SessionId to the
    /// DeliveryRequested operation, then sends the first prompt. Prompt
    /// acceptance settles Delivered; unknown acceptance remains pending.
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

    /// Reconcile a bound execution after restart from an exact Host-observed
    /// first user prompt. No external effect is issued by this function.
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

    /// work-029: js-plan operates on the single derived plan path with
    /// read / edit / rewrite.
    val executeJsPlan:
        root: string ->
        workId: string ->
        action: string ->
        content: string option ->
        patches: (string * string) list option ->
            Result<string, string>
