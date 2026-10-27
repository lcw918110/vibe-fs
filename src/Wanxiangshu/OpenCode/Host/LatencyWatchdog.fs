namespace Wanxiangshu.OpenCode

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core

type LatencyWatchdogPorts =
    { Now: unit -> float
      Schedule: float -> (unit -> unit) -> IDisposable
      Abort: string -> Task<bool>
      Continue: string -> string -> (unit -> unit) -> (string -> unit) -> Task<string>
      Return: string -> string -> unit
      DropObserver: string -> unit
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

    [<Emit("Promise.resolve($1).then($2, $3)")>]
    let observe (pending: Task<'t>) (onSuccess: 't -> unit) (onFailure: obj -> unit) : unit = jsNative

    let gate = obj ()
    let recovering = Dictionary<string, int>()
    let measurements = Dictionary<string, LatencyMeasurement>()
    let mutable generation = 0
    let mutable disposed = false

    let diagnostic kind reason =
        try
            ports.Diagnostic kind reason
        with _ ->
            ()

    let validRecovery sessionId turn =
        lock gate (fun () ->
            not disposed
            && (match recovering.TryGetValue sessionId with
                | true, current -> current = turn
                | _ -> false))

    let finishRecovery sessionId turn =
        lock gate (fun () ->
            match recovering.TryGetValue sessionId with
            | true, current when current = turn ->
                recovering.Remove sessionId |> ignore
                true
            | _ -> false)

    let failRecovery sessionId turn reason =
        if finishRecovery sessionId turn then
            diagnostic "latency-degeneration-returned" reason
            try ports.Return sessionId reason
            with error -> diagnostic "latency-degeneration-return-failed" error.Message

    let revokeRecovery sid =
        if recovering.Remove sid then
            try ports.DropObserver sid
            with error -> diagnostic "latency-degeneration-dispose-observer-failed" error.Message

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
            let armed =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | true, measured when measured.Generation = thisGeneration && measured.Armed ->
                        disposeQuietly measured.Timer
                        measurements[sessionId] <- { measured with Armed = false; Aborting = true; Timer = None }
                        recovering[sessionId] <- thisGeneration
                        true
                    | _ -> false)

            if armed then
                // Callback-driven handoff: no Task is awaited by this timer,
                // the event hook, or the physical acceptance dispatcher.
                let onAccepted () =
                    if finishRecovery sessionId thisGeneration then
                        diagnostic "latency-degeneration-resumed" sessionId

                let onContinueFailure reason =
                    failRecovery sessionId thisGeneration ("continuation: " + reason)

                let onAbortAccepted accepted =
                    let stillOurs =
                        lock gate (fun () ->
                            match measurements.TryGetValue sessionId with
                            | true, recorded when recorded.Generation = thisGeneration -> Some recorded
                            | _ -> None)

                    match stillOurs with
                    | None ->
                        finishRecovery sessionId thisGeneration |> ignore
                    | Some recorded when not accepted ->
                        // A definite refusal leaves the original request alive,
                        // unless the Host has already observed it idle. The
                        // latter has no Provider left to finish the work, so
                        // the original managed child must return a terminal.
                        let idled =
                            lock gate (fun () ->
                                match measurements.TryGetValue sessionId with
                                | true, current when current.Generation = thisGeneration ->
                                    if current.Idle then
                                        measurements.Remove(sessionId) |> ignore
                                        true
                                    else
                                        measurements[sessionId] <- { current with Aborting = false }
                                        false
                                | _ -> false)
                        if idled then
                            failRecovery sessionId thisGeneration "interrupt refused after session became idle"
                        // Otherwise preserve the original request and its
                        // recovery ownership until a real terminal, a newer
                        // provider start, a cancellation, or an idle event.
                        // No fallback timer is created.
                        diagnostic "latency-degeneration-abort-refused" sessionId
                    | Some recorded ->
                        // Claim the handoff before invoking any external callback:
                        // a synchronous Host acceptance must not be lost.
                        let released =
                            lock gate (fun () ->
                                match measurements.TryGetValue sessionId with
                                | true, current when current.Generation = thisGeneration ->
                                    measurements.Remove(sessionId) |> ignore
                                    true
                                | _ -> false)

                        if released && validRecovery sessionId thisGeneration then
                            try
                                detector.ObserveCensored(
                                    sessionId,
                                    max (ports.Now() - recorded.StartedAt) thresholdMs
                                )
                                observe
                                    (ports.Continue sessionId (ports.Text sessionId) onAccepted onContinueFailure)
                                    (fun reason ->
                                        if not (String.IsNullOrEmpty reason) then
                                            onContinueFailure reason)
                                    (fun error -> onContinueFailure (string error))
                            with error ->
                                onContinueFailure error.Message

                try
                    observe
                        (ports.Abort sessionId)
                        onAbortAccepted
                        (fun error ->
                            let stillOurs =
                                lock gate (fun () ->
                                    match measurements.TryGetValue sessionId with
                                    | true, current when current.Generation = thisGeneration ->
                                        measurements.Remove(sessionId) |> ignore
                                        true
                                    | _ -> false)
                            if stillOurs then
                                failRecovery sessionId thisGeneration ("interrupt: " + string error))
                with error ->
                    lock gate (fun () ->
                        match measurements.TryGetValue sessionId with
                        | true, current when current.Generation = thisGeneration ->
                            measurements.Remove(sessionId) |> ignore
                        | _ -> ())
                    failRecovery sessionId thisGeneration ("interrupt: " + error.Message)

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
                            revokeRecovery sessionId
                            disposeQuietly recorded.Timer
                            measurements.Remove(sessionId) |> ignore

                            diagnostic
                                "latency-degeneration-sample-dropped"
                                (sessionId + ": superseded without terminal")

                            startMeasurement sessionId run
                        | false, _ ->
                            revokeRecovery sessionId
                            startMeasurement sessionId run)

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
                if finishRecovery sessionId recorded.Generation then
                    try ports.DropObserver sessionId
                    with error -> diagnostic "latency-degeneration-dispose-observer-failed" error.Message
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
                        revokeRecovery sessionId
                        disposeQuietly recorded.Timer
                    | _ -> ())

    /// Session deletion: drop the measurement, record nothing.
    member _.Cancel(sessionId: string) =
        if not (String.IsNullOrWhiteSpace sessionId) then
            lock gate (fun () ->
                revokeRecovery sessionId
                match measurements.TryGetValue sessionId with
                | true, recorded ->
                    measurements.Remove(sessionId) |> ignore
                    disposeQuietly recorded.Timer
                | false, _ -> ())

    /// Idle drops an armed measurement. An interrupt in flight keeps its
    /// measurement until its receipt decides whether to continue.
    member _.OnIdle(sessionId: string) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            let removed, rejectedRun =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | true, recorded when not recorded.Aborting ->
                        measurements.Remove(sessionId) |> ignore
                        disposeQuietly recorded.Timer
                        true, (if not recorded.Armed then Some recorded.Generation else None)
                    | true, recorded ->
                        measurements[sessionId] <- { recorded with Idle = true }
                        false, None
                    | _ -> false, None)

            if removed then
                diagnostic "latency-degeneration-idle-without-completion" sessionId
            match rejectedRun with
            | Some run ->
                failRecovery sessionId run "interrupt was refused; source request became idle without terminal"
            | None -> ()

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

                measurements.Clear()
                for session in recovering.Keys |> Seq.toArray do revokeRecovery session
                recovering.Clear())

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
              Continue = fun session prompt onAccepted _lateRefused ->
                  task {
                      let! error = continueAction session prompt
                      if String.IsNullOrEmpty error then onAccepted ()
                      return error
                  }
              Return = fun session reason -> diagnostic "latency-degeneration-returned" (session + ": " + reason)
              DropObserver = fun _ -> ()
              Managed = managed
              Text = text
              Diagnostic = diagnostic }

        LatencyWatchdog(ports, detector)


    /// Exercises the same production actor with observed physical acceptance.
    /// There is deliberately no wall-clock deadline in this port.
    let createObservedLatencyWatchdog
        (now: unit -> float)
        (schedule: float -> (unit -> unit) -> IDisposable)
        (abort: string -> Task<bool>)
        (continueObserved: string -> string -> (unit -> unit) -> (string -> unit) -> Task<string>)
        (managed: string -> bool)
        (text: string -> string)
        (diagnostic: string -> string -> unit)
        (returnFailure: string -> string -> unit)
        (dropObserver: string -> unit)
        (detector: RequestLatencyDetector)
        : LatencyWatchdog =
        LatencyWatchdog(
            { Now = now
              Schedule = schedule
              Abort = abort
              Continue = continueObserved
              Managed = managed
              Text = text
              Diagnostic = diagnostic
              Return = returnFailure
              DropObserver = dropObserver },
            detector
        )
