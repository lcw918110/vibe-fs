namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop

/// The one Host-object mutation the Companion transform performs.
///
/// In-place, and that is not a style preference. `plugin.trigger` discards the hook's
/// return value (`plugin/index.ts:284-293`) and the Host then reads its original
/// `msgs` binding (`prompt.ts:1262`), so `output.messages = rewritten` is silently
/// ignored: the provider receives the untouched transcript while every assertion
/// passes. Confirmed against Host source (`plugin/index.ts` discard + `prompt.ts`
/// original `msgs` binding).
///
/// HOST-016: 对 provider-facing 消息做非空 content 兜底保障，避免仅 reasoning
/// 或空 content 在上游 API 报 messages[i].content cannot be empty 400 错误；
/// 并在连续 user 消息之间插入 assistant "." 占位消息，避免上游报角色非交替错误。
module HostMessageProjection =

    let private readField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let replaceMessagesInPlace (rawOutObj: obj) (transformed: obj list) =
        emitJsExpr (rawOutObj?messages, List.toArray transformed) "$0.length = 0; $0.push(...$1);"
        |> ignore

    let private readRole (raw: obj) : string =
        if isNull raw then
            ""
        else
            let info = readField raw "info"
            let fromInfo = if isNull info then null else readField info "role"
            let chosen = if isNull fromInfo then readField raw "role" else fromInfo
            if isNull chosen then "" else unbox<string> chosen

    let private readParts (raw: obj) : obj array =
        let parts = readField raw "parts"

        if isNull parts then
            [||]
        elif emitJsExpr parts "Array.isArray($0)" then
            unbox<obj array> parts
        else
            [||]

    let private partKind (p: obj) : string =
        let typeVal = if isNull p then null else readField p "type"

        if isNull typeVal then
            ""
        else
            (unbox<string> typeVal).ToLowerInvariant()

    let private isNonEmptyTextPart (p: obj) : bool =
        let textVal = if isNull p then null else readField p "text"

        partKind p = "text"
        && not (isNull textVal)
        && not (String.IsNullOrWhiteSpace(unbox<string> textVal))

    let private hasTextFromParts (rawParts: obj array) : bool =
        rawParts |> Array.exists isNonEmptyTextPart

    let private hasTextFromTopLevel (raw: obj) : bool =
        let contentVal = readField raw "content"

        let fromContent =
            not (isNull contentVal)
            && emitJsExpr contentVal "typeof $0 === 'string'"
            && not (String.IsNullOrWhiteSpace(unbox<string> contentVal))

        let textVal = readField raw "text"

        let fromText =
            not (isNull textVal) && not (String.IsNullOrWhiteSpace(unbox<string> textVal))

        fromContent || fromText

    let private hasNonEmptyText (raw: obj) (rawParts: obj array) : bool =
        hasTextFromParts rawParts || hasTextFromTopLevel raw

    let private isToolPart (p: obj) : bool =
        let kind = partKind p

        kind = "tool"
        || kind = "tool-call"
        || kind = "tool_call"
        || kind = "tool-result"
        || kind = "tool_result"

    let private hasToolFromParts (rawParts: obj array) : bool = rawParts |> Array.exists isToolPart

    let private hasToolFromTopLevel (raw: obj) : bool =
        let toolCallsVal = readField raw "tool_calls"

        not (isNull toolCallsVal)
        && emitJsExpr toolCallsVal "Array.isArray($0) && $0.length > 0"

    let private hasTool (raw: obj) (rawParts: obj array) : bool =
        hasToolFromParts rawParts || hasToolFromTopLevel raw

    let private tryNonEmptyString (value: obj) : string option =
        if isNull value then
            None
        elif String.IsNullOrWhiteSpace(unbox<string> value) then
            None
        else
            Some(unbox<string> value)

    let private reasoningFromPart (p: obj) : string option =
        let kind = partKind p

        if kind <> "reasoning" && kind <> "thinking" then
            None
        else
            [ readField p "text"; readField p "reasoning"; readField p "thinking" ]
            |> List.tryPick tryNonEmptyString

    let private fallbackText (role: string) (reasoningText: string option) : string =
        match reasoningText with
        | Some _ -> "."
        | None -> if role.ToLowerInvariant() = "assistant" then "..." else "#"

    let private withFallbackTextPart (raw: obj) (rawParts: obj array) (text: string) : obj =
        let textPart = createObj [ "type", box "text"; "text", box text ]
        let newParts = Array.append rawParts [| textPart |]
        let cloned = emitJsExpr raw "Object.assign({}, $0)"
        emitJsExpr (cloned, newParts) "$0.parts = $1" |> ignore
        cloned

    let private sanitizeNonNull (raw: obj) : obj =
        let rawParts = readParts raw

        if hasNonEmptyText raw rawParts || hasTool raw rawParts then
            raw
        else
            let role = readRole raw
            let reasoningText = rawParts |> Array.tryPick reasoningFromPart
            withFallbackTextPart raw rawParts (fallbackText role reasoningText)

    let private createAssistantDotMessage () : obj =
        createObj
            [ "info", box (createObj [ "role", box "assistant" ])
              "role", box "assistant"
              "parts", box [| createObj [ "type", box "text"; "text", box "." ] |] ]

    let private isUserRole (raw: obj) : bool =
        if isNull raw then
            false
        else
            (readRole raw).ToLowerInvariant() = "user"

    let rec private intersperseAssistantBetweenConsecutiveUsers (messages: obj list) : obj list =
        match messages with
        | [] -> []
        | [ single ] -> [ single ]
        | first :: (second :: _ as rest) when isUserRole first && isUserRole second ->
            first
            :: createAssistantDotMessage ()
            :: intersperseAssistantBetweenConsecutiveUsers rest
        | first :: rest -> first :: intersperseAssistantBetweenConsecutiveUsers rest

    let sanitizeMessage (raw: obj) : obj =
        if isNull raw then raw else sanitizeNonNull raw

    let sanitizeMessages (messages: obj list) : obj list =
        messages
        |> List.map sanitizeMessage
        |> intersperseAssistantBetweenConsecutiveUsers

    let sanitizeOutputMessages (outObj: obj) : unit =
        let currentMessages = unbox<obj array> outObj?messages |> Array.toList
        let sanitized = sanitizeMessages currentMessages
        replaceMessagesInPlace outObj sanitized
