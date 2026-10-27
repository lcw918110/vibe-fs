namespace Wanxiangshu.Context.Scope

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.HostSession

/// Snapshot codec for delegated post-scope views. The planner owns every keep/drop decision.
module SnapshotScope =

    [<Emit("$0 == null ? undefined : $0[$1]")>]
    let private field (value: obj) (_name: string) : obj = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (_value: obj) : bool = jsNative

    let private text (value: obj) =
        if isNull value then "" else string value

    let private parts (message: obj) : obj array =
        let value = field message "parts"
        if isArray value then unbox<obj array> value else [||]

    let private neutralPart (item: obj) =
        match text (field item "type") with
        | "toolCall"
        | "tool-call"
        | "tool_call"
        | "tool" ->
            let name = field item "name"
            let tool = if isNull name then field item "tool" else name
            let id = field item "id"
            createObj [ "id" ==> id; "tool" ==> tool ]
        | "toolResult"
        | "tool-result"
        | "tool_result" -> createObj [ "resultFor" ==> field item "id" ]
        | kind -> createObj [ "type" ==> kind ]

    let private resultPartId (part: obj) =
        match text (field part "type") with
        | "toolResult"
        | "tool-result"
        | "tool_result" ->
            let value = field part "id"
            if isNull value then None else Some(text value)
        | _ -> None

    let private neutralResultId (message: obj) =
        let direct = field message "toolCallId"

        if not (isNull direct) then
            text direct
        else
            parts message |> Array.tryPick resultPartId |> Option.defaultValue ""

    let private neutralMessage (message: obj) =
        let role = text (field message "role")

        let kind =
            match role with
            | "user" -> "user"
            | "assistant" -> "assistant"
            | "tool"
            | "toolResult"
            | "tool-result"
            | "tool_result" -> "result"
            | "custom" -> "custom"
            | _ -> "other"

        let isOperator =
            kind = "user"
            || (kind = "custom" && text (field message "attribution") = "user")

        createObj
            [ "kind" ==> kind
              "operator" ==> isOperator
              "sideRequest" ==> false
              "key" ==> ""
              "timestamp" ==> null
              "parts" ==> (parts message |> Array.map neutralPart)
              "resultFor" ==> neutralResultId message
              "resultField" ==> "" ]

    [<Emit("({...$0})")>]
    let private copy (_value: obj) : obj = jsNative

    let private keptMessage (original: obj array) (slot: obj) =
        let index = unbox<int> (field slot "index")
        let message = original.[index]
        let selection = field slot "parts"

        if isNull selection then
            message
        else
            let source = parts message
            let indices = unbox<int array> selection
            let clone = copy message
            clone?parts <- indices |> Array.map (fun i -> source.[i])
            emitJsExpr clone "delete $0.providerPayload; delete $0.responseId" |> ignore
            clone

    let filterSnapshot (messages: SessionMessage list) : obj array =
        let source = SessionView.ofSnapshotMessages messages
        let neutral = source |> Array.map neutralMessage
        Surface.planFilter (box neutral) |> Array.map (keptMessage source)
