namespace Wanxiangshu.OpenCode

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Host.Ports

module OpenCodeTransport =
    type Connection =
        { Sending: IOpenCodePort option
          Snapshot: ISessionSnapshotPort option
          SessionCatalog: ISessionPort option }

    let private unavailable =
        { Sending = None
          Snapshot = None
          SessionCatalog = None }

    let private sdk workspace client =
        { Sending = Some(OpenCodePortAdapter.SdkClientPort(client, workspace) :> IOpenCodePort)
          Snapshot = Some(SessionSnapshotPort.SdkSnapshotPort(client, workspace) :> ISessionSnapshotPort)
          SessionCatalog = Some(SessionSnapshotPort.SdkSessionPort(client, workspace) :> ISessionPort) }

    let private http address =
        { Sending = Some(OpenCodePortAdapter.HttpPort(address) :> IOpenCodePort)
          Snapshot = Some(SessionSnapshotPort.HttpSnapshotPort(address) :> ISessionSnapshotPort)
          SessionCatalog = Some(SessionSnapshotPort.HttpSessionPort(address) :> ISessionPort) }

    let openConnection (workspaceDirectory: string option) (input: obj) : Connection =
        if isNull input then
            unavailable
        elif not (isNull input?client) && not (isNull input?client?session) then
            sdk workspaceDirectory input?client
        elif not (isNull input?serverUrl) then
            http (input?serverUrl)
        elif not (isNull input?baseUrl) then
            http (input?baseUrl)
        elif not (isNull input?port) then
            let port: int = input?port
            http (sprintf "http://127.0.0.1:%d" port)
        else
            unavailable
