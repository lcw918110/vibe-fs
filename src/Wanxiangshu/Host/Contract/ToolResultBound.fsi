namespace Wanxiangshu.Host.Contract

module ToolResultBound =
    val DefaultHostMaxLines: int
    val DefaultHostMaxBytes: int
    val Marker: string
    val MarkerBytes: int
    val MarkerLineCost: int
    val HostMaxLines: unit -> int
    val HostMaxBytes: unit -> int
    val ContentMaxLines: unit -> int
    val ContentMaxBytes: unit -> int
    val setHostLimits: maxLines: obj -> maxBytes: obj -> unit
    val clearHostLimitsForTests: unit -> unit
    val fitsInWindow: text: string -> bool
    val bound: text: string -> string
