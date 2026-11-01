namespace Wanxiangshu.Persistence.EventStore

open System.Threading.Tasks

[<RequireQualifiedAccess>]
module RetentionSurface =
    val retainedWriterIdsAt: stateRoot: string -> nowMs: float -> string[]
    val reorganizeWithSegmentCap: stateRoot: string -> capBytes: int -> Task<obj>
