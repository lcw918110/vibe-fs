namespace Wanxiangshu.Persistence.EventStore

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop

[<Sealed>]
type ProcessEventLog internal (writer: obj) =
    member internal _.Writer = writer

type private NativeSegmentedWriter =
    abstract writerId: string
    abstract filePath: unit -> string

type StoreFileGate internal (lease: obj) =
    member _.Release() : Task<unit> = lease?release ()

[<RequireQualifiedAccess>]
module ProcessEventLog =
    [<RequireQualifiedAccess>]
    type PhysicalAppendCompletion =
        | NoAppend
        | AppendDurable

    [<RequireQualifiedAccess>]
    type PhysicalAppendFailure =
        | BeforeAppend of AppendFault
        | AfterAppend of primary: AppendFault * cleanupFailures: AppendFault list

    type WriterPhysicalMetadata =
        { WriterId: string
          FileNames: string list
          LastActivityMs: float }

    [<Import("default", "node:fs")>]
    let private nodeFs: obj = jsNative

    [<Import("default", "node:path")>]
    let private nodePath: obj = jsNative

    [<Import("join", "node:path")>]
    let private join2 (a: string) (b: string) : string = jsNative

    [<Import("acquireLocalLock", "wanxiangli/local-eventstore")>]
    let private acquireLocalLock (target: Func<string>) : Task<obj> = jsNative

    [<Import("createLocalWriter", "wanxiangli/local-eventstore")>]
    let private createLocalWriter (options: obj) : obj = jsNative

    [<Import("appendLocalEvents", "wanxiangli/local-eventstore")>]
    let private appendLocalEvents (writer: obj) (events: obj array) : obj = jsNative

    [<Import("readLocalStreams", "wanxiangli/local-eventstore")>]
    let private readLocalStreams (directory: Func<string>) (nowMs: float) : obj = jsNative

    [<Import("localWriterMetadata", "wanxiangli/local-eventstore")>]
    let private localWriterMetadata (directory: Func<string>) : obj array = jsNative

    [<Import("convergeLocalWriter", "wanxiangli/local-eventstore")>]
    let private convergeLocalWriter (directory: Func<string>) (writerId: string) (segmentCap: int) : obj = jsNative

    [<Import("isAcquireTimeout", "wanxiangli/file-lock")>]
    let private isAcquireTimeout (cause: exn) : bool = jsNative

    [<Emit("Date.now()")>]
    let private currentTimeMs () : float = jsNative

    let private localPath (value: string) =
        let rejectGit (candidate: string) =
            if
                candidate.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))
            then
                invalidArg "stateRoot" "Runtime persistence cannot read or write inside .git"

        if String.IsNullOrWhiteSpace value then
            invalidArg "stateRoot" "A local state root is required"

        rejectGit value
        let resolved: string = nodePath?resolve value
        rejectGit resolved

        if unbox<bool> (nodeFs?existsSync resolved) then
            let physical: string = nodeFs?realpathSync resolved
            rejectGit physical

        resolved

    let internal lockDirectory commonDir =
        join2 (localPath commonDir) "wanxiangshu" |> localPath

    let internal eventsDirectory commonDir =
        join2 (lockDirectory commonDir) "events" |> localPath

    let internal validateWriterRoot (commonDir: string) =
        if String.IsNullOrWhiteSpace commonDir then
            invalidArg "commonDir" "a local state root is required"

        if
            commonDir.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))
        then
            invalidArg "commonDir" "runtime persistence cannot be placed inside .git"

    exception StoreLockAcquireTimeout of attempts: int * waitedMs: float

    let internal translateCause (cause: exn) : exn =
        if isAcquireTimeout cause then
            StoreLockAcquireTimeout(unbox<int> cause?attempts, unbox<float> cause?waitedMs)
        else cause

    let internal appendFaultValue (value: obj) : AppendFault =
        let phase: string = value?phase
        { Phase =
            match phase with
            | "GateAcquire" -> AppendPhase.GateAcquire
            | "Preparation" -> AppendPhase.Preparation
            | "BeforePhysicalAppend" -> AppendPhase.BeforePhysicalAppend
            | "PhysicalAppend" -> AppendPhase.PhysicalAppend
            | "DurabilityOpen" -> AppendPhase.DurabilityOpen
            | "DurabilityBarrier" -> AppendPhase.DurabilityBarrier
            | "DurabilityClose" -> AppendPhase.DurabilityClose
            | "CurrentCommit" -> AppendPhase.CurrentCommit
            | "StoreRelease" -> AppendPhase.StoreRelease
            | other -> invalidOp (sprintf "unknown local append phase: %s" other)
          Cause =
            let cause = unbox<exn> value?cause
            if phase = "GateAcquire" then translateCause cause else cause }

    let acquireStoreLock (commonDir: string) : Task<StoreFileGate> =
        task {
            try
                let! lease = acquireLocalLock (Func<string>(fun () -> lockDirectory commonDir))
                return new StoreFileGate(lease)
            with cause ->
                return raise (translateCause cause)
        }

    let withStoreLock (commonDir: string) (work: unit -> Task<'T>) : Task<'T> =
        task {
            let! gate = acquireStoreLock commonDir
            try
                let! result = work ()
                do! gate.Release()
                return result
            with cause ->
                do! gate.Release()
                return raise cause
        }

    [<Literal>]
    let DefaultSegmentCapBytes = 67108864

    let createWithSegmentCap (commonDir: string) (writerId: string) (segmentCap: int) : ProcessEventLog =
        validateWriterRoot commonDir
        ProcessEventLog(
            createLocalWriter
                (createObj
                    [ "directory" ==> Func<string>(fun () -> eventsDirectory commonDir)
                      "writerId" ==> writerId
                      "segmentCap" ==> segmentCap ])
        )

    let create (commonDir: string) (writerId: string) : ProcessEventLog =
        createWithSegmentCap commonDir writerId DefaultSegmentCapBytes

    let writerId (log: ProcessEventLog) = (unbox<NativeSegmentedWriter> log.Writer).writerId
    let filePath (log: ProcessEventLog) = (unbox<NativeSegmentedWriter> log.Writer).filePath ()

    let append (log: ProcessEventLog) (events: EventEnvelope list) =
        let outcome =
            appendLocalEvents log.Writer (events |> List.map CanonicalEventCodec.toLibraryEvent |> List.toArray)
        if unbox<bool> outcome?ok then
            match unbox<string> outcome?completion with
            | "no-append" -> Ok PhysicalAppendCompletion.NoAppend
            | "durable" -> Ok PhysicalAppendCompletion.AppendDurable
            | value -> invalidOp ("unknown writer completion: " + value)
        else
            let primary = appendFaultValue outcome?primary
            match unbox<string> outcome?kind with
            | "before-append" -> Error(PhysicalAppendFailure.BeforeAppend primary)
            | "after-append" ->
                let cleanup =
                    unbox<obj array> outcome?cleanupFailures
                    |> Array.map appendFaultValue |> Array.toList
                Error(PhysicalAppendFailure.AfterAppend(primary, cleanup))
            | value -> invalidOp ("unknown writer failure: " + value)

    let writerPhysicalMetadata (commonDir: string) : WriterPhysicalMetadata list =
        localWriterMetadata (Func<string>(fun () -> eventsDirectory commonDir))
        |> Array.map (fun value ->
            { WriterId = unbox<string> value?writerId
              FileNames = unbox<string array> value?fileNames |> Array.toList
              LastActivityMs = unbox<float> value?lastActivityMs })
        |> Array.toList

    let internal convergeWriterFiles commonDir writerId segmentCap : Result<unit, string> =
        let outcome =
            convergeLocalWriter (Func<string>(fun () -> eventsDirectory commonDir)) writerId segmentCap
        if unbox<bool> outcome?ok then Ok()
        else Error(unbox<string> outcome?reason)

    let readStreamsAt (commonDir: string) (nowMs: float) : Result<(string * EventEnvelope list) list, StorageInvalid> =
        let outcome = readLocalStreams (Func<string>(fun () -> eventsDirectory commonDir)) nowMs
        if unbox<bool> outcome?ok then
            unbox<obj array> outcome?streams
            |> Array.map (fun stream ->
                let events =
                    unbox<obj array> stream?events
                    |> Array.map CanonicalEventCodec.ofLibraryEvent |> Array.toList
                unbox<string> stream?writerId, events)
            |> Array.toList |> Ok
        else Error(CanonicalEventCodec.invalidOfLibraryError outcome?error)

    let readStreams (commonDir: string) : Result<(string * EventEnvelope list) list, StorageInvalid> =
        readStreamsAt commonDir (currentTimeMs ())
