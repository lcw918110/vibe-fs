namespace Wanxiangshu.Interaction.Attention

[<RequireQualifiedAccess>]
module AttentionSurface =
    val internal projection: state: obj -> AttentionProjectionState
    val empty: unit -> obj
    val record: session: string -> occurrence: string -> text: string -> state: obj -> obj

    val consume: session: string -> workIds: string array -> state: obj -> obj

    /// ATTENTION-004: close one life, leaving its remaining work as
    /// consumption receipts so replay cannot resurrect it.
    val closeLife: session: string -> state: obj -> obj

    val pending: session: string -> state: obj -> obj
