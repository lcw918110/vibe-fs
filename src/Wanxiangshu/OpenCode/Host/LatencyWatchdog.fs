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
      /// Parked after a final Completed terminal: the work was delivered, and
      /// only a newer Provider start proves the session loop went on.
      EndedFinal: bool
      /// The request already counted as a complete sample; a later
      /// continuation must not add a censored one for it.
      SampledComplete: bool
      ProviderRun: string option
      Timer: IDisposable option }

/// How a terminal event relates to the session's in-flight measurement.
type private TerminalVerdict =
    | NoInFlightMeasurement
    | Sampled of LatencyMeasurement
    /// Complete sample whose interrupt receipt is still pending (parked).
    | SampledParked of LatencyMeasurement
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
    /// Live recovery generations per session, newest first. A session can
    /// hold several at once: a second measurement's interrupt may be sent
    /// while the first interrupt's Host receipt is still pending, and each
    /// pending receipt settles its own recovery.
    let recovering = Dictionary<string, int list>()
    let measurements = Dictionary<string, LatencyMeasurement>()
    /// An interrupt already sent to the Host (Aborting) whose request was
    /// ended by a tool boundary, a failure terminal or a newer Provider start
    /// before the Host answered. The Host's AbortSession stops the whole
    /// session loop, so an accepted interrupt still owes a continuation; the
    /// measurement is parked here instead of being dropped with its recovery.
    let interruptedInFlight = Dictionary<string, LatencyMeasurement list>()
    /// The generation whose continuation is already dispatched and owns the
    /// session loop's resume. Other live receipts stay pending for their own
    /// settlement but must not dispatch a second physical continuation.
    let dispatched = Dictionary<string, int>()
    // DSL-MUTABLE: resource — attempt generations, the dispatched continuation
    // latch and the watchdog disposal latch
    let mutable generation = 0
    let mutable disposed = false

    let diagnostic kind reason =
        try
            ports.Diagnostic kind reason
        with _ ->
            ()

    let finishRecovery sessionId turn =
        lock gate (fun () ->
            match recovering.TryGetValue sessionId with
            | true, current when List.contains turn current ->
                let rest = current |> List.filter (fun gen -> gen <> turn)

                if List.isEmpty rest then
                    recovering.Remove sessionId |> ignore
                else
                    recovering[sessionId] <- rest

                match dispatched.TryGetValue sessionId with
                | true, owner when owner = turn -> dispatched.Remove sessionId |> ignore
                | _ -> ()

                true
            | _ -> false)

    let failRecovery sessionId turn reason =
        if finishRecovery sessionId turn then
            // The Failed return settles this degeneration episode: pending
            // receipts of other interrupts must not revive or re-settle it.
            lock gate (fun () ->
                recovering.Remove sessionId |> ignore
                dispatched.Remove sessionId |> ignore)

            diagnostic "latency-degeneration-returned" reason
            try ports.Return sessionId reason
            with error -> diagnostic "latency-degeneration-return-failed" error.Message

    /// A parked interrupt (see interruptedInFlight) keeps its recovery until
    /// the Host receipt decides; only Cancel and Dispose clear it first. A
    /// recovery without a parked entry has no pending receipt left to decide
    /// it (its measurement was dropped or superseded): revoke it, and release
    /// the dispatched continuation's observer with it.
    let revokeRecovery sid =
        let removedDispatched =
            lock gate (fun () ->
                match recovering.TryGetValue sid with
                | false, _ -> false
                | true, current ->
                    let parkedGenerations =
                        match interruptedInFlight.TryGetValue sid with
                        | true, parked -> parked |> List.map (fun entry -> entry.Generation) |> Set.ofList
                        | false, _ -> Set.empty

                    let kept = current |> List.filter (fun gen -> Set.contains gen parkedGenerations)
                    let removed = current |> List.filter (fun gen -> not (Set.contains gen parkedGenerations))

                    if List.isEmpty kept then
                        recovering.Remove sid |> ignore
                    else
                        recovering[sid] <- kept

                    let latchRemoved =
                        match dispatched.TryGetValue sid with
                        | true, owner when List.contains owner removed ->
                            dispatched.Remove sid |> ignore
                            true
                        | _ -> false

                    latchRemoved)

        if removedDispatched then
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

    /// Called under `gate` while removing an Aborting measurement: keep the
    /// recovery ownership alive until the interrupt receipt decides.
    let parkInterrupted sessionId (recorded: LatencyMeasurement) =
        disposeQuietly recorded.Timer
        let parked = { recorded with Timer = None }

        match interruptedInFlight.TryGetValue sessionId with
        | true, existing -> interruptedInFlight[sessionId] <- parked :: existing
        | false, _ -> interruptedInFlight[sessionId] <- [ parked ]

        diagnostic
            "latency-degeneration-interrupt-parked"
            (sessionId + ": provider run ended while the interrupt was in flight; recovery kept")

    let takeParked sessionId turn =
        lock gate (fun () ->
            match interruptedInFlight.TryGetValue sessionId with
            | true, parked ->
                match List.tryFind (fun entry -> entry.Generation = turn) parked with
                | None -> None
                | Some entry ->
                    let rest = List.filter (fun candidate -> candidate.Generation <> turn) parked

                    if List.isEmpty rest then
                        interruptedInFlight.Remove sessionId |> ignore
                    else
                        interruptedInFlight[sessionId] <- rest

                    Some entry
            | false, _ -> None)

    let continueInterrupted sessionId turn (recorded: LatencyMeasurement) =
        // One physical continuation in flight per session loop stop: the
        // first dispatched receipt owns the resume, later live receipts are
        // absorbed with their own censored sample.
        let decision =
            lock gate (fun () ->
                if disposed then
                    None
                else
                    let live =
                        match recovering.TryGetValue sessionId with
                        | true, current -> List.contains turn current
                        | false, _ -> false

                    let latched =
                        match dispatched.TryGetValue sessionId with
                        | true, owner -> owner <> turn
                        | false, _ -> false

                    if live && not latched then
                        dispatched[sessionId] <- turn
                        Some true
                    elif live then
                        Some false
                    else
                        None)

        match decision with
        | Some true ->
            let onAccepted () =
                if finishRecovery sessionId turn then
                    diagnostic "latency-degeneration-resumed" sessionId

            let onContinueFailure reason =
                failRecovery sessionId turn ("continuation: " + reason)

            try
                if not recorded.SampledComplete then
                    detector.ObserveCensored(
                        sessionId,
                        max (ports.Now() - recorded.StartedAt) recorded.ThresholdMs
                    )

                observe
                    (ports.Continue sessionId (ports.Text sessionId) onAccepted onContinueFailure)
                    (fun reason ->
                        if not (String.IsNullOrEmpty reason) then
                            onContinueFailure reason)
                    (fun error -> onContinueFailure (string error))
            with error ->
                onContinueFailure error.Message
        | Some false ->
            if not recorded.SampledComplete then
                detector.ObserveCensored(
                    sessionId,
                    max (ports.Now() - recorded.StartedAt) recorded.ThresholdMs
                )

            // The receipt decided; its settlement is "the resume is owned
            // elsewhere". End this recovery so nothing stale lingers.
            finishRecovery sessionId turn |> ignore

            diagnostic
                "latency-degeneration-interrupt-absorbed"
                (sessionId + ": another continuation owns the session loop's resume; receipt settled with its sample")
        | None -> ()

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
                        let pending =
                            match recovering.TryGetValue sessionId with
                            | true, current -> current
                            | false, _ -> []

                        recovering[sessionId] <- thisGeneration :: pending
                        true
                    | _ -> false)

            if armed then
                diagnostic
                    "latency-degeneration-interrupt"
                    (sprintf "%s: request exceeded its latency threshold %.0fms; interrupting and continuing" sessionId thresholdMs)
                // The event hook never waits for a Host promise.
                let onAbortAccepted accepted =
                    let stillOurs =
                        lock gate (fun () ->
                            match measurements.TryGetValue sessionId with
                            | true, recorded when recorded.Generation = thisGeneration -> Some recorded
                            | _ -> None)

                    match stillOurs with
                    | None ->
                        // The measured request already ended (tool boundary,
                        // failure terminal or newer Provider start) while the
                        // interrupt was in flight. An accepted interrupt has
                        // still stopped the session loop: continue it. A
                        // refused or failed one stopped nothing.
                        match takeParked sessionId thisGeneration with
                        | Some parked when accepted && not parked.EndedFinal ->
                            continueInterrupted sessionId thisGeneration parked
                        | Some _ ->
                            if finishRecovery sessionId thisGeneration then
                                diagnostic
                                    "latency-degeneration-abort-refused"
                                    (sessionId + ": request had already ended")
                        | None -> ()
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

                        if released then
                            continueInterrupted sessionId thisGeneration recorded

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
                                failRecovery sessionId thisGeneration ("interrupt: " + string error)
                            else
                                // The request had already ended on its own; a
                                // failed interrupt stopped nothing.
                                match takeParked sessionId thisGeneration with
                                | Some _ -> finishRecovery sessionId thisGeneration |> ignore
                                | None -> ())
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
        // Novice protection: until the detector has enough observations its
        // threshold is +Infinity. The request is still measured (its sample
        // is what ends the protection) but no timer is armed at all.
        let armed = not (Double.IsPositiveInfinity threshold) && not (Double.IsNaN threshold)

        let timer =
            if armed then
                Some(ports.Schedule threshold (fun () -> fire sessionId thisGeneration threshold))
            else
                None

        measurements[sessionId] <-
            { Generation = thisGeneration
              StartedAt = startedAt
              ThresholdMs = threshold
              Armed = armed
              Idle = false
              Aborting = false
              EndedFinal = false
              SampledComplete = false
              ProviderRun = Some providerRun
              Timer = timer }

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
                        match interruptedInFlight.TryGetValue sessionId with
                        | true, parked when parked |> List.exists (fun entry -> entry.EndedFinal) ->
                            interruptedInFlight[sessionId] <-
                                parked
                                |> List.map (fun entry ->
                                    if entry.EndedFinal && entry.ProviderRun <> Some run then
                                        { entry with EndedFinal = false }
                                    else
                                        entry)
                        | _ -> ()

                        match measurements.TryGetValue sessionId with
                        | true, recorded when recorded.ProviderRun = Some run -> ()
                        | true, recorded when recorded.Aborting ->
                            measurements.Remove(sessionId) |> ignore
                            parkInterrupted sessionId recorded
                            startMeasurement sessionId run
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
    member this.Complete(sessionId: string, providerRun: string option) =
        this.Settle(sessionId, providerRun, false)

    /// A tool-calls boundary: this exact model request ended, but the session
    /// loop goes on. When the interrupt was already sent, the Host abort will
    /// stop that loop, so the recovery is parked instead of finished.
    member this.CompleteStep(sessionId: string, providerRun: string option) =
        this.Settle(sessionId, providerRun, true)

    /// The Host is about to execute a tool of this session. OpenCode runs
    /// tools inside the provider step: the assistant message only gets its
    /// finish=tool-calls / time.completed after every tool of the step has
    /// returned. The model request itself ended when it handed the tool over,
    /// so the in-flight measurement ends here as a step boundary, whatever
    /// its provider run. A long tool (join, bash, a consultation) is never
    /// timed as model latency and never interrupted by this watchdog.
    member this.ToolStarted(sessionId: string) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            let run =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | true, recorded -> recorded.ProviderRun
                    | _ -> None)

            match run with
            | Some _ -> this.Settle(sessionId, run, true)
            | None -> ()

    member private _.Settle(sessionId: string, providerRun: string option, stepBoundary: bool) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            let verdict =
                lock gate (fun () ->
                    match measurements.TryGetValue sessionId with
                    | false, _ -> NoInFlightMeasurement
                    | true, recorded ->
                        match recorded.ProviderRun, providerRun with
                        | Some recordedRun, Some completingRun when recordedRun = completingRun && recorded.Aborting ->
                            // The interrupt is in flight: whether it stopped
                            // anything is decided by its receipt. A final
                            // terminal is a real complete sample.
                            measurements.Remove(sessionId) |> ignore
                            parkInterrupted
                                sessionId
                                { recorded with
                                    EndedFinal = not stepBoundary
                                    SampledComplete = not stepBoundary }
                            if stepBoundary then NoInFlightMeasurement else SampledParked recorded
                        | Some recordedRun, Some completingRun when recordedRun = completingRun ->
                            measurements.Remove(sessionId) |> ignore
                            disposeQuietly recorded.Timer
                            Sampled recorded
                        | Some _, Some _ -> ForeignProviderRun
                        | _ when recorded.Aborting ->
                            // The interrupt is already in flight: whatever this
                            // identity-less terminal was, the Host's AbortSession
                            // (once accepted) still stopped the session loop and
                            // owes a continuation. Park instead of dropping so the
                            // abort receipt decides — same as a matched terminal
                            // or a discarded failure while Aborting.
                            measurements.Remove(sessionId) |> ignore
                            parkInterrupted
                                sessionId
                                { recorded with
                                    EndedFinal = false
                                    SampledComplete = false }
                            NoInFlightMeasurement
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
            | SampledParked recorded -> detector.ObserveCompleted(sessionId, ports.Now() - recorded.StartedAt)
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
                    | true, recorded when recorded.ProviderRun = Some observedRun && recorded.Aborting ->
                        measurements.Remove(sessionId) |> ignore
                        parkInterrupted sessionId recorded
                    | true, recorded when recorded.ProviderRun = Some observedRun ->
                        measurements.Remove(sessionId) |> ignore
                        revokeRecovery sessionId
                        disposeQuietly recorded.Timer
                    | _ -> ())

    /// An exact physical cancellation can acknowledge an in-flight abort
    /// before the Host returns its interrupt promise. Never await that promise
    /// after the provider has already stopped.
    member _.OnProviderCancelled(sessionId: string, providerRun: string option) =
        if not disposed && not (String.IsNullOrWhiteSpace sessionId) then
            match providerRun with
            | None -> ()
            | Some observedRun ->
                let cancelled =
                    lock gate (fun () ->
                        match measurements.TryGetValue sessionId with
                        | true, recorded when recorded.ProviderRun = Some observedRun ->
                            measurements.Remove(sessionId) |> ignore
                            disposeQuietly recorded.Timer
                            Some recorded
                        | _ -> None)

                let parked =
                    match cancelled with
                    | Some recorded when not recorded.Aborting ->
                        lock gate (fun () ->
                            match recovering.TryGetValue sessionId with
                            | true, turn :: _ -> Some turn
                            | _ -> None)
                        |> Option.bind (takeParked sessionId)
                    | _ -> None

                match cancelled, parked with
                | _, Some parked ->
                    // A newer step of the same session was physically stopped
                    // by the in-flight interrupt: resume now.
                    continueInterrupted sessionId parked.Generation parked
                | Some recorded, None when recorded.Aborting ->
                    continueInterrupted sessionId recorded.Generation recorded
                | Some recorded, None ->
                    failRecovery
                        sessionId
                        recorded.Generation
                        "source provider cancelled after the interrupt was refused"
                | None, None -> ()

    /// Session deletion: drop the measurement, record nothing.
    member _.Cancel(sessionId: string) =
        if not (String.IsNullOrWhiteSpace sessionId) then
            lock gate (fun () ->
                // Session deletion clears the parked interrupts first: no
                // Host receipt will decide them any more.
                interruptedInFlight.Remove sessionId |> ignore
                revokeRecovery sessionId
                dispatched.Remove sessionId |> ignore
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
                        // Not armed and finite threshold: the timer fired and its
                        // interrupt was refused. A never-armed (novice) measurement
                        // has no recovery to fail.
                        let refused =
                            not recorded.Armed
                            && not (Double.IsPositiveInfinity recorded.ThresholdMs)
                            && not (Double.IsNaN recorded.ThresholdMs)

                        true, (if refused then Some recorded.Generation else None)
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
                interruptedInFlight.Clear()
                for session in recovering.Keys |> Seq.toArray do revokeRecovery session
                recovering.Clear()
                dispatched.Clear())

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
