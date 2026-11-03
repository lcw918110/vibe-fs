namespace Wanxiangshu.Process

type PtyOptions = { Cols: int; Rows: int }

type Command =
    { FileName: string
      Arguments: string list
      WorkingDirectory: string option
      Environment: Map<string, string> option
      Stdin: string option
      PtyOptions: PtyOptions option }
