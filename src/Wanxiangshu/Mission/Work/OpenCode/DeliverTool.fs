namespace Wanxiangshu.Mission.Work.OpenCode

open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Mission.Work

/// Lifecycle deliver is not a registered tool. Material handback stays on wxs_deliver.
module DeliverTool =

    let description () = DeliverPolicy.description ()

    let recordedFenceReplay
        (work: DurableWorkHost.InspectedWork)
        (providerRunId: string)
        (toolCallId: string)
        : Result<string, string> option =
        DeliverPolicy.recordedFenceReplay work providerRunId toolCallId
