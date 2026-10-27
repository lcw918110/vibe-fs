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
open Fable.Core
open Fable.Core.JsInterop

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
module private FlightInterop =
    [<Import("ManagedChatExecutionFlight", "wanxiangli/chat-execution")>]
    let flightCtor: obj = jsNative

    let create () : obj =
        createNew flightCtor [||]

type private ManagedChatExecutionFlight'() =
    let inner: obj = FlightInterop.create ()

    member _.BeginFlight(runtimeId: string, sessionId: string, physicalUserMessageId: string, work: obj) : unit =
        inner?beginFlight(runtimeId, sessionId, physicalUserMessageId, work)

    member _.Finish(runtimeId: string, sessionId: string, physicalUserMessageId: string) : bool =
        inner?finish(runtimeId, sessionId, physicalUserMessageId)

    member _.TryGet(runtimeId: string, sessionId: string, physicalUserMessageId: string) : obj option =
        let res: obj = inner?get(runtimeId, sessionId, physicalUserMessageId)
        if isNull res then None else Some res

    member _.Clear() : unit =
        inner?clear()

    member _.Run(runtimeId: RuntimeId, key: ChatExecutionKey, operation: unit -> Task<'value>) : Task<'value> =
        let rid = RuntimeId.value runtimeId
        let sid = SessionId.value key.SessionId
        let msgId = PhysicalUserMessageId.value key.PhysicalUserMessageId

        let preceding : Task =
            let existing: obj = inner?get(rid, sid, msgId)
            if isNull existing then AsyncSupport.completedTask () else unbox<Task> existing

        // DSL-MUTABLE: single-flight
        let mutable marker: Task = null

        let started =
            Defer.defer (fun () ->
                task {
                    try
                        do! preceding
                        return! operation ()
                    finally
                        let cur: obj = inner?get(rid, sid, msgId)
                        if Object.ReferenceEquals(cur, marker) then
                            inner?finish(rid, sid, msgId) |> ignore
                })

        marker <- started :> Task
        inner?beginFlight(rid, sid, msgId, marker)
        started

module private ManagedChatExecutionFlight' =
    let private instance = ManagedChatExecutionFlight'()

    let run (runtimeId: RuntimeId) (key: ChatExecutionKey) (operation: unit -> Task<'value>) : Task<'value> =
        instance.Run(runtimeId, key, operation)

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
