namespace Wanxiangshu.Participant.Provider.Attempt

open Fable.Core

/// 保留主体的结果类型与诊断；词法观察由 response-text 提供。
[<RequireQualifiedAccess>]
module TerminalValidity =
    [<StringEnum; RequireQualifiedAccess>]
    type private TextShape =
        | [<CompiledName("Empty")>] Empty
        | [<CompiledName("ToolMarkup")>] ToolMarkup
        | [<CompiledName("Text")>] Text

    [<Import("classify", "wanxiangli/response-text")>]
    let private classify (text: string) : TextShape = jsNative

    [<RequireQualifiedAccess>]
    type Rejection =
        | Empty
        | XmlOnly

    let describe (rejection: Rejection) =
        match rejection with
        | Rejection.Empty -> "empty terminal"
        | Rejection.XmlOnly -> "XML-only terminal"

    let check (text: string) : Result<unit, Rejection> =
        match classify text with
        | TextShape.Empty -> Error Rejection.Empty
        | TextShape.ToolMarkup -> Error Rejection.XmlOnly
        | TextShape.Text -> Ok()

    let isValid (text: string) : bool =
        check text |> Result.isOk
