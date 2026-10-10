namespace Wanxiangshu.Persistence.EventStore

open System.Threading.Tasks

[<RequireQualifiedAccess>]
module WriterStreamSync =
    type RemoteWriter =
        { WriterId: string
          Text: string
          LastActivityMs: float option }

    val retentionMilliseconds: unit -> float
    val isWriterActiveAt: nowMs: float -> lastActivityMs: float -> bool
    val tryCachedLocalSnapshot: commonDir: string -> StoreSnapshot option
    val materializeLocalAt: raw: IGitRawStore -> commonDir: string -> nowMs: float -> Task<StoreSnapshot>
    val materializeLocal: raw: IGitRawStore -> commonDir: string -> Task<StoreSnapshot>

    val syncWriterStreamsAt:
        raw: IGitRawStore ->
        commonDir: string ->
        remote: StoreSnapshot option ->
        nowMs: float ->
            Task<Result<StoreSnapshot, ConvergeError>>

    val syncWriterStreams:
        raw: IGitRawStore ->
        commonDir: string ->
        remote: StoreSnapshot option ->
            Task<Result<StoreSnapshot, ConvergeError>>

    val readRemoteStreamsAt:
        raw: IGitRawStore ->
        commonDir: string ->
        nowMs: float ->
        snapshot: StoreSnapshot ->
            Task<Result<RemoteWriter list, ConvergeError>>

    val tryCachedMergedAt: commonDir: string -> nowMs: float -> remote: StoreSnapshot -> StoreSnapshot option

    val syncWithoutRemoteUnderLock:
        raw: IGitRawStore -> commonDir: string -> nowMs: float -> Task<Result<StoreSnapshot, ConvergeError>>

    val mergeRemoteStreamsUnderLock:
        raw: IGitRawStore ->
        commonDir: string ->
        nowMs: float ->
        writers: RemoteWriter list ->
            Task<Result<StoreSnapshot, ConvergeError>>
