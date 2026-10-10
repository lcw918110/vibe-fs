namespace Wanxiangshu.OpenCode.Host

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.OpenCode

module ManagerReviewContract =

    /// host-boundary-032: one static stash definition for the review contract
    /// field. The deletion-failure compensation stays on: a failed hide
    /// deletes the saved record again before the error propagates.
    let private stash: ProtocolArgumentStash.StashSpec =
        { Symbol = emitJsExpr () "Symbol('manager-review-contract')"
          Fields =
            [ { Name = "contract"
                SavedKey = "descriptor" } ]
          ReappearanceFields = [ "contract" ]
          HoldMessage = "Tool arguments cannot hold or modify the review contract"
          RestoreOrderMessage = "Tool arguments cannot restore the review contract's original key order"
          RestoreFrozenMessage = "Tool arguments are frozen or not extensible during contract restore"
          FieldDeleteFailure = fun _ -> "Review contract could not be hidden"
          RestoreDeleteFailure =
            fun field ->
                if field = "contract" then
                    "Failed to delete contract property during restore"
                else
                    "Failed to restore review argument key order"
          SymbolDeleteFailure = "Failed to delete saved review contract key"
          CompensateDeleteFailure = true }

    let hide (args: obj) : unit = ProtocolArgumentStash.hide stash args

    let hideForCall (owner: ProtocolArgumentCall option) (args: obj) : unit =
        ProtocolArgumentStash.hideForCall stash owner args

    let classifyHiddenArguments (owner: ProtocolArgumentCall option) (args: obj) : HiddenProtocolArguments =
        ProtocolArgumentStash.classifyHiddenArguments stash owner args

    let restore (args: obj) : unit =
        ProtocolArgumentStash.restore stash args

    let restoreForCall (owner: ProtocolArgumentCall option) (args: obj) : unit =
        ProtocolArgumentStash.restoreForCall stash owner args

    [<Emit("Array.isArray($0)")>]
    let private isArray (value: obj) : bool = jsNative

    [<Emit("typeof $0 === 'object' && $0 !== null && !Array.isArray($0)")>]
    let private isPlainObject (value: obj) : bool = jsNative

    [<Literal>]
    let private reviewContractDescription =
        "仅用于当前尚未被接纳的独立评审。评审一旦被系统接纳，不得再次调用本工具。固定填写 do-not-use-except-for-review。"

    let private appendContractIfMissing (reqArr: obj array) : obj array =
        let exists = reqArr |> Array.exists (fun x -> string x = "contract")

        if exists then
            reqArr
        else
            Array.append reqArr [| box "contract" |]

    let private ensureRequiredContract (schemaObj: obj) (toolId: string) : unit =
        let required = schemaObj?required

        if isNull required then
            schemaObj?required <- box [| "contract" |]
        elif isArray required then
            let reqArr = unbox<obj array> required
            schemaObj?required <- box (appendContractIfMissing reqArr)
        else
            raise (InvalidOperationException(sprintf "Tool %s parameters schema required field is not an array" toolId))

    let private ensurePropertiesContract (schemaObj: obj) (toolId: string) : unit =
        let properties = schemaObj?properties

        if isNull properties || not (isPlainObject properties) then
            raise (InvalidOperationException(sprintf "Tool %s parameters schema missing object properties" toolId))

        if isNull properties?contract then
            let contractProperty =
                createObj
                    [ "type", box "string"
                      "enum", box [| ManagerReviewTools.contractValue |]
                      "description", box reviewContractDescription ]

            properties?contract <- contractProperty

    let private decorateSchemaObject (schemaObj: obj) (toolId: string) : unit =
        ensurePropertiesContract schemaObj toolId
        ensureRequiredContract schemaObj toolId

    let private decorateParametersSchema (toolOutput: obj) (toolId: string) (hasJsonSchema: bool) : unit =
        let parameters = toolOutput?parameters
        let parametersProperties = parameters?properties

        if not (isNull parametersProperties) && isPlainObject parametersProperties then
            decorateSchemaObject parameters toolId
        elif hasJsonSchema then
            parameters?properties <- toolOutput?jsonSchema?properties
            parameters?required <- toolOutput?jsonSchema?required
        else
            raise (InvalidOperationException(sprintf "Tool %s parameters schema missing object properties" toolId))

    let private applyDefinitionDecoration (toolOutput: obj) (toolId: string) : unit =
        let hasJsonSchema =
            not (isNull toolOutput?jsonSchema) && isPlainObject toolOutput?jsonSchema

        let hasParameters =
            not (isNull toolOutput?parameters) && isPlainObject toolOutput?parameters

        if not hasJsonSchema && not hasParameters then
            raise (InvalidOperationException(sprintf "Tool %s parameters schema is not a valid object schema" toolId))

        if hasJsonSchema then
            decorateSchemaObject toolOutput?jsonSchema toolId

        if hasParameters then
            decorateParametersSchema toolOutput toolId hasJsonSchema
        else
            toolOutput?parameters <- toolOutput?jsonSchema

    let private decorateReviewToolDefinition (toolInput: obj) (toolOutput: obj) : unit =
        let toolId =
            if isNull toolInput?toolID then
                ""
            else
                string toolInput?toolID

        if ManagerReviewTools.isReviewTool toolId then
            applyDefinitionDecoration toolOutput toolId

    let decorateDefinition (toolInput: obj) (toolOutput: obj) : unit =
        if not (isNull toolInput) && not (isNull toolOutput) then
            decorateReviewToolDefinition toolInput toolOutput
