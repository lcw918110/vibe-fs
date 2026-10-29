namespace Wanxiangshu.OpenCode

open System.Threading.Tasks

module HostSignalSubscribeSurface =
    val recordInterrupt: sessionId: string -> unit
    val refuseInterrupt: sessionId: string -> unit
    val forgetInterrupts: sessionId: string -> unit
    val interruptCursor: sessionId: string -> (unit -> bool)
    val trySubscribe: input: obj -> onSignalEvent: obj -> Task<obj>
