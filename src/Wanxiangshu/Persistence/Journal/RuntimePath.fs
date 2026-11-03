namespace Wanxiangshu.Persistence.Journal

open System.Threading.Tasks
open Fable.Core

/// 产品布局由宿主提供；工作区定位、状态目录检查和初始化归库。
module RuntimePath =
    [<Import("join", "node:path")>]
    let private joinPath (left: string) (right: string) : string = jsNative

    [<Import("primaryDirectory", "wanxiangli/workspace-state")>]
    let private primaryDirectory (workspace: string) : Task<string> = jsNative

    [<Import("primaryDirectorySync", "wanxiangli/workspace-state")>]
    let private primaryDirectorySync (workspace: string) : string = jsNative

    [<Import("checkStateDirectory", "wanxiangli/workspace-state")>]
    let private checkStateDirectory (stateRoot: string) : string = jsNative

    [<Import("initializeStateDirectory", "wanxiangli/workspace-state")>]
    let private initializeStateDirectory (stateRoot: string) : string = jsNative

    let private runtimeDirectory root =
        joinPath (joinPath root "wanxiangshu") "runtimes"

    let private stateDirectory primary =
        joinPath primary ".Herdchestra" |> checkStateDirectory

    let stateRoot (workspace: string) : Task<string> =
        task {
            let! primary = primaryDirectory workspace
            return stateDirectory primary
        }

    let initializeStateRoot (workspace: string) : Task<string> =
        task {
            let! target = stateRoot workspace
            return initializeStateDirectory target
        }

    let forWorkspace (workspace: string) : Task<string> =
        task {
            let! target = stateRoot workspace
            return runtimeDirectory target
        }

    let stateRootSync (workspace: string) =
        primaryDirectorySync workspace |> stateDirectory

    let forStateRoot (stateRoot: string) =
        checkStateDirectory stateRoot |> runtimeDirectory
