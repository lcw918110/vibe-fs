namespace Wanxiangshu.OpenCode

open System
open Fable.Core

/// Host-side ports for the per-session request latency watchdog.
/// Every physical effect (clock, timer, interrupt, continuation, prose,
/// diagnostics) is injected; the watchdog owns only its state machine.
type LatencyWatchdogPorts =
    { Now: unit -> float
      Schedule: float -> (unit -> unit) -> IDisposable
      Abort: string -> System.Threading.Tasks.Task<bool>
      Continue: string -> string -> (unit -> unit) -> (string -> unit) -> System.Threading.Tasks.Task<string>
      Return: string -> string -> unit
      DropObserver: string -> unit
      Managed: string -> bool
      Text: string -> string
      Diagnostic: string -> string -> unit }

/// The library-owned per-session latency statistics (wanxiangli/degeneration).
type RequestLatencyDetector =
    abstract ThresholdMs: sessionId: string -> float
    abstract ObserveCompleted: sessionId: string * durationMs: float -> unit
    abstract ObserveCensored: sessionId: string * censoredMs: float -> unit
    abstract Forget: sessionId: string -> unit

[<AttachMembers>]
type LatencyWatchdog =
    new: ports: LatencyWatchdogPorts * detector: RequestLatencyDetector -> LatencyWatchdog
    /// A provider attempt starts. Unmanaged sessions, duplicate starts and
    /// starts without the provider run identity are ignored; an existing
    /// measurement keeps its start. A start of a different provider run in
    /// the same session drops the previous measurement without a sample.
    member Begin: sessionId: string * providerRun: string option -> unit
    /// A terminal assistant message. Records a complete sample only when the
    /// in-flight measurement provably belongs to the same provider run. A
    /// terminal of a different run is ignored and leaves the measurement
    /// untouched; a terminal whose identity is missing on either side drops
    /// the measurement without a sample.
    member Complete: sessionId: string * providerRun: string option -> unit
    /// A tool-calls step boundary of the exact provider run. Same as Complete,
    /// except that an interrupt already in flight keeps its recovery: the Host
    /// abort stops the session loop and the continuation is still owed.
    member CompleteStep: sessionId: string * providerRun: string option -> unit
    /// The Host starts executing a tool of this session: the model request of
    /// the current step has ended (OpenCode only completes the assistant
    /// message after its tools return). Ends the in-flight measurement as a
    /// step boundary; tool execution time is never measured or interrupted.
    member ToolStarted: sessionId: string -> unit
    /// End a failed/cancelled exact provider attempt without treating it as a complete sample.
    member Discard: sessionId: string * providerRun: string option -> unit
    /// React to a physically cancelled exact Provider request, including an abort with no Host receipt.
    member OnProviderCancelled: sessionId: string * providerRun: string option -> unit
    /// Session deletion: drop the measurement, record nothing.
    member Cancel: sessionId: string -> unit
    /// Idle drops an armed measurement; an interrupt in flight remains until
    /// its receipt decides whether to continue.
    member OnIdle: sessionId: string -> unit
    /// The session left the host: its latency statistics are stale.
    member ForgetDetector: sessionId: string -> unit
    /// Diagnostic view: one entry per in-flight measurement.
    member Snapshot: unit -> obj
    member Dispose: unit -> unit

/// Positional factory for JS consumers: the compiled record field names of
/// LatencyWatchdogPorts are not a stable contract, the factory's parameter
/// order is.
module LatencyWatchdogFactory =

    val createLatencyWatchdog:
        now: (unit -> float) ->
        schedule: (float -> (unit -> unit) -> IDisposable) ->
        abort: (string -> System.Threading.Tasks.Task<bool>) ->
        continueAction: (string -> string -> System.Threading.Tasks.Task<string>) ->
        managed: (string -> bool) ->
        text: (string -> string) ->
        diagnostic: (string -> string -> unit) ->
        detector: RequestLatencyDetector ->
            LatencyWatchdog


    val createObservedLatencyWatchdog:
        now: (unit -> float) ->
        schedule: (float -> (unit -> unit) -> IDisposable) ->
        abort: (string -> System.Threading.Tasks.Task<bool>) ->
        continueObserved:
            (string -> string -> (unit -> unit) -> (string -> unit) -> System.Threading.Tasks.Task<string>) ->
        managed: (string -> bool) ->
        text: (string -> string) ->
        diagnostic: (string -> string -> unit) ->
        returnFailure: (string -> string -> unit) ->
        dropObserver: (string -> unit) ->
        detector: RequestLatencyDetector ->
            LatencyWatchdog
