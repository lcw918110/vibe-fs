namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks

module JsPrograms =
    val forSession: sessionId: string -> obj
    val pendingNames: sessionId: string -> string list
    /// The program executing this physical call. A review from inside that program
    /// is not blocked by its own name.
    val noteCaller: sessionId: string -> callId: string -> name: string -> unit
    val clearCaller: sessionId: string -> callId: string -> unit
    val callerName: sessionId: string -> callId: string -> string option
    val deliveryBlockersWithChoicesForSession: sessionId: string -> (string * string) list
    val closeSession: sessionId: string -> Task<string list>
    val closeAll: unit -> Task<unit>
