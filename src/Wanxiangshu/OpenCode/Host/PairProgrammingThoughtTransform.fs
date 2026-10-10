namespace Wanxiangshu.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Concern
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.OpenCode.Host.PairProgramming
open Wanxiangshu.Context.Trace
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.Requirement.Grounding

/// HOST-013：永久 pair-programming auto-injected pairs。
///
/// Ordinary Host 编码是 ResultGap 上的一条 completed synthetic `skill({ name: "" })` tool part。
/// OpenCode `toModelMessagesEffect` 把它展开成 provider tool-call + tool-result；Cursor 保持真实
/// terminal tool result，并在 NUL+BOM 后拼同一 canonical instruction payload。
/// 禁止 pending/running：Host 会把它们收成 "[Tool execution was interrupted]"。
/// 每个 occurrence 的 transcript 位置由 durable CallGap/ResultGap 决定；同一 placement
/// occasion（SessionId + CallGap + ResultGap）最多一个 pair，重复 transform 只 replay、
/// 不再新增。同 epoch 内前次 provider wire 必须是后次 wire 的字节前缀（ARCH-004）。
module PairProgrammingThoughtTransform =

    [<RequireQualifiedAccess>]
    type CursorPresentationOwner =
        | PairGuidance
        | RequirementGrounding

    [<Literal>]
    let private PairProgrammingGuidelinePath = "host/pair-programming-guideline"

    /// HOST-013 English canonical used by tests; production loads via session language.
    let text =
        ProviderProse.instructionLines ProviderLanguage.SimplifiedChinese PairProgrammingGuidelinePath Map.empty
        |> LlmFacing.renderInstructions

    // ── JS Evidence parsers（flat；无嵌套 decision）──────────────────────────

    let private tryObj (value: obj) : obj option =
        if isNull value then None else Some value

    let private tryUnboxString (value: obj) : string option =
        if isNull value then None else Some(unbox<string> value)

    let private messageInfo (rawMsg: obj) : obj option =
        if isNull rawMsg then None
        elif isNull rawMsg?info then None
        else Some(rawMsg?info)

    let private providerIdFromInfo (info: obj) : string option =
        if not (isNull info?providerID) then
            Some(unbox<string> info?providerID)
        elif not (isNull info?model) && not (isNull info?model?providerID) then
            Some(unbox<string> info?model?providerID)
        else
            None

    /// Provider id on a Host message (`info.providerID` or `info.model.providerID`).
    let providerIdOfMessage (rawMsg: obj) : string option =
        messageInfo rawMsg |> Option.bind providerIdFromInfo

    /// Most recent provider id on the transcript (assistant `providerID` or user `model.providerID`).
    let providerIdFromMessages (rawMessages: obj list) : string option =
        rawMessages |> List.rev |> List.tryPick providerIdOfMessage

    /// Emergency fuse only. Cursor is a provider-specific projection, not an
    /// occurrence bypass: it still creates/replays the same durable HOST-013 fact.
    let skipAutoInjectedRequested (_providerId: string option) : bool =
        match Environment.GetEnvironmentVariable "WANXIANGSHU_SKIP_AUTO_INJECTED" with
        | "1" -> true
        | _ -> false

    /// Unconditionally true: Cursor mode (NUL+BOM tool result suffix injection without synthetic tool messages)
    /// is unified across all providers.
    let isCursorProvider (providerId: string option) =
        ignore providerId
        true

    /// The marker's source identity (HOST-013). Filtering must use this, never
    /// the text: a real user may quote the sentence.
    let source = "pair-programming-auto-injected"

    let private idPrefix = "pair-programming-auto-injected-"

    /// HOST-013 borrows the Host-owned skill wire. Empty name is injection-only;
    /// real non-empty skill names remain ordinary executable Host skills.
    let toolName = "skill"

    let skillName = ""

    let private isMarkerSource (markerSource: string) = markerSource = source

    /// HOST-013：marker 身份仅按 `info.source`。
    let isPairProgrammingThought (rawMsg: obj) : bool =
        messageInfo rawMsg
        |> Option.map (fun info -> unbox<string> info?source)
        |> Option.exists isMarkerSource

    /// Active empty-name skill loads are reserved for synthetic injection only.
    let reprimandText (lang: ProviderLanguage option) : string =
        match lang with
        | Some ProviderLanguage.SimplifiedChinese ->
            "DENIED. `skill` 只保留给系统注入的 pair-programming hint，不能主动调用或读取。真实 `skill` 工具仍可正常使用；请只加载 available skills 列表中的非空 name。"
        | _ ->
            "DENIED. `skill` is reserved for the injected pair-programming hint and cannot be called or read manually. The real `skill` tool remains available; load only non-empty names from the available skills list."

    let private isSkillToolPart (part: obj) : bool =
        not (isNull part) && (tryUnboxString part?tool |> Option.exists ((=) toolName))

    let private isReservedSkillToolPart (part: obj) : bool =
        isSkillToolPart part
        && not (isNull part?state)
        && (isNull part?state?input
            || isNull part?state?input?name
            || (tryUnboxString part?state?input?name |> Option.exists String.IsNullOrWhiteSpace))

    let private writeCompletedReprimandState (reprimand: string) (part: obj) (originalState: obj) =
        if isNull originalState then
            let s =
                createObj
                    [ "status", box "completed"
                      "input", box (createObj [])
                      "output", box reprimand
                      "time", box (createObj [ "start", box 0; "end", box 0 ]) ]

            part?state <- s
        else
            originalState?status <- box "completed"
            originalState?output <- box reprimand
            emitJsExpr originalState "delete $0.error; delete $0.errorText" |> ignore

    let private clearPartError (part: obj) =
        if not (isNull part?error) then
            emitJsExpr part "delete $0.error; delete $0.errorText" |> ignore

    let private overwritePartOutput (reprimand: string) (part: obj) =
        if not (isNull part?output) then
            part?output <- box reprimand

    let private applyReservedSkillReprimand (reprimand: string) (part: obj) : obj =
        writeCompletedReprimandState reprimand part (part?state)
        clearPartError part
        overwritePartOutput reprimand part
        part

    let private reprimandToolPart (lang: ProviderLanguage option) (part: obj) : obj =
        if not (isReservedSkillToolPart part) then
            part
        else
            applyReservedSkillReprimand (reprimandText lang) part

    let private isJsArray (value: obj) : bool =
        not (isNull value) && emitJsExpr value "Array.isArray($0)"

    let private asJsArray (value: obj) : obj array option =
        if isNull value then None
        elif isJsArray value then Some(unbox<obj array> value)
        else None

    let private rawParts (rawMsg: obj) : obj array =
        if isNull rawMsg then
            [||]
        else
            asJsArray rawMsg?parts
            |> Option.orElseWith (fun () -> asJsArray rawMsg?content)
            |> Option.defaultValue [||]

    let private reprimandReservedSkillParts (lang: ProviderLanguage option) (parts: obj array) =
        parts
        |> Array.filter isReservedSkillToolPart
        |> Array.iter (fun part -> reprimandToolPart lang part |> ignore)

    let private sanitizeNonMarkerMessage (lang: ProviderLanguage option) (rawMsg: obj) : obj =
        let parts = rawParts rawMsg

        if Array.isEmpty parts then
            rawMsg
        else
            reprimandReservedSkillParts lang parts
            rawMsg

    let private sanitizeActiveReservedSkillMessage (lang: ProviderLanguage option) (rawMsg: obj) : obj =
        if isNull rawMsg || isPairProgrammingThought rawMsg then
            rawMsg
        else
            sanitizeNonMarkerMessage lang rawMsg

    /// Transform only active `skill({ name: "" })` calls from failed into completed DENIED results.
    /// Every non-empty real skill call passes through untouched.
    let sanitizeActiveToolCalls (lang: ProviderLanguage option) (rawMessages: obj list) : obj list =
        rawMessages |> List.map (sanitizeActiveReservedSkillMessage lang)

    /// Durable/memory pair with both halves' transcript gap anchors.
    type PairProgrammingGuidelineWire =
        { Ordinal: int64
          CallId: string
          MarkerText: string
          CallGap: TranscriptGap
          ResultGap: TranscriptGap
          ConcernPlacement: ConcernPlacementBatch option }

    /// Process-local fallback when journal is unavailable (tests / no workspace).
    /// Keyed by transcript identity; append-only within process.
    let private memoryLedger =
        Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>()

    let private transcriptKey (sessionId: string option) : string = defaultArg sessionId ""

    /// CallId = digest(transcript + source + ordinal). Stable across restarts.
    let stableCallId (sessionId: string option) (ordinal: int64) : string =
        let digest =
            HostDigest.sha256Hex ((transcriptKey sessionId) + source + string ordinal)

        idPrefix + digest.Substring(0, 24)

    let private buildPairMessage (callId: string) (markerText: string) : obj =
        let part =
            createObj
                [ "type", box "tool"
                  "tool", box toolName
                  "callID", box callId
                  "state",
                  box (
                      createObj
                          [ "status", box "completed"
                            "input", box (createObj [])
                            "output", box markerText
                            "time", box (createObj [ "start", box 0; "end", box 0 ]) ]
                  ) ]

        createObj
            [ "info",
              box (
                  createObj
                      [ "id", box callId
                        "role", box "assistant"
                        "source", box source
                        "synthetic", box true ]
              )
              "parts", box [| part |] ]

    let private roleFrom (target: obj) : string option = tryUnboxString target?role

    let private messageRole (rawMsg: obj) : string =
        if isNull rawMsg then
            ""
        else
            messageInfo rawMsg
            |> Option.bind roleFrom
            |> Option.orElseWith (fun () -> roleFrom rawMsg)
            |> Option.defaultValue ""
            |> fun value -> value.ToLowerInvariant()

    let private partTypeKind (part: obj) : string option =
        tryUnboxString part?``type`` |> Option.map (fun t -> t.ToLowerInvariant())

    let private isToolPartKind (kind: string) =
        kind = "tool"
        || kind = "tool-call"
        || kind = "tool_call"
        || kind = "tool-result"
        || kind = "tool_result"

    let private isToolPart (part: obj) : bool =
        if isNull part then
            false
        else
            partTypeKind part |> Option.exists isToolPartKind

    let private statusFromState (state: obj) : string option =
        tryUnboxString state?status
        |> Option.map (fun status -> status.ToLowerInvariant())

    let private partStatus (part: obj) : string option =
        if isNull part then
            None
        else
            tryObj part?state |> Option.bind statusFromState

    // ── transcript addressing ────────────────────────────────────────────────

    let private callIdFromRawId (raw: string) : string =
        if raw.EndsWith("-call", StringComparison.Ordinal) then
            raw.Substring(0, raw.Length - 5)
        else
            raw

    let private syntheticCallIdFromInfo (info: obj) : string option =
        tryUnboxString info?pairCallID
        |> Option.orElseWith (fun () -> tryUnboxString info?id |> Option.map callIdFromRawId)

    /// The callId a stripped synthetic message belongs to: the call half's id
    /// is `callId + "-call"`, the result half's id is `callId`.
    let private syntheticCallIdOf (rawMsg: obj) : string option =
        messageInfo rawMsg |> Option.bind syntheticCallIdFromInfo

    let private claimAddress (seen: Set<string>) (message: obj) : Result<string * Set<string>, string> =
        match ProviderWireDecode.hostMessageId message with
        | None -> Error "transcript message without address (HOST-013)"
        | Some id when Set.contains id seen -> Error(sprintf "duplicate transcript address %s (HOST-013)" id)
        | Some id -> Ok(id, Set.add id seen)

    /// Real messages in transcript order, each carrying its Host message
    /// address (`info.id` / `id`). Every real message must have one — a message
    /// without an address could never anchor a synthetic half, and a duplicate
    /// address would make anchors ambiguous.
    let private addressedRealMessages (realMessages: obj list) : Result<(string * obj) list, string> =
        let rec loop acc seen =
            function
            | [] -> Ok(List.rev acc)
            | message :: rest ->
                claimAddress seen message
                |> Result.bind (fun (id, seen') -> loop ((id, message) :: acc) seen' rest)

        loop [] Set.empty realMessages

    // ── replay：唯一合法渲染路径 ─────────────────────────────────────────────

    /// The one and only HOST-013 renderer: real messages + durable anchored
    /// pairs → the exact provider wire. Historical completed rows sit at their
    /// own durable ResultGap; nothing re-decides their position (`historyBlock` 禁止).
    ///
    /// 组内排序唯一合法：`Ordinal` 升序。
    ///
    /// Anchor 不在当前真实消息里的 historical pair **不重放、不报错**。
    /// XWire prefix probe 用 FrozenRecordPrefix 替换已覆盖前缀时会 drop 那些
    /// 消息（CTX-010 canonical `CutoffExclusive`）；被覆盖区里的 pair 属于被替换的前缀，
    /// 不应再注入 rewritten view，更不能因此 AbortSession 杀死当前物理 retry。
    /// Durable fact 仍保留；完整 transcript 回来时 anchor 在场即可再 replay。
    let cursorGuidanceSeparator = "\u0000\uFEFF"

    let private systemBlockPrefix = "<system>"
    let private systemBlockSuffix = "</system>"

    let systemBlock (body: string) : string =
        let content = if isNull body then "" else body.Trim()

        if
            content.StartsWith(systemBlockPrefix, StringComparison.Ordinal)
            && content.EndsWith(systemBlockSuffix, StringComparison.Ordinal)
        then
            content
        else
            String.concat "\n" [ systemBlockPrefix; content; systemBlockSuffix ]

    let private isString (value: obj) : bool =
        not (isNull value) && emitJsExpr value "typeof $0 === 'string'"

    let private terminalGuidanceIndex (index: int) (part: obj) : int option =
        match isToolPart part, partStatus part, tryObj part?state with
        | false, _, _ -> None
        | true, Some "completed", Some state when isString state?output -> Some index
        | true, Some "error", Some state when isString state?error -> Some index
        | true, Some "error", Some state when isString state?output -> Some index
        | _ -> None

    let private applyGuidanceSuffix (suffix: string) (originalPart: obj) (clonedState: obj) =
        let originalState = originalPart?state

        match partStatus originalPart with
        | Some "completed" -> clonedState?output <- box ((unbox<string> originalState?output) + suffix)
        | Some "error" when isString originalState?error ->
            clonedState?error <- box ((unbox<string> originalState?error) + suffix)
        | Some "error" -> clonedState?output <- box ((unbox<string> originalState?output) + suffix)
        | _ -> ()

    let appendCursorSuffixes (suffixTexts: string list) (rawMsg: obj) : obj option =
        let parts = rawParts rawMsg

        match parts |> Array.mapi terminalGuidanceIndex |> Array.choose id |> Array.tryLast with
        | None -> None
        | Some index ->
            let suffix =
                suffixTexts
                |> List.map (fun text -> cursorGuidanceSeparator + text)
                |> String.concat ""

            let originalPart = parts.[index]
            let originalState = originalPart?state
            let clonedState = emitJsExpr originalState "Object.assign({}, $0)"
            applyGuidanceSuffix suffix originalPart clonedState
            let clonedPart = emitJsExpr originalPart "Object.assign({}, $0)"
            clonedPart?state <- clonedState
            let clonedParts = Array.copy parts
            clonedParts.[index] <- clonedPart
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?parts <- box clonedParts
            Some clonedMessage

    let private mapTerminalResultText (mapText: string -> string) (originalPart: obj) : obj =
        let originalState = originalPart?state
        let clonedState = emitJsExpr originalState "Object.assign({}, $0)"

        match partStatus originalPart with
        | Some "completed" when isString originalState?output ->
            clonedState?output <- box (mapText (unbox<string> originalState?output))
        | Some "error" when isString originalState?error ->
            clonedState?error <- box (mapText (unbox<string> originalState?error))
        | Some "error" when isString originalState?output ->
            clonedState?output <- box (mapText (unbox<string> originalState?output))
        | _ -> ()

        let clonedPart = emitJsExpr originalPart "Object.assign({}, $0)"
        clonedPart?state <- clonedState
        clonedPart

    let private stripKnownCursorSuffixes (suffixTexts: string list) (originalPart: obj) : obj =
        let stripKnown (value: string) =
            (value, suffixTexts)
            ||> List.fold (fun current text ->
                current
                    .Replace(cursorGuidanceSeparator + text, "", StringComparison.Ordinal)
                    .Replace(cursorGuidanceSeparator + systemBlock text, "", StringComparison.Ordinal))

        mapTerminalResultText stripKnown originalPart

    let private terminalTextPartIndex (parts: obj array) : int option =
        parts
        |> Array.mapi (fun index part ->
            if isNull part then
                None
            elif
                isString part?text
                && (isNull part?``type`` || unbox<string> part?``type`` = "text")
            then
                Some index
            elif isString part then
                Some index
            else
                None)
        |> Array.choose id
        |> Array.tryLast

    let private appendFallbackUserText (suffix: string) (rawMsg: obj) (parts: obj array) : obj option =
        if isString rawMsg?content then
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?content <- box ((unbox<string> rawMsg?content) + suffix)
            Some clonedMessage
        elif isString rawMsg?text then
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?text <- box ((unbox<string> rawMsg?text) + suffix)
            Some clonedMessage
        else
            let textPart = createObj [ "type", box "text"; "text", box suffix ]
            let clonedParts = Array.append parts [| textPart |]
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?parts <- box clonedParts
            Some clonedMessage

    let private partText (part: obj) : string =
        if isString part?text then unbox<string> part?text
        elif isString part then unbox<string> part
        else ""

    let private appendCursorGuidanceToUserMessage (markerTexts: string list) (rawMsg: obj) : obj option =
        let parts = rawParts rawMsg

        let suffix =
            markerTexts
            |> List.map (fun text -> cursorGuidanceSeparator + systemBlock text)
            |> String.concat ""

        match terminalTextPartIndex parts with
        | Some index ->
            let originalPart = parts.[index]
            let clonedPart = emitJsExpr originalPart "Object.assign({}, $0)"
            let originalText = partText originalPart

            clonedPart?text <- box (originalText + suffix)
            let clonedParts = Array.copy parts
            clonedParts.[index] <- clonedPart
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?parts <- box clonedParts
            Some clonedMessage
        | None -> appendFallbackUserText suffix rawMsg parts

    let private stripFallbackUserText (stripKnown: string -> string) (rawMsg: obj) : obj =
        if isString rawMsg?content then
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?content <- box (stripKnown (unbox<string> rawMsg?content))
            clonedMessage
        elif isString rawMsg?text then
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?text <- box (stripKnown (unbox<string> rawMsg?text))
            clonedMessage
        else
            rawMsg

    let private stripUserMessageCursorSuffixes (suffixTexts: string list) (rawMsg: obj) : obj =
        let parts = rawParts rawMsg

        let stripKnown (value: string) =
            (value, suffixTexts)
            ||> List.fold (fun current text ->
                current
                    .Replace(cursorGuidanceSeparator + systemBlock text, "", StringComparison.Ordinal)
                    .Replace(cursorGuidanceSeparator + text, "", StringComparison.Ordinal))

        match terminalTextPartIndex parts with
        | Some index ->
            let originalPart = parts.[index]
            let originalText = partText originalPart
            let clonedPart = emitJsExpr originalPart "Object.assign({}, $0)"
            clonedPart?text <- box (stripKnown originalText)
            let clonedParts = Array.copy parts
            clonedParts.[index] <- clonedPart
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?parts <- box clonedParts
            clonedMessage
        | None -> stripFallbackUserText stripKnown rawMsg

    let stripCursorSuffixes (suffixTexts: string list) (rawMsg: obj) : obj =
        let parts = rawParts rawMsg

        match parts |> Array.mapi terminalGuidanceIndex |> Array.choose id |> Array.tryLast with
        | Some index ->
            let clonedParts = Array.copy parts
            clonedParts.[index] <- stripKnownCursorSuffixes suffixTexts parts.[index]
            let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
            clonedMessage?parts <- box clonedParts
            clonedMessage
        | None when messageRole rawMsg = "user" -> stripUserMessageCursorSuffixes suffixTexts rawMsg
        | None -> rawMsg

    type private CursorPresentation =
        { GuidanceBytes: string
          RequirementBytes: string }

    type private CursorResultIdentity =
        { MessageId: TranscriptMessageAddress
          PartId: HostToolPartId option
          CallId: ToolCallId }

    type private CapturedCursorResult =
        { Body: string
          Presentations: CursorPresentation list }

    type private CapturedCursorResults =
        { HasTrace: bool
          Results: Map<CursorResultIdentity, CapturedCursorResult> }

    let private cursorPartId (part: obj) =
        if isNull part?id then
            Some None
        elif isString part?id then
            Some(Some(HostToolPartId.create (unbox<string> part?id)))
        else
            None

    let private cursorResultIdentity (rawMsg: obj) =
        let parts = rawParts rawMsg

        parts
        |> Array.mapi terminalGuidanceIndex
        |> Array.choose id
        |> Array.tryLast
        |> Option.filter (fun index -> isString rawMsg?info?id && isString parts.[index]?callID)
        |> Option.bind (fun index ->
            cursorPartId parts.[index]
            |> Option.map (fun partId ->
                index,
                { MessageId = TranscriptMessageAddress.create (unbox<string> rawMsg?info?id)
                  PartId = partId
                  CallId = ToolCallId.create (unbox<string> parts.[index]?callID) }))

    let private cursorPresentationPrefixes
        (guidance: PairProgrammingGuideline list)
        (occurrences: RequirementGroundingOccurrence list)
        =
        let guidanceBytes =
            guidance
            |> List.map (fun pair -> cursorGuidanceSeparator + pair.MarkerText)
            |> String.concat ""

        occurrences
        |> List.scan
            (fun prefix occurrence ->
                let bytes =
                    occurrence.Reads
                    |> List.map (fun read -> cursorGuidanceSeparator + read.CursorResultBytes)
                    |> String.concat ""

                prefix + bytes)
            ""
        |> List.collect (fun reads ->
            [ { GuidanceBytes = guidanceBytes
                RequirementBytes = reads }
              { GuidanceBytes = ""
                RequirementBytes = reads } ])

    let private presentationsAtMessage (session: SessionAgentProjection option) (rawMsg: obj) =
        let atMessage gap =
            match gap with
            | TranscriptGap.After address ->
                isString rawMsg?info?id
                && TranscriptMessageAddress.value address = unbox<string> rawMsg?info?id
            | _ -> false

        match session with
        | None -> []
        | Some state ->
            let guidance = state.Guidelines |> Option.defaultValue GuidelineProjection.empty

            let grounding =
                state.RequirementGrounding
                |> Option.defaultValue RequirementGroundingProjection.empty

            let atAnchor (pairs: PairProgrammingGuideline list) (occurrences: RequirementGroundingOccurrence list) =
                cursorPresentationPrefixes
                    (pairs |> List.filter (fun pair -> atMessage pair.ResultGap))
                    (occurrences |> List.filter (fun occurrence -> atMessage occurrence.ResultGap))

            atAnchor (GuidelineProjection.pairs guidance) (RequirementGroundingProjection.occurrences grounding)
            @ atAnchor
                (GuidelineProjection.visiblePairs guidance)
                (RequirementGroundingProjection.visibleOccurrences grounding)
            |> List.distinct

    let private captureCursorResult journal trace session (rawMsg: obj) =
        let capture identity =
            taskResult {
                let! body =
                    XTraceMaterialization.tryHostToolResult
                        journal
                        identity.MessageId
                        identity.PartId
                        identity.CallId
                        trace

                return
                    body
                    |> Option.map (fun original ->
                        identity,
                        { Body = original
                          Presentations = presentationsAtMessage session rawMsg })
            }

        cursorResultIdentity rawMsg
        |> Option.map (snd >> capture)
        |> Option.defaultWith (fun () -> Task.FromResult(Ok None))

    let private captureCursorResults journal sessionId rawMessages =
        let session =
            AgentJournal.snapshot journal
            |> fun projection -> AgentProjection.tryFind sessionId projection.AgentProjections

        let capture trace =
            taskResult {
                let! captured =
                    rawMessages
                    |> TaskResultList.traverseM (captureCursorResult journal trace session)

                return
                    { HasTrace = true
                      Results = captured |> List.choose id |> Map.ofList }
            }

        session
        |> Option.bind _.XTrace
        |> Option.map capture
        |> Option.defaultWith (fun () ->
            Task.FromResult(
                Ok
                    { HasTrace = false
                      Results = Map.empty }
            ))

    let private findCapturedCursorResult captures rawMsg =
        cursorResultIdentity rawMsg
        |> Option.bind (fun (index, identity) ->
            captures.Results
            |> Map.tryFind identity
            |> Option.map (fun result -> index, result))

    let private mapCapturedResult index mapText (rawMsg: obj) =
        let clonedParts = Array.copy (rawParts rawMsg)
        clonedParts.[index] <- mapTerminalResultText mapText clonedParts.[index]
        let clonedMessage = emitJsExpr rawMsg "Object.assign({}, $0)"
        clonedMessage?parts <- box clonedParts
        clonedMessage

    let private matchingPresentations captured value =
        captured.Presentations
        |> List.filter (fun presentation ->
            value = captured.Body + presentation.GuidanceBytes + presentation.RequirementBytes)

    let private stripCapturedPresentation owner captured value =
        let retain presentation =
            match owner with
            | CursorPresentationOwner.PairGuidance -> captured.Body + presentation.RequirementBytes
            | CursorPresentationOwner.RequirementGrounding -> captured.Body + presentation.GuidanceBytes

        match matchingPresentations captured value |> List.map retain |> List.distinct with
        | [] -> Ok value
        | [ retained ] -> Ok retained
        | _ -> Error "Cursor presentation ownership is ambiguous at the captured Host result"

    let private stripCapturedResult captures owner suffixTexts rawMsg =
        let strip (index, captured) =
            let originalPart = (rawParts rawMsg).[index]
            let state = originalPart?state

            let value =
                if partStatus originalPart = Some "error" && isString state?error then
                    unbox<string> state?error
                else
                    unbox<string> state?output

            stripCapturedPresentation owner captured value
            |> Result.map (fun retained -> mapCapturedResult index (fun _ -> retained) rawMsg)

        if not captures.HasTrace || messageRole rawMsg = "user" then
            Ok(stripCursorSuffixes suffixTexts rawMsg)
        else
            findCapturedCursorResult captures rawMsg
            |> Option.map strip
            |> Option.defaultValue (Ok rawMsg)

    let stripCursorSuffixesWithJournal
        (journal: AgentJournal)
        (sessionId: SessionId)
        (owner: CursorPresentationOwner)
        (suffixTexts: string list)
        (rawMessages: obj list)
        : Task<Result<obj list, string>> =
        taskResult {
            let! captured = captureCursorResults journal sessionId rawMessages

            return!
                rawMessages
                |> List.traverseResultM (stripCapturedResult captured owner suffixTexts)
        }

    let private appendCursorGuidanceToTerminalToolResult (markerTexts: string list) (rawMsg: obj) : obj option =
        appendCursorSuffixes markerTexts rawMsg

    let private gapPresent (addressSet: Set<string>) (gap: TranscriptGap) =
        match gap with
        | TranscriptGap.Start -> true
        | TranscriptGap.Before address
        | TranscriptGap.After address -> Set.contains (TranscriptMessageAddress.value address) addressSet

    let private bucketAdd
        (table: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (address: TranscriptMessageAddress)
        (pair: PairProgrammingGuidelineWire)
        =
        let key = TranscriptMessageAddress.value address

        match table.TryGetValue key with
        | true, entries -> entries.Add pair
        | false, _ ->
            // DSL-MUTABLE: algorithm-scratch — new entry list for dictionary insert
            let entries = ResizeArray<PairProgrammingGuidelineWire>()
            entries.Add pair
            table.[key] <- entries

    let private addAtGap
        (starts: ResizeArray<PairProgrammingGuidelineWire>)
        (before: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (after: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (pair: PairProgrammingGuidelineWire)
        (gap: TranscriptGap)
        =
        match gap with
        | TranscriptGap.Start -> starts.Add pair
        | TranscriptGap.Before address -> bucketAdd before address pair
        | TranscriptGap.After address -> bucketAdd after address pair

    let private placeCursorResultGap
        (cursorAfter: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (pair: PairProgrammingGuidelineWire)
        =
        match pair.ResultGap with
        | TranscriptGap.After address -> bucketAdd cursorAfter address pair
        | _ -> ()

    let private registerPlaceablePair
        (providerId: string option)
        (starts: ResizeArray<PairProgrammingGuidelineWire>)
        (before: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (after: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (cursorAfter: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (pair: PairProgrammingGuidelineWire)
        =
        if isCursorProvider providerId then
            placeCursorResultGap cursorAfter pair
        else
            addAtGap starts before after pair pair.ResultGap

    let private ordered (entries: ResizeArray<PairProgrammingGuidelineWire>) =
        entries |> Seq.sortBy (fun pair -> pair.Ordinal) |> Seq.toList

    let private emitPairs (output: ResizeArray<obj>) (entries: ResizeArray<PairProgrammingGuidelineWire>) =
        for pair in ordered entries do
            output.Add(buildPairMessage pair.CallId pair.MarkerText)

    let private tryBucket (table: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>) (address: string) =
        match table.TryGetValue address with
        | true, entries -> Some entries
        | _ -> None

    let private emitBucketPairs
        (output: ResizeArray<obj>)
        (table: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (address: string)
        =
        tryBucket table address |> Option.iter (emitPairs output)

    let private terminalResultText (part: obj) =
        let state = part?state

        if partStatus part = Some "error" && isString state?error then
            tryUnboxString state?error
        else
            tryUnboxString state?output

    let private projectCapturedGuidance captures markerTexts message =
        let insert (index, captured) =
            let guidanceBytes =
                markerTexts
                |> List.map (fun text -> cursorGuidanceSeparator + text)
                |> String.concat ""

            let present value =
                captured.Presentations
                |> List.tryFind (fun presentation ->
                    presentation.GuidanceBytes = ""
                    && value = captured.Body + presentation.RequirementBytes)
                |> Option.map (fun presentation -> captured.Body + guidanceBytes + presentation.RequirementBytes)

            rawParts message
            |> fun parts -> terminalResultText parts.[index]
            |> Option.bind present
            |> Option.map (fun output -> mapCapturedResult index (fun _ -> output) message)

        findCapturedCursorResult captures message |> Option.bind insert

    let private projectGuidanceToMessage captures (markerTexts: string list) (message: obj) : obj =
        if messageRole message = "user" then
            appendCursorGuidanceToUserMessage markerTexts message
            |> Option.defaultValue message
        else
            projectCapturedGuidance captures markerTexts message
            |> Option.orElseWith (fun () -> appendCursorGuidanceToTerminalToolResult markerTexts message)
            |> Option.defaultValue message

    let private projectCursorMessage
        captures
        (cursorAfter: Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>)
        (address: string)
        (message: obj)
        =
        match tryBucket cursorAfter address with
        | None -> message
        | Some entries ->
            entries
            |> Seq.sortBy (fun pair -> pair.Ordinal)
            |> Seq.map (fun pair -> pair.MarkerText)
            |> Seq.toList
            |> fun markerTexts -> projectGuidanceToMessage captures markerTexts message

    let private replayAddressed
        captures
        (providerId: string option)
        (addressed: (string * obj) list)
        (pairs: PairProgrammingGuidelineWire list)
        : obj list =
        let addressSet = addressed |> List.map fst |> Set.ofList

        // Both durable anchors must remain present even though ordinary and
        // Cursor only render at ResultGap. CallGap stays durable for placement
        // identity and reversible Cursor → ordinary replay.
        let placeable =
            pairs
            |> List.filter (fun pair -> gapPresent addressSet pair.CallGap && gapPresent addressSet pair.ResultGap)

        // DSL-MUTABLE: algorithm-scratch — placeable pair start accumulator
        let starts = ResizeArray<PairProgrammingGuidelineWire>()
        // DSL-MUTABLE: algorithm-scratch — before-gap pair bucket
        let before = Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>()
        // DSL-MUTABLE: algorithm-scratch — after-gap pair bucket
        let after = Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>()
        // DSL-MUTABLE: algorithm-scratch — cursor result-gap pair bucket
        let cursorAfter = Dictionary<string, ResizeArray<PairProgrammingGuidelineWire>>()

        for pair in placeable do
            registerPlaceablePair providerId starts before after cursorAfter pair

        // DSL-MUTABLE: algorithm-scratch — output pair accumulator
        let output = ResizeArray<obj>()
        emitPairs output starts

        for address, message in addressed do
            emitBucketPairs output before address
            output.Add(projectCursorMessage captures cursorAfter address message)
            emitBucketPairs output after address

        Seq.toList output

    let private replay
        captures
        (providerId: string option)
        (realMessages: obj list)
        (pairs: PairProgrammingGuidelineWire list)
        : Result<obj list, string> =
        addressedRealMessages realMessages
        |> Result.map (fun addressed -> replayAddressed captures providerId addressed pairs)

    // ── 本轮新 pair 的 placement（只读当前真实消息）──────────────────────────

    // semantic-decorator-owner: guidance-delivery
    // semantic-decorator-WHAT: guidance-delivery-011
    // semantic-decorator-trace-relation: R_gap_pair(address) = (gapCtor address, gapCtor address), left then right, with a pure extensional constructor
    // semantic-decorator-proof: requirements/guidance-delivery/tests/pair-gap-constructor.test.mjs::WHAT[guidance-delivery-011] PPT_gap_constructor_receives_the_same_address_exactly_twice_in_pair_order
    // semantic-decorator-failure-policy: a constructor exception propagates immediately and prevents the second invocation
    // semantic-decorator-cancel-policy: pure synchronous gap construction introduces no cancellation boundary
    // semantic-decorator-deadline-policy: pure synchronous gap construction introduces no deadline
    // semantic-decorator-retry-bound: 2
    let internal gapsAroundAddress
        (gapCtor: TranscriptMessageAddress -> TranscriptGap)
        (message: obj)
        (errorMsg: string)
        =
        match ProviderWireDecode.hostMessageId message with
        | Some id ->
            let address = TranscriptMessageAddress.create id
            Ok(gapCtor address, gapCtor address)
        | None -> Error errorMsg

    /// A message hosts a cursor guidance carrier when the replay renderer can
    /// actually change its bytes: user messages through a terminal text part,
    /// every other role through a terminal tool result (HOST-013). A placement
    /// may only be anchored where the renderer produces bytes — otherwise the
    /// occurrence is committed but never rendered.
    let private messageCarriesGuidance (rawMsg: obj) : bool =
        if messageRole rawMsg = "user" then
            terminalTextPartIndex (rawParts rawMsg) |> Option.isSome
        else
            rawParts rawMsg
            |> Array.mapi terminalGuidanceIndex
            |> Array.exists Option.isSome

    /// 本轮新 occurrence 的 placement：**最新一条真实消息**，且只有它承载
    /// cursor 呈现时才有 placement；否则本轮不新增 occurrence。
    ///
    /// 旧实现按「有无 tool batch / 是否 trailing user」分支，产出的
    /// `Before(user)`、`After(assistant 文本)` 形状 cursor 渲染器不认，于是
    /// durable occurrence 记了却一个字节也不出现（首轮之后的纯用户轮次、重锚
    /// 后的空窗）。锚定最新消息既保证字节一定落在本次追加区（append-only），
    /// 又保证落点一定是渲染器真会改写的消息。
    ///
    /// 空 transcript 无 placement；最新消息不是载体（例如末尾是纯 assistant
    /// 文本）本轮不生成 occurrence，等下一次出现载体的请求再追加。
    let decideCurrentPlacement (realMessages: obj list) : Result<(TranscriptGap * TranscriptGap) option, string> =
        match List.rev realMessages with
        | [] -> Ok None
        | last :: _ when messageCarriesGuidance last ->
            gapsAroundAddress TranscriptGap.After last "guidance carrier without transcript address (HOST-013)"
            |> Result.map Some
        | _ -> Ok None

    // ── durable / memory history ─────────────────────────────────────────────

    let private readDurableHistory (journal: AgentJournal) (sessionId: SessionId) : PairProgrammingGuidelineWire list =
        match AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections with
        | None -> []
        | Some session ->
            session.Guidelines
            |> Option.map GuidelineProjection.pairs
            |> Option.defaultValue []
            |> List.map (fun pair ->
                { Ordinal = pair.Ordinal
                  CallId = ToolCallId.value pair.CallId
                  MarkerText = pair.MarkerText
                  CallGap = pair.CallGap
                  ResultGap = pair.ResultGap
                  ConcernPlacement = None })

    let private readDurableVisibleHistory
        (journal: AgentJournal)
        (sessionId: SessionId)
        : PairProgrammingGuidelineWire list =
        match AgentProjection.tryFind sessionId (AgentJournal.snapshot journal).AgentProjections with
        | None -> []
        | Some session ->
            session.Guidelines
            |> Option.map GuidelineProjection.visiblePairs
            |> Option.defaultValue []
            |> List.map (fun pair ->
                { Ordinal = pair.Ordinal
                  CallId = ToolCallId.value pair.CallId
                  MarkerText = pair.MarkerText
                  CallGap = pair.CallGap
                  ResultGap = pair.ResultGap
                  ConcernPlacement = None })

    let private readMemoryHistory (key: string) : PairProgrammingGuidelineWire list =
        match memoryLedger.TryGetValue key with
        | true, pairs -> pairs |> Seq.toList
        | false, _ -> []

    let private appendMemory (key: string) (pair: PairProgrammingGuidelineWire) : Result<unit, string> =
        match memoryLedger.TryGetValue key with
        | true, pairs -> pairs.Add pair
        | false, _ ->
            // DSL-MUTABLE: algorithm-scratch — new pair list for dictionary insert
            let pairs = ResizeArray<PairProgrammingGuidelineWire>()
            pairs.Add pair
            memoryLedger.[key] <- pairs

        Ok()

    let private appendDurable
        (journal: AgentJournal)
        (sessionId: SessionId)
        (pair: PairProgrammingGuidelineWire)
        : Task<Result<unit, string>> =
        task {
            let hostConcernPlacement =
                pair.ConcernPlacement
                |> Option.map (fun batch ->
                    { Wanxiangshu.Host.ConcernPlacementBatch.AnnouncedGenerations = batch.AnnouncedGenerations
                      Wanxiangshu.Host.ConcernPlacementBatch.DeliveredMessages = batch.DeliveredMessages })

            let fact =
                HostFact.PairProgrammingGuidelineAnchored
                    {| SessionId = sessionId
                       Ordinal = pair.Ordinal
                       CallId = ToolCallId.create pair.CallId
                       MarkerText = pair.MarkerText
                       CallGap = pair.CallGap
                       ResultGap = pair.ResultGap
                       ConcernPlacement = hostConcernPlacement |}

            match! AgentJournal.appendAgent (StreamId.Session sessionId) None fact journal with
            | Ok _ -> return Ok()
            | Error failure -> return Error(JournalAppendFailure.describe failure)
        }

    let private nextGuidelineOrdinal (history: PairProgrammingGuidelineWire list) =
        match history with
        | [] -> 1L
        | pairs -> (List.last pairs).Ordinal + 1L

    let private findPlacement
        (history: PairProgrammingGuidelineWire list)
        (callGap: TranscriptGap)
        (resultGap: TranscriptGap)
        =
        history
        |> List.tryFind (fun pair -> pair.CallGap = callGap && pair.ResultGap = resultGap)

    let private commitPairInjection
        (replayMessages: obj list -> PairProgrammingGuidelineWire list -> Result<obj list, string>)
        (providerId: string option)
        (history: PairProgrammingGuidelineWire list)
        (visibleHistory: PairProgrammingGuidelineWire list)
        (append: PairProgrammingGuidelineWire -> Task<Result<unit, string>>)
        (sessionId: string option)
        (markerText: string)
        (concernPlacement: ConcernPlacementBatch option)
        (realMessages: obj list)
        (callGap: TranscriptGap)
        (resultGap: TranscriptGap)
        : Task<Result<obj list * bool, string>> =
        taskResult {
            let existing = findPlacement visibleHistory callGap resultGap

            if Option.isSome existing || skipAutoInjectedRequested providerId then
                let! replayed = replayMessages realMessages visibleHistory
                return (replayed, false)
            else
                let ordinal = nextGuidelineOrdinal history

                let candidate =
                    { Ordinal = ordinal
                      CallId = stableCallId sessionId ordinal
                      MarkerText = markerText
                      CallGap = callGap
                      ResultGap = resultGap
                      ConcernPlacement = concernPlacement }

                let! rendered = replayMessages realMessages (visibleHistory @ [ candidate ])
                do! append candidate
                return (rendered, true)
        }

    let private sessionRole (journal: AgentJournal option) (sessionId: SessionId) : Role option =
        journal
        |> Option.bind (fun durable ->
            let projections = (AgentJournal.snapshot durable).AgentProjections

            PromptAuthorityProjectionQueries.activeProfile sessionId projections
            |> Option.orElseWith (fun () ->
                PromptAuthorityProjectionQueries.lastAuthorityProfile sessionId projections)
            |> Option.map (fun profile -> profile.CanonicalRole)
            |> Option.orElseWith (fun () ->
                projections.HandleByChildSession
                |> Map.tryFind sessionId
                |> Option.map (fun handle -> handle.CanonicalRole)))

    let private isInternalRole (roleOpt: Role option) =
        roleOpt |> Option.exists Roles.isInternal

    let private messageRoleIsInternal (msg: obj) : bool =
        if isNull msg then
            false
        else
            let agent =
                messageInfo msg
                |> Option.bind (fun info -> tryUnboxString info?agent)
                |> Option.orElseWith (fun () -> tryUnboxString msg?agent)

            let role =
                agent
                |> Option.bind HostSessionContext.roleOf
                |> Option.orElseWith (fun () ->
                    messageInfo msg
                    |> Option.bind (fun info -> tryUnboxString info?role)
                    |> Option.orElseWith (fun () -> tryUnboxString msg?role)
                    |> Option.bind Roles.tryParseRole)

            isInternalRole role

    let private isInternalSessionOrMessage
        (journal: AgentJournal option)
        (sessionId: string option)
        (rawMessages: obj list)
        =
        match journal, sessionId with
        | Some durable, Some sid when not (String.IsNullOrWhiteSpace sid) ->
            let session = SessionId.create sid
            let snapshot = AgentJournal.snapshot durable

            SessionAssociationProjection.isCompanion session snapshot.AgentProjections.Associations
            || isInternalRole (sessionRole journal session)
            || (rawMessages |> List.exists messageRoleIsInternal)
        | _ -> rawMessages |> List.exists messageRoleIsInternal

    let private commitCurrentPairPlacement
        (replayMessages: obj list -> PairProgrammingGuidelineWire list -> Result<obj list, string>)
        providerId
        history
        visibleHistory
        append
        sessionId
        markerText
        concernPlacement
        realMessages
        =
        taskResult {
            let! placementOpt = decideCurrentPlacement realMessages

            match placementOpt with
            | None ->
                let! replayed = replayMessages realMessages visibleHistory
                return (replayed, false)
            | Some(callGap, resultGap) ->
                return!
                    commitPairInjection
                        replayMessages
                        providerId
                        history
                        visibleHistory
                        append
                        sessionId
                        markerText
                        concernPlacement
                        realMessages
                        callGap
                        resultGap
        }

    // ── 入口 ─────────────────────────────────────────────────────────────────

    /// HOST-013 commit 顺序（fail closed）：
    ///
    /// 1. 读 durable history
    /// 2. 内存 strip（只有 durable 记录能解释的 synthetic 才允许删除）
    /// 3. 决定本轮候选 placement（仅当该 placement 尚不存在）
    /// 4. 内存构造候选 fact
    /// 5. 内存渲染完整 wire（replay，校验全部 anchor）
    /// 6. append durable fact —— 失败 fail closed，禁止忽略后照发
    /// 7. 返回已校验的渲染消息
    ///
    /// 同一 placement 重复进入只 replay，不 append 新 fact。
    let private tryInjectCore
        (journal: AgentJournal option)
        (sessionId: string option)
        (markerText: string)
        (concernPlacement: ConcernPlacementBatch option)
        (rawMessages: obj list)
        : Task<Result<obj list * bool, string>> =
        taskResult {
            let key = transcriptKey sessionId

            let history, visibleHistory, append =
                match journal, sessionId with
                | Some durable, Some sid when not (String.IsNullOrWhiteSpace sid) ->
                    let session = SessionId.create sid

                    readDurableHistory durable session,
                    readDurableVisibleHistory durable session,
                    appendDurable durable session
                | _ ->
                    let memory = readMemoryHistory key
                    memory, memory, (fun pair -> Task.FromResult(appendMemory key pair))

            let suffixes = history |> List.map (fun pair -> pair.MarkerText)

            let! captures =
                match journal, sessionId with
                | Some durable, Some sid -> captureCursorResults durable (SessionId.create sid) rawMessages
                | _ ->
                    Task.FromResult(
                        Ok
                            { HasTrace = false
                              Results = Map.empty }
                    )

            let! rawMessages =
                rawMessages
                |> List.traverseResultM (stripCapturedResult captures CursorPresentationOwner.PairGuidance suffixes)

            let strippedCallIds =
                rawMessages
                |> List.filter isPairProgrammingThought
                |> List.choose syntheticCallIdOf

            let lang =
                if markerText.Contains "以" || markerText.Contains "我" || markerText.Contains "结对" then
                    Some ProviderLanguage.SimplifiedChinese
                else
                    Some ProviderLanguage.English

            let realMessages =
                rawMessages
                |> List.filter (isPairProgrammingThought >> not)
                |> sanitizeActiveToolCalls lang

            let providerId = providerIdFromMessages realMessages
            let replayMessages = replay captures providerId

            let knownCallIds = history |> List.map (fun pair -> pair.CallId) |> Set.ofList

            let orphaned =
                strippedCallIds
                |> List.filter (fun callId -> not (Set.contains callId knownCallIds))

            do!
                Result.requireTrue
                    (sprintf
                        "synthetic messages without durable record (callId %s, HOST-013)"
                        (String.Join(", ", orphaned |> List.truncate 3)))
                    (List.isEmpty orphaned)

            if isInternalSessionOrMessage journal sessionId rawMessages then
                let! replayed = replayMessages realMessages visibleHistory
                return (replayed, false)
            else
                return!
                    commitCurrentPairPlacement
                        replayMessages
                        providerId
                        history
                        visibleHistory
                        append
                        sessionId
                        markerText
                        concernPlacement
                        realMessages
        }

    // ── Pair guideline injection (migrated from PluginTransforms composition root) ──

    type private SessionTermination = SessionId -> string -> Task<Result<unit, string>>

    let private skipPairGuideline (journal: AgentJournal option) (projectionSessionIdOpt: string option) : bool =
        match journal, projectionSessionIdOpt with
        | Some durable, Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
            let sid = SessionId.create sessionId
            let snapshot = AgentJournal.snapshot durable

            SessionAssociationProjection.isCompanion sid snapshot.AgentProjections.Associations
            || isInternalRole (sessionRole journal sid)
        | _ -> false

    let private toolEstimateText
        (journal: AgentJournal option)
        (projectionSessionIdOpt: string option)
        (language: ProviderLanguage)
        : string option =
        match journal, projectionSessionIdOpt with
        | Some durable, Some sessionId ->
            let port = AgentJournalPortAdapter.forDelegatedToolEstimate durable

            DelegatedToolEstimateLedger.tryRemaining port (SessionId.create sessionId)
            |> Option.map (PairProgrammingCalibration.renderToolEstimate language)
        | _ -> None

    /// crash-reconciliation-018: the restart status guidance is one more
    /// instruction of the same marker; it is never a second physical payload.
    let private withRestartGuidance
        (restartGuidance: string option)
        (document: LlmFacing.Document)
        : LlmFacing.Document =
        match restartGuidance with
        | Some text when not (String.IsNullOrWhiteSpace text) -> LlmFacing.withInstruction text document
        | _ -> document

    let private composeMarkerDocument
        (journal: AgentJournal option)
        (projectionSessionIdOpt: string option)
        (elapsed: string option)
        (toolEstimate: string option)
        (guideline: string)
        (restartGuidance: string option)
        : Task<LlmFacing.Document> =
        match journal, projectionSessionIdOpt with
        | Some durable, Some sessionId ->
            task {
                let! guidance = EnforcerTipGuidance.latestTipGuidance durable (SessionId.create sessionId)

                return
                    PairProgrammingCalibration.documentWithElapsed guidance elapsed toolEstimate guideline
                    |> withRestartGuidance restartGuidance
            }
        | _ ->
            Task.FromResult(
                PairProgrammingCalibration.documentWithElapsed None elapsed toolEstimate guideline
                |> withRestartGuidance restartGuidance
            )

    let private concernInstructions language (prepared: ConcernPreparedFragments) =
        let announcements =
            prepared.Announcements
            |> List.map (fun (id, concern) ->
                ProviderProse.render
                    language
                    "concern-routing/subscription-announcement"
                    (Map [ "id", id; "concern", concern ]))

        let messages =
            prepared.Messages
            |> List.map (fun (id, message) ->
                ProviderProse.render language "concern-routing/message-delivery" (Map [ "id", id; "message", message ]))

        match announcements @ messages with
        | [] -> []
        | fragments ->
            ProviderProse.render language "concern-routing/pair-heading" Map.empty
            :: fragments

    let private prepareConcernFragments journal projectionSessionIdOpt language =
        match journal, projectionSessionIdOpt with
        | Some durable, Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
            let recipient = SessionId.create sessionId
            let state = (AgentJournal.snapshot durable).AgentProjections.Concern
            let prepared = ConcernProjection.prepareFragments recipient state

            concernInstructions language prepared, Some prepared.Batch
        | _ -> [], None

    let private terminateSessionIfPresent
        (terminateSession: SessionTermination)
        (projectionSessionIdOpt: string option)
        (reason: string)
        : Task =
        match projectionSessionIdOpt with
        | Some sessionId ->
            task {
                let! _ = terminateSession (SessionId.create sessionId) reason
                ()
            }
        | None -> Task.FromResult()

    let private markDeliveredIfAnchored restartGuidance markRestartGuidanceDelivered anchored =
        // crash-reconciliation-018: the restart guidance is delivered
        // exactly once; a replayed placement never consumes it.
        if anchored && Option.isSome restartGuidance then
            markRestartGuidanceDelivered ()

    let private failClosedInject terminateSession projectionSessionIdOpt reason =
        task {
            Diagnostic.emit
                "host-013-fail-closed"
                [ "session_id", (defaultArg projectionSessionIdOpt ""); "result", reason ]

            do! terminateSessionIfPresent terminateSession projectionSessionIdOpt reason
        }

    let private applyInjectResult
        outObj
        projectionSessionIdOpt
        restartGuidance
        markRestartGuidanceDelivered
        terminateSession
        injectResult
        =
        task {
            match injectResult with
            | Ok(newMessages, anchored) ->
                markDeliveredIfAnchored restartGuidance markRestartGuidanceDelivered anchored
                HostMessageProjection.replaceMessagesInPlace outObj newMessages
            | Error reason -> do! failClosedInject terminateSession projectionSessionIdOpt reason
        }

    let private injectPairProgrammingGuideline
        (journal: AgentJournal option)
        (projectionSessionIdOpt: string option)
        (sessionStartedAt: DateTimeOffset option)
        (clock: IClockPort)
        (terminateSession: SessionTermination)
        (language: ProviderLanguage)
        (restartGuidance: string option)
        (markRestartGuidanceDelivered: unit -> unit)
        (outObj: obj)
        : Task =
        task {
            let messages = unbox<obj array> outObj?messages |> Array.toList

            let guideline = ProviderProse.render language PairProgrammingGuidelinePath Map.empty

            let elapsed =
                sessionStartedAt
                |> Option.map (fun startedAt ->
                    let elapsedMilliseconds = (clock.UtcNow() - startedAt).TotalMilliseconds
                    PairProgrammingCalibration.renderElapsed language elapsedMilliseconds)

            let toolEstimate = toolEstimateText journal projectionSessionIdOpt language

            let! markerDocument =
                composeMarkerDocument journal projectionSessionIdOpt elapsed toolEstimate guideline restartGuidance

            let concernInstructions, concernPlacement =
                prepareConcernFragments journal projectionSessionIdOpt language

            let markerText =
                markerDocument
                |> LlmFacing.withInstructions concernInstructions
                |> LlmFacing.render

            let! injectResult = tryInjectCore journal projectionSessionIdOpt markerText concernPlacement messages

            do!
                applyInjectResult
                    outObj
                    projectionSessionIdOpt
                    restartGuidance
                    markRestartGuidanceDelivered
                    terminateSession
                    injectResult
        }

    let maybeInjectGuideline
        (journal: AgentJournal option)
        (projectionSessionIdOpt: string option)
        (sessionStartedAt: DateTimeOffset option)
        (clock: IClockPort)
        (terminateSession: SessionTermination)
        (language: ProviderLanguage)
        (restartGuidance: string option)
        (markRestartGuidanceDelivered: unit -> unit)
        (outObj: obj)
        : Task =
        if skipPairGuideline journal projectionSessionIdOpt then
            Task.FromResult()
        else
            injectPairProgrammingGuideline
                journal
                projectionSessionIdOpt
                sessionStartedAt
                clock
                terminateSession
                language
                restartGuidance
                markRestartGuidanceDelivered
                outObj

    let tryInject
        (journal: AgentJournal option)
        (sessionId: string option)
        (markerText: string)
        (rawMessages: obj list)
        : Task<Result<obj list, string>> =
        task {
            let! result = tryInjectCore journal sessionId markerText None rawMessages
            return Result.map fst result
        }
