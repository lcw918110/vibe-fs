namespace Wanxiangshu.Foundation

open Fable.Core

/// 存储表示由库拥有；这里适配 F# 类型并组合文本呈现。
[<RequireQualifiedAccess>]
module ReturnedMaterial =
    [<Import("encode", "wanxiangli/returned-material")>]
    let private encodeLibrary (body: string array) (carry: string array) : string = jsNative

    [<Import("body", "wanxiangli/returned-material")>]
    let private bodyLibrary (encoded: string) : string array = jsNative

    [<Import("carry", "wanxiangli/returned-material")>]
    let private carryLibrary (encoded: string) : string array = jsNative

    [<Import("validationError", "wanxiangli/returned-material")>]
    let private validationError (encoded: string) : string = jsNative

    [<Import("entries", "wanxiangli/returned-material")>]
    let private entriesLibrary (encoded: string) : obj array = jsNative

    [<Import("fullText", "wanxiangli/returned-material")>]
    let private fullTextLibrary (encoded: string) : string = jsNative

    let encode body carry =
        encodeLibrary (List.toArray body) (List.toArray carry)

    let body encoded = bodyLibrary encoded |> Array.toList
    let carry encoded = carryLibrary encoded |> Array.toList
    let entries encoded = entriesLibrary encoded
    let fullText encoded = fullTextLibrary encoded

    let validate encoded =
        match validationError encoded with
        | null -> Ok()
        | error -> Error error

    let carryData encoded =
        carry encoded |> LlmFacing.Data.textTables "returned"

    let render encoded =
        LlmFacing.instructions (body encoded)
        |> LlmFacing.withData (carryData encoded)
        |> LlmFacing.render

    let renderBody encoded =
        LlmFacing.instructions (body encoded) |> LlmFacing.render
