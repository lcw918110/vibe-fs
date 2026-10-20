namespace Wanxiangshu.Composition.Durable

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation.AsyncSupport
open Wanxiangshu.Foundation.Identity

open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Participant.Provider

[<RequireQualifiedAccess>]
type ManagedChatSupersessionError =
    | MissingAccepted of ChatExecutionKey
    | PreProviderSettlementFailed of PreProviderSettlementError
    | ProviderSettlementFailed of ManagedChatProviderLifecycleError

/// Durable binding of ChatExecution persistence records to the shared
/// `AgentJournal`. Every operation replays through the same snapshot+append
/// pair so the acceptance/lifecycle projections stay single-writer.
///
/// `ManagedChatExecutionFlight` lives in `Acceptance.fs` but is module-private
/// there. The same single-flight semantic is re-established here keyed by
/// `(runtimeId, key)`; identical call sites still serialize globally.
module private ManagedChatExecutionFlight' =
    let private gate = obj ()
    let private inFlight = Dictionary<RuntimeId * ChatExecutionKey, Task>()

    let private preceding (flightKey: RuntimeId * ChatExecutionKey) : Task =
        match inFlight.TryGetValue flightKey with
        | true, existing -> existing
        | false, _ -> AsyncSupport.completedTask ()

    let private removeIfCurrent (flightKey: RuntimeId * ChatExecutionKey) (marker: Task) =
        lock gate (fun () ->
            match inFlight.TryGetValue flightKey with
            | true, current when Object.ReferenceEquals(current, marker) -> inFlight.Remove flightKey |> ignore
            | _ -> ())

    let private start
        (flightKey: RuntimeId * ChatExecutionKey)
        (precedingFlight: Task)
        (operation: unit -> Task<'value>)
        : Task<'value> =
        // DSL-MUTABLE: single-flight
        let mutable marker: Task = null

        let started =
            Defer.defer (fun () ->
                task {
                    try
                        do! precedingFlight
                        return! operation ()
                    finally
                        removeIfCurrent flightKey marker
                })

        marker <- started :> Task
        inFlight.[flightKey] <- marker
        started

    let run (runtimeId: RuntimeId) (key: ChatExecutionKey) (operation: unit -> Task<'value>) : Task<'value> =
        lock gate (fun () ->
            let flightKey = runtimeId, key
            start flightKey (preceding flightKey) operation)

module ManagedChatAcceptance =

    let private forJournal (journal: AgentJournal) : ManagedChatAcceptancePersistence =
        { ReadExact =
            fun key ->
                AgentJournal.snapshot journal
                |> fun projection -> projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key
          AppendAccepted =
            fun key evidence ->
                task {
                    let! appended =
                        AgentJournal.appendAgent
                            (StreamId.Session key.SessionId)
                            None
                            (ChatExecutionFact.Accepted
                                {| SchemaVersion = 1
                                   Key = key
                                   Evidence = evidence |})
                            journal

                    return appended |> Result.map ignore
                } }

    let private acceptOnce
        (journal: AgentJournal)
        (key: ChatExecutionKey)
        (evidence: AcceptedChatExecutionEvidence)
        : Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>> =
        ManagedChatAcceptance.acceptWith (forJournal journal) key evidence

    /// Establish durable acceptance. Equal concurrent requests share the one
    /// physical append across every caller holding the same journal runtime.
    let accept
        (journal: AgentJournal)
        (key: ChatExecutionKey)
        (evidence: AcceptedChatExecutionEvidence)
        : Task<Result<ManagedChatAcceptanceWitness, ManagedChatAcceptanceError>> =
        ManagedChatExecutionFlight'.run (AgentJournal.runtimeId journal) key (fun () -> acceptOnce journal key evidence)

[<RequireQualifiedAccess>]
module ManagedChatProviderLifecycle =

    let internal forJournal (journal: AgentJournal) : ManagedChatProviderLifecyclePersistence =
        { ReadExact =
            fun key ->
                AgentJournal.snapshot journal
                |> fun projection -> projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key
          AppendFact =
            fun startedEvidence fact ->
                task {
                    let! appended =
                        AgentJournal.appendAgent
                            (StreamId.Session startedEvidence.Accepted.SessionId)
                            (Some startedEvidence.ProviderRun)
                            (AgentFact.ChatExecution fact)
                            journal

                    return appended |> Result.map ignore
                } }

    let providerStarted
        (journal: AgentJournal)
        (key: ChatExecutionKey)
        (acceptedEvidence: AcceptedChatExecutionEvidence)
        (providerRun: ProviderRunIdentity)
        (requestKind: ProviderRequestKind)
        =
        ManagedChatExecutionFlight'.run (AgentJournal.runtimeId journal) key (fun () ->
            ManagedChatProviderLifecycle.startWith (forJournal journal) key acceptedEvidence providerRun requestKind)

    let terminal
        (journal: AgentJournal)
        (key: ChatExecutionKey)
        (startedEvidence: ProviderStartedEvidence)
        (disposition: ChatExecutionTerminalDisposition)
        =
        ManagedChatExecutionFlight'.run (AgentJournal.runtimeId journal) key (fun () ->
            ManagedChatProviderLifecycle.terminalWith (forJournal journal) key startedEvidence disposition)

[<RequireQualifiedAccess>]
module PreProviderSettlement =

    let internal forJournal (journal: AgentJournal) : PreProviderSettlementPersistence =
        { ReadExact =
            fun key ->
                AgentJournal.snapshot journal
                |> fun projection -> projection.AgentProjections.ChatExecutions
                |> ChatExecutionProjection.byKey key
          AppendTerminal =
            fun key evidence disposition ->
                task {
                    let fact =
                        ChatExecutionFactCases.Terminal
                            {| SchemaVersion = 1
                               Key = key
                               Evidence = ChatExecutionTerminalEvidence.PreProvider evidence
                               Disposition = disposition |}

                    let! appended =
                        AgentJournal.appendAgent
                            (StreamId.Session key.SessionId)
                            None
                            (AgentFact.ChatExecution fact)
                            journal

                    return appended |> Result.map ignore
                } }

    let settle
        (journal: AgentJournal)
        (key: ChatExecutionKey)
        (evidence: AcceptedChatExecutionEvidence)
        (disposition: ChatExecutionTerminalDisposition)
        =
        ManagedChatExecutionFlight'.run (AgentJournal.runtimeId journal) key (fun () ->
            PreProviderSettlement.settleWith (forJournal journal) key evidence disposition)

[<RequireQualifiedAccess>]
module ManagedChatSupersession =

    let settle (journal: AgentJournal) (key: ChatExecutionKey) =
        ManagedChatExecutionFlight'.run (AgentJournal.runtimeId journal) key (fun () ->
            task {
                match
                    (AgentJournal.snapshot journal).AgentProjections.ChatExecutions
                    |> ChatExecutionProjection.byKey key
                with
                | None -> return Error(ManagedChatSupersessionError.MissingAccepted key)
                | Some state ->
                    let lifecycle =
                        match state with
                        | ChatExecutionState.Accepted _ -> DurableExecutionLifecycle.AcceptedBeforeProvider
                        | ChatExecutionState.Started _ -> DurableExecutionLifecycle.ProviderStarted
                        | ChatExecutionState.EndedBeforeStart _
                        | ChatExecutionState.EndedAfterStart _ -> DurableExecutionLifecycle.Terminal

                    let decision =
                        ExecutionFailurePolicy.decideSupersession key lifecycle CapacityOwnership.NoCapacityFence

                    match decision.Resolution, state with
                    | ExecutionFailureResolution.TerminalizeAcceptedPreProvider(_, disposition),
                      ChatExecutionState.Accepted accepted ->
                        let! settled =
                            PreProviderSettlement.settleWith
                                (PreProviderSettlement.forJournal journal)
                                key
                                accepted
                                disposition

                        return
                            settled
                            |> Result.map ignore
                            |> Result.mapError ManagedChatSupersessionError.PreProviderSettlementFailed
                    | ExecutionFailureResolution.TerminalizeProviderStarted(_, disposition),
                      ChatExecutionState.Started started ->
                        let! settled =
                            ManagedChatProviderLifecycle.terminalWith
                                (ManagedChatProviderLifecycle.forJournal journal)
                                key
                                started
                                disposition

                        return
                            settled
                            |> Result.map ignore
                            |> Result.mapError ManagedChatSupersessionError.ProviderSettlementFailed
                    | ExecutionFailureResolution.PreserveCurrentFact, _ -> return Ok()
                    | _ -> return invalidOp "supersession policy produced an incompatible execution disposition"
            })
