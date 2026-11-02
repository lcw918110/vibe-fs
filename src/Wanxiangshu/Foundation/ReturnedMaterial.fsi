namespace Wanxiangshu.Foundation

/// 适配库的材料存储协议，并组合正文与材料的呈现。
[<RequireQualifiedAccess>]
module ReturnedMaterial =
    val encode: body: string list -> carry: string list -> string
    val body: encoded: string -> string list
    val carry: encoded: string -> string list
    val fullText: encoded: string -> string
    val validate: encoded: string -> Result<unit, string>
    val entries: encoded: string -> obj array
    val carryData: encoded: string -> LlmFacing.DataBlock list
    val render: encoded: string -> string
    val renderBody: encoded: string -> string
