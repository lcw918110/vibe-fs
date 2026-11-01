namespace Wanxiangshu.Persistence.EventStore

open System.Threading.Tasks

[<Sealed>]
type ProcessEventLog

[<Class>]
type StoreFileGate =
    member Release: unit -> Task<unit>

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

    val processAwareFs: obj
    val acquireStoreLock: commonDir: string -> Task<StoreFileGate>
    val withStoreLock<'T> : commonDir: string -> work: (unit -> Task<'T>) -> Task<'T>
    val create: commonDir: string -> writerId: string -> ProcessEventLog

    /// durable-events-005: create with an injected single-segment byte cap;
    /// `create` uses the 64 MiB default.
    val createWithSegmentCap: commonDir: string -> writerId: string -> segmentCap: int -> ProcessEventLog

    val writerId: log: ProcessEventLog -> string
    val filePath: log: ProcessEventLog -> string

    val append:
        log: ProcessEventLog -> events: EventEnvelope list -> Result<PhysicalAppendCompletion, PhysicalAppendFailure>

    val readLastCompleteLine: path: string -> Result<string option, string>
    val writerPhysicalMetadata: commonDir: string -> WriterPhysicalMetadata list

    /// durable-events-006/013/017: deterministic inventory of every writer file
    /// (name + canonical file identity) without reading historical content.
    val physicalInventory: commonDir: string -> string

    val internal withSegmentCap: cap: int -> work: (unit -> 'T) -> 'T
    val internal withSegmentCapTask: cap: int -> work: (unit -> Task<'T>) -> Task<'T>
    val internal convergeWriterFiles: commonDir: string -> writerId: string -> Result<unit, string>

    [<Literal>]
    val DefaultSegmentCapBytes: int = 67108864

    val readStreamsAt: commonDir: string -> nowMs: float -> Result<(string * EventEnvelope list) list, StorageInvalid>
    val readStreams: commonDir: string -> Result<(string * EventEnvelope list) list, StorageInvalid>
    val payloadDigest: content: byte[] -> string
