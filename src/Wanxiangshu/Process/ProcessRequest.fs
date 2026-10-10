namespace rec Wanxiangshu.Process

open System

/// PTY geometry, kept on the command record for round-trip but ignored by the
/// process runner (handled by the PTY pipeline).
type PtyOptions = { Cols: int; Rows: int }

/// Stable public protocol for one process execution.
type Command =
    { FileName: string
      Arguments: string list
      WorkingDirectory: string option
      Environment: Map<string, string> option
      Stdin: string option
      PtyOptions: PtyOptions option }

type EstimatedRuntime = RuntimeSeconds of float
type EstimatedOutput = OutputBytes of int64

[<RequireQualifiedAccess>]
type EstimatedMemory =
    | Medium
    | Large

type ProcessEstimate =
    { EstimatedRuntime: EstimatedRuntime
      EstimatedOutput: EstimatedOutput
      EstimatedMemory: EstimatedMemory }

type ProcessContext =
    {
        WorkingDirectory: string option
        /// EXEC-011: the administrator's ceiling on any single process.
        ///
        /// Not optional. The clause requires a finite hard limit, and an `option` here
        /// would make "no limit configured" expressible — which is the unbounded case
        /// it forbids. Callers that have no configuration pass
        /// `ProcessEstimate.DefaultHardLimit`.
        HardLimit: TimeSpan
    }

[<RequireQualifiedAccess>]
type ProcessOutcome =
    | Completed of exitCode: int * stdout: string * stderr: string * spooled: bool
    | Spooled of exitCode: int * spoolPath: string * totalBytes: int64 * chunkCount: int

[<RequireQualifiedAccess>]
type ProcessError =
    | TimeoutExceeded of duration: TimeSpan
    | SpawnFailed of reason: string
    | ProcessCancelled of reason: string
    | ExecutionFailed of reason: string

module ProcessEstimate =

    /// EXEC-011 fallback ceiling: one hour.
    ///
    /// A real finite value, not a sentinel. The previous code clamped to 36500 days
    /// and called that a bound; at that scale a runaway process is indistinguishable
    /// from an unbounded one, which is exactly what "hard limit 必须有限" rules out.
    let DefaultHardLimit = TimeSpan.FromHours 1.0

    /// EXEC-011 / GrandRewrite: `min(deadline_seconds, configured hard limit)`.
    /// Provider willingness is applied at face value — no ×3 inflation.
    let effectiveDeadline (RuntimeSeconds seconds) (hardLimit: TimeSpan) =
        let modelBudget =
            if Double.IsNaN seconds || Double.IsInfinity seconds || seconds <= 0.0 then
                hardLimit
            else
                TimeSpan.FromSeconds seconds

        min modelBudget hardLimit

    let outputThreshold (OutputBytes bytes) = if bytes <= 0L then 0L else bytes
