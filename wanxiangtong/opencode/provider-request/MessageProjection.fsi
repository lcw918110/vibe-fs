namespace Wanxiangshu.OpenCode

module HostMessageProjection =
    val replaceMessagesInPlace: rawOutObj: obj -> transformed: obj list -> unit
    val isRequestSeparator: message: obj -> bool
    val sanitizeMessage: raw: obj -> obj
    val sanitizeMessages: messages: obj list -> obj list
    val sanitizeOutputMessages: outObj: obj -> unit
