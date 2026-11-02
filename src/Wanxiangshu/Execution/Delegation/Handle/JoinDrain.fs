namespace Wanxiangshu.Execution.Delegation.Handle

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery

module JoinDrain =
    let orderedCandidates projection =
        let history = HandleProjection.native projection
        let raw: obj array = history?orderedCandidates ()
        raw |> Array.map HandleProjection.recordOf |> Array.toList

    let private failure (raw: obj) =
        match unbox<string> raw?kind with
        | "TerminalMaterializationFailed" -> ForkError.TerminalMaterializationFailed(unbox raw?error)
        | _ -> ForkError.NotFound(unbox raw?error)

    let private optional mapping (raw: obj) =
        if isNull raw then
            None
        else
            Some(mapping (unbox<string> raw))

    let private deliveredOutcome agentId role (payload: obj) =
        match unbox<string> payload?kind with
        | "completed" ->
            AgentCompleted
                { AgentId = agentId
                  RunId = payload?runId
                  Role = role
                  View = payload?view
                  ChildSessionId = optional SessionId.create payload?childSessionId
                  AuthorityRoot = optional AuthorityRootUserMessageId.create payload?authorityRoot
                  ProviderRun = optional ProviderRunIdentity.create payload?providerRun
                  Directory = optional id payload?directory }
        | _ ->
            AgentFailed
                { AgentId = agentId
                  RunId = payload?runId
                  Role = Some role
                  Code = payload?code
                  Message = payload?message
                  ChildSessionId = optional SessionId.create payload?childSessionId }

    let private completion completedAt (raw: obj) : RunCompletion =
        let record = HandleProjection.recordOf raw?record
        let payload: obj = raw?payload

        let runId, outcome =
            if isNull payload then
                unbox<string> raw?runId,
                AgentCompletion.abandoned (unbox<string> raw?record?handle?id) (unbox<string> raw?abandoned)
            else
                unbox<string> payload?runId, deliveredOutcome raw?record?handle?id record.CanonicalRole payload

        { RunId = runId
          AgentName = record.TargetAgent
          Role = record.CanonicalRole
          Outcome = outcome
          CompletedAt = completedAt }

    let reconcileFalseAborts durable parentId =
        task {
            let controller = HandleDurable.create durable parentId
            let! outcome = unbox<Task<obj>> (controller?reconcileFalseAborts ())

            return
                if unbox<bool> outcome?ok then
                    Ok()
                else
                    Error(failure outcome)
        }

    let drainFromJournalWhere
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (maxCount: int)
        (completedAt: DateTimeOffset)
        (accept: HandleRecord -> bool)
        : Task<Result<RunCompletion list, ForkError>> =
        task {
            let controller = HandleDurable.create durable parentId
            let predicate raw = accept (HandleProjection.recordOf raw)
            let! outcome = unbox<Task<obj>> (controller?drain (maxCount, predicate))

            if unbox<bool> outcome?ok then
                return
                    Ok(
                        (unbox<obj array> outcome?value)
                        |> Array.map (completion completedAt)
                        |> Array.toList
                    )
            else
                return Error(failure outcome)
        }
