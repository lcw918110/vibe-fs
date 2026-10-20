namespace Wanxiangshu.Process

open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Repository.Programming.Js

/// JS-011/JS-053 sandbox: arbitrary model JavaScript runs in a Node vm
/// context that carries only the injected `api` object — no fs / process /
/// network / env globals reach the program. `new Function` is just the
/// invocation mechanism; the vm context is the authority boundary.
module JsSandbox =

    /// Park lifecycle state carried by the api closure. The park member reads
    /// and writes these fields through the same mutable object the workflow
    /// holds, so a parked program and the orchestrator observe one state.
    ///
    /// `ParkSignalResolve` is the park-signal resolver the workflow installs
    /// before each race: the park member calls it with the payload when the
    /// program suspends, completing the signal the workflow races against the
    /// program promise. `ParkDeferredResolve` / `ParkDeferredReject` are the
    /// deferred-promise resolvers the park member creates: `unpark` calls
    /// resolve with the injected value (the program continues as if the await
    /// returned it); `abandon` calls reject with the `__jsAbandoned` marker so
    /// native try/finally runs. `AbandonRequested` marks the cleanup window
    /// during which park is refused.
    type ParkState =
        {
            mutable ParkRequested: bool
            mutable ParkPayload: obj
            /// The park-signal resolver (a JS function) installed before each
            /// race; the park member calls it with the payload to complete the
            /// signal the workflow races against. `null` before install.
            mutable ParkSignalResolve: obj
            /// The deferred-promise resolver the park member creates; `unpark`
            /// calls it with the injected value. `null` while not parked.
            mutable ParkDeferredResolve: obj
            /// The deferred-promise rejecter the park member creates; `abandon`
            /// calls it with the `__jsAbandoned` marker. `null` while not parked.
            mutable ParkDeferredReject: obj
            /// The JS stack captured when the park member fired. The workflow
            /// uses it to mark the model-source line that is currently
            /// suspended; null when no stack was captured.
            mutable ParkStack: string
            mutable AbandonRequested: bool
        }

    /// Handle to a started sandbox program: the async IIFE promise is exposed
    /// (not awaited to completion) so a caller may hold it across calls, and
    /// the deadline reference is mutable so a caller may reset the async
    /// deadline without re-wrapping the program.
    type JsRunHandle =
        { ProgramPromise: Task<string>
          DeadlineRef: obj
          Api: obj
          ParkState: ParkState }

    [<Import("Buffer", "node:buffer")>]
    let private nodeBuffer: obj = jsNative

    [<Import("createContext", "node:vm")>]
    let private createContext (sandbox: obj) : obj = jsNative

    [<Import("runInContext", "node:vm")>]
    let private runInContext (code: string) (context: obj) (options: obj) : obj = jsNative

    [<Emit("JSON.parse($0)")>]
    let private parseJson (text: string) : obj = jsNative

    [<Emit("$0.current = $1")>]
    let private setDeadline (deadlineRef: obj) (epochMs: int64) : unit = jsNative

    /// Sentinel payloads the wrapper returns instead of the program result;
    /// recognized by prefix, never by exception-message sniffing (JS-019).
    let private failedPrefix = "{\"__jsProgramFailed\":"
    let private hostFailedPrefix = "{\"__jsHostFailed\":"
    let private invalidReturnPrefix = "{\"__jsInvalidReturn\":"
    let private parkedPrefix = "{\"__jsParked\":"
    let private deliveredPrefix = "{\"__jsDelivered\":"

    /// The ABANDONED protocol sentinel. Rejecting the program promise with an
    /// error carrying this marker unwinds the program stack from its await
    /// point so native try/finally blocks run as a finalizer (PLAN S2 step 6 /
    /// algorithm F; Python GeneratorExit precedent). The marker is a stable
    /// prefix, not a message-text sniff.
    let abandonedSentinel = "__jsAbandoned"

    /// Wrap base class + model source into an async IIFE that evaluates to a
    /// JSON string. The api is proxied so every member call re-checks the
    /// mutable injected deadline reference: synchronous segments are bounded by
    /// the vm timeout, async segments by the proxy (JS-054.1). The deadline is
    /// read from the vm-global `__deadlineRef.current` at each call, so a
    /// caller may reset it between calls without re-wrapping the program.
    ///
    /// Park lifecycle: the api's `js.park(payload)` member (built by
    /// ToolsBindings) sets `__parkState.ParkRequested`, fires the park signal
    /// the workflow races against, and returns a deferred Promise the program
    /// awaits. The wrapper itself is a plain async IIFE: it stays pending while
    /// the program is suspended at `await js.park`, and resolves with the final
    /// result when the program finishes (or with a failure/timeout sentinel).
    /// `unpark` resolves the deferred with the injected value, letting the
    /// program continue as if the await returned that value.
    ///
    /// The abandon path rejects the program promise with an `__jsAbandoned`
    /// marker so native try/finally blocks run as a finalizer (PLAN S2 step 6).
    let wrapProgram (baseClassSource: string) (modelSource: string) : string =
        "(async () => {\n"
        + baseClassSource
        + "\n"
        + "const js = api.js;\n"
        + "if (typeof JsCallable === 'function') { js.Callable = JsCallable; }\n"
        + modelSource
        + "\n"
        + "const __wrap = (target) => new Proxy(target, {\n"
        + "  get(target, prop) {\n"
        + "    const value = target[prop];\n"
        + "    if (typeof value === 'function') {\n"
        + "      return (...args) => {\n"
        + "        if (Date.now() > __deadlineRef.current) {\n"
        + "          const err = new Error('program exceeded its deadline');\n"
        + "          err.__jsFailure = { code: 'PROGRAM_TIMEOUT', reason: 'program exceeded its deadline' };\n"
        + "          throw err;\n"
        + "        }\n"
        + "        return value(...args);\n"
        + "      };\n"
        + "    }\n"
        + "    if (value && typeof value === 'object') return __wrap(value);\n"
        + "    return value;\n"
        + "  }\n"
        + "});\n"
        + "const __api = __wrap(api);\n"
        + "let __result;\n"
        + "try { __result = await new Js(__api).run(); }\n"
        + "catch (__e) {\n"
        + "  if (__e && __e.__jsFailure && typeof __e.__jsFailure.code === 'string') {\n"
        + "    return JSON.stringify({\n"
        + "      __jsHostFailed: true,\n"
        + "      code: __e.__jsFailure.code,\n"
        + "      reason: String(__e.__jsFailure.reason || __e.__jsFailure.code)\n"
        + "    });\n"
        + "  }\n"
        + "  if (__e && __e.__jsAbandoned) {\n"
        + "    return JSON.stringify({ __jsAbandoned: true });\n"
        + "  }\n"
        + "  if (__e && __e.__jsDelivered) {\n"
        + "    return JSON.stringify({ __jsDelivered: true, value: __e.__jsDelivered.value, view: __e.__jsDelivered.view });\n"
        + "  }\n"
        + "  return JSON.stringify({ __jsProgramFailed: true, message: String((__e && __e.message) || __e) });\n"
        + "}\n"
        + "try {\n"
        + "  const __json = JSON.stringify(__result);\n"
        + "  return typeof __json === 'string' ? __json : JSON.stringify({ __jsInvalidReturn: true });\n"
        + "}\n"
        + "catch (__e2) { return JSON.stringify({ __jsInvalidReturn: true }); }\n"
        + "})()"

    /// Classify a synchronous vm error: SyntaxError → INVALID_PROGRAM;
    /// vm timeout → PROGRAM_TIMEOUT; anything else → PROGRAM_FAILED.
    let private classifySyncError (error: obj) : JsFailure =
        let name = string (error?name)
        let message = string (error?message)

        if name = "SyntaxError" then
            JsFailure.InvalidProgram
        elif message.Contains "timed out" then
            JsFailure.ProgramTimeout
        else
            JsFailure.ProgramFailed message

    let private decodeHostFailed (json: string) : JsFailure =
        try
            let payload = parseJson json
            JsFailure.ofWire (string payload?code) (string payload?reason)
        with _ ->
            JsFailure.ProgramFailed "program threw"

    let private decodeProgramFailed (json: string) : JsFailure =
        try
            let payload = parseJson json
            JsFailure.ProgramFailed(string payload?message)
        with _ ->
            JsFailure.ProgramFailed "program threw"

    /// Decode the wrapper's JSON string into a typed result. `outputBoundBytes`
    /// bounds the serialized result (JS-054.2). Sentinel prefixes are matched
    /// before the byte bound so failures are never misreported as oversized.
    /// A parked sentinel is a normal outcome (the program suspended at
    /// `await js.park`), not a failure; it decodes to `Ok` with the sentinel
    /// JSON so the workflow can register the site.
    let private decodeRunResult (json: string) (outputBoundBytes: int) : Result<string, JsFailure> =
        if json.StartsWith hostFailedPrefix then
            Error(decodeHostFailed json)
        elif json.StartsWith failedPrefix then
            Error(decodeProgramFailed json)
        elif json.StartsWith invalidReturnPrefix then
            Error JsFailure.InvalidReturnValue
        elif json.StartsWith deliveredPrefix then
            Ok json
        elif json.StartsWith parkedPrefix then
            Ok json
        elif (nodeBuffer?byteLength (json, "utf8") |> unbox<int>) > outputBoundBytes then
            Error(JsFailure.ResultTooLarge None)
        else
            Ok json

    /// Start a wrapped program with an explicit park lifecycle state. The
    /// caller owns the ParkState and builds the api with park support
    /// (JsToolsBindings.createApiWithPark); this function injects both the api
    /// and the park state into the vm context.
    let runSurfaceWithPark
        (baseClassSource: string)
        (modelSource: string)
        (api: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        (parkState: ParkState option)
        : Task<JsRunHandle> =
        task {
            let deadlineRef = createObj [ "current" ==> deadlineEpochMs ]

            let state =
                match parkState with
                | Some state -> state
                | None ->
                    { ParkRequested = false
                      ParkPayload = null
                      ParkSignalResolve = null
                      ParkDeferredResolve = null
                      ParkDeferredReject = null
                      ParkStack = null
                      AbandonRequested = false }

            try
                let context =
                    createContext (
                        createObj [ "api" ==> api; "__deadlineRef" ==> deadlineRef; "__parkState" ==> state ]
                    )

                let promise =
                    runInContext
                        (wrapProgram baseClassSource modelSource)
                        context
                        (createObj [ "timeout" ==> deadlineMs ])

                return
                    { ProgramPromise = promise :?> Task<string>
                      DeadlineRef = deadlineRef
                      Api = api
                      ParkState = state }
            with ex ->
                return
                    { ProgramPromise = task { return raise ex }
                      DeadlineRef = deadlineRef
                      Api = api
                      ParkState = state }
        }

    /// Start a wrapped program in a fresh vm context without awaiting it to
    /// completion. The async IIFE's promise is exposed as `ProgramPromise` so a
    /// caller may hold it across calls; the deadline is injected as a mutable
    /// reference (`DeadlineRef`) so a caller may reset the async deadline
    /// without re-wrapping. The vm context is held by the promise's closure
    /// chain while it remains unsettled — it is not explicitly lifted.
    ///
    /// `deadlineMs` bounds the synchronous segment via the vm timeout; the
    /// injected proxy bounds async segments (JS-054.1). Synchronous errors
    /// (SyntaxError, vm timeout) surface as a rejected `ProgramPromise` and are
    /// classified by `awaitResult`.
    let runSurface
        (baseClassSource: string)
        (modelSource: string)
        (api: obj)
        (deadlineMs: int)
        (deadlineEpochMs: int64)
        : Task<JsRunHandle> =
        runSurfaceWithPark baseClassSource modelSource api deadlineMs deadlineEpochMs None

    /// Await a started sandbox program and classify its outcome. Synchronous
    /// errors (SyntaxError → INVALID_PROGRAM, vm timeout → PROGRAM_TIMEOUT) are
    /// classified here; async failures arrive as sentinel JSON and are decoded
    /// by `decodeRunResult`.
    let awaitResult (handle: JsRunHandle) (outputBoundBytes: int) : Task<Result<string, JsFailure>> =
        task {
            try
                let! json = handle.ProgramPromise
                return decodeRunResult json outputBoundBytes
            with ex ->
                return Error(classifySyncError ex)
        }

    /// Classify one already-awaited wrapper JSON string (sentinel prefixes and
    /// output bound) without re-awaiting the program promise. The park race
    /// uses this so a program failure on the raced path decodes exactly like
    /// the plain path.
    let classifyResult (json: string) (outputBoundBytes: int) : Result<string, JsFailure> =
        decodeRunResult json outputBoundBytes

    /// Reset the async deadline of an in-flight sandbox program. Subsequent api
    /// member calls re-check `deadlineRef.current`, so the new epoch takes
    /// effect without re-wrapping or re-running the program.
    let resetDeadline (handle: JsRunHandle) (deadlineEpochMs: int64) : unit =
        setDeadline handle.DeadlineRef deadlineEpochMs

    let private extractFrameDigits (frame: string) : string option =
        let marker = "evalmachine.<anonymous>:"
        let at = frame.IndexOf marker

        if at < 0 then
            None
        else
            let rest = frame.Substring(at + marker.Length)
            let colon = rest.IndexOf ':'
            Some(if colon >= 0 then rest.Substring(0, colon) else rest)

    let private parseModelLine (modelStartLine: int) (modelEndLine: int) (digits: string) : int option =
        match System.Int32.TryParse digits with
        | true, line when line >= modelStartLine && line <= modelEndLine -> Some(line - modelStartLine)
        | _ -> None

    let private tryResolveFrameLine (modelStartLine: int) (modelEndLine: int) (frame: string) : int option =
        extractFrameDigits frame
        |> Option.bind (parseModelLine modelStartLine modelEndLine)

    let private locateSuspendedLine (modelStartLine: int) (modelEndLine: int) (stack: string) : int =
        if isNull stack then
            -1
        else
            stack.Split('\n')
            |> Array.tryPick (tryResolveFrameLine modelStartLine modelEndLine)
            |> Option.defaultValue -1

    /// Render the full model program with the suspended line marked. The
    /// wrapper places line 1 at `(async () => {`, the generated base class
    /// next, then the model source; `modelStartLine` is the base class line
    /// count plus two. The closest vm frame at or after that line is the
    /// model frame that called park (directly or through where). The marker
    /// is `>| ` on the suspended line and ` | ` everywhere else; a missing
    /// stack degrades to ` | ` on every line, never to a failed park.
    let sourceWithExecutionMarker (baseClassSource: string) (modelSource: string) (stack: string) : string =
        let lines = modelSource.Split('\n')
        // Wrapper lines: 1 = "(async () => {", then the base class, then the
        // `const js = api.js;` prelude and the optional JsCallable bind.
        let modelStartLine = baseClassSource.Split('\n').Length + 4
        let modelEndLine = modelStartLine + lines.Length - 1
        let currentLine = locateSuspendedLine modelStartLine modelEndLine stack

        lines
        |> Array.mapi (fun index line -> (if index = currentLine then ">| " else " | ") + line)
        |> String.concat "\n"
        |> fun marked -> marked + "\n"

    /// Install the park signal the workflow races against the program promise.
    /// The returned Task completes with the parked payload when the program's
    /// `js.park(payload)` member fires; the resolver is stored in
    /// `parkState.ParkSignalResolve` so the park member can call it.
    let installParkSignal (parkState: ParkState) : Task<obj> =
        parkState.ParkRequested <- false
        parkState.ParkPayload <- null
        parkState.ParkSignalResolve <- null

        let signal: obj =
            emitJsExpr
                (parkState)
                "(() => { const state = $0; return new Promise((resolve) => { state.ParkSignalResolve = resolve; }); })()"

        signal :?> Task<obj>

    /// Suspend the program through the shared park channel: mark the request,
    /// fire the signal the workflow races against, and hand back the deferred
    /// promise the program awaits until unpark resolves it (or abandon
    /// rejects it). Callers own the abandon-window refusal and the stack
    /// capture so this stays the one suspension mechanism.
    let suspendProgram (parkState: ParkState) (payload: obj) : obj =
        parkState.ParkRequested <- true
        parkState.ParkPayload <- payload

        if not (isNull parkState.ParkSignalResolve) then
            emitJsExpr (parkState.ParkSignalResolve, payload) "$0($1)" |> ignore

        emitJsExpr
            parkState
            "(function(state) { return new Promise(function(resolve, reject) { state.ParkDeferredResolve = resolve; state.ParkDeferredReject = reject; }); })($0)"

    /// Resolve the deferred promise the program is awaiting at `await js.park`.
    /// Called by `unpark` with the injected value; the program continues as if
    /// the await returned that value.
    let unpark (parkState: ParkState) (value: obj) : unit =
        if not (isNull parkState.ParkDeferredResolve) then
            emitJsExpr (parkState.ParkDeferredResolve, value) "$0($1)" |> ignore

    /// Reject the deferred promise with the `__jsAbandoned` marker. Called by
    /// `abandon`/`reap`; the rejection unwinds the program stack from the await
    /// point so native try/finally blocks run as a finalizer.
    let abandon (parkState: ParkState) : unit =
        parkState.AbandonRequested <- true

        if not (isNull parkState.ParkDeferredReject) then
            emitJsExpr
                (parkState.ParkDeferredReject, abandonedSentinel)
                "$0(Object.assign(new Error($1), { __jsAbandoned: true, message: $1 }))"
            |> ignore
