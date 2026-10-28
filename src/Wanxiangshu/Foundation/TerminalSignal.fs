namespace Wanxiangshu.Foundation.TerminalSignal

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type TerminalStop =
    {
        Reason: string
        AuthorityRootUserMessageId: AuthorityRootUserMessageId option
        /// The provider run that was interrupted, when this stop IS a designed-in
        /// delivery interrupt. `None` for every abort unrelated to a delivery
        /// interrupt — never inferred. This carries an existing identity to the
        /// settlement point; it is NOT a terminal fact source and never decides
        /// whether a run is closed (authority closure is decided only by durable
        /// `Authority*Terminal` facts).
        ProviderRun: ProviderRunIdentity option
    }

[<RequireQualifiedAccess>]
module TerminalStop =
    let session reason =
        { Reason = reason
          AuthorityRootUserMessageId = None
          ProviderRun = None }

    let forAuthority authorityRoot reason =
        { Reason = reason
          AuthorityRootUserMessageId = Some authorityRoot
          ProviderRun = None }

    /// A designed-in delivery interrupt: the stop carries the provider run of
    /// the turn that was interrupted, so settlement can match the durable
    /// delivery witness by exact fence. Only the interrupt path uses this.
    let forAuthorityRun authorityRoot providerRun reason =
        { Reason = reason
          AuthorityRootUserMessageId = Some authorityRoot
          ProviderRun = Some providerRun }

    let belongsTo authorityRoot stop =
        stop.AuthorityRootUserMessageId = Some authorityRoot

type TerminalOutcome =
    | Completed of result: AgentRunResult
    | Aborted of stop: TerminalStop
    | Failed of stop: TerminalStop

type TerminalCompletionListener = SessionId -> TerminalOutcome -> unit

type IEventObservationPort =
    abstract SubscribeTerminalListener: listener: TerminalCompletionListener -> IDisposable
    abstract SubscribeFutureTerminalListener: listener: TerminalCompletionListener -> IDisposable
    abstract NotifyTerminal: sessionId: SessionId -> outcome: TerminalOutcome -> bool

/// The session-scoped terminal subscription contract, shared by the product and
/// the host adapter.
///
/// The neutral host ports carry no per-session terminal subscription (HNI A2,
/// owner ruling 2026-10-22), so the product owns the terminal facts and their
/// subscribers. The host adapter implements this contract over the product's
/// registry; it never holds a second copy of the facts.
type ISessionTerminalSource =
    inherit IEventObservationPort

    abstract SubscribeTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable
    abstract SubscribeFutureTerminal: sessionId: SessionId * listener: TerminalCompletionListener -> IDisposable
    abstract HasSubscriber: sessionId: SessionId -> bool

/// Latency-degeneration interrupts the host has sent to a managed session.
///
/// The interrupt is a Host session abort, so the session reports an `Aborted`
/// terminal; but the latency watchdog owns that abort and either continues the
/// same session or returns `Failed`. A consumer that fails fast on `Aborted`
/// (Advisor consult, SyncDelegate call) consumes one interrupt per `Aborted`
/// it ignores, instead of treating the watchdog's own abort as the answer.
[<RequireQualifiedAccess>]
module DegenerationInterrupts =
    // DSL-MUTABLE: resource — per-session count of latency-degeneration interrupts sent to the Host
    let private sent = System.Collections.Generic.Dictionary<string, int>()

    let private countOf (sessionId: SessionId) =
        match sent.TryGetValue(SessionId.value sessionId) with
        | true, count -> count
        | _ -> 0

    let record (sessionId: SessionId) : unit =
        sent[SessionId.value sessionId] <- countOf sessionId + 1

    /// The Host refused the interrupt: it produced no abort.
    let unrecord (sessionId: SessionId) : unit =
        match countOf sessionId with
        | count when count > 1 -> sent[SessionId.value sessionId] <- count - 1
        | _ -> sent.Remove(SessionId.value sessionId) |> ignore

    let count (sessionId: SessionId) : int = countOf sessionId

    let forget (sessionId: SessionId) : unit =
        sent.Remove(SessionId.value sessionId) |> ignore

    /// One consumer's view: `IsOwnAbort()` is true once per interrupt recorded
    /// after the cursor was opened.
    type Cursor(sessionId: SessionId) =
        let mutable seen = countOf sessionId

        member _.IsOwnAbort() : bool =
            let current = countOf sessionId

            if current > seen then
                seen <- seen + 1
                true
            else
                false

/// Late consultation answers the transform boundary stopped (WHAT[delegation-039]).
///
/// An answer queued while the agent was still writing its closing prose wakes
/// the Host loop straight into that answer: the session never goes idle on the
/// finished report, the stop aborts the queued step, and the only turn after
/// the answer is an empty aborted one. Reconcile reads this record so the run's
/// terminal is the finished report the stop preserved, not the abort it made.
[<RequireQualifiedAccess>]
module CompletedContinuationStops =
    // DSL-MUTABLE: resource — per-session physical user ids whose provider step the late-consultation stop aborted
    let private stopped =
        System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>()

    let record (sessionId: SessionId) (physicalUserMessageId: string) : unit =
        let key = SessionId.value sessionId

        match stopped.TryGetValue key with
        | true, set -> set.Add physicalUserMessageId |> ignore
        | _ ->
            let set = System.Collections.Generic.HashSet<string>()
            set.Add physicalUserMessageId |> ignore
            stopped[key] <- set

    let contains (sessionId: SessionId) (physicalUserMessageId: string) : bool =
        match stopped.TryGetValue(SessionId.value sessionId) with
        | true, set -> set.Contains physicalUserMessageId
        | _ -> false

    let forget (sessionId: SessionId) : unit =
        stopped.Remove(SessionId.value sessionId) |> ignore
