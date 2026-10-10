namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Strength

/// The read-only delegation schema contract and
/// parameter boundary. Schema decoration, budget/note validation, provider
/// argument evidence preservation and bilingual investigation outlook prose live
/// here. This module never creates a child session, sends a provider
/// request, computes the batch max, or writes business events.
module ReadonlyDelegationContract =

    let private roundsField = InvestigationEstimateContract.EstimatedReadonlyRoundsField

    [<Literal>]
    let private noteField = "self_note"

    [<Literal>]
    let private maximumRounds = 2147483647

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isNumberValue (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'undefined'")>]
    let private isUndefinedValue (value: obj) : bool = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private isFiniteNumber (value: obj) : bool = jsNative

    [<Emit("Number.isInteger($0)")>]
    let private isIntegerNumber (value: obj) : bool = jsNative

    [<Emit("Object.keys($0).sort().join(',')")>]
    let private ownKeysSignature (value: obj) : string = jsNative

    // Schema fragments, verbatim.
    [<Literal>]
    let private readonlyRoundsDescriptionEn =
        "Estimate how many consecutive read-only investigation rounds will still be needed after ALL tool calls in this response have completed, before a substantive change, a command, user clarification, a conclusion, or a consequential judgment that you must make yourself. One round is one model request and may contain several parallel tool calls; do not count the current batch. Routine choices about which reference or file to inspect are part of investigation. Use 0 when no such investigation remains or the next step already reaches one of those boundaries. Give your current best estimate; it need not be exact, and do not add work to match it."

    [<Literal>]
    let private selfNoteDescriptionEn =
        "Optional. When this call's estimated_readonly_rounds is greater than 0 you may leave a brief outlook for the next investigation rounds: what evidence or relationships to inspect and what finding will make the next step possible. One to three sentences are enough. Do not provide a progress report, generic filler, instructions to another worker, or a full reasoning trace. Omit it freely."

    [<Literal>]
    let private readonlyRoundsDescriptionZh =
        "当前响应的全部工具执行完成后，预计还需要连续进行多少轮只读查证，才会到达实质修改、执行命令、向用户确认、给出结论，或必须亲自权衡的关键判断？一轮是一次模型请求，可以包含多个并行工具调用；当前这批不计入。选择接着查哪个文件或引用属于普通调查，不必一概当成关键判断。已经没有后续查证，或下一步就到达上述边界时，填 0。按当前材料估计即可，不要求精确，也不要为了符合估计增加调查。"

    [<Literal>]
    let private selfNoteDescriptionZh =
        "可选。本次调用的 estimated_readonly_rounds 大于 0 时，可以用一至三句话给自己留下后续调查的展望：准备核对哪些材料或关系，什么证据出现后可以进入下一步。不要写完成情况、泛泛感想、对其他执行者的指令或完整思考过程。不填也完全可以。"

    // Short outlook prose, verbatim.
    [<Literal>]
    let private englishInvestigationOutlook =
        "Investigation outlook: estimated_readonly_rounds estimates the consecutive read-only investigation rounds after the current batch. self_note is an optional brief outlook of what to inspect next; fill it or not as it helps you."

    [<Literal>]
    let private chineseInvestigationOutlook =
        "调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数。self_note 是可选短记，简述接下来查什么、查到什么即可进入下一步；填与不填都行。"

    [<Literal>]
    let private chineseInvestigationOutlookAlt =
        "调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数；self_note 可选地简述下一步核对目标。"

    // Legacy v1 investigation outlook blocks.
    // Kept strictly for internal backward-compatibility stripping in stripInvestigationOutlookBlocks;
    // never published as current model-visible description.
    [<Literal>]
    let private legacyEnglishInvestigationOutlookBlock =
        "Fill in delegate_readonly_rounds on every tool call. After all calls in this response complete, the maximum value grants your companion up to that many read-only model requests. A 0 does not veto a positive value elsewhere in the batch. One request may call several tools in parallel. Your companion sees the current conversation, may stop early, and returns actual tool calls and results. Use 0 when you need to retain control. Do not invent work to fill the budget; using it up does not require an edit.\n\nWorking together is a way to learn about each other and build trust. Judge by the investigation you can actually see in the conversation. When your companion does well, trust him more and let him do as much of the work as your judgment supports. When he struggles in particular situations, delegate less or not at all in similar situations next time. When in doubt, estimating how many steps remain before the first edit can give you a starting point. This is only a heuristic baseline, not an optimal policy or a substitute for judgment, and it does not require an eventual edit.\n\nOptionally leave a brief self_note in the first person, such as \u201cI suspect the caller and entry point disagree about empty values; I want to check the callers next.\u201d Your companion will naturally see it in the conversation. Omit it when it adds nothing. A short intention or uncertainty is enough; do not write a full analysis or present a guess as a fact."

    [<Literal>]
    let private legacyChineseInvestigationOutlookBlock =
        "每个工具调用都要填写 delegate_readonly_rounds。当前响应的全部工具完成后，系统取这些值的最大值，把接下来的至多这么多轮只读调查交给你的同伴。0 不会否决同批其他调用的正数。一轮是一次模型请求，可以并行调用多个工具。同伴看到当前对话，只能只读，可随时提前结束；真实工具调用与结果会回到你的上下文。需要亲自判断时填 0；不要为了用满预算增加调查，预算到期也不要求立即修改代码。\n\n和同伴合作，也是逐渐了解彼此、建立信任的过程。根据你在对话中实际看见的调查表现判断：他做得好，就尽可能多信任他、多让他工作；他不善于某些状况，下次遇到类似状况就适当少用或不用。当拿不准时，可以先估计距离第一次 edit 还有几步，作为抛砖引玉的基线。这只是启发式方法，不代表最优，不能代替实际判断，也不要求你最终一定 edit。\n\n需要留下当前意图或线索时，可选填 self_note，用第一人称给自己留一句短记，例如“我怀疑入口与调用方对空值的约定不同，接下来先核对调用点”。同伴会在对话中自然看见它。没有有用的话就省略；不必写完整分析，也不要把猜测写成事实。"

    /// JS 边界先检查原生 number、有限值、整数和范围，再构造 F# 类型。
    /// 绝不使用 parseInt、字符串强转或 truthy 判断代替验证。
    let private createRoundBudget (numeric: float) =
        match ReadonlyRoundBudget.tryCreate (int numeric) with
        | Ok budget -> Ok budget
        | Error message -> Error message

    let private validateNumericBudget (numeric: float) =
        if numeric < 0.0 then
            Error "estimated_readonly_rounds-negative"
        elif numeric > 2147483647.0 then
            Error "estimated_readonly_rounds-out-of-range"
        else
            createRoundBudget numeric

    let tryReadonlyRoundBudget (value: obj) : Result<ReadonlyRoundBudget, string> =
        if isUndefinedValue value then
            Error "estimated_readonly_rounds-missing"
        elif isNull value then
            Error "estimated_readonly_rounds-null"
        elif not (isNumberValue value) then
            Error "estimated_readonly_rounds-not-number"
        elif not (isFiniteNumber value) then
            Error "estimated_readonly_rounds-not-finite"
        elif not (isIntegerNumber value) then
            Error "estimated_readonly_rounds-not-integer"
        else
            let numeric = unbox<float> value
            validateNumericBudget numeric

    /// host-boundary-032: one static stash definition for the read-only
    /// delegation estimate fields. Deletion-failure compensation stays off:
    /// a failed field deletion leaves the saved record for the caller's
    /// same-source restore, matching the current behavior.
    let private stash: ProtocolArgumentStash.StashSpec =
        { Symbol = emitJsExpr () "Symbol('readonly-delegation-args')"
          Fields =
            [ { Name = roundsField
                SavedKey = "rounds" }
              { Name = noteField; SavedKey = "note" } ]
          ReappearanceFields = [ roundsField; noteField; "delegate_readonly_rounds" ]
          HoldMessage = "Tool arguments cannot hold or modify the readonly delegation fields"
          RestoreOrderMessage = "Tool arguments cannot restore the readonly delegation fields' original key order"
          RestoreFrozenMessage = "Tool arguments are frozen or not extensible during readonly delegation restore"
          FieldDeleteFailure = fun field -> sprintf "Tool arguments cannot hide the readonly delegation field %s" field
          RestoreDeleteFailure =
            fun field -> sprintf "Tool arguments cannot hide the readonly delegation field %s" field
          SymbolDeleteFailure = "Failed to delete the saved readonly delegation key"
          CompensateDeleteFailure = false }

    let hide (args: obj) : unit = ProtocolArgumentStash.hide stash args

    let hideForCall (owner: ProtocolArgumentCall option) (args: obj) : unit =
        ProtocolArgumentStash.hideForCall stash owner args

    let classifyHiddenArguments (owner: ProtocolArgumentCall option) (args: obj) : HiddenProtocolArguments =
        ProtocolArgumentStash.classifyHiddenArguments stash owner args

    let restore (args: obj) : unit =
        ProtocolArgumentStash.restore stash args

    let restoreForCall (owner: ProtocolArgumentCall option) (args: obj) : unit =
        ProtocolArgumentStash.restoreForCall stash owner args

    let private currentLanguage () =
        Wanxiangshu.Participant.Provider.GlobalProviderLanguage.current ()

    let private budgetProperty () : obj =
        let desc =
            match currentLanguage () with
            | ProviderLanguage.SimplifiedChinese -> readonlyRoundsDescriptionZh
            | _ -> readonlyRoundsDescriptionEn

        createObj
            [ "type", box "integer"
              "minimum", box 0
              "maximum", box maximumRounds
              "description", box desc ]

    let private noteProperty () : obj =
        let desc =
            match currentLanguage () with
            | ProviderLanguage.SimplifiedChinese -> selfNoteDescriptionZh
            | _ -> selfNoteDescriptionEn

        createObj [ "type", box "string"; "description", box desc ]

    let private isSameBudgetProperty (value: obj) : bool =
        not (isNull value)
        && isPlainObject value
        && ownKeysSignature value = "description,maximum,minimum,type"
        && string value?``type`` = "integer"
        && unbox<float> value?minimum = 0.0
        && unbox<float> value?maximum = 2147483647.0
        && (string value?description = readonlyRoundsDescriptionEn
            || string value?description = readonlyRoundsDescriptionZh)

    let private isSameNoteProperty (value: obj) : bool =
        not (isNull value)
        && isPlainObject value
        && ownKeysSignature value = "description,type"
        && string value?``type`` = "string"
        && (string value?description = selfNoteDescriptionEn
            || string value?description = selfNoteDescriptionZh)

    let private ensureBudgetProperty (properties: obj) (toolId: string) : unit =
        let existing = properties?(roundsField)

        if isNull existing then
            properties?(roundsField) <- budgetProperty ()
        elif not (isSameBudgetProperty existing) then
            raise (
                InvalidOperationException(
                    sprintf
                        "Tool %s defines a conflicting %s property that differs from the readonly delegation protocol"
                        toolId
                        roundsField
                )
            )

    let private ensureNoteProperty (properties: obj) (toolId: string) : unit =
        let existing = properties?(noteField)

        if isNull existing then
            properties?(noteField) <- noteProperty ()
        elif not (isSameNoteProperty existing) then
            raise (
                InvalidOperationException(
                    sprintf
                        "Tool %s defines a conflicting %s property that differs from the readonly delegation protocol"
                        toolId
                        noteField
                )
            )

    let private appendBudgetIfMissing (reqArr: obj array) : obj array =
        let exists = reqArr |> Array.exists (fun x -> string x = roundsField)

        if exists then
            reqArr
        else
            Array.append reqArr [| box roundsField |]

    /// Only the budget joins required; the note never does. Original required
    /// entries are preserved. A non-array required fails loudly.
    let private ensureRequiredBudget (schemaObj: obj) (toolId: string) : unit =
        let required = schemaObj?required

        if isNull required then
            schemaObj?required <- box [| box roundsField |]
        elif isArray required then
            schemaObj?required <- box (appendBudgetIfMissing (unbox<obj array> required))
        else
            raise (InvalidOperationException(sprintf "Tool %s parameters schema required field is not an array" toolId))

    /// Decorate one root schema view. The original composition structure
    /// ($ref/oneOf/nullable/strict) is preserved; only properties and
    /// required are extended.
    let private decorateRootSchema (schemaObj: obj) (toolId: string) : unit =
        if isNull schemaObj || not (isPlainObject schemaObj) then
            raise (InvalidOperationException(sprintf "Tool %s schema is not a plain object schema" toolId))

        let properties = schemaObj?properties

        if isNull properties || not (isPlainObject properties) then
            raise (InvalidOperationException(sprintf "Tool %s parameters schema missing object properties" toolId))

        ensureBudgetProperty properties toolId
        ensureNoteProperty properties toolId
        ensureRequiredBudget schemaObj toolId

    let private stripInvestigationOutlookBlocks (text: string) : string =
        text
            .Replace(englishInvestigationOutlook, "")
            .Replace(chineseInvestigationOutlook, "")
            .Replace(chineseInvestigationOutlookAlt, "")
            .Replace(legacyEnglishInvestigationOutlookBlock, "")
            .Replace(legacyChineseInvestigationOutlookBlock, "")
            .TrimEnd('\n')

    let private appendInvestigationOutlookDescription (toolOutput: obj) : unit =
        if not (isPlainObject toolOutput) then
            raise (InvalidOperationException "Tool definition output must be an object")

        let current =
            if isNull toolOutput?description then
                ""
            else
                string toolOutput?description

        let language = currentLanguage ()

        let block =
            match language with
            | ProviderLanguage.SimplifiedChinese -> chineseInvestigationOutlook
            | _ -> englishInvestigationOutlook

        toolOutput?description <- stripInvestigationOutlookBlocks current + "\n\n" + block

    /// A definition that already states its provider view as `properties` on
    /// `parameters` needs no Effect rendering; it is decorated where it stands
    /// and published as the JSON schema so the Host sends exactly these bytes.
    [<Emit("Boolean($0 && typeof $0 === 'object' && !Array.isArray($0) && (($0.properties && typeof $0.properties === 'object' && !Array.isArray($0.properties)) || $0.type === 'object'))")>]
    let private parametersHoldSchemaView (parameters: obj) : bool = jsNative

    /// The Host renders the provider-visible schema itself for any tool whose
    /// definition carries an Effect argument schema (`ToolJsonSchema.fromTool`:
    /// a stated JSON schema, otherwise `Schema.toJsonSchemaDocument` of
    /// `parameters`, opencode 1.18.32 `src/tool/json-schema.ts`). Decoration
    /// therefore publishes through the JSON schema view, whichever shape the
    /// definition arrives in:
    ///
    ///  - a stated `jsonSchema` (plugin tools such as the engineering surfaces)
    ///    is decorated where it stands;
    ///  - `parameters` that already carries `properties` is that view itself;
    ///    it is decorated in place and published as the JSON schema;
    ///  - otherwise `parameters` is the Effect argument schema of a Host
    ///    built-in (read, edit, question, …) and the JSON schema is rendered
    ///    with the same conversion the Host applies.
    ///
    /// The Effect schema is never modified — the Host decodes the real
    /// arguments with it, and `tool.execute.before` hides the protocol fields
    /// before that decode.
    let private applyToolDecoration
        (toolOutput: obj)
        (toolId: string)
        (hasJsonSchema: bool)
        (jsonSchema: obj)
        (parameters: obj)
        =
        if hasJsonSchema then
            decorateRootSchema jsonSchema toolId
        elif parametersHoldSchemaView parameters then
            decorateRootSchema parameters toolId
            toolOutput?jsonSchema <- parameters
        else
            let rendered = ToolSchemaJson.providerSchema parameters
            decorateRootSchema rendered toolId
            toolOutput?jsonSchema <- rendered

        appendInvestigationOutlookDescription toolOutput

    let private decorateToolParameters (toolOutput: obj) (toolId: string) =
        let jsonSchema = toolOutput?jsonSchema
        let parameters = toolOutput?parameters
        let hasJsonSchema = not (isNull jsonSchema) && isPlainObject jsonSchema

        if not hasJsonSchema && not (isPlainObject parameters) then
            raise (InvalidOperationException(sprintf "Tool %s parameters schema is not a valid object schema" toolId))

        applyToolDecoration toolOutput toolId hasJsonSchema jsonSchema parameters

    let private decorateToolDefinition (toolInput: obj) (toolOutput: obj) : unit =
        let toolId =
            if isNull toolInput?toolID then
                ""
            else
                string toolInput?toolID

        match InvestigationEstimateContract.classifyTool toolId with
        | InvestigationEstimateContract.InvestigationToolPolicy.EstimateAfterCall ->
            decorateToolParameters toolOutput toolId
        | InvestigationEstimateContract.InvestigationToolPolicy.NoEstimate
        | InvestigationEstimateContract.InvestigationToolPolicy.Unreviewed -> ()

    let decorateDefinition (toolInput: obj) (toolOutput: obj) : unit =
        if not (isNull toolInput) && not (isNull toolOutput) then
            decorateToolDefinition toolInput toolOutput
