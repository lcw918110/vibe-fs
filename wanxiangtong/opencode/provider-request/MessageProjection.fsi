namespace Wanxiangshu.OpenCode

module HostMessageProjection =
    val replaceMessagesInPlace: rawOutObj: obj -> transformed: obj list -> unit
    val sanitizeMessage: raw: obj -> obj
    val sanitizeMessages: messages: obj list -> obj list
    val sanitizeOutputMessages: outObj: obj -> unit
