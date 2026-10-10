namespace Wanxiangshu.Process

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// JS-native time/process boundary. Clocks, timers and deadlines are opaque
/// capabilities; estimates, elapsed text and validation results are plain data.
module ProcessSurface =

    let killAckGraceMs = NodeProcessWait.KillAckGraceMs

    type private TimerPortHandle(port: VirtualTimerPort) =
        member _.Port = port

    type private ClockPortHandle(port: VirtualClockPort) =
        member _.Port = port

    type private SessionStartHandle(state: SessionStartedAtProjectionState) =
        member _.State = state

    type private CommandHandle(command: Command) =
        member _.Command = command

    type private EstimateHandle(estimate: ProcessEstimate) =
        member _.Estimate = estimate

    type private ContextHandle(context: ProcessContext) =
        member _.Context = context

    type private CancellationHandle(source: CancellationTokenSource) =
        member _.Source = source

    type private RegistrationHandle(registration: CancellationTokenRegistration) =
        member _.Registration = registration

    type internal ChildHandle(child: NodeProcessHost.ChildProcess, killCount: unit -> int) =
        member _.Child = child
        member _.KillCount = killCount

    type private OutputHandle(collector: ProcessOutput.OutputCollector) =
        member _.Collector = collector

    type private SpoolHandle(spool: Spool.StreamingSpool) =
        member _.Spool = spool

    type private PtyIdHandle(id: PtyId) =
        member _.Id = id

    type private PtyCommandHandle(command: PtyCommand) =
        member _.Command = command

    type private PtyPortHandle(port: PtyPort) =
        member _.Port = port

    type private PtySessionHandle(session: PtySession) =
        member _.Session = session

    type private PtySupervisorHandle(supervisor: PtySupervisor) =
        member _.Supervisor = supervisor

    type private PendingHandle(entries: (PtyCommand * TaskCompletionSource<Result<unit, string>> option) list) =
        member _.Entries = entries

    type private PendingEntryHandle(command: PtyCommand, completion: TaskCompletionSource<Result<unit, string>> option)
        =
        member _.Command = command
        member _.Completion = completion

    type private WaitDeadlineHandle(deadline: Deadline) =
        member _.Deadline = deadline

    [<Emit("$0($1,$2)")>]
    let private apply2 (fn: obj) (first: obj) (second: obj) : obj = jsNative

    [<Emit("$0($1,$2,$3)")>]
    let private apply3 (fn: obj) (first: obj) (second: obj) (third: obj) : obj = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private promiseOf (value: obj) : JS.Promise<obj> = jsNative

    [<Emit("$0($1)")>]
    let private call1 (fn: obj) (value: obj) : obj = jsNative

    [<Emit("$0()")>]
    let internal call0 (fn: obj) : obj = jsNative

    let private optionObj (value: 'T option) : obj =
        match value with
        | Some value -> box value
        | None -> null

    let private undefinedObj: obj = emitJsExpr () "undefined"

    [<Emit("$0==null")>]
    let internal isNullish (value: obj) : bool = jsNative

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0?.[$1]"

    let private optionalResult (value: obj) : Result<string, string> option =
        if isNullish value then
            None
        elif isNullish (property value "ok") then
            None
        elif unbox<bool> (property value "ok") then
            Some(Ok(string (property value "value")))
        else
            Some(Error(string (property value "error")))

    let private optionString (value: obj) : string option =
        if isNullish value then None else Some(string value)

    let private stringArrayOf (value: obj) : string array =
        if isNullish value then
            [||]
        else
            unbox<obj array> value |> Array.map string

    let private bytesOf (value: obj) : byte[] =
        if isNullish value then
            [||]
        elif value :? string then
            Encoding.UTF8.GetBytes(string value)
        else
            unbox<byte array> value

    let private bytesView (value: byte[]) : obj =
        let values: obj array = Array.zeroCreate value.Length
        // DSL-MUTABLE: algorithm-scratch — byte view index cursor
        let mutable index = 0

        while index < value.Length do
            values.[index] <- box (int value.[index])
            index <- index + 1

        box values

    let private resultObject (result: Result<'T, string>) (view: 'T -> obj) : obj =
        match result with
        | Ok value -> box {| ok = true; value = view value |}
        | Error error -> box {| ok = false; error = error |}

    let private agentOf (name: string) : Result<ManagedAgent, string> =
        match ManagedAgent.tryParse name with
        | Some agent -> Ok agent
        | None -> Error(sprintf "Unknown managed agent: %s" name)

    let private idValue (id: PtyId) = id.Value

    let createVirtualTimer () : obj =
        TimerPortHandle(VirtualTiming.createVirtualTimerPort ()) :> obj

    let timerDelay (timer: obj) (milliseconds: int) : obj =
        let port = (timer :?> TimerPortHandle).Port.Port
        box (port.Delay milliseconds)

    let timerAwait (handle: obj) : Task<unit> = (unbox<IDeadlineHandle> handle).Delay

    let timerCancel (handle: obj) : unit =
        (unbox<IDeadlineHandle> handle).Cancel()

    let timerAdvance (timer: obj) (milliseconds: int) : unit =
        (timer :?> TimerPortHandle).Port.Advance milliseconds

    let timerNowMs (timer: obj) : int =
        (timer :?> TimerPortHandle).Port.NowMs()

    let timerDispose (timer: obj) : unit =
        (timer :?> TimerPortHandle).Port.Port.Dispose()

    let createNodeTimer () : obj = box (NodeTiming.nodeTimerPort ())

    let nodeTimerDispose (timer: obj) : unit = (unbox<ITimerPort> timer).Dispose()

    let createVirtualClock () : obj =
        ClockPortHandle(VirtualTiming.createVirtualClockPort ()) :> obj

    let clockNowIso (clock: obj) : string =
        (clock :?> ClockPortHandle).Port.Port.UtcNow().ToString("o")

    let clockNowMs (clock: obj) : int64 =
        (clock :?> ClockPortHandle).Port.Port.UtcNow().ToUnixTimeMilliseconds()

    let clockAdvanceMs (clock: obj) (milliseconds: int) : unit =
        (clock :?> ClockPortHandle).Port.AdvanceMs milliseconds

    let clockSet (clock: obj) (iso: string) : unit =
        (clock :?> ClockPortHandle).Port.Set(DateTimeOffset.Parse iso)

    let createNodeClock () : obj = box (NodeTiming.nodeClockPort ())

    let effectiveDeadlineSeconds (runtimeSeconds: float) (hardLimitSeconds: float) : float =
        if
            Double.IsNaN runtimeSeconds
            || Double.IsInfinity runtimeSeconds
            || runtimeSeconds <= 0.0
        then
            hardLimitSeconds
        else
            min runtimeSeconds hardLimitSeconds

    let outputThreshold (bytes: float) : float =
        if Double.IsNaN bytes || Double.IsInfinity bytes || bytes <= 0.0 then
            0.0
        else
            Math.Floor bytes

    let validateEstimate (runtimeSeconds: float) (outputBytes: float) : obj =
        if
            Double.IsNaN runtimeSeconds
            || Double.IsInfinity runtimeSeconds
            || runtimeSeconds <= 0.0
        then
            box
                {| ok = false
                   error = "deadline_seconds must be a finite positive number" |}
        elif Double.IsNaN outputBytes || Double.IsInfinity outputBytes || outputBytes < 0.0 then
            box
                {| ok = false
                   error = "output_budget_bytes must be non-negative" |}
        else
            box {| ok = true |}

    let defaultHardLimitSeconds = 3600.0

    let renderDeadlineExpired () =
        "No return reached you before your waiting ended."

    let renderElapsed (language: string) (elapsedMilliseconds: float) : string =
        let totalSeconds = int64 (Math.Floor(max 0.0 elapsedMilliseconds / 1000.0))
        let minutes = totalSeconds / 60L
        let seconds = totalSeconds % 60L

        if language = "zh" || language = "zh-CN" || language = "simplifiedChinese" then
            sprintf "会话墙钟时间：%d 分钟 %d 秒。" minutes seconds
        else
            let minuteUnit = if minutes = 1L then "minute" else "minutes"
            let secondUnit = if seconds = 1L then "second" else "seconds"
            sprintf "Elapsed wall-clock time: %d %s %d %s." minutes minuteUnit seconds secondUnit

    let composeWithElapsed (tip: obj) (elapsed: obj) (estimate: obj) (guideline: string) : string =
        [ tip; elapsed; estimate; box guideline ]
        |> List.choose (fun value ->
            if isNullish value then
                None
            else
                let text = string value
                if String.IsNullOrWhiteSpace text then None else Some text)
        |> String.concat "\n\n"

    let sessionStartBind (nowIso: string) (current: obj) : obj =
        if isNullish current then
            let value = SessionStartedAtProjection.bind (DateTimeOffset.Parse nowIso) None
            SessionStartHandle(value) :> obj
        else
            let existing = (current :?> SessionStartHandle).State
            SessionStartHandle(SessionStartedAtProjection.bind (DateTimeOffset.Parse nowIso) (Some existing)) :> obj

    let sessionStartAt (state: obj) : string =
        let value =
            SessionStartedAtProjection.startedAt (state :?> SessionStartHandle).State

        value.ToString("o")

    /// A bounded process-local ledger used by the JS contract to model journal
    /// replay without exposing AgentJournal or Fable records.
    type private SessionStartLedger() =
        // DSL-MUTABLE: resource — session-start handle registry by session id
        let values = Dictionary<string, SessionStartHandle>()

        member _.Append(sessionId: string, startedAt: string) =
            if not (values.ContainsKey sessionId) then
                let state = sessionStartBind startedAt null
                values.[sessionId] <- state :?> SessionStartHandle

        member _.Read(sessionId: string) : obj =
            match values.TryGetValue sessionId with
            | true, value -> value :> obj
            | false, _ -> null

    let createSessionStartLedger () : obj = SessionStartLedger() :> obj

    let appendSessionStart (ledger: obj) (sessionId: string) (startedAt: string) : unit =
        (ledger :?> SessionStartLedger).Append(sessionId, startedAt)

    let readSessionStart (ledger: obj) (sessionId: string) : obj =
        (ledger :?> SessionStartLedger).Read(sessionId)


    let command (fileName: string) (arguments: obj) (workingDirectory: obj) (stdin: obj) : obj =
        CommandHandle
            { FileName = fileName
              Arguments = stringArrayOf arguments |> Array.toList
              WorkingDirectory = optionString workingDirectory
              Environment = None
              Stdin = optionString stdin
              PtyOptions = None }
        :> obj

    let private commandOf (value: obj) = (value :?> CommandHandle).Command

    let commandView (value: obj) : obj =
        let command = commandOf value

        box
            {| fileName = command.FileName
               arguments = command.Arguments |> List.toArray
               workingDirectory = optionObj command.WorkingDirectory
               stdin = optionObj command.Stdin |}

    let estimate (runtimeSeconds: float) (outputBytes: float) (memory: string) : obj =
        let memoryValue =
            if memory.ToLowerInvariant() = "large" then
                EstimatedMemory.Large
            else
                EstimatedMemory.Medium

        EstimateHandle
            { EstimatedRuntime = RuntimeSeconds runtimeSeconds
              EstimatedOutput = OutputBytes(int64 outputBytes)
              EstimatedMemory = memoryValue }
        :> obj

    let private estimateOf (value: obj) = (value :?> EstimateHandle).Estimate

    let estimateView (value: obj) : obj =
        let estimate = estimateOf value

        let runtime =
            match estimate.EstimatedRuntime with
            | RuntimeSeconds seconds -> seconds

        let output =
            match estimate.EstimatedOutput with
            | OutputBytes bytes -> float bytes

        let memory =
            match estimate.EstimatedMemory with
            | EstimatedMemory.Large -> "large"
            | EstimatedMemory.Medium -> "medium"

        box
            {| runtimeSeconds = runtime
               outputBytes = output
               memory = memory |}

    let context (workingDirectory: obj) (hardLimitMs: float) : obj =
        let limit =
            if Double.IsNaN hardLimitMs || hardLimitMs <= 0.0 then
                3600000.0
            else
                hardLimitMs

        ContextHandle
            { WorkingDirectory = optionString workingDirectory
              HardLimit = TimeSpan.FromMilliseconds limit }
        :> obj

    let private contextOf (value: obj) = (value :?> ContextHandle).Context

    let contextView (value: obj) : obj =
        let context = contextOf value

        box
            {| workingDirectory = optionObj context.WorkingDirectory
               hardLimitMs = context.HardLimit.TotalMilliseconds |}

    let createCancellationToken (cancelled: obj) : obj =
        let source = new CancellationTokenSource()

        if not (isNullish cancelled) && unbox<bool> cancelled then
            source.Cancel()

        CancellationHandle source :> obj

    let cancel (token: obj) : unit =
        (token :?> CancellationHandle).Source.Cancel()

    let cancelToken (token: obj) : obj = token

    let tokenView (token: obj) : obj =
        box {| cancelled = (token :?> CancellationHandle).Source.IsCancellationRequested |}

    let registerCancellation (token: obj) (callback: obj) : obj =
        let registration =
            (token :?> CancellationHandle)
                .Source.Token.Register(fun () -> call0 callback |> ignore)

        RegistrationHandle registration :> obj

    let disposeCancellationRegistration (_registration: obj) : unit = ()

    let private launcherResult (value: obj) : Task<int * byte[] * byte[]> =
        task {
            let! raw = unbox<Task<obj>> (promiseOf value)
            let values = unbox<obj array> raw
            return int (string values.[0]), bytesOf values.[1], bytesOf values.[2]
        }

    let private launchWithJs
        (launcher: obj)
        (command: Command)
        (token: CancellationToken)
        : Task<int * byte[] * byte[]> =
        let register callback =
            token.Register(fun () -> call0 callback |> ignore) |> ignore
            null

        let tokenObject =
            box
                {| cancelled = token.IsCancellationRequested
                   register = register |}

        launcherResult (apply2 launcher (commandView (CommandHandle command :> obj)) tokenObject)

    let private errorView (error: ProcessError) : obj =
        match error with
        | ProcessError.TimeoutExceeded duration ->
            box
                {| kind = "TimeoutExceeded"
                   durationMs = duration.TotalMilliseconds |}
        | ProcessError.SpawnFailed reason ->
            box
                {| kind = "SpawnFailed"
                   reason = reason |}
        | ProcessError.ProcessCancelled reason ->
            box
                {| kind = "ProcessCancelled"
                   reason = reason |}
        | ProcessError.ExecutionFailed reason ->
            box
                {| kind = "ExecutionFailed"
                   reason = reason |}

    let outcomeView (outcome: obj) : obj =
        match unbox<ProcessOutcome> outcome with
        | ProcessOutcome.Completed(exitCode, stdout, stderr, spooled) ->
            box
                {| kind = "Completed"
                   exitCode = exitCode
                   stdout = stdout
                   stderr = stderr
                   spooled = spooled |}
        | ProcessOutcome.Spooled(exitCode, path, bytes, chunks) ->
            box
                {| kind = "Spooled"
                   exitCode = exitCode
                   spoolPath = path
                   totalBytes = float bytes
                   chunkCount = chunks |}

    let resultView (result: obj) : obj =
        match unbox<Result<ProcessOutcome, ProcessError>> result with
        | Ok outcome ->
            box
                {| ok = true
                   value = outcomeView (box outcome) |}
        | Error error ->
            box
                {| ok = false
                   error = errorView error |}

    let runWithLauncher (launcher: obj) (command: obj) (estimate: obj) (context: obj) (token: obj) : Task<obj> =
        task {
            let! result =
                ProcessRunner.runWithLauncher
                    (launchWithJs launcher)
                    (commandOf command)
                    (estimateOf estimate)
                    (contextOf context)
                    (token :?> CancellationHandle).Source.Token

            return resultView (box result)
        }

    let runWithHost (command: obj) (estimate: obj) (context: obj) (token: obj) : Task<obj> =
        task {
            let! result =
                ProcessRunner.run
                    (commandOf command)
                    (estimateOf estimate)
                    (contextOf context)
                    (token :?> CancellationHandle).Source.Token

            return resultView (box result)
        }

    let run (command: obj) (estimate: obj) (context: obj) (token: obj) : Task<obj> =
        runWithHost command estimate context token

    let private childOf (value: obj) = (value :?> ChildHandle).Child

    /// Create a child process handle driven externally (EXEC-011).
    /// The exit source starts unresolved; childExit resolves it. Kill invokes
    /// the supplied callback and tracks the call count. A host launcher that
    /// manages its own process lifecycle uses this to construct the handle it
    /// returns to runWithHostLauncher.
    let childCreate (onKill: obj) : obj =
        let exit = TaskCompletionSource<int>()
        // DSL-MUTABLE: cancellation — child process exit flag.
        let exited = ref false
        let callbacks = ResizeArray<unit -> unit>()
        // DSL-MUTABLE: resource — child kill count
        let mutable killCount = 0

        let kill () =
            killCount <- killCount + 1

            if not (isNullish onKill) then
                call0 onKill |> ignore

        ChildHandle(
            { Process = null
              Exit = exit
              Kill = kill
              Exited = exited
              OnExited = callbacks },
            (fun () -> killCount)
        )
        :> obj

    let childExit (child: obj) (code: int) : unit =
        let value = childOf child
        value.Exited.Value <- true
        value.Exit.SetResult code
        NodeProcessHost.notifyExited value

    let childOnExit (child: obj) (callback: obj) : unit =
        childOf child
        |> fun value -> value.OnExited.Add(fun () -> call0 callback |> ignore)

    let childView (child: obj) : obj =
        let value = childOf child

        box
            {| exited = value.Exited.Value
               killCount = (child :?> ChildHandle).KillCount() |}

    let waitOutcomeView (outcome: obj) : obj =
        let value = unbox<NodeProcessWait.WaitOutcome> outcome

        box
            {| exitCode = value.ExitCode
               timedOut = value.TimedOut |}

    let waitForExit (child: obj) (deadline: obj) (token: obj) : Task<obj> =
        task {
            let! outcome =
                NodeProcessWait.waitForExit
                    (childOf child)
                    (unbox<Deadline> deadline)
                    (token :?> CancellationHandle).Source.Token

            return waitOutcomeView (box outcome)
        }

    let outputCreate (estimate: obj) : obj =
        OutputHandle(ProcessOutput.create (estimateOf estimate)) :> obj

    let private outputOf (value: obj) = (value :?> OutputHandle).Collector

    let outputAddStdout (collector: obj) (bytes: obj) : unit =
        ProcessOutput.addStdout (outputOf collector) (bytesOf bytes)

    let outputAddStderr (collector: obj) (bytes: obj) : unit =
        ProcessOutput.addStderr (outputOf collector) (bytesOf bytes)

    let outputBuildResult (collector: obj) (exitCode: int) : obj =
        outcomeView (box (ProcessOutput.buildResult (outputOf collector) exitCode))

    let outputView (collector: obj) : obj =
        let value = outputOf collector

        box
            {| bytesObserved = float value.BytesObserved
               outputLimit = float value.OutputLimit
               spooled = value.Spool.IsSome
               stdoutChunks = value.Stdout.Count
               stderrChunks = value.Stderr.Count |}

    let spoolChunkCount (bytes: float) : int = Spool.chunkCount (int64 bytes)

    let spoolChunkBytes (chunkSize: int) (bytes: obj) : obj =
        Spool.chunkBytes chunkSize (bytesOf bytes) |> Array.map bytesView |> box

    let spoolStart () : obj =
        SpoolHandle(Spool.startStreamingSpool ()) :> obj

    let private spoolOf (value: obj) = (value :?> SpoolHandle).Spool

    let spoolAppend (spool: obj) (bytes: obj) : unit =
        Spool.appendStreamingSpool (spoolOf spool) (bytesOf bytes)

    let spoolPath (spool: obj) : string = (spoolOf spool).Path

    let spoolBytesWritten (spool: obj) : float = float (spoolOf spool).BytesWritten

    let spoolRead (spool: obj) : Task<obj array> =
        // DSL-MUTABLE: algorithm-scratch — spool chunk accumulator
        let chunks = ResizeArray<obj>()

        task {
            do!
                Spool.readChunks (spoolPath spool) (fun bytes ->
                    chunks.Add(bytesView bytes)
                    Task.FromResult<unit>(()))

            return chunks.ToArray()
        }

    let spoolDelete (path: string) : unit = Spool.delete path


    let bytes (text: string) : obj = Pty.bytes text |> bytesView

    let newId () : obj = PtyIdHandle(Pty.newId ()) :> obj

    let ptyIdView (id: obj) : string = idValue (id :?> PtyIdHandle).Id

    let registerParentAbort (parentId: string) (callback: obj) : int =
        Pty.registerParentAbort parentId (fun () -> call0 callback |> ignore)

    let unregisterParentAbort (parentId: string) (token: int) : unit =
        Pty.unregisterParentAbort parentId token


    let signalParse (name: string) : obj =
        match PtySignal.tryParse name with
        | Ok signal ->
            box
                {| ok = true
                   value = PtySupervisor.signalName signal |}
        | Error error -> box {| ok = false; error = error |}

    let commandSpawn (command: string) (cwd: string) : obj =
        PtyCommandHandle(PtyCommand.Spawn(command, cwd)) :> obj

    let commandWrite (value: obj) : obj =
        PtyCommandHandle(PtyCommand.Write(bytesOf value)) :> obj

    let commandRead () : obj = PtyCommandHandle PtyCommand.Read :> obj

    let commandSignal (name: string) : obj =
        match PtySignal.tryParse name with
        | Ok signal -> PtyCommandHandle(PtyCommand.Signal signal) :> obj
        | Error error -> invalidArg "signal" error

    let commandResize (width: int) (height: int) : obj =
        PtyCommandHandle(PtyCommand.Resize(width, height)) :> obj

    let private ptyCommandOf (value: obj) = (value :?> PtyCommandHandle).Command

    let ptyCommandView (value: obj) : obj =
        match ptyCommandOf value with
        | PtyCommand.Spawn(command, cwd) ->
            box
                {| kind = "Spawn"
                   command = command
                   cwd = cwd |}
        | PtyCommand.Write value ->
            box
                {| kind = "Write"
                   bytes = bytesView value |}
        | PtyCommand.Read -> box {| kind = "Read" |}
        | PtyCommand.Signal signal ->
            box
                {| kind = "Signal"
                   signal = PtySupervisor.signalName signal |}
        | PtyCommand.Resize(width, height) ->
            box
                {| kind = "Resize"
                   width = width
                   height = height |}

    let internal completionViewItem (item: PtyJoinItem) : obj =
        match item with
        | PtyExited value ->
            box
                {| kind = "PtyExited"
                   ptyId = value.PtyId
                   outcome = value.Outcome
                   closed = value.Closed |}
        | PtyFailed value ->
            box
                {| kind = "PtyFailed"
                   ptyId = value.PtyId
                   outcome = value.Outcome
                   closed = value.Closed
                   code = value.Code
                   message = value.Message |}
        | PtyAborted value ->
            box
                {| kind = "PtyAborted"
                   ptyId = value.PtyId
                   outcome = value.Outcome
                   closed = value.Closed
                   code = value.Code
                   message = value.Message |}

    let completionView (item: obj) : obj =
        completionViewItem (unbox<PtyJoinItem> item)

    let ptyExited (id: string) (outcome: string) : obj =
        box (
            PtyExited
                { PtyId = id
                  Outcome = outcome
                  Closed = true }
        )

    let ptyFailed (id: string) (message: string) : obj =
        box (
            PtyFailed
                { PtyId = id
                  Outcome = message
                  Closed = true
                  Code = "PTY_FAILED"
                  Message = message }
        )


    let ptyAborted (id: string) (message: string) : obj =
        box (
            PtyAborted
                { PtyId = id
                  Outcome = message
                  Closed = true
                  Code = "PTY_ABORTED"
                  Message = message }
        )

    let ptySignalParse (name: string) : obj = signalParse name

    let ptySignalView (name: string) : obj =
        match PtySignal.tryParse name with
        | Ok signal -> box (PtySupervisor.signalName signal)
        | Error error -> box {| ok = false; error = error |}

    let ptyCommandSpawn (command: string) (cwd: string) : obj = commandSpawn command cwd
    let ptyCommandWrite (value: obj) : obj = commandWrite value
    let ptyCommandRead () : obj = commandRead ()
    let ptyCommandSignal (name: string) : obj = commandSignal name
    let ptyCommandResize (width: int) (height: int) : obj = commandResize width height
    let ptyId (value: string) : obj = PtyIdHandle(PtyId.Create value) :> obj

    let ptyHandleView (handle: PtyHandle) : obj =
        box
            {| id = handle.Id.Value
               command = handle.Command
               startedAt = handle.StartedAt.ToString("o")
               agent = handle.Agent |}

    let ptyReadView (read: PtyRead) : obj =
        box
            {| id = read.Id.Value
               output = read.Output
               closed = read.Closed |}


    let private handlerOf (callback: obj) : PtyBackendHandler =
        fun id command ->
            task {
                let! raw =
                    unbox<Task<obj>> (
                        promiseOf (apply2 callback (box id.Value) (ptyCommandView (PtyCommandHandle command :> obj)))
                    )

                match optionalResult raw with
                | None -> return Ok()
                | Some(Ok _) -> return Ok()
                | Some(Error error) -> return Error error
            }

    let createPtyPort (options: obj) : obj =
        let senderValue = property options "sender"
        let handlerValue = property options "handler"
        let providerValue = property options "agentProvider"

        let sender =
            if isNullish senderValue then
                None
            else
                Some(fun (event: PtyExitEvent) ->
                    let item =
                        match event with
                        | PtyExitEvent.Exited(id, outcome) ->
                            PtyExited
                                { PtyId = id.Value
                                  Outcome = outcome
                                  Closed = true }
                        | PtyExitEvent.Failed(id, code, msg) ->
                            PtyFailed
                                { PtyId = id.Value
                                  Outcome = msg
                                  Closed = true
                                  Code = code
                                  Message = msg }
                        | PtyExitEvent.Aborted(id, code, msg) ->
                            PtyAborted
                                { PtyId = id.Value
                                  Outcome = msg
                                  Closed = true
                                  Code = code
                                  Message = msg }

                    call1 senderValue (completionViewItem item) |> ignore)

        let handler =
            if isNullish handlerValue then
                None
            else
                Some(handlerOf handlerValue)

        let port =
            match sender, handler with
            | Some sender, Some handler -> PtyPort(exitListener = sender, handler = handler)
            | Some sender, None -> PtyPort(exitListener = sender)
            | None, Some handler -> PtyPort(handler = handler)
            | None, None -> PtyPort()

        PtyPortHandle port :> obj

    let backendCreatePort () : obj =
        PtyPortHandle(PtyBackend.createPort ()) :> obj

    let private ptyPortOf (value: obj) = (value :?> PtyPortHandle).Port
    let private ptyIdOf (value: obj) = (value :?> PtyIdHandle).Id

    /// Register an additional JS callback for physical PTY completion.
    /// The callback receives the same plain completion object as the optional
    /// constructor sender; the underlying port remains an opaque capability.
    let portAddMailboxSender (port: obj) (sender: obj) : unit =
        (ptyPortOf port)
            .AddExitListener(fun (event: PtyExitEvent) ->
                let item =
                    match event with
                    | PtyExitEvent.Exited(id, outcome) ->
                        PtyExited
                            { PtyId = id.Value
                              Outcome = outcome
                              Closed = true }
                    | PtyExitEvent.Failed(id, code, msg) ->
                        PtyFailed
                            { PtyId = id.Value
                              Outcome = msg
                              Closed = true
                              Code = code
                              Message = msg }
                    | PtyExitEvent.Aborted(id, code, msg) ->
                        PtyAborted
                            { PtyId = id.Value
                              Outcome = msg
                              Closed = true
                              Code = code
                              Message = msg }

                call1 sender (completionViewItem item) |> ignore)

    let portFork (port: obj) (command: string) (agentName: string) (ptyId: obj) (cwd: obj) : obj =
        match agentOf agentName with
        | Error error -> invalidArg "agent" error
        | Ok agent ->
            let id = if isNullish ptyId then None else Some(ptyIdOf ptyId)
            let directory = optionString cwd
            PtyIdHandle((ptyPortOf port).Fork(command, agent.Name, ?ptyId = id, ?cwd = directory)) :> obj

    let portExists (port: obj) (id: obj) : bool = (ptyPortOf port).Exists(ptyIdOf id)
    let portKnown (port: obj) (id: obj) : bool = (ptyPortOf port).Known(ptyIdOf id)

    let portSend (port: obj) (id: obj) (command: obj) : Task<obj> =
        task {
            let! result = (ptyPortOf port).Send(ptyIdOf id, ptyCommandOf command)
            return resultObject result (fun _ -> undefinedObj)
        }

    let portRead (port: obj) (id: obj) : Task<obj> =
        task {
            let! result = (ptyPortOf port).Read(ptyIdOf id)

            return
                match result with
                | Ok(output, closed) ->
                    box
                        {| ok = true
                           value = {| output = output; closed = closed |} |}
                | Error error -> box {| ok = false; error = error |}
        }

    let portReadResult (port: obj) (id: obj) (output: string) (closed: bool) : unit =
        (ptyPortOf port).ReadResult(ptyIdOf id, output, closed)

    let portFailRead (port: obj) (id: obj) (reason: string) : unit =
        (ptyPortOf port).FailRead(ptyIdOf id, reason)

    let portRegisterExitTask (port: obj) (id: obj) (taskValue: obj) : unit =
        (ptyPortOf port).RegisterExitTask(ptyIdOf id, unbox<Task> taskValue)

    let ptyRaceExit (exitTask: obj) (milliseconds: int) : Task<bool> =
        NodeTiming.raceExit (unbox<Task> exitTask) milliseconds

    let portComplete (port: obj) (id: obj) (outcome: obj) : unit =
        match optionalResult outcome with
        | None -> (ptyPortOf port).Complete(ptyIdOf id)
        | Some result -> (ptyPortOf port).Complete(ptyIdOf id, ?outcome = Some result)

    let portCompleteAborted (port: obj) (id: obj) (message: obj) : unit =
        (ptyPortOf port).CompleteAborted(ptyIdOf id, ?message = optionString message)

    let portClose (port: obj) (id: obj) : unit = (ptyPortOf port).Close(ptyIdOf id)

    let portCloseAll (port: obj) (graceMs: obj) : Task<unit> =
        (ptyPortOf port)
            .CloseAll(
                ?graceMs =
                    (if isNullish graceMs then
                         None
                     else
                         Some(int (string graceMs)))
            )

    let agentView (agent: AgentRecord) : obj =
        box
            {| agentId = agent.AgentId
               agent = agent.Agent
               completionCellSettled = agent.CompletionCellSettled |}

    let portList (port: obj) : obj =
        let ptys = (ptyPortOf port).List()

        box
            {| agents = [||]
               ptys = ptys |> List.map ptyHandleView |> List.toArray |}




    let sessionCreate (id: string) (backend: obj) : obj =
        PtySessionHandle(PtySession.create id backend) :> obj

    let private sessionOf (value: obj) = (value :?> PtySessionHandle).Session

    let sessionView (session: obj) : obj =
        let value = sessionOf session

        box
            {| ptyId = value.PtyId
               backend = value.Backend
               closed = value.Closed
               awaitingFirstByte = value.AwaitingFirstByte
               output = value.OutputBuffer.ToString()
               pendingCount = value.Pending.Count
               exitPending = not value.ExitCompleted |}

    let sessionSetClosed (session: obj) (closed: bool) : unit = (sessionOf session).Closed <- closed
    let sessionSetBackend (session: obj) (backend: obj) : unit = (sessionOf session).Backend <- backend

    let sessionAppendOutput (session: obj) (text: string) : unit =
        (sessionOf session).OutputBuffer.Append(text) |> ignore

    let sessionPushPending (session: obj) (command: obj) : unit =
        (sessionOf session).Pending.Add(ptyCommandOf command, None)

    /// Enqueue a pending command with a settleable completion (EXEC-015).
    /// Returns a JS-native Promise that resolves to { ok: true } on success
    /// or { ok: false, error } on failure. The TCS is internal: callers
    /// never construct or inspect Fable task sources.
    let sessionPushPendingTask (session: obj) (command: obj) : Task<obj> =
        let tcs = TaskCompletionSource<Result<unit, string>>()
        (sessionOf session).Pending.Add(ptyCommandOf command, Some tcs)

        task {
            let! value = tcs.Task

            match value with
            | Ok() -> return box {| ok = true |}
            | Error error -> return box {| ok = false; error = error |}
        }

    let supervisorCreate () : obj =
        PtySupervisorHandle(PtySupervisor.create ()) :> obj

    let private supervisorOf (value: obj) =
        (value :?> PtySupervisorHandle).Supervisor

    let supervisorAdd (supervisor: obj) (id: obj) (session: obj) : unit =
        PtySupervisor.add (supervisorOf supervisor) (ptyIdOf id) (sessionOf session)

    let supervisorTryGet (supervisor: obj) (id: obj) : obj =
        match PtySupervisor.tryGet (supervisorOf supervisor) (ptyIdOf id) with
        | Some session -> PtySessionHandle session :> obj
        | None -> null

    let supervisorGet (supervisor: obj) (id: obj) : obj =
        PtySessionHandle(PtySupervisor.get (supervisorOf supervisor) (ptyIdOf id)) :> obj

    let supervisorRemove (supervisor: obj) (id: obj) : unit =
        PtySupervisor.remove (supervisorOf supervisor) (ptyIdOf id)

    let supervisorList (supervisor: obj) : string array =
        PtySupervisor.list (supervisorOf supervisor) |> List.map idValue |> List.toArray

    let supervisorSignalName (name: string) : obj =
        match PtySignal.tryParse name with
        | Ok signal -> box (PtySupervisor.signalName signal)
        | Error error -> box {| ok = false; error = error |}

    let supervisorEnsureSpawn (supervisor: obj) : Task<unit> =
        PtySupervisor.ensureSpawn (supervisorOf supervisor)

    let supervisorSpawnSync (supervisor: obj) (command: string) (cwd: string) : obj =
        PtySupervisor.spawnSync (supervisorOf supervisor) command cwd

    let supervisorFailPending (pending: obj) (reason: string) : unit =
        let entries = (pending :?> PendingHandle).Entries
        PtySupervisor.failPending entries reason

    let supervisorTakePending (supervisor: obj) (id: obj) : obj =
        PendingHandle(PtySupervisor.takePending (supervisorOf supervisor) (ptyIdOf id)) :> obj

    let supervisorDropPending (supervisor: obj) (id: obj) : obj =
        PendingHandle(PtySupervisor.drop (supervisorOf supervisor) (ptyIdOf id)) :> obj

    let supervisorApplyLive (supervisor: obj) (port: obj) (id: obj) (command: obj) : Task<obj> =
        task {
            let! result =
                PtySupervisor.applyLive (supervisorOf supervisor) (ptyPortOf port) (ptyIdOf id) (ptyCommandOf command)

            return resultObject result (fun _ -> undefinedObj)
        }


    /// Attach a live terminal to a supervisor session (EXEC-015).
    /// The exit completion source is created internally — callers pass only
    /// the terminal handle. The supervisor owns the full exit lifecycle.
    let supervisorAttach (supervisor: obj) (port: obj) (id: obj) (term: obj) : unit =
        let exitTcs = TaskCompletionSource<unit>()
        PtySupervisor.attach (supervisorOf supervisor) (ptyPortOf port) (ptyIdOf id) term exitTcs

    let spoolReadPath (path: string) : Task<obj array> =
        // DSL-MUTABLE: algorithm-scratch — spool chunk accumulator
        let chunks = ResizeArray<obj>()

        task {
            do!
                Spool.readChunks path (fun bytes ->
                    chunks.Add(bytesView bytes)
                    Task.FromResult<unit>(()))

            return chunks.ToArray()
        }

    let spoolBytesToTempFile (bytes: obj) : obj =
        let path, count, chunks = Spool.spoolBytesToTempFile (bytesOf bytes)

        box
            {| path = path
               bytesWritten = float count
               chunkCount = chunks |}

    let abortParent (parentId: string) : unit = Pty.abortParent parentId

    let readPlanView (plan: ReadPlan) : obj =
        match plan with
        | Unknown reason -> box {| kind = "Unknown"; reason = reason |}
        | AlreadyInProgress -> box {| kind = "AlreadyInProgress" |}
        | ClosedImmediate -> box {| kind = "ClosedImmediate" |}
        | Park _ -> box {| kind = "Park" |}

    let ptySessionCreate (id: string) (backend: obj) : obj = sessionCreate id backend
    let ptySessionView (session: obj) : obj = sessionView session
    let ptySessionSetClosed (session: obj) (closed: bool) : unit = sessionSetClosed session closed
    let ptySessionSetBackend (session: obj) (backend: obj) : unit = sessionSetBackend session backend
    let ptySessionPushPending (session: obj) (command: obj) : unit = sessionPushPending session command

    let sessionExitPending (session: obj) : bool = not ((sessionOf session).ExitCompleted)

    let sessionResolveExit (session: obj) : unit =
        let value = sessionOf session
        value.ExitCompleted <- true
        value.ExitCompletion.SetResult()

    let ptySessionExitPending (session: obj) : bool = sessionExitPending session
    let ptySessionResolveExit (session: obj) : unit = sessionResolveExit session

    let sessionPendingView (session: obj) : obj array =
        (sessionOf session).Pending
        |> Seq.map (fun (command, _) -> ptyCommandView (PtyCommandHandle command :> obj))
        |> Seq.toArray

    let supervisorCancelAll (supervisor: obj) : unit =
        PtySupervisor.cancelAll (supervisorOf supervisor)

    let supervisorSetSpawn (supervisor: obj) (spawn: obj) : unit =
        (supervisorOf supervisor).SpawnFn <- Some spawn

    let supervisorPendingEntries (pending: obj) : obj array =
        (pending :?> PendingHandle).Entries
        |> List.map (fun (command, completion) -> PendingEntryHandle(command, completion) :> obj)
        |> List.toArray

    let pendingCommands (pending: obj) : obj array = supervisorPendingEntries pending

    let pendingEntryView (entry: obj) : obj =
        let value = entry :?> PendingEntryHandle

        box
            {| command = ptyCommandView (PtyCommandHandle value.Command :> obj)
               hasCompletion = value.Completion.IsSome |}

    let pendingResolve (pending: obj) (index: int) (result: obj) : unit =
        let entries = (pending :?> PendingHandle).Entries
        let _, completion = entries |> List.item index

        match completion, optionalResult result with
        | Some source, Some(Ok _) -> source.SetResult(Ok())
        | Some source, Some(Error error) -> source.SetResult(Error error)
        | _ -> ()

    let renderPtyCompletion (label: string) (_id: string) (outcome: string) (exitCode: int) : string =
        sprintf "# %s has %s.\nexit_code = %d" label outcome exitCode

    let runWithHostLauncher (host: obj) (command: obj) (estimate: obj) (context: obj) (token: obj) : Task<obj> =
        let hostSpawn
            (cmd: Command)
            (ctx: ProcessContext)
            (_onStdout: byte[] -> unit)
            (_onStderr: byte[] -> unit)
            (parentToken: CancellationToken)
            : Task<Result<NodeProcessHost.ChildProcess, string>> =
            task {
                let register callback =
                    parentToken.Register(fun () -> call0 callback |> ignore) |> ignore
                    null

                let tokenView =
                    box
                        {| cancelled = parentToken.IsCancellationRequested
                           register = register |}

                let! raw =
                    unbox<Task<obj>> (
                        promiseOf (
                            apply3
                                host
                                (commandView (CommandHandle cmd :> obj))
                                (contextView (ContextHandle ctx :> obj))
                                tokenView
                        )
                    )

                if isNullish raw then
                    return Error "host launcher returned no child"
                elif not (isNullish (property raw "ok")) && not (unbox<bool> (property raw "ok")) then
                    return Error(string (property raw "error"))
                else
                    let candidate =
                        if raw :? ChildHandle then
                            raw
                        elif not (isNullish (property raw "value")) then
                            property raw "value"
                        else
                            raw

                    match candidate with
                    | :? ChildHandle as child -> return Ok child.Child
                    | _ -> return Error "host launcher returned an invalid child"
            }

        task {
            let! result =
                ProcessRunner.runWithHost
                    hostSpawn
                    (commandOf command)
                    (estimateOf estimate)
                    (contextOf context)
                    (token :?> CancellationHandle).Source.Token

            return resultView (box result)
        }
