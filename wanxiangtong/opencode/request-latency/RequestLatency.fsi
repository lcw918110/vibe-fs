namespace Wanxiangshu.OpenCode

open Fable.Core
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Persistence.Journal

[<AttachMembers>]
type OpenCodeRequestLatency =
    new: watchdog: LatencyWatchdog * isCompacting: (string -> bool) -> OpenCodeRequestLatency
    member ObserveEvent: raw: obj -> unit
    member ToolStarted: session: string -> unit
    member SessionDeleted: session: string -> unit
    member Dispose: unit -> unit

module RequestLatency =
    val create:
        sessionPort: ISessionHostPort ->
        rootWorkspace: IRootWorkspaceReader ->
        eventPort: IEventObservationPort ->
        journal: AgentJournal option ->
        isCompacting: (string -> bool) ->
            OpenCodeRequestLatency
