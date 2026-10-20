namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module SharedTerminalBus =
    val acquire: directory: string -> Events.HostEventPort
    val release: directory: string option -> port: Events.HostEventPort option -> unit
    val tryAcquireForWorkspace: workspace: string option -> Task<(string * Events.HostEventPort) option>
