namespace Wanxiangshu.Mission.Work.Review

open FsToolkit.ErrorHandling
open Fable.Core.JsInterop
open Wanxiangshu.Mission.Work

module Model =
    let schemaJson =
        """{"type":"object","additionalProperties":false,"required":["findings"],"properties":{"findings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["key"],"properties":{"key":{"type":"string"},"value":{"type":["string","null"]}}}},"note":{"type":"string"}}}"""

    let private keys (value: obj) : string array =
        emitJsExpr value "Object.keys($0 ?? {})"

    let private property (value: obj) (name: string) : obj = emitJsExpr (value, name) "$0[$1]"

    let private isStringValue (value: obj) : bool =
        emitJsExpr value "typeof $0 === 'string'"

    let private readString (value: obj) (name: string) : Result<string, string> =
        let raw = property value name

        if isStringValue raw then
            Ok(unbox<string> raw)
        else
            Error("review finding field must be a string: " + name)

    let private readNullableString (value: obj) (name: string) : Result<string option, string> =
        let raw = property value name

        if emitJsExpr raw "$0 === null" then
            Ok None
        elif isStringValue raw then
            Ok(Some(unbox<string> raw))
        else
            Error("review finding field must be a string or null: " + name)

    let private readFinding (value: obj) : Result<ReviewFinding, string> =
        result {
            let! key = readString value "key"
            let! value = readNullableString value "value"

            return { Key = key; Value = value }
        }

    let private readFindings (value: obj) : Result<ReviewFinding list, string> =
        if not (emitJsExpr value "Array.isArray($0)") then
            Error "review findings must be an array"
        else
            let length: int = emitJsExpr value "$0.length"

            [ 0 .. length - 1 ]
            |> List.traverseResultM (fun index -> property value (string index) |> readFinding)

    let private validateNote value =
        let hasNote: bool = emitJsExpr value "'note' in ($0 ?? {})"
        let noteIsString: bool = emitJsExpr value "typeof ($0 ?? {})['note'] === 'string'"

        if not hasNote || noteIsString then
            Ok()
        else
            Error "review note must be a string"

    let private validateKeys value =
        let actualKeys = keys value |> Set.ofArray
        let requiredSet = Set.singleton "findings"
        // `scope` is the Host-injected annotation every tool call carries
        // (common law «Scope»); ScopeAnnotationGate consumes it before the body.
        let allowedSet = requiredSet |> Set.add "note" |> Set.add "scope"

        if not (Set.isSubset requiredSet actualKeys) then
            Error "review arguments must contain the findings array"
        elif not (Set.isSubset actualKeys allowedSet) then
            Error "review arguments contain unexpected fields"
        else
            Ok()

    let tryParse (value: obj) =
        if isNull value || emitJsExpr value "typeof $0 !== 'object' || Array.isArray($0)" then
            Error "review arguments must be an object"
        else
            result {
                do! validateKeys value
                do! validateNote value
                let! findings = property value "findings" |> readFindings
                return! ReviewFindings.tryCreate findings
            }
