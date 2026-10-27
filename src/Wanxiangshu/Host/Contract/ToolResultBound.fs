namespace Wanxiangshu.Host.Contract

open Fable.Core
open Fable.Core.JsInterop

module ToolResultBound =

    [<Import("toolResultBound", "wanxiangli/projection")>]
    let private boundStore: obj = jsNative

    let DefaultHostMaxLines: int = 2000
    let DefaultHostMaxBytes: int = 51200
    let Marker: string = "...head truncated (tail kept)...\n\n"
    let MarkerBytes: int = 34
    let MarkerLineCost: int = 2

    let HostMaxLines () : int = boundStore?hostMaxLines ()
    let HostMaxBytes () : int = boundStore?hostMaxBytes ()
    let ContentMaxLines () : int = boundStore?contentMaxLines ()
    let ContentMaxBytes () : int = boundStore?contentMaxBytes ()

    let setHostLimits (maxLines: obj) (maxBytes: obj) : unit =
        boundStore?setHostLimits (maxLines, maxBytes)

    let clearHostLimitsForTests () : unit =
        boundStore?clearHostLimitsForTests ()

    let fitsInWindow (text: string) : bool =
        boundStore?fitsInWindow (text)

    let bound (text: string) : string =
        boundStore?bound (text)
