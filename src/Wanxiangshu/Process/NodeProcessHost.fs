namespace Wanxiangshu.Process

open System
open System.Threading
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.AsyncSupport

/// JS-host process adapter. Non-PTY child-process execution delegates to
/// wanxiangli/runtime (ProcessSession). Output spooling belongs to
/// wanxiangli/runtime through Spool.fs.
module NodeProcessHost =

    [<Import("ProcessSession", "wanxiangli/runtime")>]
    let private ProcessSessionCtor: obj = jsNative

    [<Import("join", "node:path")>]
    let private pathJoin (a: string) (b: string) : string = jsNative

    [<Import("statSync", "node:fs")>]
    let private statSync (path: string) : obj = jsNative

    [<Import("accessSync", "node:fs")>]
    let private accessSync (path: string) (mode: int) : unit = jsNative

    [<Emit("""
        ((envEntries) => {
            const out = {};
            if (process && process.env) {
                for (const k of Object.keys(process.env)) {
                    const v = process.env[k];
                    if (typeof k === 'string' && k.length > 0 && !k.includes('=') && !k.includes('\0') && typeof v === 'string' && !v.includes('\0')) {
                        out[k] = v;
                    }
                }
            }
            if (Array.isArray(envEntries)) {
                for (const pair of envEntries) {
                    const k = pair[0];
                    const v = pair[1];
                    if (typeof k === 'string' && k.length > 0 && !k.includes('=') && !k.includes('\0') && typeof v === 'string' && !v.includes('\0')) {
                        out[k] = v;
                    }
                }
            }
            return out;
        })($0)
    """)>]
    let private buildEnvSnapshot (entries: (string * string) array) : obj = jsNative

    [<Emit("""
        ((rawCwd, pathJoin) => {
            const base = (typeof rawCwd === 'string' && rawCwd.length > 0) ? rawCwd : process.cwd();
            return base.startsWith('/') ? base : pathJoin(process.cwd(), base);
        })($0, $1)
    """)>]
    let private resolveAbsoluteCwd (rawCwd: obj) (joinFn: string -> string -> string) : string = jsNative

    [<Emit("""
        ((fileName, cwd, envObj, pathJoin, statSync, accessSync) => {
            if (typeof fileName !== 'string' || fileName.length === 0 || fileName.includes('\0')) {
                return null;
            }
            const isExecutableFile = (candidate) => {
                try {
                    const st = statSync(candidate);
                    if (!st || typeof st.isFile !== 'function' || !st.isFile()) return false;
                    accessSync(candidate, 1);
                    return true;
                } catch (_) {
                    return false;
                }
            };
            if (fileName.startsWith('/')) {
                return fileName;
            }
            if (fileName.includes('/')) {
                return pathJoin(cwd, fileName);
            }
            const pathText = (envObj && typeof envObj.PATH === 'string')
                ? envObj.PATH
                : ((process && process.env && typeof process.env.PATH === 'string') ? process.env.PATH : '');
            for (const dir of pathText.split(':')) {
                const folder = dir.length > 0 ? dir : cwd;
                const candidate = pathJoin(folder, fileName);
                if (isExecutableFile(candidate)) {
                    return candidate;
                }
            }
            return pathJoin('/nonexistent-wanxiang-bin', fileName);
        })($0, $1, $2, $3, $4, $5)
    """)>]
    let private resolveExecutablePath
        (fileName: string)
        (cwd: string)
        (envObj: obj)
        (joinFn: string -> string -> string)
        (statFn: string -> obj)
        (accessFn: string -> int -> unit)
        : string =
        jsNative

    [<Emit("""
        (async function(ProcessSession, executable, args, cwd, env, stdinText, onStdout, onStderr, onClose) {
            let session;
            let failure = null;
            const rememberFailure = (error) => {
                if (failure === null) {
                    failure = error instanceof Error ? error : new Error(String(error && error.message || error));
                }
                session.signal('SIGKILL');
            };
            try {
                session = new ProcessSession({
                    executable,
                    args: Array.from(args),
                    cwd,
                    env,
                    tailBytes: 0,
                    maxWriteBytes: 16777216,
                }, (channel, bytes) => {
                    (channel === 'stdout' ? onStdout : onStderr)(bytes);
                });
            } catch (err) {
                return { ok: false, error: (err && err.message) ? String(err.message) : 'Failed to create ProcessSession' };
            }
            const startResult = await session.started();
            if (!startResult || startResult.kind === 'Failed') {
                const msg = (startResult && startResult.error && startResult.error.message)
                    ? String(startResult.error.message)
                    : ('Failed to spawn process: ' + executable);
                return { ok: false, error: msg };
            }
            const inputDone = (async () => {
                try {
                    if (typeof stdinText === 'string' && stdinText.length > 0) {
                        const encoded = new TextEncoder().encode(stdinText);
                        for (let pos = 0; pos < encoded.byteLength; pos += 16777216) {
                            const part = encoded.slice(pos, Math.min(pos + 16777216, encoded.byteLength));
                            const w = await session.write(part);
                            if (!w || w.kind !== 'Written') {
                                throw new Error(w && w.error ? w.error.message : 'Input write was not confirmed');
                            }
                        }
                    }
                    session.endInput();
                } catch (error) {
                    rememberFailure(error);
                }
            })();
            (async () => {
                while (true) {
                    const waited = await session.wait(20);
                    if (waited.state.failure) rememberFailure(waited.state.failure);
                    if (waited.kind === 'Closed') {
                        await inputDone;
                        const exitInfo = waited.state.exit;
                        const code = exitInfo && typeof exitInfo.code === 'number' ? exitInfo.code : -1;
                        if (!exitInfo && failure === null) failure = new Error('Process closed without an exit receipt');
                        onClose(code, failure);
                        break;
                    }
                }
            })();
            const killFn = () => {
                try { session.signal('SIGKILL'); } catch (_) {}
            };
            return { ok: true, session, kill: killFn };
        })($0, $1, $2, $3, $4, $5, $6, $7, $8)
    """)>]
    let private startProcessSession
        (ctor: obj)
        (executable: string)
        (args: string array)
        (cwd: string)
        (envObj: obj)
        (stdinText: obj)
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (onClose: int -> obj -> unit)
        : Task<obj> =
        jsNative

    /// Handle returned by spawn.
    ///
    /// `Exit` carries the exit code, -1 for signal exit, or a pipe/receiver failure.
    /// `Exited` is set ONLY by the close handler. `Kill` must not set it:
    /// sending SIGKILL is not the process ending, and conflating the two makes
    /// "kill sent" look like "exit observed" to every waiter.
    /// DSL-state-combination: physical — child process exit receipt and callback buffer.
    type ChildProcess =
        { Process: obj
          Exit: TaskCompletionSource<int>
          Kill: unit -> unit
          Exited: bool ref
          OnExited: (unit -> unit) ResizeArray }

    let private notifyExitedList (callbacks: (unit -> unit) ResizeArray) =
        let cbs = callbacks |> Seq.toList
        callbacks.Clear()

        for cb in cbs do
            cb ()

    let notifyExited (child: ChildProcess) = notifyExitedList child.OnExited

    let private buildEnv (envOpt: Map<string, string> option) : obj =
        let entries =
            match envOpt with
            | Some envMap -> envMap |> Map.toArray
            | None -> [||]

        buildEnvSnapshot entries

    let private cwdValue (cmd: Command) (ctx: ProcessContext) : string =
        let raw =
            match cmd.WorkingDirectory |> Option.orElse ctx.WorkingDirectory with
            | Some wd -> box wd
            | None -> null

        resolveAbsoluteCwd raw pathJoin

    let private stdinValue (stdinOpt: string option) : obj =
        match stdinOpt with
        | Some text -> box text
        | None -> null

    let private invokeKill (killObj: obj) =
        try
            emitJsExpr killObj "$0()" |> ignore
        with _ ->
            ()

    let private killIfStillRunning (exitedRef: bool ref) (killObj: obj) =
        if not exitedRef.Value then
            invokeKill killObj

    let private markProcessClosed
        (exitedRef: bool ref)
        (exitTcs: TaskCompletionSource<int>)
        (onExited: (unit -> unit) ResizeArray)
        (code: int)
        (error: obj)
        =
        if not exitedRef.Value then
            exitedRef.Value <- true

            if isNullOrUndefined error then
                trySetResult exitTcs code |> ignore
            else
                exitTcs.SetException(unbox<exn> error)

            notifyExitedList onExited

    let private finalizeStartedSession
        (started: obj)
        (exitTcs: TaskCompletionSource<int>)
        (exitedRef: bool ref)
        (onExited: (unit -> unit) ResizeArray)
        (ct: CancellationToken)
        : Result<ChildProcess, string> =
        let ok = emitJsExpr started "Boolean($0 && $0.ok)"

        if not ok then
            let errText: string =
                emitJsExpr started "String(($0 && $0.error) || 'Failed to spawn process')"

            Error errText
        else
            let sessionObj: obj = emitJsExpr started "$0.session"
            let killObj: obj = emitJsExpr started "$0.kill"
            use _ = ct.Register(fun () -> killIfStillRunning exitedRef killObj)

            Ok
                { Process = sessionObj
                  Exit = exitTcs
                  Kill = (fun () -> invokeKill killObj)
                  Exited = exitedRef
                  OnExited = onExited }

    let private launchExecutableSession
        (executable: string)
        (cwd: string)
        (envObj: obj)
        (cmd: Command)
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (ct: CancellationToken)
        : Task<Result<ChildProcess, string>> =
        task {
            let exitTcs = TaskCompletionSource<int>()
            emitJsExpr exitTcs.Task "$0.catch(() => {})" |> ignore
            // DSL-MUTABLE: algorithm-scratch — exit callback list accumulator
            let onExited = ResizeArray<unit -> unit>()
            // DSL-MUTABLE: cancellation — child process exit flag.
            let exitedRef = ref false
            let onClose = markProcessClosed exitedRef exitTcs onExited

            let! started =
                startProcessSession
                    ProcessSessionCtor
                    executable
                    (cmd.Arguments |> List.toArray)
                    cwd
                    envObj
                    (stdinValue cmd.Stdin)
                    onStdout
                    onStderr
                    onClose

            return finalizeStartedSession started exitTcs exitedRef onExited ct
        }

    let private spawnOrReject
        (cmd: Command)
        (ctx: ProcessContext)
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (ct: CancellationToken)
        =
        task {
            let envObj = buildEnv cmd.Environment
            let cwd = cwdValue cmd ctx

            let executable =
                resolveExecutablePath cmd.FileName cwd envObj pathJoin statSync accessSync

            if isNull executable then
                return Error(sprintf "Failed to spawn process: %s" cmd.FileName)
            else
                return! launchExecutableSession executable cwd envObj cmd onStdout onStderr ct
        }

    let spawn
        (cmd: Command)
        (ctx: ProcessContext)
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (ct: CancellationToken)
        : Task<Result<ChildProcess, string>> =
        task {
            if ct.IsCancellationRequested then
                return Error "Cancelled before spawn"
            else
                return! spawnOrReject cmd ctx onStdout onStderr ct
        }
