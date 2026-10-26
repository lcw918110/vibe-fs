namespace Wanxiangshu.Mission.Work.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.OpenCode

/// The one `review` tool. Plan and Manager send the same Review command into Work.
module ReviewShell =
    val spec: factory: HostToolFactory -> scope: ToolRuntimeScope -> ToolSpec
