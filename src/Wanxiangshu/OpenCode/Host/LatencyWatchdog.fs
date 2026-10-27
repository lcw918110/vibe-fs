namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core

type LatencyWatchdogPorts =
    { Now: unit -> float
      Schedule: float -> (unit -> unit) -> IDisposable
      Abort: string -> Task<bool>
      Continue: string -> string -> Task<string>
      Managed: string -> bool
      Text: string -> string
      Diagnostic: string -> string -> unit }

type RequestLatencyDetector =
    abstract ThresholdMs: sessionId: string -> float
    abstract ObserveCompleted: sessionId: string * durationMs: float -> unit
    abstract ObserveCensored: sessionId: string * censoredMs: float -> unit
    abstract Forget: sessionId: string -> unit

/// One in-flight request measurement per session.
/// Generation separates one attempt's timer callback from the next attempt's
/// state, so a late timer never interrupts or samples a newer request.
type private LatencyMeasurement =
    { Generation: int
      StartedAt: float
      ThresholdMs: float
      Armed: bool
      Idle: bool
      Aborting: bool
      ProviderRun: string option
      Timer: IDisposable option }

/// How a terminal event relates to the session's in-flight measurement.
type private TerminalVerdict =
    | NoInFlightMeasurement
    | Sampled of LatencyMeasurement
    | ForeignProviderRun
    | DroppedUnattributable

/// Per-session request-latency watchdog.
///
/// Owns exactly the state machine: when a managed session's request exceeds the
/// session's latency threshold (from the injected detector), it interrupts the
/// request once and sends a continuation prompt that preserves the work already
/// produced. The clock, timers, interrupt, continuation, prose and diagnostics
/// all live behind LatencyWatchdogPorts; the latency statistics live in the
/// library detector. Nothing here reads a clock or a global directly.
///
/// Requests and terminals are paired by the provider run identity (the
/// assistant message's own id), which is unique per provider attempt. The
/// physical user message id is shared by every provider step of the same turn
/// and cannot pair one terminal to one attempt. When either side of a terminal
/// lacks that identity, the sample is dropped, never attributed.
[<AttachMembers>]
type LatencyWatchdog(ports: LatencyWatchdogPorts, detector: RequestLatencyDetector) =

    let gate = obj ()
    let measurements = Dictionary<string, LatencyMeasurement>()
    let mutable generation = 0
    let mutable disposed = false

    let diagnostic kind reason =
        try
            ports.Diagnostic kind reason
        with _ ->
            ()

    let disposeQuietly (handle: IDisposable option) =
        match handle with
        | None -> ()
        | Some handle ->
            try
                handle.Dispose()
            with _ ->
                ()

    /// The timer body. Everything it does re-validates the generation first,
    /// because the host may have completed, cancelled or replaced the request
    /// while the callback was pending or while Abort was in flight.
    let fire (sessionId: string) (thisGeneration: int) (thresholdMs: float) =
        if not disposed then
            task {
                try
                    let armed =
                        lock gate (fun () ->
                            match measurements.TryGetValue sessionId with
                            | true, recorded when
                                recorded.Generation = thisGeneration && recorded.Armed
                                ->
                                // Disarm and drop the timer handle first: the
                                // interrupt is issued at most once per attempt,
                                // even if Abort hangs or throws.
                                disposeQuietly recorded.Timer
                                measurements[sessionId] <- { recorded with Armed = false; Aborting = true; Timer = None }
                                true
                            | _ -> false)

                    if armed then
                        let! accepted = ports.Abort sessionId

                        // Abort raced with a real completion: the host may have
                        // settled the request while Abort was in flight. Only
                        // the attempt that still owns the measurement records
                        // a censored sample; an attempt that already completed
                        // or was cancelled keeps its own accounting, so one
                        // request is never counted both complete and censored.
                        let stillOurs =
                            lock gate (fun () ->
                                match measurements.TryGetValue sessionId with
                                | true, recorded when recorded.Generation = thisGeneration -> Some recorded
                                | _ -> None)

                        match accepted, stillOurs with
                        | true, Some recorded ->
                            let elapsed = ports.Now() - recorded.StartedAt
                            detector.ObserveCensored(sessionId, max elapsed thresholdMs)
                            let removed =
                                lock gate (fun () ->
                                    match measurements.TryGetValue sessionId with
                                    | true, current when current.Generation = thisGeneration ->
                                        measurements.Remove(sessionId) |> ignore
                                        true
                                    | _ -> false)

                            if removed then
                                let! error = ports.Continue sessionId (ports.Text sessionId)
                                if not (String.IsNullOrEmpty error) then
                                    diagnostic "latency-degeneration-continue-failed" error
                        | true, None -> ()
                        | false, Some _ ->
                            lock gate (fun () ->
                                match measurements.TryGetValue sessionId with
                                | true, current when current.Generation = thisGeneration ->
                                    if current.Idle then
                                        measurements.Remove(sessionId) |> ignore
                                    else
                                        measurements[sessionId] <- { current with Aborting = false }
                                | _ -> ())

                            diagnostic "latency-degeneration-abort-refused" sessionId
                        | false, None -> ()
                with error ->
                    diagnostic "latency-degeneration-timer-failed" error.Message
            }
            |> ignore<Task<unit>>

    let startMeasurement (sessionId: string) (providerRun: string) =
        let threshold = detector.ThresholdMs sessionId
        generation <- generation + 1
        let thisGeneration = generation
        let startedAt = ports.Now()
        let timer = ports.Schedule threshold (fun () -> fire sessionId thisGeneration threshold)

        measurements[sessionId] <-
            { Generation = thisGeneration
              StartedAt = startedAt
              ThresholdMs = threshold
              Armed = true
              Idle = false
              Aborting = false
              ProviderRun = Some providerRun
              Timer = Some timer }

    /// A provider attempt starts. Unmanaged sessions and duplicate starts are
    /// ignored; an existing measurement keeps its start. A start without the
    /// provider run identity cannot be paired with any terminal, so no
    /// measurement is created at all. A start of a different provider run in
    /// the same session means the previous attempt's terminal was never
    /// observed: its measurement is dropped without a sample.
    member _.Begin(sessionId: string, providerRun: string option) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) && ports.Managed sessionId then
            match providerRun with
            | None -> ()
            | Some run ->
                lock gate (fun () ->
                    if not disposed then
                        match measurements.TryGetValue sessionId with
                        | true, recorded when recorded.ProviderRun = Some run -> ()
                        | true, recorded ->
                            disposeQuietly recorded.Timer
                            measurements.Remove(sessionId) |> ignore

                            diagnostic
                                "latency-degeneration-sample-dropped"
                                (sessionId + ": superseded without terminal")

                            startMeasurement sessionId run
                        | false, _ -> startMeasurement sessionId run)

    /// A terminal assistant message for this session. Only a measurement that
    /// is still in flight and provably belongs to the same provider run records
    /// a complete sample. A terminal of a different provider run is ignored and
    /// leaves the in-flight measurement untouched. A terminal whose identity is
    /// missing on either side proves nothing: the measurement is dropped
    /// without a sample rather than mis-attributed or left armed.
    member _.Complete(sessionId: string, providerRun: string option) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            let verdict =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | false, _ -> NoInFlightMeasurement
                    | true, recorded ->
                        match recorded.ProviderRun, providerRun with
                        | Some recordedRun, Some completingRun when recordedRun = completingRun ->
                            measurements.Remove(sessionId) |> ignore
                            disposeQuietly recorded.Timer
                            Sampled recorded
                        | Some _, Some _ -> ForeignProviderRun
                        | _ ->
                            measurements.Remove(sessionId) |> ignore
                            disposeQuietly recorded.Timer
                            DroppedUnattributable)

            match verdict with
            | Sampled recorded ->
                detector.ObserveCompleted(sessionId, ports.Now() - recorded.StartedAt)
            | ForeignProviderRun -> ()
            | DroppedUnattributable ->
                diagnostic
                    "latency-degeneration-sample-dropped"
                    (sessionId + ": terminal without request identity")
            | NoInFlightMeasurement -> ()

    /// An observed failed or cancelled provider attempt has ended, but is not
    /// a completed latency sample. Ignore a late terminal from an older run.
    member _.Discard(sessionId: string, providerRun: string option) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            match providerRun with
            | None -> ()
            | Some observedRun ->
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | true, recorded when recorded.ProviderRun = Some observedRun ->
                        measurements.Remove(sessionId) |> ignore
                        disposeQuietly recorded.Timer
                    | _ -> ())

    /// Session deletion: drop the measurement, record nothing.
    member _.Cancel(sessionId: string) =
        if not (String.IsNullOrWhiteSpace sessionId) then
            lock gate (fun () ->
                match measurements.TryGetValue sessionId with
                | true, recorded ->
                    measurements.Remove(sessionId) |> ignore
                    disposeQuietly recorded.Timer
                | false, _ -> ())

    /// Idle drops an armed measurement. An interrupt in flight keeps its
    /// measurement until its receipt decides whether to continue.
    member _.OnIdle(sessionId: string) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            let removed =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | true, recorded when not recorded.Aborting ->
                        measurements.Remove(sessionId) |> ignore
                        disposeQuietly recorded.Timer
                        true
                    | true, recorded ->
                        measurements[sessionId] <- { recorded with Idle = true }
                        false
                    | _ -> false)

            if removed then
                diagnostic "latency-degeneration-idle-without-completion" sessionId

    /// The session left the host: its latency statistics are stale.
    member _.ForgetDetector(sessionId: string) =
        if not (String.IsNullOrWhiteSpace sessionId) then
            try
                detector.Forget sessionId
            with _ ->
                ()

    member _.Snapshot() : obj =
        lock gate (fun () ->
            let entries =
                [| for KeyValue (sessionId, recorded) in measurements ->
                       {| SessionId = sessionId
                          StartedAt = recorded.StartedAt
                          ThresholdMs = recorded.ThresholdMs
                          Armed = recorded.Armed |} |]

            box entries)

    member _.Dispose() =
        if not disposed then
            lock gate (fun () ->
                disposed <- true

                for KeyValue (_sessionId, recorded) in measurements do
                    disposeQuietly recorded.Timer

                measurements.Clear())

/// Positional factory for JS consumers: the compiled record field names of
/// LatencyWatchdogPorts are not a stable contract, the factory's parameter
/// order is.
module LatencyWatchdogFactory =

    let createLatencyWatchdog
        (now: unit -> float)
        (schedule: float -> (unit -> unit) -> IDisposable)
        (abort: string -> Task<bool>)
        (continueAction: string -> string -> Task<string>)
        (managed: string -> bool)
        (text: string -> string)
        (diagnostic: string -> string -> unit)
        (detector: RequestLatencyDetector)
        : LatencyWatchdog =
        let ports =
            { Now = now
              Schedule = schedule
              Abort = abort
              Continue = continueAction
              Managed = managed
              Text = text
              Diagnostic = diagnostic }

        LatencyWatchdog(ports, detector)
