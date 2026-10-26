namespace Wanxiangshu.OpenCode

open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop

module SharedState =

    [<Import("sessionTopologyStore", "wanxiangli/ownership")>]
    let private topologyStore: obj = jsNative

    let SessionParents: Dictionary<string, string> = topologyStore?parentsMap

    let SessionDirectories: Dictionary<string, string> = topologyStore?directoriesMap
