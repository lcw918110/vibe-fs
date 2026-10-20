namespace Wanxiangshu.Context.Companion

open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation.Identity

/// Companion state wrapper for the physical caches used at Host boundaries.
type Companion(?initialMemory: CompanionMemory, ?durable: ICompanionDurablePort, ?sessionId: SessionId) =
    let lockObj = obj ()

    let restoredMemory = initialMemory

    // DSL-MUTABLE: resource — in-memory X-trace projection mirror (durable-backed)
    let mutable xTraceProjection: XTraceProjectionState =
        restoredMemory
        |> Option.map (fun m -> m.XTrace)
        |> Option.defaultValue XTraceProjection.empty

    member _.Memory: CompanionMemory =
        lock lockObj (fun () -> { XTrace = xTraceProjection })

    /// COMPANION-007: refresh the in-memory XTrace mirror after the transform
    /// boundary captured new parts. Without this the chunker keeps mapping the
    /// ingest cursor against the trace captured at construction, which is empty
    /// at first and re-reads the projection head every round.
    member _.RefreshXTrace(state: XTraceProjectionState) : unit =
        lock lockObj (fun () -> xTraceProjection <- state)
