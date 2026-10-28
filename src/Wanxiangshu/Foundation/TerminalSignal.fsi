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
    val session: reason: string -> TerminalStop
    val forAuthority: authorityRoot: AuthorityRootUserMessageId -> reason: string -> TerminalStop

    /// A designed-in delivery interrupt: the stop carries the provider run of
    /// the turn that was interrupted, so settlement can match the durable
    /// delivery witness by exact fence. Only the interrupt path uses this.
    val forAuthorityRun:
        authorityRoot: AuthorityRootUserMessageId -> providerRun: ProviderRunIdentity -> reason: string -> TerminalStop

    val belongsTo: authorityRoot: AuthorityRootUserMessageId -> stop: TerminalStop -> bool

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
/// The watchdog owns the resulting `Aborted`: it continues the session or
/// returns `Failed`. Fail-fast consumers ignore one `Aborted` per interrupt.
[<RequireQualifiedAccess>]
module DegenerationInterrupts =
    val record: sessionId: SessionId -> unit
    val unrecord: sessionId: SessionId -> unit
    val count: sessionId: SessionId -> int
    val forget: sessionId: SessionId -> unit

    [<Class>]
    type Cursor =
        new: sessionId: SessionId -> Cursor
        member IsOwnAbort: unit -> bool
