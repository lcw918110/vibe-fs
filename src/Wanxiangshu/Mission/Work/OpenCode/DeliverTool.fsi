namespace Wanxiangshu.Mission.Work.OpenCode

module DeliverTool =
    val description: unit -> string

    /// Algorithm E1.1: the state one deliver fence already recorded, if the work
    /// settled it. The recorded outcome travels with the result, so a replay
    /// tells a handoff from an end. A `None` means this call is not a replay
    /// of a retirement.
    val recordedFenceReplay:
        work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork ->
        providerRunId: string ->
        toolCallId: string ->
            Result<string, string> option
