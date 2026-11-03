namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host.Ports

module SessionSnapshotPort =
    val projectMessage: raw: obj -> SessionMessage option
    val projectMessages: rawMessages: obj array -> SessionMessage list

    type SdkSnapshotPort =
        new: client: obj * workspaceDirectory: string option -> SdkSnapshotPort
        interface ISessionSnapshotPort

    type HttpSnapshotPort =
        new: baseUrl: string -> HttpSnapshotPort
        interface ISessionSnapshotPort

    type SdkSessionPort =
        new: client: obj * workspaceDirectory: string option -> SdkSessionPort
        interface ISessionPort

    type HttpSessionPort =
        new: baseUrl: string -> HttpSessionPort
        interface ISessionPort
