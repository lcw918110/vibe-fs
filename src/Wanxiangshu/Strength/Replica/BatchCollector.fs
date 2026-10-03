namespace Wanxiangshu.Strength.Replica

open Wanxiangshu.Foundation
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Strength

open System
open Wanxiangshu.Foundation.Identity

/// STRENGTH-003/005: recover completed Replica provider-request batches from the
/// Host wire transcript. Request boundaries come from assistant messages, never
/// tool-call count. A batch is emitted only when every call has exactly one result
/// before the next provider/user message; result arrival order is normalized back
/// to provider call order.
[<RequireQualifiedAccess>]
module StrengthBatchCollector =

    type private Call =
        { Id: ToolCallId
          Name: string
          Arguments: string }

    let private callsOf (message: ProviderProjection.WireMessage) =
        message.Parts
        |> List.choose (function
            | ProviderProjection.WireToolCall(callId, name, arguments) ->
                Some
                    { Id = callId
                      Name = name
                      Arguments = arguments }
            | _ -> None)

    let private resultParts (message: ProviderProjection.WireMessage) =
        message.Parts
        |> List.choose (function
            | ProviderProjection.WireToolResult(callId, result) -> Some(callId, result)
            | _ -> None)

    let private textParts (message: ProviderProjection.WireMessage) =
        message.Parts
        |> List.choose (function
            | ProviderProjection.WireText text -> Some text
            | _ -> None)

    let private isRequestBoundary (message: ProviderProjection.WireMessage) =
        String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)

    let private addResult
        (callIds: Set<string>)
        (state: Result<Map<string, string>, unit>)
        ((callId, result): ToolCallId * string)
        : Result<Map<string, string>, unit> =
        state
        |> Result.bind (fun current ->
            let key = ToolCallId.value callId

            if not (Set.contains key callIds) || Map.containsKey key current then
                Error()
            else
                Ok(Map.add key result current))

    let private collectNextResults
        (all: ProviderProjection.WireMessage array)
        (callIds: Set<string>)
        (index: int)
        (results: Map<string, string>)
        : Result<Map<string, string>, unit> =
        resultParts all.[index] |> List.fold (addResult callIds) (Ok results)

    let private collectResults
        (all: ProviderProjection.WireMessage array)
        (callIds: Set<string>)
        (startIndex: int)
        : Result<Map<string, string> * int, unit> =
        let rec loop index results =
            if index >= all.Length || isRequestBoundary all.[index] then
                Ok(results, index)
            else
                collectNextResults all callIds index results
                |> Result.bind (fun current -> loop (index + 1) current)

        loop startIndex Map.empty

    let private completeAssistant
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (nextOrdinal: int)
        (collected: StrengthRequestBatch list)
        (calls: Call list)
        (recurse: int -> int -> StrengthRequestBatch list -> StrengthRequestBatch list)
        : StrengthRequestBatch list =
        let callIds = calls |> List.map (fun call -> ToolCallId.value call.Id) |> Set.ofList

        match collectResults all callIds (index + 1) with
        | Error() -> List.rev collected
        | Ok(results, _) when Map.count results <> List.length calls -> List.rev collected
        | Ok(results, nextIndex) ->
            let exchanges =
                calls
                |> List.map (fun call ->
                    { ToolName = call.Name.Trim().ToLowerInvariant()
                      CanonicalArguments = call.Arguments
                      CanonicalResult = Map.find (ToolCallId.value call.Id) results })

            recurse
                nextIndex
                nextOrdinal
                ({ RequestOrdinal = nextOrdinal
                   AssistantText = textParts all.[index]
                   Exchanges = exchanges }
                 :: collected)

    let private processAssistant
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (requestOrdinal: int)
        (collected: StrengthRequestBatch list)
        (calls: Call list)
        (recurse: int -> int -> StrengthRequestBatch list -> StrengthRequestBatch list)
        : StrengthRequestBatch list =
        match calls, textParts all.[index], resultParts all.[index] with
        | [], texts, [] when not (List.isEmpty texts) ->
            List.rev (
                { RequestOrdinal = requestOrdinal + 1
                  AssistantText = texts
                  Exchanges = [] }
                :: collected
            )
        | [], _, _ -> List.rev collected
        | _ -> completeAssistant all index (requestOrdinal + 1) collected calls recurse

    let private advanceBatch
        (all: ProviderProjection.WireMessage array)
        (index: int)
        (requestOrdinal: int)
        (collected: StrengthRequestBatch list)
        (message: ProviderProjection.WireMessage)
        (recurse: int -> int -> StrengthRequestBatch list -> StrengthRequestBatch list)
        : StrengthRequestBatch list =
        if not (String.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) then
            recurse (index + 1) requestOrdinal collected
        else
            processAssistant all index requestOrdinal collected (callsOf message) recurse

    let collectCompleteBatches (messages: ProviderProjection.WireMessage list) : StrengthRequestBatch list =
        let all = List.toArray messages

        let rec loop
            (index: int)
            (requestOrdinal: int)
            (collected: StrengthRequestBatch list)
            : StrengthRequestBatch list =
            if index >= all.Length then
                List.rev collected
            else
                advanceBatch all index requestOrdinal collected all.[index] loop

        loop 0 0 []
