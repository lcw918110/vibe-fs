namespace Wanxiangshu.OpenCode

open System
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Foundation.Identity

module HostJoinAttemptFactory =
    /// New join-attempt owner for one plugin session scope: keyed begin/signal
    /// plus session-level clear, backed by the wanxiangli/handle-lifecycle
    /// join attempt registry.
    val create: unit -> obj
