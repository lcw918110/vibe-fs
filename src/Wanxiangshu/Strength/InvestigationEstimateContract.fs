namespace Wanxiangshu.Strength

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Participant.Provider

module InvestigationEstimateContract =

    /// Plain `let`, not `[<Literal>]`: Fable inlines a literal and emits no export,
    /// so tests and JS callers can import the constant from dist.
    let EstimatedReadonlyRoundsField = "estimated_readonly_rounds"

    [<Literal>]
    let private LegacyRoundsField = "delegate_readonly_rounds"

    [<Literal>]
    let private NoteField = "self_note"

    /// Plain `let`, not `[<Literal>]`: Fable inlines a literal and emits no export,
    /// so tests and JS callers can import the constant from dist.
    let ProtocolRevision = 2

    [<RequireQualifiedAccess>]
    type InvestigationToolPolicy =
        | EstimateAfterCall
        | NoEstimate
        | Unreviewed

    let classifyTool (toolName: string) : InvestigationToolPolicy =
        match toolName with
        | "read"
        | "glob"
        | "grep"
        | "js-manager"
        | "js-engineer"
        | "js-devops"
        | "edit"
        | "write"
        | "mv"
        | "rm"
        | "fetch"
        | "run" -> InvestigationToolPolicy.EstimateAfterCall
        | "fork"
        | "resume"
        | "commission"
        | "join"
        | "horizon"
        | "review"
        | "suicide"
        | "fission"
        | "js-plan"
        | "ask"
        | "handoff"
        | "deliver"
        | "open-terminal"
        | "send-terminal"
        | "read-terminal"
        | "signal-terminal"
        | "skill"
        | "todowrite"
        | "assume"
        | "defer"
        | "publish"
        | "chronicle"
        | "js-bookkeeper"
        | "js-predictor"
        | "bash-honeypot"
        | "invalid"
        | "js-orchestrator"
        | "js-blogger" -> InvestigationToolPolicy.NoEstimate
        | _ -> InvestigationToolPolicy.Unreviewed

    [<Struct>]
    type EstimatedReadonlyRounds = private EstimatedReadonlyRounds of int

    module EstimatedReadonlyRounds =
        let value (EstimatedReadonlyRounds v) = v

        let toExecutionBudget (rounds: EstimatedReadonlyRounds) : ReadonlyRoundBudget =
            match ReadonlyRoundBudget.tryCreate (value rounds) with
            | Ok b -> b
            | Error msg -> failwith ("unexpected invalid budget from valid estimate: " + msg)

    [<RequireQualifiedAccess>]
    type EstimateArgumentError =
        | MissingEstimate
        | WrongNumberType
        | InvalidRange
        | MixedProtocolFields
        | InvalidArgumentObject

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwn (target: obj) (key: string) : bool = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isJsNumber (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'string'")>]
    let private isJsString (value: obj) : bool = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private isJsFinite (value: obj) : bool = jsNative

    [<Emit("Number.isInteger($0)")>]
    let private isJsInteger (value: obj) : bool = jsNative

    /// 检查非负以及是否在 [0, 2147483647]；-0 在 JS 转换为 float 后与 0.0 相等，按 0 处理。
    let private toEstimateRange (num: float) : Result<int, EstimateArgumentError> =
        if num < 0.0 || num > 2147483647.0 then
            Error EstimateArgumentError.InvalidRange
        else
            Ok(int (if num = 0.0 then 0.0 else num))

    let private validateNumber (raw: obj) : Result<int, EstimateArgumentError> =
        if not (isJsNumber raw) then
            Error EstimateArgumentError.WrongNumberType
        elif not (isJsFinite raw) || not (isJsInteger raw) then
            Error EstimateArgumentError.InvalidRange
        else
            toEstimateRange (unbox<float> raw)

    /// self_note 是纯建议性展望：填与不填、填什么类型，都不构成失败。
    /// 只有字符串才原样保留；其余一律按缺失处理，不报错、不修正。
    let private advisoryNote (arguments: obj) : string option =
        if hasOwn arguments NoteField && isJsString arguments?(NoteField) then
            Some(string arguments?(NoteField))
        else
            None

    let private parseRoundsArguments
        (arguments: obj)
        : Result<EstimatedReadonlyRounds * string option, EstimateArgumentError> =
        match validateNumber arguments?(EstimatedReadonlyRoundsField) with
        | Error err -> Error err
        | Ok rounds -> Ok(EstimatedReadonlyRounds rounds, advisoryNote arguments)

    let parseParticipatingArguments
        (arguments: obj)
        : Result<EstimatedReadonlyRounds * string option, EstimateArgumentError> =
        if not (isPlainObject arguments) then
            Error EstimateArgumentError.InvalidArgumentObject
        elif hasOwn arguments LegacyRoundsField then
            Error EstimateArgumentError.MixedProtocolFields
        elif not (hasOwn arguments EstimatedReadonlyRoundsField) then
            Error EstimateArgumentError.MissingEstimate
        else
            parseRoundsArguments arguments

    let private describeArgumentErrorZhText (error: EstimateArgumentError) : string =
        match error with
        | EstimateArgumentError.MissingEstimate -> "必须提供 estimated_readonly_rounds 估计字段"
        | EstimateArgumentError.WrongNumberType -> "estimated_readonly_rounds 必须为数字类型"
        | EstimateArgumentError.InvalidRange -> "estimated_readonly_rounds 必须为 0 至 2147483647 之间的非负整数"
        | EstimateArgumentError.MixedProtocolFields -> "不得携带旧协议字段 delegate_readonly_rounds"
        | EstimateArgumentError.InvalidArgumentObject -> "工具参数必须为合法的普通对象"

    let private describeArgumentErrorEnText (error: EstimateArgumentError) : string =
        match error with
        | EstimateArgumentError.MissingEstimate -> "The estimated_readonly_rounds field must be provided"
        | EstimateArgumentError.WrongNumberType -> "estimated_readonly_rounds must be a number"
        | EstimateArgumentError.InvalidRange ->
            "estimated_readonly_rounds must be a non-negative integer between 0 and 2147483647"
        | EstimateArgumentError.MixedProtocolFields -> "The legacy delegate_readonly_rounds field must not be used"
        | EstimateArgumentError.InvalidArgumentObject -> "Tool arguments must be a valid plain object"

    let describeArgumentError (language: ProviderLanguage) (error: EstimateArgumentError) : string =
        match language with
        | ProviderLanguage.SimplifiedChinese -> describeArgumentErrorZhText error
        | ProviderLanguage.English -> describeArgumentErrorEnText error

    let formatArgumentError (language: ProviderLanguage) (error: EstimateArgumentError) : string =
        describeArgumentError language error

    let describeArgumentErrorZh (error: EstimateArgumentError) : string =
        describeArgumentError ProviderLanguage.SimplifiedChinese error

    let describeArgumentErrorEn (error: EstimateArgumentError) : string =
        describeArgumentError ProviderLanguage.English error

    let errorCode (error: EstimateArgumentError) : string =
        match error with
        | EstimateArgumentError.MissingEstimate -> "MissingEstimate"
        | EstimateArgumentError.WrongNumberType -> "WrongNumberType"
        | EstimateArgumentError.InvalidRange -> "InvalidRange"
        | EstimateArgumentError.MixedProtocolFields -> "MixedProtocolFields"
        | EstimateArgumentError.InvalidArgumentObject -> "InvalidArgumentObject"

    /// `errorCode` 的反向投影（WHAT[016] §6）：机器可判的稳定错误码回到唯一的
    /// 分类，使「码 → 分类 → 文案」成为一条可观察链。码的字面量与 `errorCode`
    /// 同一份集合，本函数不持有、不复制任何文案。
    let tryFromCode (code: string) : EstimateArgumentError option =
        match code with
        | "MissingEstimate" -> Some EstimateArgumentError.MissingEstimate
        | "WrongNumberType" -> Some EstimateArgumentError.WrongNumberType
        | "InvalidRange" -> Some EstimateArgumentError.InvalidRange
        | "MixedProtocolFields" -> Some EstimateArgumentError.MixedProtocolFields
        | "InvalidArgumentObject" -> Some EstimateArgumentError.InvalidArgumentObject
        | _ -> None

    /// 与 `tryFromCode` 成对：未知码按 `ProviderLanguage.parse` 的既有语义
    /// 直接抛错，绝不静默降级为任何默认分类。
    let parseFromCode (code: string) : EstimateArgumentError =
        match tryFromCode code with
        | Some error -> error
        | None ->
            raise (System.ArgumentException(sprintf "unrecognized EstimateArgumentError code: %s (WHAT[016])" code))

    let policyCode (policy: InvestigationToolPolicy) : string =
        match policy with
        | InvestigationToolPolicy.EstimateAfterCall -> "EstimateAfterCall"
        | InvestigationToolPolicy.NoEstimate -> "NoEstimate"
        | InvestigationToolPolicy.Unreviewed -> "Unreviewed"
