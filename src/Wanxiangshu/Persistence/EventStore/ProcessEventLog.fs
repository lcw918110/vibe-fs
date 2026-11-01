namespace Wanxiangshu.Persistence.EventStore

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// writer 的状态和物理追加由公开库对象独占。
[<Sealed>]
type ProcessEventLog internal (writer: obj) =
    member internal _.Writer = writer

type private NativeSegmentedWriter =
    abstract writerId: string
    abstract filePath: unit -> string
    abstract append: text: string -> obj

type private NativeSegmentedReader =
    abstract readStreams: directory: (unit -> string) * nowMs: float -> obj
    abstract writerPhysicalMetadata: directory: (unit -> string) -> obj array
    abstract physicalInventory: directory: (unit -> string) -> string

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

    [<Import("default", "node:fs")>]
    let private nodeFs: obj = jsNative

    [<Import("default", "node:path")>]
    let private nodePath: obj = jsNative

    [<Import("acquire", "wanxiangli/file-lock")>]
    let private acquireLease (path: string) (wait: obj) : Task<obj> = jsNative

    [<Import("join", "node:path")>]
    let private join2 (a: string) (b: string) : string = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (path: string) (options: obj) : unit = jsNative

    [<Import("existsSync", "node:fs")>]
    let private existsSync (path: string) : bool = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeTextFileSync (path: string) (content: string) (encoding: string) : unit = jsNative

    [<Import("appendFileSync", "node:fs")>]
    let private AppendAllText (path: string) (content: string) (encoding: string) : unit = jsNative

    [<Import("readFileSync", "node:fs")>]
    let private readBytesFileSync (path: string) : byte[] = jsNative

    [<Import("readdirSync", "node:fs")>]
    let private readdirSync (path: string) : string[] = jsNative

    [<Import("statSync", "node:fs")>]
    let private statSync (path: string) : obj = jsNative

    [<Import("unlinkSync", "node:fs")>]
    let private unlinkSync (path: string) : unit = jsNative

    [<Import("utimesSync", "node:fs")>]
    let private utimesSync (path: string) (atime: float) (mtime: float) : unit = jsNative

    [<Emit("Date.now()")>]
    let private currentTimeMs () : float = jsNative

    [<Import("openSync", "node:fs")>]
    let private openSync (path: string) (flags: string) : int = jsNative

    [<Import("fsyncSync", "node:fs")>]
    let private fsyncSync (fd: int) : unit = jsNative

    [<Import("closeSync", "node:fs")>]
    let private closeSync (fd: int) : unit = jsNative

    [<Import("createHash", "node:crypto")>]
    let private createHash (algorithm: string) : obj = jsNative

    [<Emit("$0.update($1)")>]
    let private hashUpdate (hash: obj) (content: byte[]) : obj = jsNative

    [<Emit("$0.mtimeMs")>]
    let private statMtimeMs (stat: obj) : float = jsNative

    /// Producer-side file activity is a millisecond observation carried by a
    /// nanosecond file clock: `utimes` cannot store an exact millisecond, so the
    /// raw readback is always slightly earlier than the value written. Quantize
    /// to whole milliseconds so the activity a replica publishes in
    /// `writer-manifest` round-trips through an import unchanged.
    let private fileActivityMs (stat: obj) : float = statMtimeMs stat |> round

    [<Emit("$0.size")>]
    let private statSize (stat: obj) : int = jsNative

    [<Emit("$0.digest('hex')")>]
    let private hashHex (hash: obj) : string = jsNative

    [<Literal>]
    let WanxiangshuRootName = "wanxiangshu"

    // 所有读写入口都经过此处；不能只在 writer 创建时拒绝旧目录。
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

    let private wanxiangDirectory commonDir =
        join2 (localPath commonDir) WanxiangshuRootName |> localPath

    let private eventsDirectory commonDir =
        join2 (wanxiangDirectory commonDir) "events" |> localPath

    let private ensureDirectory path =
        mkdirSync path (createObj [ "recursive" ==> true ])

    type WriterPhysicalMetadata =
        { WriterId: string
          FileNames: string list
          LastActivityMs: float }

    [<Emit("new $0()")>]
    let private construct (_class: obj) : obj = jsNative

    [<Import("SegmentedReader", "wanxiangli/segments")>]
    let private readerClass: obj = jsNative

    let private reader: NativeSegmentedReader = construct readerClass |> unbox

    /// Typed physical diagnosis: the store lock stayed contended past the
    /// acquisition budget. It carries no history meaning.
    exception StoreLockAcquireTimeout of attempts: int * waitedMs: float

    type private AcquireTimeout =
        abstract attempts: int
        abstract waitedMs: float

    [<Import("isAcquireTimeout", "wanxiangli/file-lock")>]
    let private isAcquireTimeout (cause: exn) : bool = jsNative

    let private acquireAvailableLock (target: string) : Task<obj> =
        task {
            try
                return! acquireLease target (createObj [ "kind" ==> "Budget"; "budgetMs" ==> 30000.0 ])
            with cause when isAcquireTimeout cause ->
                let timeout = unbox<AcquireTimeout> cause
                return raise (StoreLockAcquireTimeout(timeout.attempts, timeout.waitedMs))
        }

    /// Cross-process physical serialization shared by local runtime writers.
    /// It protects bytes/snapshot boundaries
    /// only; it is not durable/domain state and contains no history meaning.
    let acquireStoreLock (commonDir: string) : Task<StoreFileGate> =
        task {
            let target = wanxiangDirectory commonDir
            ensureDirectory target
            let! lease = acquireAvailableLock target
            return new StoreFileGate(lease)
        }

    /// Resource CE for the physical cross-process gate. Completion of the
    /// returned Task includes completion of proper-lockfile release; no
    /// heartbeat/retry handle may escape the operation boundary.
    let withStoreLock (commonDir: string) (work: unit -> Task<'T>) : Task<'T> =
        task {
            let! gate = acquireStoreLock commonDir

            try
                let! result = work ()
                do! gate.Release()
                return result
            with ex ->
                do! gate.Release()
                return raise ex
        }

    /// durable-events-005: 本机追加段的字节上限。它不是 Git 传输限制。
    [<Literal>]
    let DefaultSegmentCapBytes = 67108864

    type private SegmentCapState = { mutable Override: int option }

    // DSL-MUTABLE: resource — test-injected segment cap for the
    // materialization boundary; production always reads the default.
    let private segmentCapState = { Override = None }

    let internal segmentCapBytes () =
        match segmentCapState.Override with
        | Some cap -> cap
        | None -> DefaultSegmentCapBytes

    /// Run one synchronous materialization-boundary operation under an
    /// injected segment cap. The append path carries its cap inside the
    /// ProcessEventLog record instead, so a handle keeps its cap for its
    /// whole lifecycle.
    let internal withSegmentCap (cap: int) (work: unit -> 'T) : 'T =
        let previous = segmentCapState.Override
        segmentCapState.Override <- Some cap

        try
            work ()
        finally
            segmentCapState.Override <- previous

    /// Run one asynchronous convergence pass under an injected segment cap;
    /// the override spans every await of the pass.
    let internal withSegmentCapTask (cap: int) (work: unit -> Task<'T>) : Task<'T> =
        task {
            let previous = segmentCapState.Override
            segmentCapState.Override <- Some cap

            try
                return! work ()
            finally
                segmentCapState.Override <- previous
        }

    /// durable-events-005/014: writer segment naming and grouping has one
    /// owner, wanxiangli/segments (CUT-19): `<WriterId>-dddd.ndjson` with
    /// exactly four ASCII digits, ordinals 0..9999, contiguous-series
    /// grouping and the legacy/segment layout verdict. This log only lists
    /// the directory and moves bytes.
    [<Import("WriterSegments", "wanxiangli/segments")>]
    let private segmentsClass: obj = jsNative

    let private segmentLaw: obj = construct segmentsClass

    /// Throws instead of spelling a five-digit name that would read back as
    /// another writer.
    let private segmentFileOf (writerId: string) (ordinal: int) : string =
        unbox<string> (segmentLaw?segmentFileName (writerId, ordinal))

    let private legacyFileOf (writerId: string) : string =
        unbox<string> (segmentLaw?legacyFileName writerId)

    let private ordinalOfSegmentFileName (name: string) : int =
        let parsed: obj = segmentLaw?parseSegmentFileName name

        if isNull parsed then
            invalidArg "name" (sprintf "not a segment file name: %s" name)
        else
            unbox<int> parsed?ordinal

    [<Import("SegmentedWriter", "wanxiangli/segments")>]
    let private writerClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private constructWriter (_constructor: obj) (_options: obj) : obj = jsNative

    let createWithSegmentCap (commonDir: string) (writerId: string) (segmentCap: int) : ProcessEventLog =
        if String.IsNullOrWhiteSpace commonDir then
            invalidArg "commonDir" "a local state root is required"

        if
            commonDir.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))
        then
            invalidArg "commonDir" "runtime persistence cannot be placed inside .git"

        ProcessEventLog(
            constructWriter
                writerClass
                (box
                    {| directory = fun () -> eventsDirectory commonDir
                       writerId = writerId
                       segmentCap = segmentCap |})
        )

    let create (commonDir: string) (writerId: string) : ProcessEventLog =
        createWithSegmentCap commonDir writerId DefaultSegmentCapBytes

    let private writerOf (log: ProcessEventLog) : NativeSegmentedWriter = unbox log.Writer

    let writerId (log: ProcessEventLog) = (writerOf log).writerId

    /// The file this writer currently appends to: a durable-events-005
    /// segment, or the legacy suffixless file while it awaits reorganization.
    let filePath (log: ProcessEventLog) = (writerOf log).filePath ()

    let private tryAppendFault phase action =
        try
            Ok(action ())
        with cause ->
            Error { Phase = phase; Cause = cause }

    let private durabilityBarrier path =
        let fd = openSync path "r+"

        try
            fsyncSync fd
        finally
            closeSync fd

    [<Emit("Buffer.byteLength($0, 'utf8')")>]
    let private byteLengthUtf8 (text: string) : int = jsNative

    let private seriesFilesOf (commonDir: string) (writerId: string) : string list =
        let directory = eventsDirectory commonDir

        if not (existsSync directory) then
            []
        else
            unbox<string array> (segmentLaw?seriesOf (writerId, readdirSync directory))
            |> Array.toList

    let private physicalFault (value: obj) : AppendFault =
        let phase: string = value?phase

        { Phase =
            match phase with
            | "before-append" -> AppendPhase.BeforePhysicalAppend
            | "append" -> AppendPhase.PhysicalAppend
            | "open" -> AppendPhase.DurabilityOpen
            | "fsync" -> AppendPhase.DurabilityBarrier
            | "close" -> AppendPhase.DurabilityClose
            | other -> invalidOp (sprintf "unknown writer phase: %s" other)
          Cause = unbox<exn> value?cause }

    let private appendEncoded (log: ProcessEventLog) text =
        let outcome = (writerOf log).append text

        if outcome?ok then
            let completion: string = outcome?completion

            match completion with
            | "no-append" -> Ok PhysicalAppendCompletion.NoAppend
            | "durable" -> Ok PhysicalAppendCompletion.AppendDurable
            | other -> invalidOp (sprintf "unknown writer completion: %s" other)
        else
            let primary = physicalFault outcome?primary
            let kind: string = outcome?kind

            match kind with
            | "before-append" -> Error(PhysicalAppendFailure.BeforeAppend primary)
            | "after-append" ->
                let cleanup = unbox<obj array> outcome?cleanupFailures |> Array.map physicalFault |> Array.toList
                Error(PhysicalAppendFailure.AfterAppend(primary, cleanup))
            | other -> invalidOp (sprintf "unknown writer failure: %s" other)

    /// One semantic append = one sequence of complete canonical JSON+LF lines.
    /// Existing bytes are never read or rewritten.
    let append
        (log: ProcessEventLog)
        (events: EventEnvelope list)
        : Result<PhysicalAppendCompletion, PhysicalAppendFailure> =
        let encoded =
            tryAppendFault AppendPhase.BeforePhysicalAppend (fun () ->
                events
                |> List.map (EventEnvelope.normalize >> CanonicalEventCodec.encode)
                |> String.concat "")

        match encoded with
        | Error primary -> Error(PhysicalAppendFailure.BeforeAppend primary)
        | Ok "" -> Ok PhysicalAppendCompletion.NoAppend
        | Ok text -> appendEncoded log text

    let writerPhysicalMetadata (commonDir: string) : WriterPhysicalMetadata list =
        reader.writerPhysicalMetadata (fun () -> eventsDirectory commonDir)
        |> Array.map (fun value ->
            { WriterId = unbox<string> value?writerId
              FileNames = unbox<string array> value?fileNames |> Array.toList
              LastActivityMs = unbox<float> value?lastActivityMs })
        |> Array.toList

    let physicalInventory (commonDir: string) : string =
        reader.physicalInventory (fun () -> eventsDirectory commonDir)

    let private setWriterActivity path activityMs =
        let seconds = activityMs / 1000.0
        utimesSync path seconds seconds

    /// durable-events-005: the one coexistence verdict over the bytes of a
    /// legacy file and its segment series (wanxiangli/segments `coexistence`,
    /// CUT-19-a): the read layer and the reorganization both act on it, so they
    /// can never disagree about which form is the stream.
    let private coexistenceOf (legacyBytes: byte[]) (segmentBytes: byte[]) : string * int =
        let result: obj = segmentLaw?coexistence (legacyBytes, segmentBytes)
        let kind = unbox<string> result?kind

        kind,
        (if kind = "continuation" then
             unbox<int> result?copiedLines
         else
             0)

    /// durable-events-005: split canonical text into segment-sized chunks at
    /// line boundaries. A single line larger than the cap occupies its own
    /// segment; empty text produces no segments.
    /// Greedy segment assembly state for the fold below.
    type private SegmentAssembly =
        { CurrentLines: string list
          CurrentBytes: int
          Completed: string list }

    let private emptyHeadroomMarker (assembly: SegmentAssembly) : SegmentAssembly =
        // Virtual headroom exhausted before the first line fit: emit
        // an empty marker piece so the caller rolls to a fresh
        // segment instead of appending onto the tail.
        { CurrentLines = []
          CurrentBytes = 0
          Completed = "" :: assembly.Completed }

    let private sealEmptyAssembly (assembly: SegmentAssembly) : SegmentAssembly =
        if assembly.CurrentBytes > 0 then
            emptyHeadroomMarker assembly
        else
            assembly

    let private sealAssembly (assembly: SegmentAssembly) : SegmentAssembly =
        if List.isEmpty assembly.CurrentLines then
            sealEmptyAssembly assembly
        else
            let body = assembly.CurrentLines |> List.rev |> String.concat "\n"

            { CurrentLines = []
              CurrentBytes = 0
              Completed = (body + "\n") :: assembly.Completed }

    let private absorbLine (cap: int) (assembly: SegmentAssembly) (line: string) : SegmentAssembly =
        let lineBytes = byteLengthUtf8 (line + "\n")

        let sealedAssembly =
            if assembly.CurrentBytes > 0 && assembly.CurrentBytes + lineBytes > cap then
                sealAssembly assembly
            else
                assembly

        { sealedAssembly with
            CurrentLines = line :: sealedAssembly.CurrentLines
            CurrentBytes = sealedAssembly.CurrentBytes + lineBytes }

    /// durable-events-005: split canonical text into pieces that fit the
    /// segment cap at line boundaries. `headroom` is the byte budget already
    /// consumed in the first (tail) segment — the line-continuation
    /// convergence appends onto the series tail this way. A single line
    /// larger than the cap occupies its own piece; empty text produces no
    /// pieces; an exhausted headroom emits one leading empty marker piece
    /// meaning "start at a fresh segment".
    let internal splitTextIntoSegmentsWithHeadroom (text: string) (cap: int) (headroom: int) : string list =
        if text.Length = 0 then
            []
        else
            let lines = text.Split('\n') |> Array.filter (fun line -> line.Length > 0)

            let final =
                Array.fold
                    (fun assembly line -> absorbLine cap assembly line)
                    { CurrentLines = []
                      CurrentBytes = headroom
                      Completed = [] }
                    lines

            (sealAssembly final).Completed |> List.rev

    let internal splitTextIntoSegments (text: string) (cap: int) : string list =
        splitTextIntoSegmentsWithHeadroom text cap 0

    /// Write one writer's logical stream as a fresh segment series; every
    /// segment lands (with its durability barrier) before any superseded
    /// physical file is removed.
    let private writeSegmentSeries (commonDir: string) (writerId: string) (text: string) : string list =
        let directory = eventsDirectory commonDir
        let segments = splitTextIntoSegments text (segmentCapBytes ())

        let names =
            List.init (List.length segments) (fun index -> segmentFileOf writerId index)

        segments
        |> List.iteri (fun index body ->
            let path = join2 directory names.[index]
            writeTextFileSync path body "utf8"
            durabilityBarrier path)

        names

    let private reorganizeLegacy (commonDir: string) (writerId: string) : Result<unit, string> =
        let directory = eventsDirectory commonDir
        let legacyFile = join2 directory (legacyFileOf writerId)
        let bytes = readBytesFileSync legacyFile

        match CanonicalEventCodec.tryDecodeUtf8Text bytes with
        | Error reason -> Error(sprintf "writer history is not valid UTF-8: %A" reason)
        | Ok text ->
            let names = writeSegmentSeries commonDir writerId text
            // The legacy file's producer-side activity survives the physical
            // reassignment; durable Journal activity lives in the bytes.
            let legacyActivity = statSync legacyFile |> fileActivityMs
            setWriterActivity (join2 directory (List.last names)) legacyActivity
            unlinkSync legacyFile
            Ok()

    /// durable-events-005: materialization-boundary convergence for one
    /// writer. An oversized legacy file is reorganized into segments; a
    /// coexisting legacy/segment pair (an interrupted reorganization or
    /// import) is reconciled by byte-prefix validation — the longer stream
    /// wins and the shorter physical form is removed. Diverged bytes fail
    /// closed; nothing is deleted before every replacement byte has landed.
    let private convergeLegacyOnly (commonDir: string) (writerId: string) (legacyFile: string) : Result<unit, string> =
        if not (existsSync legacyFile) then
            Ok()
        elif statSync legacyFile |> statSize <= segmentCapBytes () then
            Ok()
        else
            reorganizeLegacy commonDir writerId

    let private dropSeriesAndReassign
        (commonDir: string)
        (writerId: string)
        (legacyFile: string)
        (segments: string list)
        : Result<unit, string> =
        // The legacy bytes extend the partial series: the series is a
        // reorganization remnant; remove it and reassign when oversized.
        let directory = eventsDirectory commonDir
        segments |> List.iter (fun name -> unlinkSync (join2 directory name))

        if statSync legacyFile |> statSize > segmentCapBytes () then
            reorganizeLegacy commonDir writerId
        else
            Ok()

    /// durable-events-005: line-continuation convergence — append the
    /// legacy file's canonical lines verbatim onto the series tail (rolling
    /// fresh segments at the cap), then remove the legacy file only after
    /// every appended byte has landed.
    let private seriesTailOf (directory: string) (writerId: string) (segments: string list) : string * int =
        match List.rev segments |> List.tryHead with
        | Some latest -> latest, statSync (join2 directory latest) |> statSize
        | None -> segmentFileOf writerId 0, 0

    let private fileNameForPiece (tailName: string) (writerId: string) (index: int) : string =
        match index with
        | 0 -> tailName
        | _ -> segmentFileOf writerId (ordinalOfSegmentFileName tailName + index)

    let private appendLegacyPiece (directory: string) (fileName: string) (piece: string) : unit =
        if piece.Length > 0 then
            let path = join2 directory fileName
            AppendAllText path piece "utf8"
            durabilityBarrier path

    let private appendLegacyLinesToSeries
        (commonDir: string)
        (writerId: string)
        (legacyFile: string)
        (copied: int)
        : Result<unit, string> =
        let directory = eventsDirectory commonDir

        match CanonicalEventCodec.tryDecodeUtf8Text (readBytesFileSync legacyFile) with
        | Error reason -> Error(sprintf "writer history is not valid UTF-8: %A" reason)
        | Ok text ->
            let segments = seriesFilesOf commonDir writerId
            let tailName, headroom = seriesTailOf directory writerId segments
            let remaining = text.Split('\n') |> Array.skip copied |> String.concat "\n"

            let pieces =
                splitTextIntoSegmentsWithHeadroom remaining (segmentCapBytes ()) headroom

            // Every target name is spelled before any byte lands: an ordinal
            // past 9999 refuses here, not after part of the legacy lines moved.
            let names =
                pieces |> List.mapi (fun index _ -> fileNameForPiece tailName writerId index)

            List.zip names pieces
            |> List.iter (fun (fileName, piece) -> appendLegacyPiece directory fileName piece)

            if copied > 0 then
                segments |> List.iter (fun name -> durabilityBarrier (join2 directory name))

            unlinkSync legacyFile
            Ok()

    let private reconcileCoexistingForms
        (commonDir: string)
        (writerId: string)
        (legacyFile: string)
        (segments: string list)
        : Result<unit, string> =
        let directory = eventsDirectory commonDir
        let legacyBytes = readBytesFileSync legacyFile

        let segmentBytes =
            segments
            |> List.map (fun name -> readBytesFileSync (join2 directory name))
            |> Array.concat

        match coexistenceOf legacyBytes segmentBytes with
        | "series", _ ->
            // The segment series extends (or equals) the legacy bytes:
            // the series is authoritative; drop the legacy file.
            unlinkSync legacyFile
            Ok()
        | "legacy", _ -> dropSeriesAndReassign commonDir writerId legacyFile segments
        | "continuation", copied -> appendLegacyLinesToSeries commonDir writerId legacyFile copied
        | _ -> Error(sprintf "writer files diverged: %s" writerId)

    let internal convergeWriterFiles (commonDir: string) (writerId: string) : Result<unit, string> =
        let directory = eventsDirectory commonDir
        let legacyFile = join2 directory (legacyFileOf writerId)
        let segments = seriesFilesOf commonDir writerId

        if List.isEmpty segments then
            convergeLegacyOnly commonDir writerId legacyFile
        elif not (existsSync legacyFile) then
            Ok()
        else
            reconcileCoexistingForms commonDir writerId legacyFile segments

    /// Frozen retained writer streams, sorted only by WriterId for deterministic
    /// enumeration. Retention is whole-writer physical policy; the canonical
    /// Integrator still owns cross-stream ordering and interpretation. Segments
    /// aggregate into one stream before k-way merge (durable-events-014).
    let readStreamsAt (commonDir: string) (nowMs: float) : Result<(string * EventEnvelope list) list, StorageInvalid> =
        let outcome = reader.readStreams((fun () -> eventsDirectory commonDir), nowMs)
        if unbox<bool> outcome?ok then
            unbox<obj array> outcome?streams
            |> Array.map (fun stream ->
                let events =
                    unbox<obj array> stream?events
                    |> Array.map CanonicalEventCodec.ofLibraryEvent
                    |> Array.toList
                unbox<string> stream?writerId, events)
            |> Array.toList
            |> Ok
        else
            Error(CanonicalEventCodec.invalidOfLibraryError outcome?error)

    let readStreams (commonDir: string) : Result<(string * EventEnvelope list) list, StorageInvalid> =
        readStreamsAt commonDir (currentTimeMs ())

    /// Public content digest for inline payload staging (durable-events-012).
    /// The digest IS the PayloadRef content address.
    let payloadDigest (content: byte[]) : string =
        createHash "sha256" |> fun hash -> hashUpdate hash content |> hashHex
