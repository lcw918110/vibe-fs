namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Host.Ports

module OpenCodeTransport =
    type Connection =
        { Sending: IOpenCodePort option
          Snapshot: ISessionSnapshotPort option
          SessionCatalog: ISessionPort option }

    val openConnection: workspaceDirectory: string option -> input: obj -> Connection
