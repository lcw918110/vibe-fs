namespace Wanxiangshu.Mission.Planning

open System
open System.Threading.Tasks
open Wanxiangshu.Host

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
    let private resolvePathAndDigest
        (content: string)
        (root: string)
        (workId: string)
        : Result<string * string, string> =
        match PlanPath.resolvePlanPath root workId with
        | Error err -> Error(sprintf "Cannot resolve plan path: %s" err)
        | Ok planPath ->
            let digest = HostDigest.sha256Hex content
            Ok(digest, planPath)

    let readAndValidatePlan (root: string) (workId: string) : Result<string * string * string, string> =
        match PlanPath.readPlan root workId with
        | Error err -> Error(sprintf "Cannot deliver: plan artifact is empty or missing (%s)" err)
        | Ok content when String.IsNullOrWhiteSpace content -> Error "Cannot deliver: plan artifact is empty"
        | Ok content ->
            resolvePathAndDigest content root workId
            |> Result.map (fun (digest, planPath) -> (content, digest, planPath))

    /// The one lifecycle work this session owns, read from the wanxiangli
    /// journal selected by the git common directory.
    let private lifecycleWorkOf
        (gitCommonDir: string)
        (workId: string)
        : Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option =
        if String.IsNullOrWhiteSpace gitCommonDir then
            None
        else
            Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected gitCommonDir workId

    let private retired () : Result<'a, string> =
        Error "lifecycle deliver is retired; review is the only entry"

    let executeDeliver
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (incumbencyId: string)
        (providerRunId: string)
        (toolCallId: string)
        (retirementBlockers: (unit -> string array))
        (createSession: (string -> string -> Task<Result<string, string>>))
        (promptSession: (string -> string -> string -> Task<DeliverySpawnOutcome>))
        (discardSession: (string -> Task<Result<unit, string>>))
        (retirementCursor: int64)
        : Task<Result<DeliverResult, string>> =
        ignore gitCommonDir
        ignore root
        ignore workId
        ignore incumbencyId
        ignore providerRunId
        ignore toolCallId
        ignore retirementBlockers
        ignore createSession
        ignore promptSession
        ignore discardSession
        ignore retirementCursor
        Task.FromResult(retired (): Result<DeliverResult, string>)

    let enterPostS3OrRelay
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (incumbencyId: string)
        (providerRunId: string)
        (toolCallId: string)
        (retirementBlockers: (unit -> string array))
        (retirementCursor: int64)
        : Task<Result<DeliverResult, string>> =
        ignore gitCommonDir
        ignore root
        ignore workId
        ignore incumbencyId
        ignore providerRunId
        ignore toolCallId
        ignore retirementBlockers
        ignore retirementCursor
        Task.FromResult(retired (): Result<DeliverResult, string>)

    let startPostS3Execution
        (gitCommonDir: string)
        (root: string)
        (workId: string)
        (providerRunId: string)
        (toolCallId: string)
        (retirementBlockers: (unit -> string array))
        (createSession: (string -> string -> Task<Result<string, string>>))
        (promptSession: (string -> string -> string -> Task<DeliverySpawnOutcome>))
        (discardSession: (string -> Task<Result<unit, string>>))
        (retirementCursor: int64)
        : Task<Result<DeliverResult, string>> =
        ignore gitCommonDir
        ignore root
        ignore workId
        ignore providerRunId
        ignore toolCallId
        ignore retirementBlockers
        ignore createSession
        ignore promptSession
        ignore discardSession
        ignore retirementCursor
        Task.FromResult(retired (): Result<DeliverResult, string>)

    let reconcileBoundExecution
        (gitCommonDir: string)
        (workId: string)
        (execSessionId: string)
        (observedPrompt: string)
        (blockers: string array)
        : Result<DeliverResult, string> =
        ignore gitCommonDir
        ignore workId
        ignore execSessionId
        ignore observedPrompt
        ignore blockers
        retired (): Result<DeliverResult, string>

    let bindObservedExecutionSession
        (gitCommonDir: string)
        (workId: string)
        (operationId: string)
        (execSessionId: string)
        : Result<unit, string> =
        ignore gitCommonDir
        ignore workId
        ignore operationId
        ignore execSessionId
        retired (): Result<unit, string>

    // work-028: at most one pending ask delegation per work. The tracker
    // is process-local memory — crash recovery has no hanging ask state
    // (work-035), so this never becomes a durable state machine.
    // DSL-MUTABLE: resource — in-flight ask delegation gate
    let private pendingAskGate = obj ()
    // DSL-MUTABLE: resource — works with one pending ask delegation
    let private pendingAskWorks = System.Collections.Generic.HashSet<string>()

    let private tryBeginPendingAsk (workId: string) : bool =
        lock pendingAskGate (fun () -> pendingAskWorks.Add workId)

    let private clearPendingAsk (workId: string) : unit =
        lock pendingAskGate (fun () -> pendingAskWorks.Remove workId |> ignore)

    // Snapshot has no delivered mark. Do not treat delivery authorization as already delivered.
    let private validateAskWork
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (intent: string)
        : Result<unit, string> =
        if String.IsNullOrWhiteSpace intent then
            Error "Intent cannot be empty"
        elif work.ActiveId.IsNone then
            Error "No active incumbency found for this work"
        else
            Ok()

    let private captureDelegation (delegateTo: string -> Task<string>) (intent: string) : Task<Result<string, string>> =
        task {
            try
                let! record = delegateTo intent
                return Ok record
            with ex ->
                return Error(sprintf "DevOps delegation failed: %s" ex.Message)
        }

    // The binding is persisted single-flight after the delegation returns.
    // An already-bound work is Done and writes nothing (work-028).
    let private ensureDevOpsBound
        (gitCommonDir: string)
        (workId: string)
        (incumbencyId: string)
        : Task<Result<unit, string>> =
        task {
            let requestId = workId + ":bind:" + incumbencyId

            let command =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.command
                    "BindDevOps"
                    [ "trigger", box "Ask"
                      "devopsId", box (PlanWorkOpen.deriveDevOpsId workId)
                      "target", null ]

            let receipt =
                Wanxiangshu.Mission.Lifecycle.DurableWorkHost.execute gitCommonDir workId requestId command

            if
                receipt.Status = "Committed"
                || receipt.Status = "Replayed"
                || receipt.Status = "Observed"
            then
                return Ok()
            else
                return
                    Error(
                        sprintf
                            "Failed to bind plan DevOps: %s"
                            (if receipt.Reason = "" then
                                 receipt.Status
                             else
                                 receipt.Reason)
                    )
        }

    let private bindDevOpsAndComplete
        (gitCommonDir: string)
        (workId: string)
        (activeId: string)
        (record: string)
        : Task<Result<AskResult, string>> =
        task {
            match! ensureDevOpsBound gitCommonDir workId activeId with
            | Error err -> return Error err
            | Ok() ->
                return
                    Ok
                        { Kind = "delegated"
                          View = record
                          Status = "completed" }
        }

    let private finishPendingAsk
        (gitCommonDir: string)
        (workId: string)
        (activeId: string)
        (delegation: Result<string, string>)
        : Task<Result<AskResult, string>> =
        task {
            match delegation with
            | Error err -> return Error err
            | Ok record when String.IsNullOrWhiteSpace record ->
                return Error "The DevOps delegation returned an empty delivered view"
            | Ok record -> return! bindDevOpsAndComplete gitCommonDir workId activeId record
        }

    let private runPendingAsk
        (gitCommonDir: string)
        (workId: string)
        (activeId: string)
        (intent: string)
        (delegateTo: string -> Task<string>)
        : Task<Result<AskResult, string>> =
        task {
            try
                let! delegation = captureDelegation delegateTo intent
                let! completed = finishPendingAsk gitCommonDir workId activeId delegation
                return completed
            finally
                clearPendingAsk workId
        }

    let private beginPendingAsk
        (gitCommonDir: string)
        (workId: string)
        (activeId: string)
        (intent: string)
        (delegateTo: string -> Task<string>)
        : Task<Result<AskResult, string>> =
        if not (tryBeginPendingAsk workId) then
            Task.FromResult(
                Error(sprintf "An ask delegation is already pending for this work (%s); wait for it to finish" workId)
            )
        else
            runPendingAsk gitCommonDir workId activeId intent delegateTo

    let executeAsk
        (gitCommonDir: string)
        (workId: string)
        (intent: string)
        (delegateTo: string -> Task<string>)
        : Task<Result<AskResult, string>> =
        let validated =
            match lifecycleWorkOf gitCommonDir workId with
            | None -> Error "lifecycle work is missing"
            | Some work -> validateAskWork work intent |> Result.map (fun () -> work)

        match validated with
        | Error err -> Task.FromResult(Error err)
        | Ok work -> beginPendingAsk gitCommonDir workId (Option.defaultValue "" work.ActiveId) intent delegateTo
