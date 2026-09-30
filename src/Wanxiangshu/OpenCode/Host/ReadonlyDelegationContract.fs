namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Strength

/// DELEGATE.md 4.2: explicit read-only delegation schema contract and
/// parameter boundary. Schema decoration, budget/note validation, provider
/// argument evidence preservation and bilingual collaboration prose live
/// here. This module never creates a child session, sends a provider
/// request, computes the batch max, or writes business events.
module ReadonlyDelegationContract =

    let private savedArgsKey: obj = emitJsExpr () "Symbol('readonly-delegation-args')"

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

    [<Emit("typeof $0 === 'string'")>]
    let private isStringValue (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'undefined'")>]
    let private isUndefinedValue (value: obj) : bool = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private isFiniteNumber (value: obj) : bool = jsNative

    [<Emit("Number.isInteger($0)")>]
    let private isIntegerNumber (value: obj) : bool = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwn (target: obj) (key: obj) : bool = jsNative

    [<Emit("Object.isExtensible($0)")>]
    let private isExtensible (target: obj) : bool = jsNative

    [<Emit("Object.getOwnPropertyDescriptor($0, $1)")>]
    let private getOwnPropertyDescriptor (target: obj) (key: obj) : obj = jsNative

    [<Emit("Object.defineProperty($0, $1, $2)")>]
    let private defineProperty (target: obj) (key: obj) (descriptor: obj) : unit = jsNative

    [<Emit("Reflect.deleteProperty($0, $1)")>]
    let private deleteProperty (target: obj) (key: obj) : bool = jsNative

    [<Emit("Object.keys($0).sort().join(',')")>]
    let private ownKeysSignature (value: obj) : string = jsNative

    [<Emit("throw new TypeError($0)")>]
    let private throwTypeError (message: string) : unit = jsNative

    // DELEGATE_REVISE.md 7.1/7.2 schema fragments, verbatim.
    [<Literal>]
    let private readonlyRoundsDescriptionEn =
        "Estimate how many consecutive read-only investigation rounds will still be needed after ALL tool calls in this response have completed, before a substantive change, a command, user clarification, a conclusion, or a consequential judgment that you must make yourself. One round is one model request and may contain several parallel tool calls; do not count the current batch. Routine choices about which reference or file to inspect are part of investigation. Use 0 when no such investigation remains or the next step already reaches one of those boundaries. Give your current best estimate; it need not be exact, and do not add work to match it."

    [<Literal>]
    let private selfNoteDescriptionEn =
        "Provide this field only when this call's estimated_readonly_rounds is greater than 0; otherwise omit the field entirely, without an empty string or null. For a positive estimate, leave a brief, non-empty outlook for the next investigation rounds: what evidence or relationships to inspect and what finding will make the next step possible. One to three sentences are enough. Do not provide a progress report, generic filler, instructions to another worker, or a full reasoning trace."

    [<Literal>]
    let private readonlyRoundsDescriptionZh =
        "当前响应的全部工具执行完成后，预计还需要连续进行多少轮只读查证，才会到达实质修改、执行命令、向用户确认、给出结论，或必须亲自权衡的关键判断？一轮是一次模型请求，可以包含多个并行工具调用；当前这批不计入。选择接着查哪个文件或引用属于普通调查，不必一概当成关键判断。已经没有后续查证，或下一步就到达上述边界时，填 0。按当前材料估计即可，不要求精确，也不要为了符合估计增加调查。"

    [<Literal>]
    let private selfNoteDescriptionZh =
        "仅当本次调用的 estimated_readonly_rounds 大于 0 时填写；否则完全省略本字段，不填空串或 null。正数时，用一至三句话给自己留下后续调查的展望：准备核对哪些材料或关系，什么证据出现后可以进入下一步。不要写完成情况、泛泛感想、对其他执行者的指令或完整思考过程。"

    // DELEGATE_REVISE.md 7.3 short outlook prose, verbatim.
    [<Literal>]
    let private englishCollaboration =
        "Investigation outlook: estimated_readonly_rounds estimates the consecutive read-only investigation rounds after the current batch. Include self_note only for a positive estimate, stating what to inspect next and what finding will make the next step possible; omit the note for 0."

    [<Literal>]
    let private chineseCollaboration =
        "调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数。只在本次估计大于 0 时填写 self_note，简述接下来查什么、查到什么即可进入下一步；估计为 0 时省略短记。"

    [<Literal>]
    let private chineseCollaborationAlt =
        "调查展望：estimated_readonly_rounds 估计当前整批完成后的连续只读查证轮数；self_note 在轮数大于 0 时简述下一步核对目标，为 0 时必须省略。"

    [<Literal>]
    let private legacyEnglishCollaboration =
        "Fill in delegate_readonly_rounds on every tool call. After all calls in this response complete, the maximum value grants your companion up to that many read-only model requests. A 0 does not veto a positive value elsewhere in the batch. One request may call several tools in parallel. Your companion sees the current conversation, may stop early, and returns actual tool calls and results. Use 0 when you need to retain control. Do not invent work to fill the budget; using it up does not require an edit.\n\nWorking together is a way to learn about each other and build trust. Judge by the investigation you can actually see in the conversation. When your companion does well, trust him more and let him do as much of the work as your judgment supports. When he struggles in particular situations, delegate less or not at all in similar situations next time. When in doubt, estimating how many steps remain before the first edit can give you a starting point. This is only a heuristic baseline, not an optimal policy or a substitute for judgment, and it does not require an eventual edit.\n\nOptionally leave a brief self_note in the first person, such as \u201cI suspect the caller and entry point disagree about empty values; I want to check the callers next.\u201d Your companion will naturally see it in the conversation. Omit it when it adds nothing. A short intention or uncertainty is enough; do not write a full analysis or present a guess as a fact."

    [<Literal>]
    let private legacyChineseCollaboration =
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

    /// self_note 缺失（JS undefined，即属性不存在）合法；出现时只接受
    /// 字符串（含空串）；null、数字、布尔、对象、数组一律拒绝，不强转；
    /// 不自动填充；不做“必须以我/I 开头”的正则门禁。
    let trySelfNote (value: obj) : Result<string option, string> =
        if isUndefinedValue value then Ok None
        elif isStringValue value then Ok(Some(string value))
        else Error "self_note-not-string"

    let private descriptorConfigurableOrAbsent (descriptor: obj) : bool =
        if isNull descriptor then
            true
        else
            let conf = descriptor?configurable
            not (isNull conf) && unbox<bool> conf

    let private ensureFieldDeleted (args: obj) (field: string) =
        let deleted = deleteProperty args field

        if not deleted then
            throwTypeError (sprintf "Tool arguments cannot hide the readonly delegation field %s" field)

    let private deleteProtocolField (args: obj) (field: string) : unit =
        if not (isNull (getOwnPropertyDescriptor args field)) then
            ensureFieldDeleted args field

    let private deleteProtocolFields (args: obj) : unit =
        try
            deleteProtocolField args roundsField
            deleteProtocolField args noteField
        with ex ->
            deleteProperty args savedArgsKey |> ignore
            raise ex

    let private hideProtocolFields (args: obj) : unit =
        let roundsDescriptor = getOwnPropertyDescriptor args roundsField
        let noteDescriptor = getOwnPropertyDescriptor args noteField

        if
            not (isExtensible args)
            || not (descriptorConfigurableOrAbsent roundsDescriptor)
            || not (descriptorConfigurableOrAbsent noteDescriptor)
        then
            throwTypeError "Tool arguments cannot hold or modify the readonly delegation fields"

        let saved = createObj [ "rounds", roundsDescriptor; "note", noteDescriptor ]

        let symbolDescriptor =
            createObj [ "value", saved; "enumerable", box false; "configurable", box true ]

        defineProperty args savedArgsKey symbolDescriptor
        deleteProtocolFields args

    /// Hide uses its own Symbol key; the manager review contract's saved
    /// record under Symbol('manager-review-contract') is never read, written
    /// or deleted here, so the two contracts coexist on the same args object.
    let hide (args: obj) : unit =
        if isNull args || not (isPlainObject args) then
            throwTypeError "Tool arguments must be an object"

        if not (hasOwn args savedArgsKey) then
            hideProtocolFields args

    let private restoreField (args: obj) (field: string) (descriptor: obj) : unit =
        let restored =
            if isNull descriptor then
                deleteProperty args field
            else
                defineProperty args field descriptor
                true

        if not restored then
            throwTypeError (sprintf "Failed to restore the readonly delegation field %s" field)

    let private restoreSavedFields (args: obj) (saved: obj) : unit =
        restoreField args roundsField saved?rounds
        restoreField args noteField saved?note

    let private restoreSavedArgs (args: obj) : unit =
        let saved = args?(savedArgsKey)

        if not (isExtensible args) then
            throwTypeError "Tool arguments are frozen or not extensible during readonly delegation restore"

        restoreSavedFields args saved

        let deletedKey = deleteProperty args savedArgsKey

        if not deletedKey then
            throwTypeError "Failed to delete the saved readonly delegation key"

    let restore (args: obj) : unit =
        if not (isNull args) && isPlainObject args && hasOwn args savedArgsKey then
            restoreSavedArgs args

    let private currentLanguage () =
        ProviderLanguageBinding.forSessionText ""

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

    let private stripCollaborationBlocks (text: string) : string =
        text
            .Replace(englishCollaboration, "")
            .Replace(chineseCollaboration, "")
            .Replace(chineseCollaborationAlt, "")
            .Replace(legacyEnglishCollaboration, "")
            .Replace(legacyChineseCollaboration, "")
            .TrimEnd('\n')

    let private appendCollaborationDescription (toolOutput: obj) : unit =
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
            | ProviderLanguage.SimplifiedChinese -> chineseCollaboration
            | _ -> englishCollaboration

        toolOutput?description <- stripCollaborationBlocks current + "\n\n" + block

    /// A definition that already states its provider view as `properties` on
    /// `parameters` needs no Effect rendering; it is decorated where it stands
    /// and published as the JSON schema so the Host sends exactly these bytes.
    let private parametersHoldSchemaView (parameters: obj) : bool =
        if not (isPlainObject parameters) then
            false
        else
            let properties = parameters?properties
            not (isNull properties) && isPlainObject properties

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

        appendCollaborationDescription toolOutput

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
