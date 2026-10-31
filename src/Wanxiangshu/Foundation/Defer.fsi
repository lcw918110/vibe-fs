namespace Wanxiangshu.Foundation

open System.Threading.Tasks

module Defer =
    val defer<'T> : work: (unit -> Task<'T>) -> Task<'T>
