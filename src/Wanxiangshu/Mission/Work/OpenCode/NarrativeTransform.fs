namespace Wanxiangshu.Mission.Work.OpenCode

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Mission.Work
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Mission.Planning
open Wanxiangshu.Persistence.Journal

module RelayNarrativeTransform =
    let private messageId message =
        ProviderWireDecode.hostMessageId message

    let private readField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    /// The Host's message role, passed to the projection library verbatim.
    let private messageRole message : obj =
        let info = readField (readField message "info") "role"

        if isNull info then readField message "role" else info

    /// Every cut candidate this message carries.
    let private toolCallIds message : string list =
        ProviderWireDecode.rawPartsOf message
        |> List.choose ProviderWireDecode.decodePart
        |> List.choose (function
            | WireToolCall(toolCallId, _, _) -> Some(ToolCallId.value toolCallId)
            | WireToolResult(toolCallId, _) -> Some(ToolCallId.value toolCallId)
            | _ -> None)

    let private facts (messages: obj list) : RelayMessageFact list =
        messages
        |> List.map (fun message ->
            { Id = messageId message
              ToolCallIds = toolCallIds message })

    /// 退休 attempt 判定的唯一实现：wanxiangli/projection `retired`（CUT-17）。
    [<AllowNullLiteral>]
    type private RetiredRule =
        abstract retired: request: obj -> bool

    [<Import("Projection", "wanxiangli/projection")>]
    let private createProjection: unit -> RetiredRule = jsNative

    let private projection = createProjection ()

    [<AllowNullLiteral>]
    type private TenureRule =
        abstract tenure: messages: obj * barrier: obj -> obj

    [<Import("Projection", "wanxiangli/projection")>]
    let private createTenure: unit -> TenureRule = jsNative

    let private tenureRule = createTenure ()

    /// The library request. The neutral core decided which message ids, cuts and
    /// accepted roots the rule sees; this only adds the pass-through role.
    let private libraryRequest (roles: obj list) (request: RelayRetirementRequest) : obj =
        let messages =
            List.map2
                (fun role (message: RelayMessagePolicy) ->
                    createObj
                        [ "role" ==> role
                          "id" ==> Option.toObj message.Id
                          "cut" ==> message.Cut
                          "accepted" ==> Option.toObj message.Accepted ])
                roles
                request.Messages
            |> List.toArray

        createObj
            [ "relayed" ==> true
              "acceptedRequest" ==> request.AcceptedRequest
              "activeRoot" ==> Option.toObj request.ActiveRoot
              "gatePhysical" ==> Option.toObj request.GatePhysical
              "messages" ==> messages ]

    let private staleRetirement
        (journal: AgentJournal)
        (sessionId: SessionId)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork)
        (acceptedRequest: bool)
        (roles: obj list)
        (messages: obj list)
        =
        RelayProjection.staleRequest journal sessionId work acceptedRequest (facts messages)
        |> Option.filter (fun request -> projection.retired (libraryRequest roles request))

    let isStaleRetiredAttempt
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (sessionId: string option)
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        (outObj: obj)
        : bool =
        match journal, sessionId, lifecycle with
        | Some durable, Some value, Some work when not (String.IsNullOrWhiteSpace value) ->
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj

            staleRetirement
                durable
                (SessionId.create value)
                work
                acceptedRequest
                (messages |> List.map messageRole)
                messages
            |> Option.isSome
        | _ -> false

    /// 条款 4：本次请求不再放行时清空消息——请求因此没有内容，LLM 看不到
    /// deliver 回复。退休 attempt 与交付裁决共用这一个动作。
    let clearRequestMessages (outObj: obj) =
        HostMessageProjection.replaceMessagesInPlace outObj []

    /// S1/S2/S3/S4 relay no longer clears context. Opening cursor records the
    /// tenure boundary; it does not strip predecessor assistant or tool
    /// messages. Retention and truncation belong to the view declaration.
    let private project
        journal
        (interruptAttempt: SessionId -> Task<unit>)
        sessionId
        acceptedRequest
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        outObj
        =
        task {
            let messages = ProviderWireDecode.messagesFromTransformOutput outObj

            // An active LogicalRun or a claimed loop gate does not identify this
            // physical request: both can coexist with the retired attempt.
            let stale =
                lifecycle
                |> Option.bind (fun work ->
                    staleRetirement journal sessionId work acceptedRequest (messages |> List.map messageRole) messages)

            match stale with
            | Some _ ->
                do! interruptAttempt sessionId
                clearRequestMessages outObj
                return RelayProjectionDisposition.RetiredAttemptStopped
            | None -> return RelayProjection.disposition lifecycle
        }

    let private stringField (value: obj) (name: string) =
        let raw = readField value name

        if isNull raw || not (emitJsExpr raw "typeof $0 === 'string'") then
            None
        else
            let text = unbox<string> raw
            if String.IsNullOrEmpty text then None else Some text

    let private roleText message =
        let raw = messageRole message
        if isNull raw then "" else string raw

    let private contentParts (message: obj) =
        let content = readField message "content"

        if isNull content || not (emitJsExpr content "Array.isArray($0)") then
            []
        else
            unbox<obj array> content |> Array.toList

    let private explicitCall (part: obj) =
        match stringField part "type" |> Option.map (fun kind -> kind.ToLowerInvariant()) with
        | Some ("toolcall" | "tool-call" | "tool_call") ->
            stringField part "id" |> Option.orElse (stringField part "callId")
        | _ -> None

    let private tenureMessage (message: obj) =
        let parts = ProviderWireDecode.rawPartsOf message @ contentParts message
        let decoded = parts |> List.choose ProviderWireDecode.decodePart

        let call =
            decoded
            |> List.tryPick (function
                | WireToolCall(id, _, _) -> Some(ToolCallId.value id)
                | _ -> None)
            |> Option.orElse (parts |> List.tryPick explicitCall)

        let answered =
            decoded
            |> List.tryPick (function
                | WireToolResult(id, _) -> Some(ToolCallId.value id)
                | _ -> None)
            |> Option.orElse (
                match roleText message with
                | "toolResult"
                | "tool" -> stringField message "toolCallId"
                | _ -> None)

        let tenureId = stringField message "tenureId"
        let identified = ProviderWireDecode.hostMessageId message

        createObj
            [ "id", (match identified with Some id -> box id | None -> null)
              "role", box (roleText message)
              "synthetic", box (emitJsExpr message " $0.synthetic === true")
              "semantic", box true
              "cursor", null
              "toolCallId", (match call with Some id -> box id | None -> null)
              "resultFor", (match answered with Some id -> box id | None -> null)
              "tenureId", (match tenureId with Some id -> box id | None -> null) ]

    /// Keep the successor's own messages after the committed review barrier.
    /// Returns None when this work has no barrier for the active tenure, so the
    /// caller leaves the request array alone. The returned messages are the
    /// original objects.
    let cutVisible
        (messages: obj array)
        (work: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        : obj array option =
        match work with
        | None -> None
        | Some inspected ->
            match inspected.BarrierToolCallId, inspected.SuccessorTenureId, inspected.ActiveId with
            | Some callId, Some successor, Some active when active = successor ->
                let neutral = messages |> Array.map tenureMessage
                let barrier =
                    createObj [ "toolCallId", box callId; "successorTenureId", box successor ]

                let kept = unbox<obj array> (tenureRule.tenure (box neutral, barrier))
                let indexes = kept |> Array.map (fun item -> int (unbox<float> item))
                let span = [| 0 .. messages.Length - 1 |]

                if indexes.Length = span.Length && Array.forall2 (fun index value -> index = value) indexes span then
                    None
                else
                    Some(indexes |> Array.map (fun index -> messages.[index]))
            | _ -> None

    /// Read the committed barrier and return the messages the model may see.
    /// The caller's array is not modified. No barrier for the active tenure
    /// returns the same reference. An unreadable journal leaves the array as it is.
    let visibleMessages (stateRoot: string) (sessionId: string) (messages: obj) : obj =
        if
            String.IsNullOrWhiteSpace stateRoot
            || String.IsNullOrWhiteSpace sessionId
            || isNull messages
            || not (emitJsExpr messages "Array.isArray($0)")
        then
            messages
        else
            try
                match Wanxiangshu.Mission.Lifecycle.DurableWorkHost.inspected stateRoot sessionId with
                | None -> messages
                | Some work ->
                    match cutVisible (unbox<obj array> messages) (Some work) with
                    | None -> messages
                    | Some kept -> box kept
            with _ ->
                messages

    let private cutRequest (outObj: obj) work =
        let messages = ProviderWireDecode.messagesFromTransformOutput outObj |> Array.ofList

        match cutVisible messages work with
        | None -> ()
        | Some kept -> HostMessageProjection.replaceMessagesInPlace outObj (Array.toList kept)

    let apply
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (interruptAttempt: SessionId -> Task<unit>)
        (sessionId: string option)
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        (outObj: obj)
        : Task<RelayProjectionDisposition> =
        task {
            // No unified work means this session is not on the lifecycle track:
            // the projection must leave the message set untouched rather than
            // re-encode it.
            match journal, sessionId, lifecycle with
            | Some durable, Some value, Some work when not (String.IsNullOrWhiteSpace value) ->
                let! disposition =
                    project durable interruptAttempt (SessionId.create value) acceptedRequest (Some work) outObj

                match disposition with
                | RelayProjectionDisposition.RetiredAttemptStopped -> ()
                | _ -> cutRequest outObj (Some work)

                return disposition
            | _, _, Some work ->
                cutRequest outObj (Some work)
                return RelayProjectionDisposition.Unchanged
            | _ -> return RelayProjectionDisposition.Unchanged
        }

    let RelayNarrativeTransform_apply
        (journal: AgentJournal option)
        (acceptedRequest: bool)
        (interruptAttempt: SessionId -> Task<unit>)
        (sessionId: string option)
        (lifecycle: Wanxiangshu.Mission.Lifecycle.DurableWorkHost.InspectedWork option)
        (outObj: obj)
        : Task<RelayProjectionDisposition> =
        apply journal acceptedRequest interruptAttempt sessionId lifecycle outObj
