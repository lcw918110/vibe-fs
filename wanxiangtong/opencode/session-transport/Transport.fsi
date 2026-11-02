namespace Wanxiangshu.OpenCode

module OpenCodePortAdapter =
    type SdkClientPort =
        new: client: obj * workspaceDirectory: string option -> SdkClientPort
        interface IOpenCodePort
    type HttpPort =
        new: baseUrl: string -> HttpPort
        interface IOpenCodePort
