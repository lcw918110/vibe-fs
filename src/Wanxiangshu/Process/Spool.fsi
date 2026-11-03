namespace Wanxiangshu.Process

open System.Threading.Tasks

/// Reads a finished library spool and removes it after consumption.
module Spool =

    val readAllBytes: path: string -> Task<byte array>
    val delete: path: string -> unit
