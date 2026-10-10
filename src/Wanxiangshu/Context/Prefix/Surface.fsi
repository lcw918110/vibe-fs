namespace Wanxiangshu.Context.Prefix

[<RequireQualifiedAccess>]
module PrefixSurface =
    val empty: obj
    val snapshot: value: obj -> obj
    val requestKindLabels: string array
    val requestKindMayCarryProbe: kind: string -> bool
    val requestKindLabel: kind: string -> string
    val requestKind: obj
    val select: value: obj -> obj
    val applyRebase: request: obj -> state: obj -> obj
    val applyReanchor: request: obj -> state: obj -> obj
    val applyTenureReanchor: request: obj -> state: obj -> obj
    val epochOf: state: obj -> int64
    val hasSnapshot: state: obj -> bool
    val reanchoredRuns: state: obj -> string array
    val reanchoredTenures: state: obj -> string array
    val isTenureReanchored: incumbencyId: string -> state: obj -> bool
    val isReanchored: run: string -> state: obj -> bool
    val forSnapshot: snapshot: obj -> memoryPreamble: string -> memoryBody: string -> obj
    val forChoice: choice: obj -> committed: obj -> memoryPreamble: string -> memoryBody: string -> obj
    val requiredBlob: choice: obj -> committed: obj -> obj

    val desiredCutoff: phaseTurnStarts: int array -> obj
    val appendCheckpoint: callId: string -> window: obj array -> obj array
    val desiredCutoffOfWindow: window: obj array -> turnByCallId: obj array -> obj
    val pruneCheckpointWindow: window: obj array -> turnByCallId: obj array -> cutoffExclusive: int -> obj array
