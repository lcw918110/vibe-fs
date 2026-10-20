namespace Wanxiangshu.Process

open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// DSL-state-combination: physical — spawn fn + load task are runtime resource slots
/// DSL-class: PhysicalHandle — process-owned spawn loader and live PTY session registry; owner PtySupervisor, law PTY lifecycle, proof pty-supervisor.
type PtySupervisor =
    { Gate: obj
      mutable SpawnFn: obj option
      mutable LoadTask: Task<unit> option
      Sessions: Dictionary<PtyId, PtySession> }

module PtySupervisor =

    /// PTY-READ-FIRST-BYTE: how long a read waits for an open terminal to say anything before it
    /// answers "". A slice, not a budget: the criterion is the byte arriving (`onData` resolves the
    /// parked reader immediately), and this only bounds silence so a quiet terminal still answers.
    [<Literal>]
    let PtyReadFirstByteMs = 250

    let create () : PtySupervisor =
        { Gate = obj ()
          SpawnFn = None
          LoadTask = None
          Sessions = Dictionary<PtyId, PtySession>() }

    let add (supervisor: PtySupervisor) (id: PtyId) (session: PtySession) : unit =
        lock supervisor.Gate (fun () -> supervisor.Sessions.[id] <- session)

    let private tryGetUnlocked (supervisor: PtySupervisor) (id: PtyId) : PtySession option =
        // DSL-MUTABLE: algorithm-scratch — byref out-slot for TryGetValue
        let mutable value = Unchecked.defaultof<PtySession>

        if supervisor.Sessions.TryGetValue(id, &value) then
            Some value
        else
            None

    let tryGet (supervisor: PtySupervisor) (id: PtyId) : PtySession option =
        lock supervisor.Gate (fun () -> tryGetUnlocked supervisor id)

    let get (supervisor: PtySupervisor) (id: PtyId) : PtySession =
        match tryGet supervisor id with
        | Some s -> s
        | None -> failwithf "Unknown PTY id: %s" id.Value

    let remove (supervisor: PtySupervisor) (id: PtyId) : unit =
        lock supervisor.Gate (fun () -> supervisor.Sessions.Remove id |> ignore)

    let list (supervisor: PtySupervisor) : PtyId list =
        lock supervisor.Gate (fun () -> supervisor.Sessions.Keys |> Seq.toList)

    let private ensureSessionUnlocked (supervisor: PtySupervisor) (id: PtyId) : PtySession =
        match tryGetUnlocked supervisor id with
        | Some s -> s
        | None ->
            let s = PtySession.create id.Value null
            supervisor.Sessions.[id] <- s
            s

    let signalName (signal: PtySignal) : string =
        match signal with
        | PtySignal.Terminate -> "SIGTERM"
        | PtySignal.Kill -> "SIGKILL"
        | PtySignal.Interrupt -> "SIGINT"
        | PtySignal.Hangup -> "SIGHUP"
        | PtySignal.Quit -> "SIGQUIT"
        | PtySignal.User1 -> "SIGUSR1"
        | PtySignal.User2 -> "SIGUSR2"

    [<Emit("(() => { try { process.kill(-$0.pid, $1); } catch (_) { process.kill($0.pid, $1); } })()")>]
    let private killProcessTree (term: obj) (signal: string) : unit = jsNative

    [<Emit("""
        (async () => {
          const candidates = ['bun-pty', process.cwd() + '/node_modules/bun-pty'];
          let lastErr;
          for (const spec of candidates) {
            try {
              const m = await import(spec);
              const spawn = m.spawn || (m.default && m.default.spawn) || m;
              if (typeof spawn === 'function') return spawn;
            } catch (err) { lastErr = err; }
          }
          throw lastErr || new Error('bun-pty spawn not found');
        })()
    """)>]
    let private loadSpawn () : JS.Promise<obj> = jsNative

    [<Emit("$0.then((spawn) => $1(spawn)).catch((err) => $2(String(err && err.message || err)))")>]
    let private runLoader (loader: JS.Promise<obj>) (onOk: obj -> unit) (onErr: string -> unit) : unit = jsNative

    let private startSpawnLoad (supervisor: PtySupervisor) : Task<unit> =
        match supervisor.LoadTask with
        | Some t -> t
        | None ->
            let tcs = TaskCompletionSource<unit>()
            supervisor.LoadTask <- Some tcs.Task

            runLoader
                (loadSpawn ())
                (fun spawn ->
                    supervisor.SpawnFn <- Some(unbox spawn)
                    tcs.SetResult(()))
                (fun err -> tcs.SetException(Exception err))

            tcs.Task

    let ensureSpawn (supervisor: PtySupervisor) : Task<unit> =
        match supervisor.SpawnFn with
        | Some _ -> Task.FromResult(())
        | None -> startSpawnLoad supervisor

    [<Emit("Promise.resolve().then(function(){ return $0('sh', ['-lc', $1], $2); })")>]
    let private invokeSpawn (spawn: obj) (command: string) (options: obj) : Task<obj> = jsNative

    let private resolveCwd (cwd: string) : obj =
        if String.IsNullOrEmpty cwd then
            emitJsExpr () "process.cwd()"
        else
            box cwd

    let spawn (supervisor: PtySupervisor) (command: string) (cwd: string) : Task<obj> =
        match supervisor.SpawnFn with
        | None -> failwith "bun-pty is not loaded"
        | Some spawn ->
            let options =
                createObj
                    [ "name", box "xterm-256color"
                      "cols", box 80
                      "rows", box 24
                      "cwd", resolveCwd cwd ]

            invokeSpawn spawn command options

    let failPending
        (entries: (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list)
        (reason: string)
        : unit =
        for (_, tcsOpt) in entries do
            tcsOpt |> Option.iter (fun tcs -> tcs.SetResult(Error reason))

    let takePending
        (supervisor: PtySupervisor)
        (id: PtyId)
        : (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list =
        lock supervisor.Gate (fun () ->
            match tryGetUnlocked supervisor id with
            | Some s ->
                let entries = s.Pending |> Seq.toList
                s.Pending.Clear()
                entries
            | None -> [])

    let drop
        (supervisor: PtySupervisor)
        (id: PtyId)
        : (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list =
        lock supervisor.Gate (fun () ->
            // DSL-MUTABLE: algorithm-scratch — byref out-slot for TryGetValue in drop
            let mutable session = Unchecked.defaultof<PtySession>

            if supervisor.Sessions.TryGetValue(id, &session) then
                supervisor.Sessions.Remove id |> ignore
                session.Pending |> Seq.toList
            else
                [])

    let private writeBackend (session: PtySession) (bytes: byte[]) =
        task {
            try
                let text = Encoding.UTF8.GetString bytes
                session.Backend?write text
                return Ok()
            with ex ->
                return Error ex.Message
        }

    let private resolveSilentRead (supervisor: PtySupervisor) (port: PtyPort) (id: PtyId) =
        match tryGet supervisor id with
        | Some s when s.AwaitingFirstByte ->
            s.AwaitingFirstByte <- false
            let text = s.OutputBuffer.ToString()
            s.OutputBuffer.Clear() |> ignore
            port.ReadResult(id, text, s.Closed)
        | _ -> ()

    let private armFirstByteWait (supervisor: PtySupervisor) (port: PtyPort) (id: PtyId) (session: PtySession) =
        session.AwaitingFirstByte <- true

        task {
            do! NodeTiming.timerTask PtyReadFirstByteMs
            resolveSilentRead supervisor port id
        }
        |> ignore

    let private readBackend (supervisor: PtySupervisor) (port: PtyPort) (id: PtyId) (session: PtySession) =
        task {
            let buffered = session.OutputBuffer.ToString()

            if buffered <> "" || session.Closed then
                session.OutputBuffer.Clear() |> ignore
                port.ReadResult(id, buffered, session.Closed)
            else
                // PTY-READ-FIRST-BYTE: an open terminal with nothing buffered has not
                // answered yet, and answering "" instantly makes the obvious agent
                // sequence — write a command, read its output — return nothing whenever
                // the shell has not echoed within the same tick. Measured: `pty-stress`
                // reads eleven times and every read came back empty under suite load,
                // while the echo turned up later in the join outcome.
                //
                // So the read waits for the next byte instead of guessing. Event-driven:
                // `onData` resolves the parked reader as soon as the terminal speaks. The
                // timer is only the answer for a genuinely silent terminal, which must
                // still get "" rather than hang.
                armFirstByteWait supervisor port id session

            return Ok()
        }

    let private signalBackend (session: PtySession) (signal: PtySignal) =
        task {
            try
                killProcessTree session.Backend (signalName signal)
                return Ok()
            with ex ->
                return Error ex.Message
        }

    let private resizeBackend (session: PtySession) (width: int) (height: int) =
        task {
            try
                session.Backend?resize(width, height)
                return Ok()
            with _ ->
                return Ok()
        }

    let private applyBackendCommand
        (supervisor: PtySupervisor)
        (port: PtyPort)
        (id: PtyId)
        (session: PtySession)
        (command: PtyCommand)
        =
        match command with
        | PtyCommand.Write bytes -> writeBackend session bytes
        | PtyCommand.Read -> readBackend supervisor port id session
        | PtyCommand.Signal signal -> signalBackend session signal
        | PtyCommand.Resize(width, height) -> resizeBackend session width height
        | PtyCommand.Spawn _ -> Task.FromResult(Ok())

    let private pendingWriteTcs (command: PtyCommand) =
        match command with
        | PtyCommand.Write _ ->
            let tcs = TaskCompletionSource<Result<unit, string>>()
            Some tcs
        | _ -> None

    let private awaitPending (tcsOpt: TaskCompletionSource<Result<unit, string>> option) =
        task {
            match tcsOpt with
            | Some tcs -> return! tcs.Task
            | None -> return Ok()
        }

    let private enqueuePending (supervisor: PtySupervisor) (session: PtySession) (command: PtyCommand) =
        let tcsOpt = pendingWriteTcs command
        lock supervisor.Gate (fun () -> session.Pending.Add(command, tcsOpt))
        awaitPending tcsOpt

    let private sessionOrCreate (supervisor: PtySupervisor) (id: PtyId) =
        lock supervisor.Gate (fun () ->
            match tryGetUnlocked supervisor id with
            | Some s -> s
            | None ->
                let s = PtySession.create id.Value null
                supervisor.Sessions.[id] <- s
                s)

    let applyLive
        (supervisor: PtySupervisor)
        (port: PtyPort)
        (id: PtyId)
        (command: PtyCommand)
        : Task<Result<unit, string>> =
        task {
            let session = sessionOrCreate supervisor id

            if session.Closed then
                return Ok()
            elif isNull session.Backend then
                return! enqueuePending supervisor session command
            else
                return! applyBackendCommand supervisor port id session command
        }

    let private drainAwaitingFirstByte (port: PtyPort) (id: PtyId) (s: PtySession) =
        if s.AwaitingFirstByte then
            s.AwaitingFirstByte <- false
            let text = s.OutputBuffer.ToString()
            s.OutputBuffer.Clear() |> ignore
            port.ReadResult(id, text, s.Closed)

    let private appendLiveData (port: PtyPort) (id: PtyId) (data: string) (s: PtySession) =
        s.OutputBuffer.Append data |> ignore
        // PTY-READ-FIRST-BYTE: a read parked on an empty buffer is answered by the
        // terminal speaking, not by a timer expiring. Draining here is what makes
        // write-then-read deterministic instead of a race against the shell's echo.
        drainAwaitingFirstByte port id s

    let private onSessionData (supervisor: PtySupervisor) (port: PtyPort) (id: PtyId) (data: string) =
        match tryGet supervisor id with
        | None -> ()
        | Some s when s.Closed -> ()
        | Some s -> appendLiveData port id data s

    let private residualOrClosed (residual: string) =
        if String.IsNullOrEmpty residual then
            PtyOutcome.Closed
        else
            residual

    let private onSessionExit (supervisor: PtySupervisor) (port: PtyPort) (id: PtyId) =
        match tryGet supervisor id with
        | None -> ()
        | Some s when s.Closed -> ()
        | Some s ->
            s.Closed <- true
            let residual = s.OutputBuffer.ToString()
            s.OutputBuffer.Clear() |> ignore
            s.ExitCompleted <- true
            s.ExitCompletion.SetResult(())
            let pending = drop supervisor id
            failPending pending "PTY exited before command was applied"
            port.FailRead(id, "PTY exited before read completed")
            port.Complete(id, Ok(residualOrClosed residual))

    let private killOrphanTerm (term: obj) =
        try
            killProcessTree term (signalName PtySignal.Kill)
        with _ ->
            ()

    let private killUnlessClosed (session: PtySession) =
        if not session.Closed then
            killOrphanTerm session.Backend

    let attach
        (supervisor: PtySupervisor)
        (port: PtyPort)
        (id: PtyId)
        (term: obj)
        (exitTcs: TaskCompletionSource<unit>)
        : unit =
        let pending =
            lock supervisor.Gate (fun () ->
                let placeholder = ensureSessionUnlocked supervisor id

                let live =
                    { placeholder with
                        Backend = term
                        OutputBuffer = StringBuilder()
                        Closed = false
                        ExitCompletion = exitTcs
                        ExitCompleted = false
                        Pending = ResizeArray<_>() }

                supervisor.Sessions.[id] <- live
                placeholder.Pending |> Seq.toList)

        term?onData (fun (data: string) -> onSessionData supervisor port id data)
        |> ignore

        term?onExit (fun (_event: obj) -> onSessionExit supervisor port id) |> ignore

        if not (port.Exists id) then
            killOrphanTerm term

        for (command, tcsOpt) in pending do
            task {
                let! result = applyLive supervisor port id command
                tcsOpt |> Option.iter (fun t -> t.SetResult result)
            }
            |> ignore

    let cancelAll (supervisor: PtySupervisor) : unit =
        let sessions =
            lock supervisor.Gate (fun () -> supervisor.Sessions.Values |> Seq.toList)

        for session in sessions do
            killUnlessClosed session
