namespace Wanxiangshu.Process

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

/// Reads a finished library spool and removes it after consumption.
module Spool =

    [<Literal>]
    let private ChunkSizeBytes: int = 204800

    [<Import("OutputSpool", "wanxiangli/runtime")>]
    let private OutputSpoolCtor: obj = jsNative

    [<Import("unlinkSync", "node:fs")>]
    let private unlinkSync (path: string) : unit = jsNative

    [<Emit("$0.readChunks($1, $2, $3)")>]
    let private readChunksJs
        (_ctor: obj)
        (_path: string)
        (_size: int)
        (_consume: byte array -> Task<unit>)
        : Task<obj> =
        jsNative

    [<Emit("((chunks) => { let size = 0; for (const c of chunks) size += c.length; const out = new Uint8Array(size); let at = 0; for (const c of chunks) { out.set(c, at); at += c.length; } return out; })($0)")>]
    let private concat (_parts: byte array array) : byte array = jsNative

    let private readChunks (path: string) (consume: byte array -> Task<unit>) : Task<unit> =
        task {
            let! result = readChunksJs OutputSpoolCtor path ChunkSizeBytes consume

            if unbox<string> result?kind <> "Read" then
                let error: obj = result?error
                return raise (InvalidOperationException(sprintf "spool read failed: %s" (string error?message)))
        }

    let readAllBytes (path: string) : Task<byte array> =
        task {
            // DSL-MUTABLE: algorithm-scratch — chunk accumulator for one complete read
            let parts = ResizeArray<byte array>()

            do!
                readChunks path (fun bytes ->
                    parts.Add bytes
                    Task.FromResult(()))

            return concat (parts.ToArray())
        }

    let private unlinkIfNamed (path: string) =
        if not (String.IsNullOrWhiteSpace path) then
            unlinkSync path

    /// Removes a finished spool file whose path the caller received in an outcome.
    let delete (path: string) : unit =
        try
            unlinkIfNamed path
        with _ ->
            ()
