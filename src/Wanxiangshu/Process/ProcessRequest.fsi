namespace Wanxiangshu.Process

open System

type PtyOptions = { Cols: int; Rows: int }

and Command =
    { FileName: string
      Arguments: string list
      WorkingDirectory: string option
      Environment: Map<string, string> option
      Stdin: string option
      PtyOptions: PtyOptions option }

and EstimatedRuntime = RuntimeSeconds of float

and EstimatedOutput = OutputBytes of int64

and [<RequireQualifiedAccess>] EstimatedMemory =
    | Medium
    | Large

and ProcessEstimate =
    { EstimatedRuntime: EstimatedRuntime
      EstimatedOutput: EstimatedOutput
      EstimatedMemory: EstimatedMemory }

and ProcessContext =
    { WorkingDirectory: string option
      HardLimit: TimeSpan }

and [<RequireQualifiedAccess>] ProcessOutcome =
    | Completed of exitCode: int * stdout: string * stderr: string * spooled: bool
    | Spooled of exitCode: int * spoolPath: string * totalBytes: int64 * chunkCount: int

and [<RequireQualifiedAccess>] ProcessError =
    | TimeoutExceeded of duration: TimeSpan
    | SpawnFailed of reason: string
    | ProcessCancelled of reason: string
    | ExecutionFailed of reason: string

module ProcessEstimate =
    val DefaultHardLimit: TimeSpan
    val effectiveDeadline: EstimatedRuntime -> hardLimit: TimeSpan -> TimeSpan
    val outputThreshold: EstimatedOutput -> int64
