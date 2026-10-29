namespace Wanxiangshu.Process

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

module private PtyPortSupport =
    let invokeTerminate (handler: PtyBackendHandler) (id: PtyId) =
        task {
            try
                return! handler id (PtyCommand.Signal PtySignal.Terminate)
            with error ->
                return Error error.Message
        }

    let claimClosedFlag (closed: bool ref) =
        lock closed (fun () ->
            if closed.Value then
                true
            else
                closed.Value <- true
                false)

    let tryDeliverExit (sender: PtyExitEvent -> unit) (item: PtyExitEvent) =
        try
            sender item
        with _ ->
            ()

    let deliverExitEvent (senders: (PtyExitEvent -> unit) list) (item: PtyExitEvent) =
        for sender in senders do
            tryDeliverExit sender item

    let isAbortSignal (command: PtyCommand) =
        match command with
        | PtyCommand.Signal(PtySignal.Terminate | PtySignal.Kill | PtySignal.Interrupt) -> true
        | _ -> false

    let markAbortIfNeeded (gate: obj) (abortPending: HashSet<PtyId>) (id: PtyId) (command: PtyCommand) =
        if isAbortSignal command then
            lock gate (fun () -> abortPending.Add id |> ignore)

    let runHandler (handler: PtyBackendHandler) (id: PtyId) (command: PtyCommand) =
        task {
            try
                return! handler id command
            with ex ->
                return Error ex.Message
        }

    let parkOrReject (readWaiters: Dictionary<PtyId, TaskCompletionSource<Result<string * bool, string>>>) (id: PtyId) =
        if readWaiters.ContainsKey id then
            AlreadyInProgress
        else
            let tcs = TaskCompletionSource<Result<string * bool, string>>()
            readWaiters.[id] <- tcs
            Park tcs

    let outcomeText (outcome: Result<string, string> option) =
        match defaultArg outcome (Ok PtyOutcome.Closed) with
        | Ok t -> t
        | Error e -> e

    let abortMessage (text: string) =
        if String.IsNullOrWhiteSpace text || text = PtyOutcome.Closed then
            "PTY aborted"
        else
            text

    let abortedExitEvent (id: PtyId) (outcome: Result<string, string> option) =
        let msg = outcomeText outcome |> abortMessage
        PtyExitEvent.Aborted(id, "PTY_ABORTED", msg)

    let naturalExitEvent (id: PtyId) (outcome: Result<string, string> option) =
        match defaultArg outcome (Ok PtyOutcome.Closed) with
        | Ok text -> PtyExitEvent.Exited(id, text)
        | Error err -> PtyExitEvent.Failed(id, "ERROR", err)

    let findExitTask (exitTasks: Dictionary<PtyId, Task>) (id: PtyId) =
        match exitTasks.TryGetValue id with
        | true, t -> Some t
        | false, _ -> None

    let applyKill (handler: PtyBackendHandler) (exitTask: Task) (id: PtyId) =
        task {
            let! killResult = handler id (PtyCommand.Signal PtySignal.Kill)

            match killResult with
            | Error err -> return raise (InvalidOperationException(sprintf "PTY kill failed for %s: %s" id.Value err))
            | Ok() -> do! exitTask
        }

    let continueAfterExitFound (handler: PtyBackendHandler) (exitTask: Task) (grace: int) (id: PtyId) =
        task {
            let! exited = NodeTiming.raceExit exitTask grace

            if exited then () else do! applyKill handler exitTask id
        }

    let awaitRegisteredExit (handler: PtyBackendHandler) (exitTaskOpt: Task option) (grace: int) (id: PtyId) =
        task {
            match exitTaskOpt with
            | None -> ()
            | Some exitTask -> do! continueAfterExitFound handler exitTask grace id
        }

/// Typed PTY lifecycle boundary. A backend receives commands; completion events
/// are supplied by Complete and share every registered exit listener.
type PtyPort(?exitListener: PtyExitEvent -> unit, ?handler: PtyBackendHandler) as this =
    let handler = defaultArg handler (fun _ _ -> Task.FromResult(Ok()))
    // DSL-MUTABLE: resource — exit listener callback registry
    let exitListeners = ResizeArray<PtyExitEvent -> unit>()
    let gate = obj ()
    // DSL-MUTABLE: resource — active PTY handle registry by PtyId.
    let active = Dictionary<PtyId, PtyHandle * ref<bool>>()
    // DSL-MUTABLE: resource — closed PTY id tracking set.
    let closedIds = HashSet<PtyId>()
    /// Owner TERM/KILL requested: next Complete for this id → PtyAborted (EXEC-020).
    // DSL-MUTABLE: resource — abort-pending PTY id set.
    let abortPending = HashSet<PtyId>()

    // DSL-MUTABLE: resource — per-PtyId read waiter registry
    let readWaiters =
        Dictionary<PtyId, TaskCompletionSource<Result<string * bool, string>>>()

    // DSL-MUTABLE: resource — per-PtyId exit task registry for CloseAll drain
    let exitTasks = Dictionary<PtyId, Task>()
    do exitListener |> Option.iter exitListeners.Add

    let publishFirstClose (id: PtyId) (closed: bool ref) (item: PtyExitEvent) =
        let alreadyClosed = PtyPortSupport.claimClosedFlag closed

        if not alreadyClosed then
            lock gate (fun () ->
                active.Remove id |> ignore
                closedIds.Add id |> ignore)

            this.FailRead(id, "PTY closed before read completed")
            let listeners = lock gate (fun () -> exitListeners |> Seq.toList)
            PtyPortSupport.deliverExitEvent listeners item

    /// Owner-initiated terminate: marks abort-pending + sends TERM. Does NOT
    /// remove from active, does NOT mark closed, does NOT FailRead, does NOT
    /// publish completion. Completion belongs exclusively to the backend's
    /// onExit → Complete path, which emits PtyAborted when abort-pending.
    let requestTerminate (id: PtyId) =
        let live =
            lock gate (fun () ->
                match active.TryGetValue id with
                | true, (_, closed) when not closed.Value ->
                    abortPending.Add id |> ignore
                    true
                | _ -> false)

        if live then
            PtyPortSupport.invokeTerminate handler id
        else
            Task.FromResult(Ok())

    /// Complete from a backend exit (onExit). This is the ONLY path that
    /// publishes completion to exit listeners. Removes from active, marks
    /// closed, fails any parked reader, then delivers the completion.
    /// Physical abort (owner kill / parent cancel TERM|KILL) → Aborted;
    /// natural exit → Exited; backend spawn/IO error → Failed.
    let completeFromExit (id: PtyId) (item: PtyExitEvent) =
        let target =
            lock gate (fun () ->
                match active.TryGetValue id with
                | true, value -> Some value
                | _ -> None)

        match target with
        | None -> ()
        | Some(_handle, closed) -> publishFirstClose id closed item

    member _.AddExitListener(listener: PtyExitEvent -> unit) =
        lock gate (fun () -> exitListeners.Add listener)

    member _.ExitListener = exitListener
    member _.Handler = handler

    /// Open a PTY for a managed agent.
    member this.Fork(command: string, agentName: string, ?ptyId: PtyId, ?cwd: string) : PtyId =
        let id =
            defaultArg ptyId (PtyId("pty-" + Guid.NewGuid().ToString("N").Substring(0, 8)))

        let handle =
            { Id = id
              Command = command
              StartedAt = DateTimeOffset.UtcNow
              Agent = agentName }

        lock gate (fun () ->
            closedIds.Remove id |> ignore
            active.[id] <- (handle, ref false))

        handler id (PtyCommand.Spawn(command, defaultArg cwd "")) |> ignore
        id

    member this.Exists(id: PtyId) =
        lock gate (fun () -> active.ContainsKey id)

    member this.Known(id: PtyId) =
        lock gate (fun () -> active.ContainsKey id || closedIds.Contains id)

    /// Sends a command to the backend. Returns the backend's outcome so callers
    /// (e.g. SendPty) can surface write errors as tool errors instead of always
    /// succeeding. Completion/exit still belongs to the backend's onExit.
    /// TERM/KILL/INT marks abort-pending so onExit → PtyAborted (EXEC-020).
    member this.Send(id: PtyId, command: PtyCommand) : Task<Result<unit, string>> =
        let live, closed =
            lock gate (fun () ->
                match active.TryGetValue id with
                | true, (_, c) -> (not c.Value, c.Value)
                | false, _ -> (false, closedIds.Contains id))

        if not live && closed then
            Task.FromResult(Error "PTY closed")
        elif not live then
            Task.FromResult(Error(sprintf "Unknown PTY id: %s" id.Value))
        else
            PtyPortSupport.markAbortIfNeeded gate abortPending id command
            PtyPortSupport.runHandler handler id command

    /// Reads the currently buffered PTY output without completing the join.
    /// At most one read may be in flight per id; a second concurrent Read
    /// returns immediately with an error. A Read after the PTY has closed
    /// returns (output="", closed=true) without parking.
    member this.Read(id: PtyId) : Task<Result<string * bool, string>> =
        let plan =
            lock gate (fun () ->
                match active.TryGetValue id with
                | true, (_, closed) when not closed.Value -> PtyPortSupport.parkOrReject readWaiters id
                | true, _ -> ClosedImmediate
                | false, _ when closedIds.Contains id -> ClosedImmediate
                | false, _ -> Unknown(sprintf "Unknown PTY id: %s" id.Value))

        match plan with
        | Unknown msg -> Task.FromResult(Error msg)
        | AlreadyInProgress -> Task.FromResult(Error "PTY read already in progress")
        | ClosedImmediate -> Task.FromResult(Ok("", true))
        | Park tcs ->
            handler id PtyCommand.Read |> ignore

            task {
                let! result = tcs.Task
                return result
            }

    /// Resolved by the backend when it has drained the buffer for a Read.
    member _.ReadResult(id: PtyId, output: string, closed: bool) =
        let tcs =
            lock gate (fun () ->
                match readWaiters.TryGetValue id with
                | true, t ->
                    readWaiters.Remove id |> ignore
                    Some t
                | false, _ -> None)

        match tcs with
        | Some t -> t.SetResult(Ok(output, closed))
        | None -> ()

    /// Resolves any parked read waiter with an error. Used by every path that
    /// ends a PTY (close, spawn failure, pending drop, onExit) so a parked
    /// reader never hangs.
    member _.FailRead(id: PtyId, reason: string) =
        let tcs =
            lock gate (fun () ->
                match readWaiters.TryGetValue id with
                | true, t ->
                    readWaiters.Remove id |> ignore
                    Some t
                | false, _ -> None)

        match tcs with
        | Some t -> t.SetResult(Error reason)
        | None -> ()

    /// Bridges a backend per-process exit task into the port so CloseAll can
    /// await process exit without the backend reaching into port dicts.
    member this.RegisterExitTask(id: PtyId, task: Task) =
        lock gate (fun () -> exitTasks.[id] <- task)

    /// Complete from a backend exit (onExit). This is the ONLY path that
    /// publishes completion to exit listeners.
    /// If owner requested terminate (Close/CloseAll/TERM), emits Aborted;
    /// else Ok → Exited, Error → Failed. Tests may call CompleteAborted
    /// to force abort without the terminate mark.
    member this.Complete(id: PtyId, ?outcome: Result<string, string>) =
        let wasAbort =
            lock gate (fun () ->
                let marked = abortPending.Remove id
                exitTasks.Remove id |> ignore
                marked)

        let item =
            if wasAbort then
                PtyPortSupport.abortedExitEvent id outcome
            else
                PtyPortSupport.naturalExitEvent id outcome

        completeFromExit id item

    /// Force Aborted (tests / callers that already know physical interrupt).
    member this.CompleteAborted(id: PtyId, ?message: string) =
        lock gate (fun () ->
            abortPending.Remove id |> ignore
            exitTasks.Remove id |> ignore)

        let msg = defaultArg message "PTY aborted"
        completeFromExit id (PtyExitEvent.Aborted(id, "PTY_ABORTED", msg))

    /// Owner-initiated close: sends TERM only. Does NOT publish completion —
    /// completion is delivered by the backend's onExit → Complete / CompleteAborted.
    /// The caller must await the exit (via CloseAll or the registered exit task).
    member this.Close(id: PtyId, ?outcome: Result<string, string>) : unit = requestTerminate id |> ignore

    /// Async owner cleanup for a single PTY id: sends TERM, awaits exit with grace, then escalates to KILL if needed.
    member this.ClosePty(id: PtyId, ?graceMs: int) : Task<unit> =
        let grace = max 0 (defaultArg graceMs PtyOutcome.termToKillGraceMs)

        task {
            let isLive = lock gate (fun () -> active.ContainsKey id)

            if isLive then
                let clock = NodeTiming.nodeClockPort ()
                let deadline = clock.UtcNow().AddMilliseconds(float grace)
                let termination = requestTerminate id
                let! answered = NodeTiming.raceExit (termination :> Task) grace

                if answered then
                    match! termination with
                    | Error reason -> return raise (InvalidOperationException(reason))
                    | Ok() -> ()

                let exitTaskOpt = lock gate (fun () -> PtyPortSupport.findExitTask exitTasks id)

                let remaining =
                    max 0 (int (Math.Ceiling((deadline - clock.UtcNow()).TotalMilliseconds)))

                do! PtyPortSupport.awaitRegisteredExit handler exitTaskOpt remaining id
                lock gate (fun () -> exitTasks.Remove id |> ignore)
        }

    /// Async owner cleanup: for each active id, send TERM (requestTerminate),
    /// await exit for `termToKillGraceMs` (or the supplied override), then
    /// escalate to KILL. If KILL itself fails, propagate the error instead of
    /// waiting forever. The exitTask resolves via the backend's onExit, which
    /// calls Complete (the only completion-publishing path). See SSOT §7.
    member this.CloseAll(?graceMs: int) : Task<unit> =
        let grace = max 0 (defaultArg graceMs PtyOutcome.termToKillGraceMs)
        let ids = lock gate (fun () -> active.Keys |> Seq.toList)

        let close id =
            task {
                try
                    do! this.ClosePty(id, graceMs = grace)
                    return None
                with error ->
                    return Some error
            }

        task {
            let pending = ids |> List.map close |> List.toArray
            let! outcomes = (emitJsExpr pending "Promise.all($0)": Task<exn option array>)

            match Array.choose id outcomes with
            | [||] -> ()
            | [| error |] -> return raise error
            | errors ->
                let combined: exn = emitJsExpr errors "new AggregateError($0, 'PTY cleanup failed')"
                return raise combined
        }

    member _.List() : PtyHandle list =
        let ptys = lock gate (fun () -> active.Values |> Seq.map fst |> Seq.toList)
        ptys
