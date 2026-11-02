namespace Wanxiangshu.Foundation

open System.Threading.Tasks
open Fable.Core

/// 将工作推迟到下一次 Promise 微任务，保留既有接纳与日志时序。
module Defer =
    [<Emit("Promise.resolve().then($0)")>]
    let private deferPhysical<'T> (work: unit -> Task<'T>) : Task<'T> = jsNative

    let defer<'T> (work: unit -> Task<'T>) : Task<'T> = deferPhysical work
