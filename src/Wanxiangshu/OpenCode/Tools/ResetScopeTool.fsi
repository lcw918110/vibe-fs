namespace Wanxiangshu.OpenCode

open Wanxiangshu.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation.HostSession

module ResetScopeTool =
    val spec:
        factory: HostToolFactory ->
        journal: AgentJournal option ->
        snapshotPort: ISessionSnapshotPort option ->
            ToolSpec
