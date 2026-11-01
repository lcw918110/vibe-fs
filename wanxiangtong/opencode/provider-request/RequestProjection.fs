namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
#nowarn "3511"

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Routing
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Scope
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Prefix.OpenCode
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Delegation.SyncDelegate.OpenCode
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Mission.Work
open Wanxiangshu.Mission.Work.OpenCode
open Wanxiangshu.Mission.Manager
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Participant.Provider.Projection.ProviderProjection
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.Journal
open PluginHostInterop

module PluginTransforms =

    [<Import("outboundViewStore", "wanxiangli/pair-hint")>]
    let private outboundViewStore: obj = jsNative

    [<Import("scopeRetention", "wanxiangli/scope-retention")>]
    let private scopeRetention: obj = jsNative


    // The real Host session contains ONLY its negotiation question. This
    // process-live view is what the model receives instead, reusing the last
    // complete delegator wire as an immutable prefix. No mutation of Host history.
    let private negotiationMessages (outObj: obj) : obj array =
        if isNull outObj || isNull outObj?messages then
            [||]
        else
            unbox<obj array> outObj?messages

    let private negotiationSessionId (input: obj) : string option =
        let session =
            if isNull input then null
            elif isNull input?sessionID then input?sessionId
            else input?sessionID

        if isNull session then
            None
        else
            let id = string session
            if String.IsNullOrWhiteSpace id then None else Some id

    let negotiationToolViolationCount (sessionId: string) : int = Surface.toolViolationCount sessionId

    let negotiationRecordToolViolation (sessionId: string) : unit = Surface.recordToolViolation sessionId

    let negotiationIsTemporary (sessionId: string) : bool = Surface.isTemporary sessionId

    let private modelIdentity (input: obj) : obj option =
        let model = if isNull input then null else input?model
        let provider = if isNull model then null else model?providerID

        let identifier =
            if isNull model then null
            elif isNull model?modelID then model?id
            else model?modelID

        let message = if isNull input then null else input?message
        let messageModel = if isNull message then null else message?model

        let variant =
            if not (isNull messageModel) && not (isNull messageModel?variant) then
                messageModel?variant
            elif not (isNull model) then
                model?variant
            else
                null

        let withVariant =
            if isNull variant then
                [ "providerID", provider; "modelID", identifier ]
            else
                [ "providerID", provider; "modelID", identifier; "variant", variant ]

        if isNull provider || isNull identifier then
            None
        else
            Some(createObj withVariant)

    let negotiationRememberModel (input: obj) : unit =
        match negotiationSessionId input, modelIdentity input with
        | Some sessionId, Some model -> Surface.rememberModel sessionId model
        | _ -> ()

    let negotiationModelFor (sessionId: string) : obj = Surface.modelFor sessionId

    let negotiationRememberSystem (input: obj) (output: obj) : unit =
        let observed = if isNull output then null else output?system

        match negotiationSessionId input with
        | Some sessionId when not (isNull observed) -> Surface.rememberSystem sessionId (unbox<string array> observed)
        | _ -> ()

    let negotiationRememberMessages (sessionId: string) (outObj: obj) : unit =
        let messages = negotiationMessages outObj

        if messages.Length > 0 then
            Surface.rememberMessages sessionId messages

    let private responseErrorReason (response: obj) (fallback: string) : string =
        if isNull response?reason then
            fallback
        else
            string response?reason

    let negotiationStartTemporary (tempId: string) (delegatorId: string) : Result<unit, string> =
        let r = Surface.startTemporary tempId delegatorId

        if not (isNull r?ok) && unbox<bool> r?ok then
            Ok()
        else
            Error(responseErrorReason r "startTemporary failed")

    let consultationStartPrevious (tempId: string) (sourceSessionId: string) (metadata: obj) : Result<unit, string> =
        let response = Surface.startTemporaryPrevious tempId sourceSessionId metadata
        if not (isNull response?ok) && unbox<bool> response?ok then Ok()
        else Error(responseErrorReason response "previous Scope source is unavailable in latest provider view")

    let consultationModelForTemporary (tempId: string) : obj = Surface.modelForTemporary tempId

    /// Previous-Scope consultation runs on the source session's current model,
    /// exactly like a fresh negotiate; the archived model is only a fallback
    /// when no current model has been observed. A stale archived identity
    /// (e.g. an upstream that has since gone away) must not strand the ask.
    let consultationModelForPrevious (sourceSessionId: string) (metadata: obj) : obj =
        let current = negotiationModelFor sourceSessionId

        if not (isNull current) then current
        elif isNull metadata then null
        else metadata?model

    let negotiationFinishTemporary (tempId: string) : unit = Surface.finishTemporary tempId

    let negotiationForgetSession (sessionId: string) : unit = Surface.forgetSession sessionId

    let negotiationProjectSystem (input: obj) (output: obj) : bool =
        negotiationSessionId input
        |> Option.bind (fun sid ->
            Surface.projectSystem
                sid
                (if isNull output || isNull output?system then
                     [||]
                 else
                     unbox<string array> output?system))
        |> Option.map (fun system ->
            let existing = if isNull output then null else output?system

            if not (isNull existing) && emitJsExpr existing "Array.isArray($0)" then
                emitJsStatement (existing, system) "$0.length = 0; $0.push(...$1)"
            else
                output?system <- system

            true)
        |> Option.defaultValue false

    let private latestTemporaryQuestion (outObj: obj) : obj option =
        negotiationMessages outObj
        |> Array.tryFindBack (fun message ->
            if isNull message then
                false
            else
                let info = message?info
                not (isNull info) && not (isNull info?role) && string info?role = "user")

    let private applyTemporaryQuestion (outObj: obj) (projected: obj array) : bool =
        HostMessageProjection.replaceMessagesInPlace
            outObj
            (HostMessageProjection.sanitizeMessages (Array.toList projected))

        true

    let negotiationProjectMessages (sessionId: string) (outObj: obj) : bool =
        let questionOpt = latestTemporaryQuestion outObj

        // The temporary turn's own steps after its question (a vetoed tool call
        // and its denial) stay visible, so the model reads the rebuke.
        let trailing =
            let messages = negotiationMessages outObj

            match questionOpt with
            | Some question ->
                match Array.tryFindIndexBack (fun m -> obj.ReferenceEquals(m, question)) messages with
                | Some index -> messages[index + 1 ..]
                | None -> [||]
            | None -> [||]

        let projectedOpt =
            questionOpt
            |> Option.bind (fun q -> Surface.projectMessages sessionId q)
            |> Option.map (fun projected -> Array.append projected trailing)

        match projectedOpt with
        | Some projected -> applyTemporaryQuestion outObj projected
        | None -> false

    type TraceTransformCapture =
        { RawMessages: obj list
          Current: XTraceProjectionState option
          Receipt: XTraceCaptureReceipt option }

    type DeliveryAttemptFence =
        { SessionId: SessionId
          ProviderRun: ProviderRunIdentity
          CallId: ToolCallId }

    /// 交付裁决（条款 4）：Continue = 仍需继续，本次请求照常放行；
    /// Stop = 无需继续，本次请求在 transform 钩子处被中断。
    [<RequireQualifiedAccess>]
    type DeliveryAdjudication =
        | ContinueDelivery
        | StopDelivery of DeliveryAttemptFence

    type NormalTransformCapabilities =
        { BeginPhysicalProviderAttempt: string option -> obj -> Task<unit>
          StopCompletedContinuation: string option -> PhysicalUserMessageId option -> obj -> Task<bool>
          BindSessionStartedAt: string option -> Task<DateTimeOffset option>
          ApplyRelayProjection: string option -> obj -> Task<RelayProjectionDisposition>
          ApplyTenureIsolation: string option -> obj -> Task<unit>
          CaptureXTraceMessages: string option -> obj -> Task<TraceTransformCapture>
          RefreshCompanionXTrace: string option -> XTraceProjectionState option -> unit
          ApplyCompanion: string option -> obj -> obj -> Task<unit>
          ApplyXWire: obj -> Task
          FreezeProviderAttemptPlan: string option -> PhysicalUserMessageId option -> Task<unit>
          InjectPairGuideline: string option -> DateTimeOffset option -> obj -> Task<unit>
          SettleAndReplaceDeferredInspections: string option -> obj -> Task<unit>
          SanitizeMessages: string option -> obj -> unit
          AdjudicateDelivery: string option -> obj -> Task<DeliveryAdjudication>
          StopAfterDelivery:
              string option -> PhysicalUserMessageId option -> DeliveryAttemptFence -> obj -> Task<unit> }

    type TransformBranchCapabilities = { RegisterOwned: string -> unit }

    let private tryGetOutMessages (outObj: obj) : obj array option =
        if not (isNull outObj) && not (isNull outObj?messages) then
            Some(unbox<obj array> outObj?messages)
        else
            None

    // Host shape is decoded once. Scope policy only sees the neutral view.
    let private scopeField (value: obj) (name: string) : obj =
        if isNull value then
            null
        else
            emitJsExpr (value, name) "$0[$1]"

    let private scopeText (value: obj) =
        if isNull value then "" else string value

    let private scopeRole (message: obj) =
        let direct = scopeField message "role"
        let info = scopeField message "info"
        let roleValue = if isNull direct then scopeField info "role" else direct

        match scopeText roleValue with
        | "user" -> "user"
        | "assistant" -> "assistant"
        | "toolResult"
        | "tool"
        | "tool-result"
        | "tool_result" -> "result"
        | "custom" -> "custom"
        | _ -> "other"

    let private scopeParts (message: obj) : obj array =
        let parts = scopeField message "parts"
        let content = scopeField message "content"

        if not (isNull parts) && emitJsExpr parts "Array.isArray($0)" then
            unbox<obj array> parts
        elif not (isNull content) && emitJsExpr content "Array.isArray($0)" then
            unbox<obj array> content
        else
            [||]

    let private scopeTextContent (message: obj) =
        let content = scopeField message "content"

        if not (isNull content) && emitJsExpr content "typeof $0 === 'string'" then
            scopeText content
        else
            scopeParts message
            |> Array.filter (fun part -> scopeText (scopeField part "type") = "text")
            |> Array.map (fun part -> scopeText (scopeField part "text"))
            |> String.concat "\n"

    let private scopeTimestamp (message: obj) =
        let time = scopeField message "timestamp"
        let info = scopeField message "info"
        let infoTimestamp = scopeField info "timestamp"
        let created = scopeField (scopeField info "time") "created"

        if not (isNull time) then time
        elif not (isNull infoTimestamp) then infoTimestamp
        else created

    let private scopeToolId (part: obj) =
        let own = scopeField part "callID"
        let direct = if isNull own then scopeField part "id" else own

        if isNull direct then
            scopeField part "toolCallId"
        else
            direct

    let private scopeTerminalField (state: obj) : string =
        let status = scopeText (scopeField state "status")
        let output = scopeField state "output"
        let error = scopeField state "error"
        let errorText = scopeField state "errorText"

        let isText v =
            not (isNull v) && emitJsExpr v "typeof $0 === 'string'"

        match status with
        | "completed" when isText output -> "output"
        | "error" when isText error -> "error"
        | "error" when isText output -> "output"
        | "error" when isText errorText -> "errorText"
        | _ -> ""

    /// Pull scope annotation off a tool part for PairHint reset barriers.
    let private scopeAnnotationOf (part: obj) : obj =
        let fromBag (bag: obj) =
            if isNull bag then null else scopeField bag "scope"
        let state = scopeField part "state"
        let candidates =
            [ fromBag part
              fromBag (scopeField part "arguments")
              fromBag (scopeField part "input")
              fromBag (scopeField state "input")
              fromBag state ]
        candidates |> List.tryFind (fun v -> not (isNull v)) |> Option.defaultValue null

    let private neutralPart (part: obj) : obj =
        let kind = scopeText (scopeField part "type")

        if kind = "tool" || kind = "toolCall" || kind = "tool-call" || kind = "tool_call" then
            let tool = scopeField part "tool"
            let name = if isNull tool then scopeField part "name" else tool
            let resultField = scopeTerminalField (scopeField part "state")
            let scope = scopeAnnotationOf part
            if isNull scope then
                createObj [ "id", scopeToolId part; "tool", name; "terminalField", box resultField ]
            else
                createObj
                    [ "id", scopeToolId part
                      "tool", name
                      "terminalField", box resultField
                      "scope", scope ]
        elif kind = "toolResult" || kind = "tool-result" || kind = "tool_result" then
            createObj [ "resultFor", scopeToolId part ]
        else
            createObj [ "type", box kind ]

    let private neutralScopeMessage (message: obj) =
        let kind = scopeRole message
        let info = scopeField message "info"
        let attribution = scopeField message "attribution"

        let authored =
            if isNull attribution then
                scopeField info "attribution"
            else
                attribution

        let operator =
            (kind = "user" && scopeText authored <> "agent")
            || (kind = "custom" && scopeText authored = "user")

        let steering = scopeField message "steering"

        let steering =
            if isNull steering then
                scopeField info "steering"
            else
                steering

        let side = kind = "user" && not operator && not (steering = box true)
        let stamp = scopeTimestamp message

        let key =
            (if isNull stamp then "" else scopeText stamp) + ":" + scopeTextContent message

        let resultId =
            let direct = scopeField message "toolCallId"

            if not (isNull direct) then
                direct
            else
                scopeField message "callID"

        createObj
            [ "kind", box kind
              "operator", box operator
              "sideRequest", box side
              "key", box key
              "timestamp", stamp
              "parts", box (scopeParts message |> Array.map neutralPart)
              "resultFor", box (scopeText resultId)
              "resultField", box (if kind = "result" then "content" else "") ]

    let private copyScope (source: obj) =
        emitJsExpr source "Object.assign({}, $0)"

    let private assignScopeParts (source: obj) (target: obj) (parts: obj array) : unit =
        if not (isNull (scopeField source "parts")) then
            target?parts <- box parts
        else
            target?content <- box parts

    let private appendScopeContentTail (message: obj) (suffix: string) : obj =
        let content = scopeField message "content"
        let updated = Surface.appendTailOnce content suffix

        if obj.ReferenceEquals(content, updated) then
            message
        else
            let rewritten = copyScope message
            rewritten?content <- updated
            rewritten

    let private appendScopePartTail (message: obj) (partIndex: int) (fieldName: string) (suffix: string) : obj =
        let parts = scopeParts message
        let part = parts.[partIndex]
        let state = scopeField part "state"
        let current = scopeField state fieldName
        let changed = Surface.appendTailOnce current suffix

        if obj.ReferenceEquals(current, changed) then
            message
        else
            let nextState = copyScope state
            emitJsExpr (nextState, fieldName, changed) "$0[$1] = $2" |> ignore
            let nextPart = copyScope part
            nextPart?state <- nextState
            let nextParts = Array.copy parts
            nextParts.[partIndex] <- nextPart
            let nextMessage = copyScope message
            assignScopeParts message nextMessage nextParts
            nextMessage

    let private appendScopeResultTail (message: obj) (hint: obj) : obj =
        let partIndex = unbox<int> hint?part
        let fieldName = scopeText hint?field
        let suffix = scopeText hint?tail

        if partIndex < 0 then
            appendScopeContentTail message suffix
        else
            appendScopePartTail message partIndex fieldName suffix

    let private applyNeutralHint (messages: obj array) (hint: obj) : unit =
        let index = unbox<int> hint?index
        let value = messages.[index]

        messages.[index] <-
            if scopeText hint?kind = "user" then
                Surface.writeUserHint value [| scopeText hint?tail |]
            else
                appendScopeResultTail value hint

    let private applyNeutralSlot (messages: obj array) (slot: obj) : obj =
        let index = unbox<int> slot?index
        let message = messages.[index]
        let selected = scopeField slot "parts"

        if isNull selected then
            message
        else
            let parts = scopeParts message
            let kept = unbox<int array> selected |> Array.map (fun j -> parts.[j])
            let rewritten = copyScope message
            assignScopeParts message rewritten kept
            emitJsExpr rewritten "delete $0.providerPayload; delete $0.responseId" |> ignore
            rewritten

    let private openCodeScopeSteer (messages: obj array) (prose: string) (strong: bool) : obj array =
        let last = messages |> Array.tryLast |> Option.defaultValue null
        let originalInfo = scopeField last "info"
        let sessionId = scopeField originalInfo "sessionID"
        let info = createObj [ "role", box "user"; "sessionID", sessionId ]

        let steer =
            createObj
                [ "info", box info
                  "parts", box [| createObj [ "type", box "text"; "text", box prose; "synthetic", box true ] |]
                  "steering", box true
                  "attribution", box "agent"
                  "strong", box strong ]

        Array.append messages [| steer |]

    /// Collapse a retracted beat's assistant message to one text part (removeLast projection).
    /// Match keys: message/info id, responseId, providerRunId, and any tool-call /
    /// step id on the message against BeatRetracted.beatId + aliases.
    let private messageBeatKeys (message: obj) : string list =
        let info = scopeField message "info"
        let top =
            [ scopeField info "id"
              scopeField message "id"
              scopeField message "responseId"
              scopeField info "responseId"
              scopeField message "providerRunId"
              scopeField info "providerRunId"
              scopeField message "provider_run_id"
              scopeField info "runId" ]
            |> List.choose (fun v -> if isNull v then None else Some(scopeText v))
            |> List.filter (fun s -> s <> "")
        let parts = scopeField message "parts"
        let fromParts =
            if isNull parts then []
            else
                try
                    let arr: obj array = unbox parts
                    arr
                    |> Array.toList
                    |> List.collect (fun part ->
                        [ scopeField part "id"
                          scopeField part "callID"
                          scopeField part "callId"
                          scopeField part "toolCallId"
                          scopeField part "providerRunId"
                          scopeField (scopeField part "state") "id" ]
                        |> List.choose (fun v -> if isNull v then None else Some(scopeText v))
                        |> List.filter (fun s -> s <> ""))
                with _ ->
                    []
        (top @ fromParts) |> List.distinct

    let private collapseRetracted (sessionId: string) (messages: obj array) : obj array =
        let rows: obj array = unbox (scopeRetention?retractions(sessionId))
        if isNull (box rows) || rows.Length = 0 then messages
        else
            // Map every alias → replacement text.
            let byKey =
                rows
                |> Array.fold
                    (fun (acc: Map<string, string>) row ->
                        let replacement = scopeText (scopeField row "replacement")
                        let beat = scopeText (scopeField row "beatId")
                        let aliasField = scopeField row "aliases"
                        let aliases =
                            let baseKeys = if beat = "" then [] else [ beat ]
                            if isNull aliasField then baseKeys
                            else
                                try
                                    let arr: obj array = unbox aliasField
                                    baseKeys
                                    @ (arr
                                       |> Array.toList
                                       |> List.map scopeText
                                       |> List.filter (fun s -> s <> ""))
                                with _ ->
                                    baseKeys
                        aliases
                        |> List.distinct
                        |> List.fold (fun m key -> Map.add key replacement m) acc)
                    Map.empty
            if Map.isEmpty byKey then messages
            else
                // DSL-MUTABLE: algorithm-scratch — beat keys already consumed by this projection pass
                let mutable used = Set.empty<string>
                messages
                |> Array.map (fun message ->
                    if scopeRole message <> "assistant" then message
                    else
                        let keys = messageBeatKeys message
                        let hit =
                            keys
                            |> List.tryPick (fun key ->
                                match Map.tryFind key byKey with
                                | Some replacement when not (Set.contains key used) -> Some(key, replacement)
                                | _ -> None)
                        match hit with
                        | None -> message
                        | Some(key, replacement) ->
                            used <- Set.add key used
                            let info = scopeField message "info"
                            createObj
                                [ "info", box info
                                  "parts",
                                  box
                                      [| createObj
                                             [ "type", box "text"
                                               "text", box replacement ] |] ])

    let private prepareNeutralScope (sessionId: string) (raw: obj array) : obj array =
        let normal = raw |> Array.map neutralScopeMessage
        let plan = Surface.planRequest sessionId (box normal)
        let staged = Array.copy raw
        unbox<obj array> plan?hints |> Array.iter (applyNeutralHint staged)
        let selected = unbox<obj array> plan?slots |> Array.map (applyNeutralSlot staged)
        let collapsed = collapseRetracted sessionId selected
        let retention: obj = scopeRetention?status(sessionId)
        if not (unbox<bool> plan?active) then collapsed
        elif not (isNull plan?strong) then openCodeScopeSteer collapsed (scopeText plan?strong) true
        elif not (isNull retention?known) && unbox<bool> retention?known
             && not (isNull retention?remaining) && unbox<int> retention?remaining = 0 then
            openCodeScopeSteer collapsed (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/reset-due" Map.empty) true
        elif isNull retention?known || not (unbox<bool> retention?known) then
            // No retention scope yet — nudge scope.kind=resetScope (resource path name is historical).
            openCodeScopeSteer collapsed (ProviderProse.render (ProviderLanguageBinding.forSessionText sessionId) "host/scope-steer/missing-estimate" Map.empty) true
        else collapsed

    let private purgeAndSuffixSession (sessionId: string) (rawMessages: obj array) (outObj: obj) : unit =
        let sideRequest =
            rawMessages
            |> Array.tryLast
            |> Option.map (fun message ->
                neutralScopeMessage message |> fun neutral -> scopeField neutral "sideRequest")
            |> Option.exists (fun value -> not (isNull value) && unbox<bool> value)

        if not sideRequest then
            Surface.onTurnStart sessionId

        let prepared = prepareNeutralScope sessionId rawMessages

        if not (Surface.isUnchanged prepared rawMessages) then
            HostMessageProjection.replaceMessagesInPlace outObj (Array.toList prepared)

    let private applyScopePurgeAndSuffix (sessionIdOpt: string option) (outObj: obj) : unit =
        match sessionIdOpt, tryGetOutMessages outObj with
        | Some sessionId, Some rawMessages -> purgeAndSuffixSession sessionId rawMessages outObj
        | _ -> ()

    /// 普通分支请求的静态管道上下文：预读的物理消息 id 与各 stage 产出的
    /// 会话起点、重放计划、已捕获 XTrace、horizon 只经这份 frame 传递。
    type private TransformFrame =
        { SessionId: string option
          InObj: obj
          OutObj: obj
          PhysicalUserMessageId: PhysicalUserMessageId option
          SessionStartedAt: DateTimeOffset option
          TracedXTrace: XTraceProjectionState option }

    /// 普通分支 stage：具名、静态列出，Run 直接调用一个或一段 caps 入口。
    /// 返回 None 表示本次请求到此终止（退休旧 attempt 已被拦截）。
    type private TransformStage =
        { Name: string
          Run: TransformFrame -> Task<TransformFrame option> }

    let private runOrdinaryStages (stages: TransformStage list) (frame: TransformFrame) : Task<unit> =
        let rec runStages stages frame =
            task {
                match stages with
                | [] -> ()
                | stage :: rest -> return! runOneStage stage rest frame
            }

        and runOneStage (stage: TransformStage) rest frame =
            task {
                match! stage.Run frame with
                | Some next -> return! runStages rest next
                | None -> return ()
            }

        runStages stages frame

    let normalTransform
        (caps: NormalTransformCapabilities)
        (projectionSessionIdOpt: string option)
        (inObj: obj)
        (outObj: obj)
        : Task<unit> =
        task {
            // One owner view: capture the exact transform input before any Scope purge.
            // The paired output is published only after the real transform finishes.
            let incomingMessages: obj array =
                let raw = scopeField outObj "messages"
                if isNull raw then [||]
                else unbox (emitJsExpr raw "structuredClone($0)")

            // 预读：在 Relay 投影改写消息数组之前捕获原始物理用户消息 id。
            let physicalUserMessageId =
                ProviderWireDecode.messagesFromTransformOutput outObj
                |> ProviderWireCapture.lastUserMessageId

            // host-boundary-019 的普通分支固定次序，不得重排；每个 stage 直接调用
            // 对应 caps 入口，Run 返回 None 时本次请求到此终止。
            let stages: TransformStage list =
                [ // 1. SessionExecutionBinding.beginPhysicalProviderAttemptForTransform (durable-evidence gate)
                  { Name = "begin-physical-provider-attempt"
                    Run =
                      fun frame ->
                          task {
                              do! caps.BeginPhysicalProviderAttempt frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 2. SessionStartedAtLedger.tryBindOrAbort
                  { Name = "bind-session-started-at"
                    Run =
                      fun frame ->
                          task {
                              let! sessionStartedAt = caps.BindSessionStartedAt frame.SessionId

                              return
                                  Some
                                      { frame with
                                          SessionStartedAt = sessionStartedAt }
                          } }
                  // 3. Relay projection cut + manager-loop opening. This MUST run
                  // before every trace/compaction owner so retired raw history
                  // cannot be reintroduced later in the composition.
                  { Name = "work-projection-cut"
                    Run =
                      fun frame ->
                          task {
                              do! caps.SettleAndReplaceDeferredInspections frame.SessionId frame.OutObj
                              let! disposition = caps.ApplyRelayProjection frame.SessionId frame.OutObj

                              if disposition = RelayProjectionDisposition.RetiredAttemptStopped then
                                  return None
                              else
                                  return Some frame
                          } }
                  // 4. Tenure isolation (baton handover). Isolates message set for
                  // active tenures (currently Plan); reanchors the prefix epoch and
                  // validates stable message identity. It does not rewrite the message set.
                  { Name = "tenure-isolation"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyTenureIsolation frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 6. XTraceCapture.captureObservedMessagesWithReceipt
                  { Name = "capture-xtrace"
                    Run =
                      fun frame ->
                          task {
                              let! capture = caps.CaptureXTraceMessages frame.SessionId frame.OutObj

                              return
                                  Some
                                      { frame with
                                          TracedXTrace = capture.Current }
                          } }
                  // 7. 交付裁决（条款 4）。投影裁剪之后、放行本次请求之前：
                  // 本次 attempt 存在一次已成立的交付时，本次请求不再放行——
                  // 抑制下一次 provider step、发出本次 attempt 的中断、清空
                  // 消息，LLM 因此看不到 deliver 回复。放在 XTrace 捕获之后
                  // 是为了让交付那一轮的证据先落进轨迹；放在 attempt 计划冻结
                  // 之前是不让一个被抑制的请求留下「已开始」的 provider 事实。
                  { Name = "delivery-adjudication"
                    Run =
                      fun frame ->
                          task {
                              let! disposition = caps.AdjudicateDelivery frame.SessionId frame.OutObj

                              match disposition with
                              | DeliveryAdjudication.ContinueDelivery -> return Some frame
                              | DeliveryAdjudication.StopDelivery fence ->
                                  do!
                                      caps.StopAfterDelivery
                                          frame.SessionId
                                          frame.PhysicalUserMessageId
                                          fence
                                          frame.OutObj

                                  return None
                          } }
                  // 8. CompanionHost.RefreshXTrace
                  { Name = "refresh-companion-xtrace"
                    Run =
                      fun frame ->
                          caps.RefreshCompanionXTrace frame.SessionId frame.TracedXTrace
                          Task.FromResult(Some frame) }
                  // 9. applyCompanionForOrdinaryMaterial
                  { Name = "apply-companion"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyCompanion frame.SessionId frame.InObj frame.OutObj
                              return Some frame
                          } }
                  // 10. XWire.applyTransform. Admits the attempt plan; message
                  // content is never rewritten.
                  { Name = "apply-xwire"
                    Run =
                      fun frame ->
                          task {
                              do! caps.ApplyXWire frame.OutObj
                              return Some frame
                          } }
                  // 11. ProviderLifecycle.freezeProviderAttemptPlanForTransform.
                  // Freeze the exact plan, then confirm the Host's real assistant
                  // identity and durable ProviderStarted before returning its body.
                  { Name = "freeze-provider-attempt-plan"
                    Run =
                      fun frame ->
                          task {
                              do! caps.FreezeProviderAttemptPlan frame.SessionId frame.PhysicalUserMessageId
                              return Some frame
                          } }
                  { Name = "scope-purge"
                    Run =
                      fun frame ->
                          applyScopePurgeAndSuffix frame.SessionId frame.OutObj
                          Task.FromResult(Some frame) }
                  { Name = "current-auxiliaries"
                    Run =
                      fun frame ->
                          task {
                              do! caps.InjectPairGuideline frame.SessionId frame.SessionStartedAt frame.OutObj
                              return Some frame
                          } }
                  // 15. Re-apply replaced inspection results after any intermediate insertions
                  { Name = "reapply-deferred-inspections"
                    Run =
                      fun frame ->
                          task {
                              do! caps.SettleAndReplaceDeferredInspections frame.SessionId frame.OutObj
                              return Some frame
                          } }
                  // 16. HostMessageProjection.sanitizeMessages
                  { Name = "sanitize-output-messages"
                    Run =
                      fun frame ->
                          caps.SanitizeMessages frame.SessionId frame.OutObj
                          Task.FromResult(Some frame) } ]

            let frame: TransformFrame =
                { SessionId = projectionSessionIdOpt
                  InObj = inObj
                  OutObj = outObj
                  PhysicalUserMessageId = physicalUserMessageId
                  SessionStartedAt = None
                  TracedXTrace = None }

            // The late-consultation stop reads the unprojected Host history only.
            // After the ordinary stages the wire is a projection: a committed
            // resetScope retires the ordinary tool parts (and thinking) before the
            // latest reset while keeping resetScope/askPreviousScope/... calls, so
            // the assistant turn just before the answer (e.g. a todowrite-only
            // step) survives as a completed step-start/step-finish shell and looks
            // like a finished tool-free turn. The predicate only inspects the turn
            // before the answer, so the retained resetScope after it never helps. Re-judging that projection aborted
            // live children right after resetScope with nothing left to resume
            // them (2026-10-27/28 field incidents), so it is judged exactly once.
            let! stopped = caps.StopCompletedContinuation projectionSessionIdOpt physicalUserMessageId outObj

            if not stopped then
                do! runOrdinaryStages stages frame

            projectionSessionIdOpt
            |> Option.iter (fun sid ->
                let transformed = negotiationMessages outObj
                if transformed.Length > 0 then Surface.rememberTransform sid incomingMessages transformed)

            outboundViewStore?recordMessages (outObj?messages) |> ignore

        }

    let createWithCaps
        (caps: NormalTransformCapabilities)
        (branches: TransformBranchCapabilities)
        : obj -> obj -> Task<unit> =
        fun (inObj: obj) (outObj: obj) ->
            task {
                let projectionSessionIdOpt =
                    projectionSessionIdFromMessages outObj
                    |> Option.orElseWith (fun () ->
                        if not (isNull inObj) && not (isNull inObj?sessionID) then
                            let sid = string inObj?sessionID
                            if String.IsNullOrWhiteSpace sid then None else Some sid
                        elif not (isNull inObj) && not (isNull inObj?sessionId) then
                            let sid = string inObj?sessionId
                            if String.IsNullOrWhiteSpace sid then None else Some sid
                        else
                            None)

                match projectionSessionIdOpt with
                | Some sessionId when negotiationIsTemporary sessionId ->
                    negotiationProjectMessages sessionId outObj |> ignore
                | _ ->
                    projectionSessionIdOpt |> Option.iter branches.RegisterOwned
                    do! normalTransform caps projectionSessionIdOpt inObj outObj
            }
