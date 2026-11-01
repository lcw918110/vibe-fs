namespace Wanxiangshu.Participant.Provider.Attempt

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module TerminalValidity =
    let private toolMarkup =
        Regex(
            "<(?:/?\\s*(?:tool_call|use_tool|call|function_call|invoke)\\b[^>]*>?)|</(?:tool_call|use_tool|call|function_call|invoke)\\s*>",
            RegexOptions.Compiled ||| RegexOptions.IgnoreCase
        )

    [<RequireQualifiedAccess>]
    type Rejection =
        | Empty
        | XmlOnly

    let describe (rejection: Rejection) =
        match rejection with
        | Rejection.Empty -> "empty terminal"
        | Rejection.XmlOnly -> "XML-only terminal"

    let check (text: string) : Result<unit, Rejection> =
        if String.IsNullOrWhiteSpace text then Error Rejection.Empty
        elif toolMarkup.IsMatch text then Error Rejection.XmlOnly
        else Ok()

    let isValid (text: string) : bool =
        check text |> Result.isOk
