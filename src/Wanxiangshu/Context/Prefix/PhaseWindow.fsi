namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module PhaseWindow =
    type PhaseCheckpoint = { ToolCallId: ToolCallId }

    type PhaseCommitWindow = { Checkpoints: PhaseCheckpoint list }

    val emptyWindow: PhaseCommitWindow

    val appendCheckpoint: callId: ToolCallId -> window: PhaseCommitWindow -> PhaseCommitWindow
