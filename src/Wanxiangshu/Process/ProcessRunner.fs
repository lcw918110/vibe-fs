namespace rec Wanxiangshu.Process

open System
open System.Threading
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.AsyncSupport

module ProcessRunner =

    open Wanxiangshu.Process.NodeProcessHost
    open Wanxiangshu.Process.NodeProcessWait
    open Wanxiangshu.Process.LargeGate
    open Wanxiangshu.Process.ProcessOutput
    open Wanxiangshu.Process.Deadline

    type private HostSpawn =
        Command
            -> ProcessContext
            -> (byte[] -> unit)
            -> (byte[] -> unit)
            -> CancellationToken
            -> Task<Result<ChildProcess, string>>

    /// EXEC-011: the effective deadline is `min(3 × estimate, administrator ceiling)`.
    /// Read from the context so the ceiling is the one the caller configured, not a
    /// second copy of the rule here.
    let private budgetSpan (estimate: ProcessEstimate) (ctx: ProcessContext) =
        ProcessEstimate.effectiveDeadline estimate.EstimatedRuntime ctx.HardLimit

    let private validateEstimate (estimate: ProcessEstimate) : Result<unit, ProcessError> =
        let (RuntimeSeconds runtime) = estimate.EstimatedRuntime
        let (OutputBytes output) = estimate.EstimatedOutput

        if Double.IsNaN runtime || Double.IsInfinity runtime || runtime <= 0.0 then
            Error(ProcessError.ExecutionFailed "deadline_seconds must be a finite positive number")
        elif output < 0L then
            Error(ProcessError.ExecutionFailed "output_budget_bytes must be non-negative")
        else
            Ok()

    let private releaseGateIfHeld (gateHeld: bool) =
        if gateHeld then
            LargeGate.release ()

    let private maybeAcquireGate (estimate: ProcessEstimate) (ct: CancellationToken) : Task<bool> =
        if estimate.EstimatedMemory = EstimatedMemory.Large then
            task {
                do! LargeGate.acquire ct
                return true
            }
        else
            Task.FromResult false

    let private killQuietly (child: ChildProcess) =
        try
            child.Kill()
        with _ ->
            ()

    let private outcomeAfterWait
        (collector: OutputCollector)
        (budget: TimeSpan)
        (waited: WaitOutcome)
        : Result<ProcessOutcome, ProcessError> =
        if waited.TimedOut then
            abandon collector
            Error(ProcessError.TimeoutExceeded budget)
        else
            buildResult collector waited.ExitCode

    let private pairWithCollector (collector: OutputCollector) (spawnResult: Result<ChildProcess, string>) =
        match spawnResult with
        | Ok child -> Ok(child, collector)
        | Error reason ->
            abandon collector
            Error(ProcessError.SpawnFailed reason)

    let private spawnHost
        (hostSpawn: HostSpawn)
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ChildProcess * OutputCollector, ProcessError>> =
        task {
            try
                let collector = create estimate
                let! spawnResult = hostSpawn cmd ctx (addStdout collector) (addStderr collector) ct

                return pairWithCollector collector spawnResult
            with
            | _ when ct.IsCancellationRequested -> return Error(ProcessError.ProcessCancelled "Cancelled by token")
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return Error(ProcessError.ProcessCancelled "Cancelled by token")
            | ex -> return Error(ProcessError.ExecutionFailed ex.Message)
        }

    let private runSpawned
        (child: ChildProcess)
        (collector: OutputCollector)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        task {
            let clock = fun () -> DateTimeOffset.UtcNow

            // The applied budget is returned alongside the outcome so the
            // reported timeout is the one that actually fired. Recomputing it
            // for the error would be a second read of the ceiling, and a
            // TimeoutExceeded carrying a different duration than the deadline
            // that expired is a lie the operator cannot detect.
            let budget = budgetSpan estimate ctx

            try
                let! waited = waitForExit child (Deadline.ofBudget (clock ()) budget) ct
                killQuietly child
                return outcomeAfterWait collector budget waited
            with ex ->
                // Cancellation or a wait failure has no outcome to own the spool file.
                abandon collector

                if ct.IsCancellationRequested then
                    return Error(ProcessError.ProcessCancelled "Cancelled by token")
                else
                    return Error(ProcessError.ExecutionFailed ex.Message)
        }

    let private runAfterGate
        (hostSpawn: HostSpawn)
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        task {
            match! spawnHost hostSpawn cmd estimate ctx ct with
            | Error e -> return Error e
            | Ok(child, collector) -> return! runSpawned child collector estimate ctx ct
        }

    let private runProgram
        (hostSpawn: HostSpawn)
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        taskResult {
            do! validateEstimate estimate
            let! gateHeld = TaskResultCE.ofTask (maybeAcquireGate estimate ct)

            try
                return! runAfterGate hostSpawn cmd estimate ctx ct
            finally
                releaseGateIfHeld gateHeld
        }

    let runWithHost
        (hostSpawn: HostSpawn)
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        runProgram hostSpawn cmd estimate ctx ct

    let run
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        runWithHost NodeProcessHost.spawn cmd estimate ctx ct

    let private chunkBytes (chunkSize: int) (bytes: byte[]) : byte[][] =
        if isNull bytes || bytes.Length = 0 then
            [||]
        else
            let count = (bytes.Length + chunkSize - 1) / chunkSize

            Array.init count (fun i ->
                let offset = i * chunkSize
                let len = Math.Min(chunkSize, bytes.Length - offset)
                Array.sub bytes offset len)

    let private emitOutputChunks
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (outBytes: byte[])
        (errBytes: byte[])
        =
        for chunk in chunkBytes 8192 outBytes do
            onStdout chunk

        for chunk in chunkBytes 8192 errBytes do
            onStderr chunk

    let private completeLauncherChild
        (parentCt: CancellationToken)
        (exited: bool ref)
        (exitTcs: TaskCompletionSource<int>)
        (onStdout: byte[] -> unit)
        (onStderr: byte[] -> unit)
        (finish: int -> unit)
        (exitCode: int)
        (outBytes: byte[])
        (errBytes: byte[])
        =
        if parentCt.IsCancellationRequested then
            exited.Value <- true
            trySetCanceled exitTcs |> ignore
        else
            emitOutputChunks onStdout onStderr outBytes errBytes
            finish exitCode

    /// Test seam that turns a pure launcher (cmd -> (exitCode, stdout, stderr))
    /// into a host so the full process lifecycle can be exercised.
    let runWithLauncher
        (launcher: Command -> CancellationToken -> Task<int * byte[] * byte[]>)
        (cmd: Command)
        (estimate: ProcessEstimate)
        (ctx: ProcessContext)
        (ct: CancellationToken)
        : Task<Result<ProcessOutcome, ProcessError>> =
        let hostSpawn
            (_cmd: Command)
            (_ctx: ProcessContext)
            (onStdout: byte[] -> unit)
            (onStderr: byte[] -> unit)
            (parentCt: CancellationToken)
            : Task<Result<ChildProcess, string>> =
            task {
                let cts = new CancellationTokenSource()
                let exitTcs = TaskCompletionSource<int>()
                // DSL-MUTABLE: algorithm-scratch — exit callback list accumulator
                let onExited = ResizeArray<unit -> unit>()
                // DSL-MUTABLE: cancellation — launcher child exit flag.
                // DSL-MUTABLE: cancellation — process exited flag
                let exited = ref false

                // Mirrors the real host: kill only signals. `exited` is set by the
                // launcher's own completion path below, which stands in for the
                // close handler — a seam that marked the child exited on kill would
                // let waitForExit return before the simulated process finished, and
                // no test could then observe the EXEC-011 kill-then-wait order.
                let kill () =
                    if not cts.IsCancellationRequested then
                        cts.Cancel()

                let child =
                    { Process = null
                      Exit = exitTcs
                      Kill = kill
                      Exited = exited
                      OnExited = onExited }

                let finish (code: int) =
                    exited.Value <- true
                    trySetResult exitTcs code |> ignore
                    NodeProcessHost.notifyExited child

                task {
                    try
                        let! (exitCode, outBytes, errBytes) = launcher cmd cts.Token

                        completeLauncherChild
                            parentCt
                            exited
                            exitTcs
                            onStdout
                            onStderr
                            finish
                            exitCode
                            outBytes
                            errBytes
                    with
                    // A killed process still reports an exit. Leaving the cell unset
                    // here would hang every waiter, since only the real exit completes
                    // it now.
                    | _ when cts.IsCancellationRequested -> finish -1
                    | _ -> finish -1
                }
                |> ignore

                return Ok child
            }

        runWithHost hostSpawn cmd estimate ctx ct
