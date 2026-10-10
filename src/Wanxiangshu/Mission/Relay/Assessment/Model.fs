namespace Wanxiangshu.Mission.Relay.Assessment

open FsToolkit.ErrorHandling
open Fable.Core.JsInterop
open Wanxiangshu.Mission.Relay

module Model =
    let schemaJson =
        """{"type":"object","additionalProperties":false,"required":["findings"],"properties":{"findings":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["acceptance_criteria","work_plan"],"properties":{"acceptance_criteria":{"type":"string"},"work_plan":{"type":"string"}}}},"note":{"type":"string"}}}"""

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

    let private readFinding (value: obj) : Result<AssessmentFinding, string> =
        result {
            let! acceptanceCriteria = readString value "acceptance_criteria"
            let! workPlan = readString value "work_plan"

            return
                { AcceptanceCriteria = acceptanceCriteria
                  WorkPlan = workPlan }
        }

    let private readFindings (value: obj) : Result<AssessmentFinding list, string> =
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
        let allowedSet = requiredSet |> Set.add "note"

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
                return! AssessmentFindings.tryCreate findings
            }
