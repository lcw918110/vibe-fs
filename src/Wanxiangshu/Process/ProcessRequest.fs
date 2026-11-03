namespace Wanxiangshu.Process

/// PTY geometry, kept on the command record for round-trip but ignored by the
/// non-PTY runtime (handled by the PTY pipeline).
type PtyOptions = { Cols: int; Rows: int }

/// Stable public protocol for one process execution.
type Command =
    { FileName: string
      Arguments: string list
      WorkingDirectory: string option
      Environment: Map<string, string> option
      Stdin: string option
      PtyOptions: PtyOptions option }
