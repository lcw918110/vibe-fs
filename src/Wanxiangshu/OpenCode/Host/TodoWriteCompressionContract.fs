namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Provider

/// Provider-only compression control for the Host-native todowrite tool.
///
/// The Host remains the physical todo executor. This contract only decorates
/// the provider-visible schema, captures retainCheckpoints at the hook boundary,
/// and hides that protocol field before the native Effect schema decodes args.
module TodoWriteCompressionContract =

    [<RequireQualifiedAccess>]
    module private Path =
        [<Literal>]
        let Description = "tool/todowrite/description"

        [<Literal>]
        let RetainCheckpoints = "tool/todowrite/arg-retain-checkpoints"

    [<Literal>]
    let private field = "retainCheckpoints"

    let private savedArgsKey: obj = emitJsExpr () "Symbol('todowrite-compression-args')"

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'number'")>]
    let private isNumberValue (value: obj) : bool = jsNative

    [<Emit("Number.isFinite($0)")>]
    let private isFiniteNumber (value: obj) : bool = jsNative

    [<Emit("Number.isInteger($0)")>]
    let private isIntegerNumber (value: obj) : bool = jsNative

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasOwn (target: obj) (key: obj) : bool = jsNative

    [<Emit("Object.getOwnPropertyDescriptor($0, $1)")>]
    let private getOwnPropertyDescriptor (target: obj) (key: obj) : obj = jsNative

    [<Emit("Object.defineProperty($0, $1, $2)")>]
    let private defineProperty (target: obj) (key: obj) (descriptor: obj) : unit = jsNative

    [<Emit("Reflect.deleteProperty($0, $1)")>]
    let private deleteProperty (target: obj) (key: obj) : bool = jsNative

    let private validateNumericRange (numeric: float) =
        if numeric < 1.0 || numeric > float Int32.MaxValue then
            Error "todowrite.retainCheckpoints must be a positive integer"
        else
            Ok(int numeric)

    let private validateValue (value: obj) : Result<int, string> =
        if isNull value then
            Error "todowrite.retainCheckpoints is required"
        elif not (isNumberValue value) then
            Error "todowrite.retainCheckpoints must be a number"
        elif not (isFiniteNumber value) then
            Error "todowrite.retainCheckpoints must be finite"
        elif not (isIntegerNumber value) then
            Error "todowrite.retainCheckpoints must be an integer"
        else
            validateNumericRange (unbox<float> value)

    let private visibleValue (args: obj) =
        if isNull args || not (isPlainObject args) || not (hasOwn args (box field)) then
            null
        else
            args?(field)

    let private savedDescriptor (args: obj) =
        if isNull args || not (isPlainObject args) then
            null
        else
            let holder = getOwnPropertyDescriptor args savedArgsKey
            if isNull holder then null else holder?value

    let private capturedOrVisible (args: obj) : Result<int, string> =
        let saved = savedDescriptor args

        if isNull saved then
            validateValue (visibleValue args)
        else
            validateValue saved?value

    let private finishHide args retain =
        if deleteProperty args (box field) then
            Ok retain
        else
            deleteProperty args savedArgsKey |> ignore
            Error "todowrite.retainCheckpoints could not be hidden from the native executor"

    let private hideDescriptor args retain descriptor =
        if isNull descriptor then
            Error "todowrite.retainCheckpoints is required"
        else
            defineProperty
                args
                savedArgsKey
                (createObj [ "value", descriptor; "enumerable", box false; "configurable", box true ])

            finishHide args retain

    let private hideCaptured args retain =
        if not (isNull (savedDescriptor args)) then
            Ok retain
        else
            hideDescriptor args retain (getOwnPropertyDescriptor args (box field))

    let captureAndHide (args: obj) : Result<int, string> =
        capturedOrVisible args |> Result.bind (hideCaptured args)

    let restore (args: obj) : unit =
        let descriptor = savedDescriptor args

        if not (isNull descriptor) then
            defineProperty args (box field) descriptor
            deleteProperty args savedArgsKey |> ignore

    let private integerProperty language =
        createObj
            [ "type", box "integer"
              "minimum", box 1
              "description", box (ProviderProse.render language Path.RetainCheckpoints Map.empty) ]

    /// host-boundary-032: repeated decoration of the same definition must be
    /// idempotent. A property this contract itself placed (same shape, same
    /// rendered description for the resolved language) is the same decoration,
    /// not a conflict; anything else is a foreign definition and fails loudly.
    /// The description is accepted in either provider language: the same
    /// definition can be decorated once with no session (global preference)
    /// and again with a session that resolves the other language, and that is
    /// still this contract's own property, not a foreign conflict.
    let private isOwnRetainCheckpointsProperty (value: obj) : bool =
        not (isNull value)
        && isPlainObject value
        && string value?``type`` = "integer"
        && unbox<float> value?minimum = 1.0
        && (let description = string value?description

            description = ProviderProse.render ProviderLanguage.English Path.RetainCheckpoints Map.empty
            || description = ProviderProse.render ProviderLanguage.SimplifiedChinese Path.RetainCheckpoints Map.empty)

    let private appendRequired (required: obj array) =
        if required |> Array.exists (fun item -> string item = field) then
            required
        else
            Array.append required [| box field |]

    let private decorateRootSchema language toolId (schema: obj) =
        if isNull schema || not (isPlainObject schema) then
            raise (InvalidOperationException(sprintf "Tool %s schema is not an object" toolId))

        let properties = schema?properties

        if isNull properties || not (isPlainObject properties) then
            raise (InvalidOperationException(sprintf "Tool %s schema has no object properties" toolId))

        let existing = properties?(field)

        if isNull existing then
            properties?(field) <- integerProperty language
        elif not (isOwnRetainCheckpointsProperty existing) then
            raise (InvalidOperationException(sprintf "Tool %s already defines %s" toolId field))

        let required = schema?required

        if isNull required then
            schema?required <- box [| field |]
        elif isArray required then
            schema?required <- box (appendRequired (unbox<obj array> required))
        else
            raise (InvalidOperationException(sprintf "Tool %s schema required field is not an array" toolId))

    let private parametersHoldSchemaView (parameters: obj) =
        isPlainObject parameters
        && not (isNull parameters?properties)
        && isPlainObject parameters?properties

    let private languageOfDefinition (toolInput: obj) =
        let sessionText =
            if isNull toolInput || isNull toolInput?sessionID then
                ""
            else
                string toolInput?sessionID

        ProviderLanguageBinding.forSessionText sessionText

    let private applyDefinition (toolInput: obj) (toolOutput: obj) =
        let toolId = string toolInput?toolID
        let language = languageOfDefinition toolInput
        let jsonSchema = toolOutput?jsonSchema
        let parameters = toolOutput?parameters

        if not (isNull jsonSchema) && isPlainObject jsonSchema then
            decorateRootSchema language toolId jsonSchema
        elif parametersHoldSchemaView parameters then
            decorateRootSchema language toolId parameters
            toolOutput?jsonSchema <- parameters
        elif isPlainObject parameters then
            let rendered = ToolSchemaJson.providerSchema parameters
            decorateRootSchema language toolId rendered
            toolOutput?jsonSchema <- rendered
        else
            raise (InvalidOperationException(sprintf "Tool %s parameters schema is not available" toolId))

        toolOutput?description <- box (ProviderProse.render language Path.Description Map.empty)

    let decorateDefinition (toolInput: obj) (toolOutput: obj) : unit =
        if
            not (isNull toolInput)
            && not (isNull toolOutput)
            && not (isNull toolInput?toolID)
            && string toolInput?toolID = "todowrite"
        then
            applyDefinition toolInput toolOutput
