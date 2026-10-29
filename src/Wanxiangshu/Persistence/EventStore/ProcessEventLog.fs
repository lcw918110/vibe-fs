namespace Wanxiangshu.Persistence.EventStore

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation.Identity

/// durable-events-004/005/010/017.
/// Runtime truth is deliberately boring: one process owns one append-only NDJSON
/// file under the project's .Herdchestra state root. Git object plumbing is not a
/// dependency of this module and therefore cannot leak into the local append path.
type ProcessEventLog =
    private
        { CommonDir: string
          WriterId: string
          SegmentCap: int
          // DSL-MUTABLE: resource — the events-directory file name this writer
          // currently appends to (a durable-events-005 segment name, or the
          // legacy suffixless name until materialization reorganizes it). A
          // vanished target re-enumerates the series on the next append.
          mutable CurrentFile: string }

type StoreFileGate internal (releaseFn: obj) =
    // DSL-MUTABLE: resource — physical one-shot release latch; not domain state.
    let mutable released = false

    member _.Release() =
        task {
            if not released then
                released <- true
                let release: unit -> Task<unit> = unbox releaseFn
                do! release ()
        }

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

    [<Emit("""
    (function(nodeFs, nodePath) {
        function isProcessAlive(pid) {
            if (typeof pid !== 'number' || pid <= 0 || isNaN(pid)) return false;
            try {
                process.kill(pid, 0);
                return true;
            } catch (e) {
                return e.code === 'EPERM';
            }
        }

        return Object.assign({}, nodeFs, {
            mkdir: function(dirPath, cb) {
                try {
                    nodeFs.mkdirSync(dirPath);
                    try {
                        nodeFs.writeFileSync(nodePath.join(dirPath, 'owner.json'), JSON.stringify({ pid: process.pid, time: Date.now() }));
                    } catch (_) {}
                    if (typeof cb === 'function') cb(null);
                } catch (err) {
                    if (typeof cb === 'function') cb(err);
                }
            },
            rmdir: function(dirPath, cb) {
                try {
                    nodeFs.rmSync(dirPath, { recursive: true, force: true });
                    if (typeof cb === 'function') cb(null);
                } catch (err) {
                    if (typeof cb === 'function') cb(err);
                }
            },
            rmdirSync: function(dirPath) {
                nodeFs.rmSync(dirPath, { recursive: true, force: true });
            },
            stat: function(dirPath, cb) {
                try {
                    var stat = nodeFs.statSync(dirPath);
                    try {
                        var ownerFile = nodePath.join(dirPath, 'owner.json');
                        if (nodeFs.existsSync(ownerFile)) {
                            var content = nodeFs.readFileSync(ownerFile, 'utf8');
                            var data = JSON.parse(content);
                            if (data && data.pid && !isProcessAlive(data.pid)) {
                                stat.mtime = new Date(0);
                                stat.mtimeMs = 0;
                            }
                        }
                    } catch (_) {}
                    if (typeof cb === 'function') cb(null, stat);
                } catch (err) {
                    if (typeof cb === 'function') cb(err);
                }
            }
        });
    })($0, $1)
    """)>]
    let private buildProcessAwareFs (nodeFs: obj) (nodePath: obj) : obj = jsNative

    let processAwareFs: obj = buildProcessAwareFs nodeFs nodePath

    [<Import("default", "proper-lockfile")>]
    let private lockfile: obj = jsNative

    [<Emit("$0($1, $2)")>]
    let private lockAsync (fn: obj) (path: string) (options: obj) : Task<obj> = jsNative

    [<Emit("($0 != null && $0.code === 'ELOCKED')")>]
    let private isLockContention (cause: exn) : bool = jsNative

    [<Import("setTimeout", "node:timers/promises")>]
    let private waitForLockRetry (milliseconds: int) : Task<unit> = jsNative

    [<Import("join", "node:path")>]
    let private join2 (a: string) (b: string) : string = jsNative

    [<Import("mkdirSync", "node:fs")>]
    let private mkdirSync (path: string) (options: obj) : unit = jsNative

    [<Import("existsSync", "node:fs")>]
    let private existsSync (path: string) : bool = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeTextFileSync (path: string) (content: string) (encoding: string) : unit = jsNative

    [<Import("writeFileSync", "node:fs")>]
    let private writeBytesFileSync (path: string) (content: byte[]) : unit = jsNative

    [<Import("appendFileSync", "node:fs")>]
    let private AppendAllText (path: string) (content: string) (encoding: string) : unit = jsNative

    [<Import("readFileSync", "node:fs")>]
    let private readBytesFileSync (path: string) : byte[] = jsNative

    [<Import("readFileSync", "node:fs")>]
    let private readTextFileSync (path: string) (encoding: string) : string = jsNative

    [<Import("readdirSync", "node:fs")>]
    let private readdirSync (path: string) : string[] = jsNative

    [<Import("statSync", "node:fs")>]
    let private statSync (path: string) : obj = jsNative

    [<Import("readSync", "node:fs")>]
    let private readSync (fd: int) (buffer: byte[]) (offset: int) (length: int) (position: int) : int = jsNative

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

    [<Emit("[$0.dev, $0.ino, $0.mode, $0.size, $0.mtimeMs, $0.ctimeMs].join(':')")>]
    let private statIdentity (stat: obj) : string = jsNative

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

    [<Emit("$0.dev")>]
    let private statDev (stat: obj) : float = jsNative

    [<Emit("$0.ino")>]
    let private statIno (stat: obj) : float = jsNative

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

    /// durable-convergence-011: the whole-writer UTC calendar-day retention
    /// rule has one owner, wanxiangli/retention (CUT-19). This log only supplies
    /// the cut-off instant and each writer's activity; it never decides the day
    /// arithmetic itself. A non-finite instant is rejected, never treated as
    /// expired.
    [<Import("WriterRetention", "wanxiangli/retention")>]
    let private retentionClass: obj = jsNative

    [<Emit("new $0()")>]
    let private constructRetention (cls: obj) : obj = jsNative

    let private retention: obj = constructRetention retentionClass

    let isWriterActiveAt (nowMs: float) (lastActivityMs: float) : bool =
        unbox<bool> (retention?isRetained (nowMs, lastActivityMs))

    let private tryAcquireLock (target: string) (options: obj) waitStep : Task<obj option> =
        task {
            try
                let! release = lockAsync lockfile target options
                return Some release
            with cause when isLockContention cause ->
                let delay = min 150.0 (Math.Round(30.0 * Math.Pow(1.1, float waitStep))) |> int
                do! waitForLockRetry delay
                return None
        }

    /// Typed physical diagnosis: the store lock stayed contended past the
    /// acquisition budget. It carries no history meaning.
    exception StoreLockAcquireTimeout of attempts: int * waitedMs: float

    /// Total wall-clock budget for one physical lock acquisition. Each retry
    /// delay is already bounded; this bounds the whole wait so a stuck peer
    /// surfaces as a typed timeout instead of an unbounded block.
    let private storeLockAcquireBudgetMs = 30000.0

    let private acquireAvailableLock (target: string) : Task<obj> =
        task {
            let options =
                createObj
                    [ "fs" ==> processAwareFs
                      "realpath" ==> false
                      "stale" ==> 5000
                      "update" ==> 1500
                      "retries" ==> 0 ]

            // DSL-MUTABLE: resource — this acquisition's physical release capability.
            let mutable acquired: obj option = None
            // DSL-MUTABLE: algorithm-scratch — index in the original ten-delay retry cycle.
            let mutable waitStep = 0
            // DSL-MUTABLE: algorithm-scratch — bounded acquisition attempt count.
            let mutable attempts = 0
            let started = currentTimeMs ()

            while acquired.IsNone && currentTimeMs () - started < storeLockAcquireBudgetMs do
                let! release = tryAcquireLock target options waitStep
                acquired <- release
                waitStep <- (waitStep + 1) % 10
                attempts <- attempts + 1

            match acquired with
            | Some release -> return release
            | None -> return raise (StoreLockAcquireTimeout(attempts, currentTimeMs () - started))
        }

    /// Cross-process physical serialization shared by local runtime writers.
    /// It protects bytes/snapshot boundaries
    /// only; it is not durable/domain state and contains no history meaning.
    let acquireStoreLock (commonDir: string) : Task<StoreFileGate> =
        task {
            let target = wanxiangDirectory commonDir
            ensureDirectory target
            let! release = acquireAvailableLock target
            return new StoreFileGate(release)
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

    let private segmentLaw: obj = constructRetention segmentsClass

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

    let private safeWriterId (writerId: string) =
        match unbox<string> (segmentLaw?writerIdFault writerId) with
        | null -> writerId
        | "required" -> invalidArg "writerId" "WriterId is required"
        | "pathSegment" -> invalidArg "writerId" "WriterId must be one path segment"
        | _ -> invalidArg "writerId" "WriterId must not end with a hyphen and four digits"

    let createWithSegmentCap (commonDir: string) (writerId: string) (segmentCap: int) : ProcessEventLog =
        if String.IsNullOrWhiteSpace commonDir then
            invalidArg "commonDir" "a local state root is required"

        if
            commonDir.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun part -> part.Equals(".git", StringComparison.OrdinalIgnoreCase))
        then
            invalidArg "commonDir" "runtime persistence cannot be placed inside .git"

        if segmentCap <= 0 then
            invalidArg "segmentCap" "segment cap must be positive"

        let writer = safeWriterId writerId

        { CommonDir = commonDir
          WriterId = writer
          SegmentCap = segmentCap
          CurrentFile = segmentFileOf writer 0 }

    let create (commonDir: string) (writerId: string) : ProcessEventLog =
        createWithSegmentCap commonDir writerId DefaultSegmentCapBytes

    let writerId (log: ProcessEventLog) = log.WriterId

    let private fileOf (log: ProcessEventLog) (fileName: string) =
        join2 (eventsDirectory log.CommonDir) fileName

    /// The file this writer currently appends to: a durable-events-005
    /// segment, or the legacy suffixless file while it awaits reorganization.
    let filePath (log: ProcessEventLog) = fileOf log log.CurrentFile

    let private tryAppendFault phase action =
        try
            Ok(action ())
        with cause ->
            Error { Phase = phase; Cause = cause }

    let private closeAfterBarrier fd barrier =
        let closed = tryAppendFault AppendPhase.DurabilityClose (fun () -> closeSync fd)

        match barrier, closed with
        | Ok(), Ok() -> Ok PhysicalAppendCompletion.AppendDurable
        | Error primary, Ok() -> Error(PhysicalAppendFailure.AfterAppend(primary, []))
        | Ok(), Error primary -> Error(PhysicalAppendFailure.AfterAppend(primary, []))
        | Error primary, Error cleanup -> Error(PhysicalAppendFailure.AfterAppend(primary, [ cleanup ]))

    let private appendDurabilityBarrier path =
        match tryAppendFault AppendPhase.DurabilityOpen (fun () -> openSync path "r+") with
        | Error primary -> Error(PhysicalAppendFailure.AfterAppend(primary, []))
        | Ok fd ->
            tryAppendFault AppendPhase.DurabilityBarrier (fun () -> fsyncSync fd)
            |> closeAfterBarrier fd

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

    let private fallbackTargetOf (commonDir: string) (writerId: string) : string =
        if existsSync (join2 (eventsDirectory commonDir) (legacyFileOf writerId)) then
            legacyFileOf writerId
        else
            segmentFileOf writerId 0

    let private highestSeriesFileOf (commonDir: string) (writerId: string) : string =
        match seriesFilesOf commonDir writerId |> List.rev with
        | latest :: _ -> latest
        | [] -> fallbackTargetOf commonDir writerId

    /// durable-events-005: resolve the file this append targets. The cached
    /// current file is probed with one stat; a vanished target
    /// (reorganization or import replaced the physical layout) re-enumerates
    /// the series. A legacy suffixless file is extended in place — append
    /// never reorganizes; materialization owns that transition.
    let private resolveAppendTarget (log: ProcessEventLog) : string =
        let cached = fileOf log log.CurrentFile

        if existsSync cached then
            cached
        else
            log.CurrentFile <- highestSeriesFileOf log.CommonDir log.WriterId
            fileOf log log.CurrentFile

    /// durable-events-005: roll to the next ordinal when the current segment
    /// is non-empty and would exceed the cap with this append. An empty
    /// segment always accepts the batch; a batch larger than the cap
    /// occupies its own segment; a legacy file is never rolled here.
    let private segmentSizeOf (path: string) : int =
        if existsSync path then statSync path |> statSize else 0

    let private nextOrdinalOf (currentFile: string) : int =
        let ordinal = ordinalOfSegmentFileName currentFile + 1

        if ordinal > 9999 then
            invalidArg "writerId" "segment ordinal exhausted the four-digit space"

        ordinal

    let private rollSegmentIfNeeded (log: ProcessEventLog) (textBytes: int) (path: string) : string =
        let size = segmentSizeOf path

        if size > 0 && size + textBytes > log.SegmentCap then
            log.CurrentFile <- segmentFileOf log.WriterId (nextOrdinalOf log.CurrentFile)
            fileOf log log.CurrentFile
        else
            path

    let private rollIfNeeded (log: ProcessEventLog) (textBytes: int) (path: string) : string =
        if log.CurrentFile = legacyFileOf log.WriterId then
            path
        else
            rollSegmentIfNeeded log textBytes path

    let private appendBytes text path =
        match tryAppendFault AppendPhase.PhysicalAppend (fun () -> AppendAllText path text "utf8") with
        | Error primary -> Error(PhysicalAppendFailure.AfterAppend(primary, []))
        | Ok() -> appendDurabilityBarrier path

    let private appendEncoded (log: ProcessEventLog) text =
        match
            tryAppendFault AppendPhase.BeforePhysicalAppend (fun () -> ensureDirectory (eventsDirectory log.CommonDir))
        with
        | Error primary -> Error(PhysicalAppendFailure.BeforeAppend primary)
        | Ok() ->
            let resolveTarget () =
                let target = resolveAppendTarget log
                rollIfNeeded log (byteLengthUtf8 text) target

            match tryAppendFault AppendPhase.BeforePhysicalAppend resolveTarget with
            | Error primary -> Error(PhysicalAppendFailure.BeforeAppend primary)
            | Ok path -> appendBytes text path

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

    /// durable-events-005/014: writer stream framing (non-empty text ends in LF, no
    /// empty interior line) and per-line canonical decode are wanxiangli/segments
    /// `decodeStream` (CUT-19-a); this keeps only the StorageInvalid projection.
    let decodeWriterText (label: string) (text: string) : Result<EventEnvelope list, StorageInvalid> =
        let outcome: obj = segmentLaw?decodeStream (label, text)

        if unbox<bool> outcome?ok then
            Ok(
                unbox<obj array> outcome?events
                |> Array.map CanonicalEventCodec.ofLibraryEvent
                |> Array.toList
            )
        else
            Error(CanonicalEventCodec.invalidOfLibraryError outcome?error)

    let decodeWriterBytes (label: string) (bytes: byte[]) : Result<EventEnvelope list, StorageInvalid> =
        CanonicalEventCodec.tryDecodeUtf8Text bytes
        |> Result.bind (decodeWriterText label)

    let private decodeFile (path: string) : Result<EventEnvelope list, StorageInvalid> =
        decodeWriterBytes path (readBytesFileSync path)

    type private WriterDecodeCacheEntry =
        { Dev: float
          Ino: float
          StatIdentity: string
          ByteLength: int
          Events: EventEnvelope list }

    /// durable-events-014/017: a process-local decoded-prefix cache. A writer file
    /// is append-only and owned by one process (durable-events-005), so a decode
    /// for an exact (dev, ino, length) is reused verbatim, and a same-inode
    /// extension decodes only the appended complete lines. The cache is never a
    /// second authority: every entry comes from the canonical decoder, and the
    /// Integrator still derives Current from the returned envelopes.
    // DSL-MUTABLE: resource — process-local decoded writer prefix keyed by path.
    let private writerDecodeCache =
        System.Collections.Generic.Dictionary<string, WriterDecodeCacheEntry>()

    let private pruneDecodeCache () =
        writerDecodeCache.Keys
        |> Seq.filter (fun path -> not (existsSync path))
        |> Seq.toList
        |> List.iter (fun path -> writerDecodeCache.Remove path |> ignore)

    let private storeDecodeCache path dev ino identity size events =
        writerDecodeCache.[path] <-
            { Dev = dev
              Ino = ino
              StatIdentity = identity
              ByteLength = size
              Events = events }

    let private lastIndexOfLf (buffer: byte[]) count =
        buffer
        |> Array.take count
        |> Array.tryFindIndexBack ((=) 10uy)
        |> Option.defaultValue -1

    let private readRequiredChunk fd bytes offset count position =
        let read = readSync fd bytes offset count position

        if read <= 0 then
            failwith "unexpected EOF while reading writer tail"
        else
            read

    let private readExactAt fd position length =
        let bytes = Array.zeroCreate<byte> length
        // DSL-MUTABLE: algorithm-scratch — bytes already filled by positional reads.
        let mutable filled = 0

        while filled < length do
            filled <- filled + readRequiredChunk fd bytes filled (length - filled) (position + filled)

        bytes

    type private TailLine = { Start: int; Text: string }

    type private ReverseBlock =
        | LocatedLineStart of int
        | ContinueBefore of int

    let private scanReverseBlock fd blockSize cursor =
        let start = max 0 (cursor - blockSize)
        let length = cursor - start

        let delimiter =
            readExactAt fd start length |> fun buffer -> lastIndexOfLf buffer length

        if delimiter >= 0 then
            ReverseBlock.LocatedLineStart(start + delimiter + 1)
        elif start = 0 then
            ReverseBlock.LocatedLineStart 0
        else
            ReverseBlock.ContinueBefore start

    let private locateLineStart fd lineEnd =
        let blockSize = 4096
        // DSL-MUTABLE: algorithm-scratch — reverse pread search carries only the next byte boundary.
        let mutable search = ReverseBlock.ContinueBefore lineEnd

        let isSearching () =
            match search with
            | ReverseBlock.ContinueBefore _ -> true
            | ReverseBlock.LocatedLineStart _ -> false

        let advance () =
            let cursor =
                match search with
                | ReverseBlock.ContinueBefore value -> value
                | ReverseBlock.LocatedLineStart value -> value

            search <- scanReverseBlock fd blockSize cursor

        while isSearching () do
            advance ()

        match search with
        | ReverseBlock.LocatedLineStart lineStart -> lineStart
        | ReverseBlock.ContinueBefore _ -> lineEnd

    let private readTailLine fd path lineEnd lineStart =
        let length = lineEnd - lineStart

        if length <= 0 then
            Error(sprintf "writer file contains empty final line: %s" path)
        else
            readExactAt fd lineStart length
            |> CanonicalEventCodec.tryDecodeUtf8Text
            |> Result.mapError (fun _ -> sprintf "writer file contains invalid UTF-8: %s" path)
            |> Result.map (fun text -> { Start = lineStart; Text = text })

    let private readCompleteLineBefore fd path lineEnd : Result<TailLine, string> =
        if lineEnd <= 0 then
            Error(sprintf "writer file contains empty final line: %s" path)
        else
            locateLineStart fd lineEnd |> readTailLine fd path lineEnd

    let private tryPhysical action onError =
        try
            action ()
        with ex ->
            onError ex

    let private withReadFd path onError action =
        let fd = openSync path "r"

        try
            tryPhysical (fun () -> action fd) onError
        finally
            closeSync fd

    /// durable-events-014/017: decode a writer through the process-local prefix
    /// cache. An exact (dev, ino, mode, size, mtime, ctime) hit reuses the decoded
    /// envelopes verbatim; a same-inode extension decodes only the appended
    /// complete lines; any other change (truncation, replacement, rewrite) falls
    /// back to the canonical full decode. The cache never becomes a second
    /// authority: entries only ever hold canonical decoder output, and a miss or
    /// changed stat always re-reads the file.
    let private decodeFileCached (path: string) : Result<EventEnvelope list, StorageInvalid> =
        let stat = statSync path
        let dev = statDev stat
        let ino = statIno stat
        let identity = statIdentity stat
        let size = statSize stat

        let fullDecode () =
            decodeFile path
            |> Result.map (fun events ->
                storeDecodeCache path dev ino identity size events
                events)

        let incrementalDecode (cached: WriterDecodeCacheEntry) =
            result {
                let! bytes =
                    withReadFd
                        path
                        (fun ex -> Error(StorageInvalid.NonCanonical(sprintf "writer read failed: %s" ex.Message)))
                        (fun fd -> Ok(readExactAt fd cached.ByteLength (size - cached.ByteLength)))

                let! text = CanonicalEventCodec.tryDecodeUtf8Text bytes
                let! appendedEvents = decodeWriterText path text
                let events = cached.Events @ appendedEvents
                storeDecodeCache path dev ino identity size events
                return events
            }

        match writerDecodeCache.TryGetValue path with
        | true, cached when cached.StatIdentity = identity -> Ok cached.Events
        | true, cached when cached.Dev = dev && cached.Ino = ino && cached.ByteLength < size -> incrementalDecode cached
        | _ -> fullDecode ()

    let private nonEmptyFileSize path =
        let exists = existsSync path
        let size = if exists then statSync path |> statSize else 0

        match exists, size with
        | false, _
        | true, 0 -> None
        | true, value -> Some value

    let private readLastLineFromFd path size fd =
        let trailing = readExactAt fd (size - 1) 1

        if trailing.[0] <> 10uy then
            Error(sprintf "writer file has incomplete trailing line: %s" path)
        else
            readCompleteLineBefore fd path (size - 1)
            |> Result.map (fun line -> Some line.Text)

    /// Exact O(last-line-bytes) NDJSON tail lookup. The scan searches raw LF bytes
    /// backwards with positional reads; UTF-8 boundaries do not matter because an
    /// unescaped LF cannot occur inside a valid JSON string.
    let readLastCompleteLine (path: string) : Result<string option, string> =
        nonEmptyFileSize path
        |> Option.map (fun size -> withReadFd path (fun ex -> Error ex.Message) (readLastLineFromFd path size))
        |> Option.defaultValue (Ok None)

    [<Emit("({ [Symbol.iterator]() { return { next: $0 } } })")>]
    let private iterableOfNext (next: unit -> obj) : obj = jsNative

    /// Where the reverse line reader stands: the open file and the LF that ends
    /// the next line to supply, or not yet inside a file.
    type private TailCursor =
        | BetweenFiles
        | InsideFile of fd: int * path: string * lineEnd: int

    /// durable-convergence-011: which tail line speaks for a writer's activity
    /// (a Journal `ObservedAt`, walking back past `ProjectionCutTail` lines,
    /// across segment seams; anything else means no durable activity) is
    /// wanxiangli/retention `lastActivity` (CUT-19). This log only supplies
    /// complete lines newest first, lazily, reading O(line bytes) per pull, and
    /// stops supplying where the physical read can no longer speak: an empty
    /// file, an incomplete trailing line, an empty or non-UTF-8 line, or a read
    /// failure. Only when no durable activity exists does the producer-side
    /// file mtime speak, and the newest file wins.
    let private writerActivityOf (paths: string list) : float =
        // DSL-MUTABLE: resource — read fds opened lazily for this one lookup; all closed below.
        let opened = ResizeArray<int>()
        // DSL-MUTABLE: algorithm-scratch — remaining files, newest first.
        let mutable files = List.rev paths
        // DSL-MUTABLE: algorithm-scratch — reverse line cursor.
        let mutable cursor = TailCursor.BetweenFiles
        // DSL-MUTABLE: algorithm-scratch — once the physical read stops speaking it never resumes.
        let mutable stopped = false

        let stop () =
            stopped <- true
            None

        let enterSizedFile path size =
            let fd = openSync path "r"
            opened.Add fd

            let endsWithLf =
                tryPhysical (fun () -> (readExactAt fd (size - 1) 1).[0] = 10uy) (fun _ -> false)

            if endsWithLf then
                cursor <- TailCursor.InsideFile(fd, path, size - 1)
                Some()
            else
                stop ()

        let enterFile path =
            match nonEmptyFileSize path with
            | None -> stop ()
            | Some size -> enterSizedFile path size

        let enterNextFile () =
            match files with
            | [] -> stop ()
            | path :: rest ->
                files <- rest
                enterFile path

        let cursorBefore fd path (lineStart: int) =
            if lineStart > 0 then
                TailCursor.InsideFile(fd, path, lineStart - 1)
            else
                TailCursor.BetweenFiles

        let readLineBefore fd path lineEnd : string option =
            match tryPhysical (fun () -> readCompleteLineBefore fd path lineEnd) (fun ex -> Error ex.Message) with
            | Error _ -> stop ()
            | Ok line ->
                cursor <- cursorBefore fd path line.Start
                Some line.Text

        let rec pull () : string option =
            match stopped, cursor with
            | true, _ -> None
            | false, TailCursor.BetweenFiles -> enterNextFile () |> Option.bind pull
            | false, TailCursor.InsideFile(fd, path, lineEnd) -> readLineBefore fd path lineEnd

        let next () : obj =
            match pull () with
            | Some line -> createObj [ "done" ==> false; "value" ==> line ]
            | None -> createObj [ "done" ==> true ]

        let verdict: obj =
            try
                retention?lastActivity (iterableOfNext next)
            finally
                opened |> Seq.iter (fun fd -> closeSync fd)

        if isNull verdict then
            paths |> List.map (fun path -> statSync path |> fileActivityMs) |> List.max
        else
            unbox<float> verdict

    /// durable-events-005/014: one writer's physical files grouped by
    /// WriterId (wanxiangli/segments `groupWriterFiles`). A tail `-dddd` name
    /// joins its prefix's segment series only when the ordinals are
    /// contiguous from 0000; otherwise the file is a legacy suffixless single
    /// file under its full stem. A legacy file and its segment series may
    /// coexist only as an interrupted reorganization or import intermediate.
    type private WriterFileGroup =
        { WriterId: string
          FileNames: string list }

    let private writerFileGroups (commonDir: string) : WriterFileGroup list =
        let directory = eventsDirectory commonDir

        if not (existsSync directory) then
            []
        else
            unbox<obj array> (segmentLaw?groupWriterFiles (readdirSync directory))
            |> Array.toList
            |> List.map (fun group ->
                { WriterId = unbox<string> group?writerId
                  FileNames = unbox<string array> group?fileNames |> Array.toList })

    let writerPhysicalMetadata (commonDir: string) : WriterPhysicalMetadata list =
        writerFileGroups commonDir
        |> List.map (fun group ->
            let paths =
                group.FileNames |> List.map (fun name -> join2 (eventsDirectory commonDir) name)

            { WriterId = group.WriterId
              FileNames = group.FileNames
              LastActivityMs = writerActivityOf paths })

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

    /// durable-events-005: the authoritative file list of one writer's
    /// logical stream. A coexisting legacy file and segment series is only a
    /// legal intermediate of an interrupted reorganization, import, or
    /// version transition: a byte-prefix relation keeps the longer form, and
    /// the line-continuation form reads as the series followed by the legacy
    /// lines. The read layer never touches disk here; materialization owns
    /// the physical convergence. Anything else is physical corruption.
    let private coexistingFormsOf
        (directory: string)
        (writerId: string)
        (legacyFile: string)
        (segments: string list)
        : Result<(string * int) list, string> =
        let legacyBytes = readBytesFileSync (join2 directory legacyFile)

        let segmentBytes =
            segments
            |> List.map (fun name -> readBytesFileSync (join2 directory name))
            |> Array.concat

        match coexistenceOf legacyBytes segmentBytes with
        | "series", _ -> Ok(segments |> List.map (fun name -> name, 0))
        | "legacy", _ -> Ok [ legacyFile, 0 ]
        | "continuation", copied -> Ok((segments |> List.map (fun name -> name, 0)) @ [ legacyFile, copied ])
        | _ -> Error(sprintf "writer files diverged: %s" writerId)

    let private logicalStreamFiles (commonDir: string) (group: WriterFileGroup) : Result<(string * int) list, string> =
        let layout: obj =
            segmentLaw?streamLayout (
                createObj [ "writerId" ==> group.WriterId; "fileNames" ==> List.toArray group.FileNames ]
            )

        let segmentNames () =
            unbox<string array> layout?segments |> Array.toList

        match unbox<string> layout?kind with
        | "segments" -> Ok(segmentNames () |> List.map (fun name -> name, 0))
        | "legacy" -> Ok [ unbox<string> layout?legacy, 0 ]
        | "coexisting" ->
            coexistingFormsOf (eventsDirectory commonDir) group.WriterId (unbox<string> layout?legacy) (segmentNames ())
        | _ -> Error(sprintf "multiple legacy files for one writer: %s" group.WriterId)

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

    let private decodeWriterFiles
        (commonDir: string)
        (group: WriterFileGroup)
        : Result<EventEnvelope list, StorageInvalid> =
        result {
            let! files =
                logicalStreamFiles commonDir group
                |> Result.mapError StorageInvalid.NonCanonical

            let! decoded =
                files
                |> List.traverseResultM (fun (name, copied) ->
                    decodeFileCached (join2 (eventsDirectory commonDir) name)
                    |> Result.map (List.skip copied))

            return List.concat decoded
        }

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
        pruneDecodeCache ()

        let rec read remaining acc =
            result {
                match remaining with
                | [] -> return List.rev acc
                | group :: tail ->
                    let! events = decodeWriterFiles commonDir group
                    return! read tail ((group.WriterId, events) :: acc)
            }

        writerPhysicalMetadata commonDir
        |> List.filter (fun writer -> isWriterActiveAt nowMs writer.LastActivityMs)
        |> List.map (fun writer ->
            { WriterId = writer.WriterId
              FileNames = writer.FileNames })
        |> fun groups -> read groups []

    let readStreams (commonDir: string) : Result<(string * EventEnvelope list) list, StorageInvalid> =
        readStreamsAt commonDir (currentTimeMs ())

    /// Public content digest for inline payload staging (durable-events-012).
    /// The digest IS the PayloadRef content address.
    let payloadDigest (content: byte[]) : string =
        createHash "sha256" |> fun hash -> hashUpdate hash content |> hashHex
