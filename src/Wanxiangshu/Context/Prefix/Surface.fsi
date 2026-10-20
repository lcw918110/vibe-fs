namespace Wanxiangshu.Context.Prefix

[<RequireQualifiedAccess>]
module PrefixSurface =
    val empty: obj
    val requestKindLabels: string array
    val requestKindLabel: kind: string -> string
    val requestKind: obj
    val applyReanchor: request: obj -> state: obj -> obj
    val applyTenureReanchor: request: obj -> state: obj -> obj
    val epochOf: state: obj -> int64
    val reanchoredRuns: state: obj -> string array
    val reanchoredTenures: state: obj -> string array
    val isTenureReanchored: incumbencyId: string -> state: obj -> bool
    val isReanchored: run: string -> state: obj -> bool
    val appendCheckpoint: callId: string -> window: obj array -> obj array
