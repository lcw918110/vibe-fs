namespace Wanxiangshu.OpenCode

module HostMessageProjection =
    val replaceMessagesInPlace: rawOutObj: obj -> transformed: obj list -> unit
    val sanitizeMessage: raw: obj -> obj
    val sanitizeMessages: messages: obj list -> obj list
    val sanitizeAdvisorMessages: messages: obj list -> obj list
    val sanitizeAdvisorOutputMessages: outObj: obj -> unit
    val sanitizeOutputMessages: outObj: obj -> unit
