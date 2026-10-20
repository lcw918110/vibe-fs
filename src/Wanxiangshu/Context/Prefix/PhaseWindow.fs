namespace Wanxiangshu.Context.Prefix

open Wanxiangshu.Foundation.Identity

[<RequireQualifiedAccess>]
module PhaseWindow =

    type PhaseCheckpoint = { ToolCallId: ToolCallId }

    type PhaseCommitWindow = { Checkpoints: PhaseCheckpoint list }

    let emptyWindow: PhaseCommitWindow = { Checkpoints = [] }

    /// Record one successful view declaration checkpoint. The same call identity
    /// re-entering is a replay of the same fact, never a second checkpoint.
    let appendCheckpoint (callId: ToolCallId) (window: PhaseCommitWindow) : PhaseCommitWindow =
        if
            window.Checkpoints
            |> List.exists (fun checkpoint -> checkpoint.ToolCallId = callId)
        then
            window
        else
            { Checkpoints = window.Checkpoints @ [ { ToolCallId = callId } ] }
