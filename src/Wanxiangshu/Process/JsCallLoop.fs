namespace Wanxiangshu.Process

open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Repository.Programming.Js

/// Host-owned execution loop for `js.call` (PLAN phase 2). The model writes one
/// Callable class; this module owns the loop: it instantiates the class,
/// validates the Callable spec, runs shell commands, calls `watch` once per
/// time slice, dispatches `onExit`, enforces `maxDuration`, and returns one
/// structured outcome. A `check` verdict of `{ park: payload }` suspends
/// through the same park channel as `js.park`.
module JsCallLoop =

    [<Import("spawn", "node:child_process")>]
    let private spawnProcess (command: string) (options: obj) : obj = jsNative

    [<Import("connect", "node:net")>]
    let private netConnect: obj = jsNative

    [<Emit("(function(connectFn, port) { const socket = connectFn({ port: port, host: '127.0.0.1' }); return new Promise(function(resolve) { socket.once('connect', function() { socket.destroy(); resolve(true); }); socket.once('error', function() { resolve(false); }); }); })($0, $1)")>]
    let private probePort (connectFn: obj) (port: int) : JS.Promise<bool> = jsNative

    [<Emit("new Promise(function(resolve) { setTimeout(function() { resolve(undefined); }, $0); })")>]
    let private delay (milliseconds: int) : JS.Promise<unit> = jsNative

    /// Race one promise against a timeout and clear the timer when the
    /// promise wins: a pending slice timer would otherwise keep the host
    /// event loop alive after the run is already over.
    [<Emit("(function(work, ms) { return new Promise(function(resolve) { let done = false; const timer = setTimeout(function() { if (!done) { done = true; resolve({ timeout: true }); } }, ms); Promise.resolve(work).then(function(value) { if (!done) { done = true; clearTimeout(timer); resolve({ value: value }); } }, function(error) { if (!done) { done = true; clearTimeout(timer); resolve({ error: String(error) }); } }); }); })($0, $1)")>]
    let private raceWorkTimeout (work: obj) (timeoutMs: int) : JS.Promise<obj> = jsNative

    [<Emit("Promise.resolve($0)")>]
    let private promiseOf (value: obj) : JS.Promise<obj> = jsNative

    [<Emit("$0.stdout.on('data', function(chunk) { $1(String(chunk)); })")>]
    let private onStdoutChunk (child: obj) (callback: string -> unit) : unit = jsNative

    [<Emit("$0.stderr.on('data', function(chunk) { $1(String(chunk)); })")>]
    let private onStderrChunk (child: obj) (callback: string -> unit) : unit = jsNative

    [<Emit("$0.on('exit', function(code) { $1(code === null || code === undefined ? null : code); })")>]
    let private onChildExit (child: obj) (callback: obj -> unit) : unit = jsNative

    [<Emit("$0.on('error', function(error) { $1(String((error && error.message) || error)); })")>]
    let private onChildError (child: obj) (callback: string -> unit) : unit = jsNative


    /// Kill the whole process group: a shell child carries its command as a
    /// grandchild, and signalling only the shell would orphan it with the
    /// inherited stdio pipes.
    [<Emit("try { process.kill(-$0.pid, $1); } catch (error) { try { $0.kill($1); } catch (fallback) {} }")>]
    let private killChildGroup (child: obj) (signal: string) : unit = jsNative

    /// Drop the pipe handles after a kill so an exited child cannot keep the
    /// host event loop alive through an unread stdio stream.
    [<Emit("try { if ($0.stdout) { $0.stdout.destroy(); } } catch (error) {} try { if ($0.stderr) { $0.stderr.destroy(); } } catch (error) {}")>]
    let private releaseChildStreams (child: obj) : unit = jsNative

    /// Write to a spawned child's stdin; interactive use is `{ send, name, input }`.
    [<Emit("try { if ($0.stdin) { $0.stdin.write($1); } } catch (error) {}")>]
    let private writeChildStdin (child: obj) (input: string) : unit = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    [<Emit("typeof $0 === 'function'")>]
    let private isFunction (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isString (value: obj) : bool = jsNative

    [<Emit("typeof $0")>]
    let private typeOf (value: obj) : string = jsNative

    [<Emit("$0 === undefined || $0 === null")>]
    let private isNullish (value: obj) : bool = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private isFiniteNumber (value: obj) : bool = jsNative

    [<Emit("$0[$1]")>]
    let private getProperty (value: obj) (name: string) : obj = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasProperty (value: obj) (name: string) : bool = jsNative

    [<Emit("$0.next($1)")>]
    let private callNext (instance: obj) (ctx: obj) : obj = jsNative

    [<Emit("$0.watch($1)")>]
    let private callWatch (instance: obj) (ctx: obj) : obj = jsNative

    [<Emit("$0.onExit($1)")>]
    let private callOnExit (instance: obj) (result: obj) : obj = jsNative

    [<Emit("$0.sanity()")>]
    let private callSanity (instance: obj) : obj = jsNative

    /// Invoke a sanity thunk: the host drives it and mechanically checks
    /// the result.
    [<Emit("$0()")>]
    let private invokeThunk (fn: obj) : obj = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    /// The thunk returns the argument array; the host applies it. The program
    /// never calls next or watch itself.
    [<Emit("$0.apply($1, $2)")>]
    let private applyMethod (method: obj) (instance: obj) (args: obj) : obj = jsNative

    /// Non-arrays stay as the returned value so the existing if can reject
    /// them; the program never calls the method itself.
    [<Emit("Array.isArray($2) ? $0.apply($1, $2) : $2")>]
    let private applyArgumentArray (method: obj) (instance: obj) (args: obj) : obj = jsNative

    [<Emit("$0 ? $1 : $2")>]
    let private sanityMessage (argsAreArray: bool) (heldFault: string) (arrayFault: string) : string = jsNative

    [<Emit("Buffer.byteLength($0, 'utf8')")>]
    let private utf8Bytes (text: string) : int = jsNative

    let private outputHeadLimit = 131072
    let private outputTailLimit = 131072
    let private defaultIntervalMs = 10000
    let private minimumIntervalMs = 10
    let private signalGraceMs = 2000
    let private waitForDefaultMs = 30000
    let private probeIntervalMs = 100

    let private appendBoundedChunk (head: string) (tail: string) (chunk: string) : string * string =
        if head.Length >= outputHeadLimit then
            head, tail + chunk
        elif chunk.Length <= outputHeadLimit - head.Length then
            head + chunk, tail
        else
            let room = outputHeadLimit - head.Length
            head + chunk.Substring(0, room), tail + chunk.Substring room

    type private OutputBuffer() =
        // DSL-MUTABLE: algorithm-scratch — bounded head/tail capture for one stream
        let mutable head = ""
        let mutable tail = ""
        let mutable totalBytes = 0

        member _.TotalBytes = totalBytes

        member _.Truncated = totalBytes > head.Length + tail.Length

        member _.Text = head + tail

        member _.Append(chunk: string) =
            totalBytes <- totalBytes + utf8Bytes chunk

            let newHead, newTail = appendBoundedChunk head tail chunk
            head <- newHead
            tail <- newTail

            if tail.Length > outputTailLimit then
                tail <- tail.Substring(tail.Length - outputTailLimit)

    type private SliceBuffer() =
        // DSL-MUTABLE: algorithm-scratch — slice delta buffer drained by check
        let mutable text = ""

        member _.Append(chunk: string) = text <- text + chunk

        member _.Take() =
            let taken = text
            text <- ""
            taken

    type private ManagedChild(name: string, child: obj) =
        member _.Name = name
        member _.Child = child
        member val Exit = TaskCompletionSource<int>() with get
        member val Exited = false with get, set
        member val ExitCode: int = 0 with get, set
        member val Failed = false with get, set
        member val Stdout = OutputBuffer() with get
        member val Stderr = OutputBuffer() with get
        member val SliceStdout = SliceBuffer() with get
        member val SliceStderr = SliceBuffer() with get

    let private nowMs () =
        System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

    /// Fable's TaskCompletionSource does not expose TrySet*; a guarded
    /// SetResult is idempotent after the first completion.
    let private trySetResult (completion: TaskCompletionSource<int>) (value: int) =
        try
            completion.SetResult value
            true
        with _ ->
            false

    let private failure (code: string) (reason: string) : obj =
        createObj [ "ok" ==> false; "code" ==> code; "reason" ==> reason ]

    let private outcome
        (status: string)
        (exitCode: int option)
        (stdout: string)
        (stderr: string)
        (truncated: bool)
        (bytes: int)
        (durationMs: int64)
        (steps: int)
        (reason: string)
        : obj =
        createObj
            [ "ok" ==> true
              "status" ==> status
              "reason" ==> (if isNull reason then null else box reason)
              "exitCode"
              ==> (match exitCode with
                   | Some code -> box code
                   | None -> null)
              "stdout" ==> stdout
              "stderr" ==> stderr
              "truncated" ==> truncated
              "bytes" ==> bytes
              "durationMs" ==> float durationMs
              "steps" ==> float steps ]

    let private awaitJs (value: obj) : Task<obj> = unbox<Task<obj>> (promiseOf value)

    let private startChild (root: string) (name: string) (command: string) : ManagedChild =
        let child =
            spawnProcess
                command
                (createObj
                    [ "shell" ==> true
                      "detached" ==> true
                      "cwd" ==> root
                      "stdio" ==> [| "pipe"; "pipe"; "pipe" |] ])

        let managed = ManagedChild(name, child)

        onStdoutChunk child (fun chunk ->
            managed.Stdout.Append chunk
            managed.SliceStdout.Append chunk)

        onStderrChunk child (fun chunk ->
            managed.Stderr.Append chunk
            managed.SliceStderr.Append chunk)

        onChildExit child (fun code ->
            managed.Exited <- true

            if not (isNullish code) then
                managed.ExitCode <- int (unbox<float> code)

            trySetResult managed.Exit managed.ExitCode |> ignore)

        onChildError child (fun reason ->
            managed.Failed <- true
            managed.Exited <- true
            managed.ExitCode <- -1
            trySetResult managed.Exit -1 |> ignore)

        managed

    let private killManaged (managed: ManagedChild) (signal: string) =
        try
            killChildGroup managed.Child signal
        with _ ->
            ()

        releaseChildStreams managed.Child

    let private childView (managed: ManagedChild) : obj =
        createObj
            [ "name" ==> managed.Name
              "exited" ==> managed.Exited
              "exitCode" ==> (if managed.Exited then box managed.ExitCode else null) ]

    /// Wait until the child exits or one time slice elapses. Returns true when
    /// the child has exited.
    let private resolveSliceWaitOutcome (managed: ManagedChild) (winner: obj) : bool =
        if hasProperty winner "timeout" then
            managed.Exited
        else
            true

    let private waitSlice (managed: ManagedChild) (sliceMs: int) : Task<bool> =
        if managed.Exited then
            Task.FromResult true
        else
            task {
                let! winner = unbox<Task<obj>> (raceWorkTimeout (box managed.Exit.Task) sliceMs)
                return resolveSliceWaitOutcome managed winner
            }

    /// Await exit after a kill, up to the grace window.
    let private awaitExit (managed: ManagedChild) (graceMs: int) : Task<unit> =
        task {
            // DSL-MUTABLE: algorithm-scratch — grace-window wait cursor
            let mutable waited = 0

            while not managed.Exited && waited < graceMs do
                let! _ = waitSlice managed (max 25 (min 250 graceMs))
                waited <- waited + 250
        }

    /// Race one host call against the remaining run budget. `None` means the
    /// budget expired first.
    let private classifyRaceWinner (winner: obj) : obj option =
        if hasProperty winner "timeout" then
            None
        elif hasProperty winner "error" then
            raise (System.Exception(string (getProperty winner "error")))
        else
            Some(getProperty winner "value")

    let private raceDeadline (work: Task<obj>) (timeoutMs: int) : Task<obj option> =
        if timeoutMs <= 0 then
            Task.FromResult None
        else
            task {
                let! winner = unbox<Task<obj>> (raceWorkTimeout (box work) timeoutMs)
                return classifyRaceWinner winner
            }

    type private Verdict =
        | Continue
        | Abort of string
        | Park of obj

    let private verdictOf (raw: obj) : Verdict =
        if isNullish raw then
            Continue
        elif typeOf raw = "boolean" && unbox<bool> raw then
            Continue
        elif typeOf raw = "boolean" then
            Abort "watch returned false"
        elif hasProperty raw "abort" && not (isNullish (getProperty raw "abort")) then
            Abort(string (getProperty raw "abort"))
        elif hasProperty raw "park" then
            Park(getProperty raw "park")
        else
            Continue

    let private dispatchParkSignal
        (state: Wanxiangshu.Process.JsSandbox.ParkState)
        (stack: string)
        (payload: obj)
        : unit =
        state.ParkRequested <- true
        state.ParkPayload <- payload

        if not (isNull stack) then
            state.ParkStack <- stack

        if not (isNull state.ParkSignalResolve) then
            emitJsExpr (state.ParkSignalResolve, payload) "$0($1)" |> ignore

    let private interpretInjectedResume (injected: obj) : string =
        if isString injected && string injected = "abort" then
            "abort"
        else
            "continue"

    type private SliceAction =
        | SliceContinue
        | SliceAbort of string
        | SlicePark of obj

    let private checkVerdictOf (raw: obj) : SliceAction =
        match verdictOf raw with
        | Continue -> SliceContinue
        | Abort reason -> SliceAbort reason
        | Park payload -> SlicePark payload

    type private ForegroundSliceTransition =
        | KeepWaiting
        | CompletedNormal
        | CompletedTimeout
        | TerminatedAbort of string
        | TerminatedParkAbort
        | TerminatedUnsupported

    let private interpretForegroundParkDecision (decision: string) : ForegroundSliceTransition =
        match decision with
        | "abort" -> TerminatedParkAbort
        | "unsupported" -> TerminatedUnsupported
        | _ -> KeepWaiting

    type private StepCommand =
        | EndOfRun
        | RunCommand of string
        | SpawnChild of name: string * command: string
        | SignalChild of name: string * signal: string
        | SendChild of name: string * input: string
        | WaitForPort of port: int * timeoutMs: int
        | InvalidCommand

    let private parseSignalName (raw: obj) : string =
        let value = getProperty raw "signal"

        if isString value && string value <> "" then
            string value
        else
            "SIGTERM"

    let private parseWaitForTimeout (waitFor: obj) : int =
        let value = getProperty waitFor "timeoutMs"

        if isNullish value || not (isFiniteNumber value) then
            waitForDefaultMs
        else
            int (unbox<float> value)

    /// Parse the `{ send }` step: the flat spelling `{ send: input, name }`
    /// and the nested spelling `{ send: { input, name } }` both spell one
    /// signal-free write to a running child.
    let private parseSendChildCommand (raw: obj) : StepCommand =
        let send = getProperty raw "send"
        let topName = string (getProperty raw "name")

        if isString send then
            // Flat spelling { send: input, name }: the tool description once
            // promised it, so it stays a legal alias of the nested form.
            SendChild(topName, string send)
        else
            let inputValue = getProperty send "input"
            let input = if isNullish inputValue then "" else string inputValue
            let nameValue = getProperty send "name"
            let targetName = if isNullish nameValue then topName else string nameValue
            SendChild(targetName, input)

    let private parseStepCommand (raw: obj) : StepCommand =
        if isNullish raw then
            EndOfRun
        elif isString raw then
            RunCommand(string raw)
        elif hasProperty raw "spawn" then
            SpawnChild(string (getProperty raw "name"), string (getProperty raw "spawn"))
        elif hasProperty raw "signal" then
            SignalChild(string (getProperty raw "name"), parseSignalName raw)
        elif hasProperty raw "send" then
            parseSendChildCommand raw
        elif hasProperty raw "waitFor" then
            let waitFor = getProperty raw "waitFor"
            WaitForPort(int (unbox<float> (getProperty waitFor "port")), parseWaitForTimeout waitFor)
        else
            InvalidCommand

    type private PortProbeOutcome =
        | PortReady
        | PortTimedOut
        | PortDeadline
        | PortAborted
        | PortUnsupported
        | PortPending

    let private decidePortProbe
        (connected: bool)
        (elapsed: int64)
        (timeoutMs: int64)
        (deadlineReached: bool)
        : PortProbeOutcome =
        if connected then PortReady
        elif elapsed >= timeoutMs then PortTimedOut
        elif deadlineReached then PortDeadline
        else PortPending

    let private interpretPortVerdict (verdict: SliceAction) (decision: string) : PortProbeOutcome =
        match verdict with
        | SliceAbort _ -> PortAborted
        | SlicePark _ when decision = "abort" -> PortAborted
        | SlicePark _ when decision = "unsupported" -> PortUnsupported
        | _ -> PortPending

    type private CallableValidated =
        { Class: obj
          Instance: obj
          MaxDurationMs: int64
          IntervalMs: int
          HasOnExit: bool }

    let private parseIntervalMs (intervalValue: obj) : int =
        if isNullish intervalValue || not (isFiniteNumber intervalValue) then
            defaultIntervalMs
        else
            max minimumIntervalMs (int (unbox<float> intervalValue))

    let private validateCallableSpec (callableClass: obj) : Result<CallableValidated, obj> =
        if not (isFunction callableClass) then
            Error(failure "CALLABLE_SPEC_INCOMPLETE" "js.call expects a class extending js.Callable")
        else
            let instance = construct callableClass
            let maxDurationValue = getProperty instance "maxDuration"
            let hasNext = isFunction (getProperty instance "next")
            let hasCheck = isFunction (getProperty instance "watch")
            let intervalValue = getProperty instance "interval"
            let intervalMs = parseIntervalMs intervalValue

            if
                isNullish maxDurationValue
                || not (isFiniteNumber maxDurationValue)
                || unbox<float> maxDurationValue <= 0.0
            then
                Error(failure "CALLABLE_SPEC_INCOMPLETE" "Callable must declare a positive maxDuration")
            elif not hasNext then
                Error(failure "CALLABLE_SPEC_INCOMPLETE" "Callable must implement next(ctx)")
            elif not hasCheck then
                Error(failure "CALLABLE_SPEC_INCOMPLETE" "Callable must implement watch(ctx)")
            elif not (isFunction (getProperty instance "sanity")) then
                Error(
                    failure
                        "CALLABLE_SPEC_INCOMPLETE"
                        "Callable must implement sanity() — a control-flow self-test returning { nextDone, watchVeto } thunks"
                )
            else
                let maxDurationMs = int64 (unbox<float> maxDurationValue)
                let hasOnExit = isFunction (getProperty instance "onExit")

                Ok
                    { Class = callableClass
                      Instance = instance
                      MaxDurationMs = maxDurationMs
                      IntervalMs = intervalMs
                      HasOnExit = hasOnExit }

    /// Suspend the run through the shared park channel and wait for the
    /// injected value. The injected string 'abort' aborts; anything else
    /// resumes the loop. Returns "unsupported" when the run has no park state.
    let private parkAtCheck
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (stack: string)
        (payload: obj)
        : Task<string> =
        task {
            if parkState.IsNone then
                return "unsupported"
            else
                let state = parkState.Value
                dispatchParkSignal state stack payload

                let deferred: obj =
                    emitJsExpr
                        state
                        "(function(state) { return new Promise(function(resolve, reject) { state.ParkDeferredResolve = resolve; state.ParkDeferredReject = reject; }); })($0)"

                let! injected = awaitJs deferred
                return interpretInjectedResume injected
        }

    /// A failed prove() is a semantic fork, not a terminal failure: suspend
    /// through the shared park channel with the diagnosis payload, and let the
    /// injected unpark value become the js.call outcome by this park point's
    /// contract. Without a park channel the diagnosis cannot be presented, so
    /// the run refuses instead of silently degrading.
    let private parkOrFailProof
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (stack: string)
        (payload: obj)
        : Task<obj> =
        match parkState with
        | None ->
            Task.FromResult(
                failure "CALL_PARK_UNSUPPORTED" "prove failure requires the park channel to present its diagnosis"
            )
        | Some state ->
            task {
                dispatchParkSignal state stack payload

                let deferred: obj =
                    emitJsExpr
                        state
                        "(function(state) { return new Promise(function(resolve, reject) { state.ParkDeferredResolve = resolve; state.ParkDeferredReject = reject; }); })($0)"

                return! awaitJs deferred
            }

    let private executeValidated
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (root: string)
        (stack: string)
        (spec: CallableValidated)
        : Task<obj> =
        task {
            let instance = spec.Instance
            let maxDurationMs = spec.MaxDurationMs
            let intervalMs = spec.IntervalMs
            let hasOnExit = spec.HasOnExit
            let startedMs = nowMs ()
            let deadlineMs = startedMs + maxDurationMs
            let children = Dictionary<string, ManagedChild>()
            let output = OutputBuffer()
            // DSL-MUTABLE: algorithm-scratch — loop step counter shared with
            // checkContext (a ref cell: a closure cannot capture a mutable let)
            let stepCell = ref 0
            // DSL-MUTABLE: algorithm-scratch — overall loop state
            let mutable running = true
            let mutable status = "completed"
            // DSL-MUTABLE: algorithm-scratch — abort/timeout explanation
            let mutable outcomeReason: string = null
            let mutable lastExitCode: int option = None
            // DSL-MUTABLE: algorithm-scratch — failure/outcome slot for this run
            let mutable result: obj = null
            let scratch = ManagedChild("wait", null)

            let checkContext (managed: ManagedChild) : obj =
                // The slice deltas of the foreground command and every
                // background child land in one observation window, so a
                // spawned REPL's replies are visible from check.
                let stdoutParts = ResizeArray<string>()
                let stderrParts = ResizeArray<string>()
                stdoutParts.Add(managed.SliceStdout.Take())
                stderrParts.Add(managed.SliceStderr.Take())

                for entry in children.Values |> Seq.sortBy (fun child -> child.Name) do
                    stdoutParts.Add(entry.SliceStdout.Take())
                    stderrParts.Add(entry.SliceStderr.Take())

                createObj
                    [ "root" ==> root
                      "step" ==> stepCell.Value
                      "stdout" ==> String.concat "" stdoutParts
                      "stderr" ==> String.concat "" stderrParts
                      "exited" ==> managed.Exited
                      "exitCode" ==> (if managed.Exited then box managed.ExitCode else null)
                      "backgrounds" ==> (children.Values |> Seq.map childView |> Seq.toArray)
                      "elapsedMs" ==> float (nowMs () - startedMs)
                      "remainingMs" ==> float (max 0L (deadlineMs - nowMs ())) ]

            let exitContext (managed: ManagedChild) : obj =
                createObj
                    [ "root" ==> root
                      "exitCode" ==> (if managed.Exited then box managed.ExitCode else null)
                      "stdout" ==> managed.Stdout.Text
                      "stderr" ==> managed.Stderr.Text
                      "elapsedMs" ==> float (nowMs () - startedMs) ]

            let interpretParkDecision (decision: string) : string =
                if decision = "unsupported" then
                    result <- failure "CALL_PARK_UNSUPPORTED" "park verdict requires the orchestrated execution path"

                    "unsupported"
                else
                    decision

            let parkOrAbort (payload: obj) : Task<string> =
                if not (isNull result) then
                    Task.FromResult "stop"
                else
                    task {
                        let! decision = parkAtCheck parkState stack payload
                        return interpretParkDecision decision
                    }

            let finishManaged (managed: ManagedChild) : Task<unit> =
                task {
                    if not managed.Exited then
                        let! _ = awaitExit managed signalGraceMs
                        ()
                }

            let checkForegroundSlice (managed: ManagedChild) : Task<ForegroundSliceTransition> =
                task {
                    let! verdictRaw = awaitJs (callWatch instance (checkContext managed))

                    match checkVerdictOf verdictRaw with
                    | SliceContinue -> return KeepWaiting
                    | SliceAbort reason -> return TerminatedAbort reason
                    | SlicePark payload ->
                        let! decision = parkOrAbort payload
                        return interpretForegroundParkDecision decision
                }

            let foregroundSliceAfterWait (managed: ManagedChild) (exited: bool) : Task<ForegroundSliceTransition> =
                if exited then
                    Task.FromResult CompletedNormal
                else
                    checkForegroundSlice managed

            let stepForegroundSlice (managed: ManagedChild) : Task<ForegroundSliceTransition> =
                task {
                    let remaining = deadlineMs - nowMs ()

                    if remaining <= 0L then
                        return CompletedTimeout
                    else
                        let slice = int (min (int64 intervalMs) remaining)
                        let! exited = waitSlice managed slice
                        return! foregroundSliceAfterWait managed exited
                }

            let applyForegroundTransition
                (managed: ManagedChild)
                (transition: ForegroundSliceTransition)
                : Task<bool * bool> =
                task {
                    match transition with
                    | KeepWaiting -> return false, false
                    | CompletedNormal -> return true, false
                    | CompletedTimeout ->
                        status <- "timeout"
                        outcomeReason <- "maxDuration exceeded"
                        killManaged managed "SIGKILL"
                        do! finishManaged managed
                        return true, false
                    | TerminatedAbort reason ->
                        status <- "aborted"
                        outcomeReason <- reason
                        killManaged managed "SIGKILL"
                        do! finishManaged managed
                        return true, true
                    | TerminatedParkAbort ->
                        status <- "aborted"
                        outcomeReason <- "check park resumed with 'abort'"
                        killManaged managed "SIGKILL"
                        do! finishManaged managed
                        return true, true
                    | TerminatedUnsupported ->
                        killManaged managed "SIGKILL"
                        do! finishManaged managed
                        return true, false
                }

            let settleForegroundChild (managed: ManagedChild) : Task<unit> =
                task {
                    lastExitCode <- Some managed.ExitCode
                    output.Append managed.Stdout.Text
                    output.Append managed.Stderr.Text
                    let! _ = awaitJs (callOnExit instance (exitContext managed))
                    ()
                }

            let runForeground (command: string) : Task<unit> =
                task {
                    let managed = startChild root "foreground" command
                    // DSL-MUTABLE: algorithm-scratch — per-command loop state
                    let mutable finished = false
                    let mutable aborted = false

                    while not finished && isNull result do
                        let! transition = stepForegroundSlice managed
                        let! (isFinished, isAborted) = applyForegroundTransition managed transition
                        finished <- isFinished
                        aborted <- isAborted

                    if isNull result && hasOnExit then
                        do! settleForegroundChild managed
                    elif isNull result then
                        lastExitCode <- Some managed.ExitCode
                        output.Append managed.Stdout.Text
                        output.Append managed.Stderr.Text

                    if aborted || status = "timeout" then
                        running <- false
                }

            let escalateKill (managed: ManagedChild) : Task<unit> =
                task {
                    if not managed.Exited then
                        killManaged managed "SIGKILL"
                        let! _ = awaitExit managed signalGraceMs
                        ()
                }

            let signalChildProcess (name: string) (signalName: string) : Task<unit> =
                task {
                    match children.TryGetValue name with
                    | false, _ -> result <- failure "CALL_CHILD_UNKNOWN" ("signal names an unknown background: " + name)
                    | true, managed ->
                        killManaged managed signalName
                        let! _ = awaitExit managed signalGraceMs
                        do! escalateKill managed
                        children.Remove name |> ignore
                }

            let normalizeChildInput (input: string) : string =
                if input = "" || input.EndsWith "\n" then
                    input
                else
                    input + "\n"

            let sendChildInput (name: string) (input: string) : unit =
                match children.TryGetValue name with
                | false, _ -> result <- failure "CALL_CHILD_UNKNOWN" ("send names an unknown background: " + name)
                | true, managed ->
                    let payload = normalizeChildInput input

                    writeChildStdin managed.Child payload

            let samplePortSlice () : Task<PortProbeOutcome> =
                task {
                    let slice = int (min (int64 intervalMs) (max 1L (deadlineMs - nowMs ())))
                    let! _ = unbox<Task<unit>> (delay (min slice probeIntervalMs))
                    let! verdictRaw = awaitJs (callWatch instance (checkContext scratch))
                    let verdict = checkVerdictOf verdictRaw

                    let! decision =
                        match verdict with
                        | SlicePark payload -> parkOrAbort payload
                        | _ -> Task.FromResult "continue"

                    return interpretPortVerdict verdict decision
                }

            let probePortSlice (port: int) (waitStarted: int64) (timeoutMs: int) : Task<PortProbeOutcome> =
                task {
                    let! connected = unbox<Task<bool>> (probePort netConnect port)

                    let initialOutcome =
                        decidePortProbe connected (nowMs () - waitStarted) (int64 timeoutMs) (nowMs () >= deadlineMs)

                    if initialOutcome <> PortPending then
                        return initialOutcome
                    else
                        return! samplePortSlice ()
                }

            let waitForPortCondition (port: int) (timeoutMs: int) : Task<unit> =
                task {
                    let waitStarted = nowMs ()
                    // DSL-MUTABLE: algorithm-scratch — waitFor poll state
                    let mutable ready = false
                    let mutable failureReason: string option = None

                    let applyPortOutcome (outcome: PortProbeOutcome) : unit =
                        match outcome with
                        | PortReady -> ready <- true
                        | PortTimedOut -> failureReason <- Some "timeout"
                        | PortDeadline ->
                            status <- "timeout"
                            outcomeReason <- "maxDuration exceeded"
                            failureReason <- Some "deadline"
                        | PortAborted -> failureReason <- Some "abort"
                        | PortUnsupported -> failureReason <- Some "unsupported"
                        | PortPending -> ()

                    while not ready && failureReason.IsNone && isNull result do
                        let! outcome = probePortSlice port waitStarted timeoutMs
                        applyPortOutcome outcome

                    if failureReason = Some "timeout" && isNull result then
                        result <- failure "CALL_WAIT_TIMEOUT" "waitFor condition was not met before its timeout"
                    elif failureReason = Some "abort" then
                        status <- "aborted"
                        outcomeReason <- "check aborted while waiting for the port"
                        running <- false
                    elif failureReason = Some "deadline" || failureReason = Some "unsupported" then
                        running <- false
                }

            let executeStepCommand (cmd: StepCommand) : Task<unit> =
                task {
                    match cmd with
                    | EndOfRun -> running <- false
                    | RunCommand c -> do! runForeground c
                    | SpawnChild(name, c) -> children.[name] <- startChild root name c
                    | SignalChild(name, sigName) -> do! signalChildProcess name sigName
                    | SendChild(name, input) -> sendChildInput name input
                    | WaitForPort(port, timeoutMs) -> do! waitForPortCondition port timeoutMs
                    | InvalidCommand ->
                        result <-
                            failure
                                "CALL_COMMAND_UNSUPPORTED"
                                "next() must return a command string, null, { spawn, name }, { send: { name, input } }, { signal, name } or { waitFor: { port, timeoutMs } }"
                }

            let applyStepOutcome (stepOutcome: obj option) : Task<unit> =
                task {
                    match stepOutcome with
                    | None ->
                        status <- "timeout"
                        outcomeReason <- "maxDuration exceeded inside next(ctx)"
                        running <- false
                    | Some raw -> do! executeStepCommand (parseStepCommand raw)
                }

            while running && isNull result do
                let ctx = createObj [ "root" ==> root; "step" ==> stepCell.Value ]
                let remaining = deadlineMs - nowMs ()
                let! stepOutcome = raceDeadline (awaitJs (callNext instance ctx)) (int remaining)
                stepCell.Value <- stepCell.Value + 1
                do! applyStepOutcome stepOutcome

            for entry in children.Values |> Seq.toArray do
                killManaged entry "SIGKILL"
                let! _ = awaitExit entry signalGraceMs
                ()

            if not (isNull result) then
                return result
            else
                return
                    outcome
                        status
                        lastExitCode
                        output.Text
                        ""
                        output.Truncated
                        output.TotalBytes
                        (nowMs () - startedMs)
                        stepCell.Value
                        outcomeReason
        }

    let private sanityPayload (message: string) (returned: obj) : obj =
        createObj [ "sanity" ==> createObj [ "failure" ==> message; "returned" ==> returned ] ]

    let private isVeto (verdict: obj) : bool =
        (typeOf verdict = "boolean" && not (unbox<bool> verdict))
        || (hasProperty verdict "abort" && not (isNullish (getProperty verdict "abort")))

    type private SanityThunks = { NextDone: obj; WatchVeto: obj }

    type private SanityFailure =
        | SpecIncomplete of error: obj
        | SanityFault of payload: obj

    let private extractSanityThunks (sanity: obj) : Result<SanityThunks, SanityFailure> =
        let nextDone = getProperty sanity "nextDone"
        let watchVeto = getProperty sanity "watchVeto"

        if not (isFunction nextDone) || not (isFunction watchVeto) then
            Error(
                SpecIncomplete(
                    failure
                        "CALLABLE_SPEC_INCOMPLETE"
                        "sanity() must return { nextDone, watchVeto } — two thunks, each returning the argument array the host applies"
                )
            )
        else
            Ok
                { NextDone = nextDone
                  WatchVeto = watchVeto }

    let private validateSanityNext (spec: CallableValidated) (nextDone: obj) : Task<Result<unit, SanityFailure>> =
        task {
            let! nextArgs = awaitJs (invokeThunk nextDone)
            let! nextResult = awaitJs (applyArgumentArray (getProperty spec.Instance "next") spec.Instance nextArgs)

            if not (isArray nextArgs) || not (isNullish nextResult) then
                let message =
                    sanityMessage
                        (isArray nextArgs)
                        "next has no null path — the termination condition does not exist yet"
                        "nextDone must return the argument array for next — the program does not call next"

                return Error(SanityFault(sanityPayload message nextResult))
            else
                return Ok()
        }

    let private validateSanityWatch (spec: CallableValidated) (watchVeto: obj) : Task<Result<unit, SanityFailure>> =
        task {
            let! watchArgs = awaitJs (invokeThunk watchVeto)
            let! watchVerdict = awaitJs (applyArgumentArray (getProperty spec.Instance "watch") spec.Instance watchArgs)

            if not (isArray watchArgs) || not (isVeto watchVerdict) then
                let message =
                    sanityMessage
                        (isArray watchArgs)
                        "watch has no veto path — the watchdog cannot say no"
                        "watchVeto must return the argument array for watch — the program does not call watch"

                return Error(SanityFault(sanityPayload message watchVerdict))
            else
                return Ok()
        }

    let private executeSanityProof
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (root: string)
        (stack: string)
        (spec: CallableValidated)
        : Task<obj> =
        task {
            let verifiedThunks: Task<Result<unit, SanityFailure>> =
                taskResult {
                    let! sanityRaw = awaitJs (callSanity spec.Instance) |> TaskResultCE.ofTask
                    let! thunks = extractSanityThunks sanityRaw
                    do! validateSanityNext spec thunks.NextDone
                    do! validateSanityWatch spec thunks.WatchVeto
                    return ()
                }

            let! outcome =
                task {
                    try
                        return! verifiedThunks
                    with ex ->
                        return Error(SanityFault(sanityPayload (string ex.Message) null))
                }

            match outcome with
            | Error(SpecIncomplete fail) -> return fail
            | Error(SanityFault payload) -> return! parkOrFailProof parkState stack payload
            | Ok() ->
                let fresh = construct spec.Class
                return! executeValidated parkState root stack { spec with Instance = fresh }
        }

    /// Execute one Callable class. `root` is the shell working directory;
    /// `stack` is the model stack captured at the `js.call(...)` call site so a
    /// check-park carries the same execution marker as a direct park.
    let execute
        (parkState: Wanxiangshu.Process.JsSandbox.ParkState option)
        (root: string)
        (callableClass: obj)
        (stack: string)
        : Task<obj> =
        match validateCallableSpec callableClass with
        | Error fail -> Task.FromResult fail
        | Ok spec -> executeSanityProof parkState root stack spec
