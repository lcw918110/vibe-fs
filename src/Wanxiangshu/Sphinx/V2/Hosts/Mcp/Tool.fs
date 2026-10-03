namespace Wanxiangshu.Sphinx.V2.Hosts

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Sphinx.V2.Core
open Wanxiangshu.Sphinx.V2.Wire

/// The MCP tool contract: one strict decoder per tool.
///
/// WHAT[sphinx-v2-036]: the seven public tools and their argument shapes.
/// Required arguments are not defaulted; replacementText is optional.
///
/// WHAT[sphinx-v2-018]: a work submit decodes the answer bytes and nothing else. A
/// certificate patch, a budget debit, an event write or a goal revision in the same
/// call is refused by name, because dropping it silently would let a worker believe
/// it changed something it did not.
///
/// WHAT[sphinx-v2-009]: decoding is the whole job here. This module never reads state
/// and never decides what a call does next.
type ToolRefusal =
    { Code: string
      Path: string
      Message: string }

type StartArgs =
    { CommandId: string
      GoalText: string
      Constraints: string list
      MaterialRefs: string list
      AuthorizationRef: string
      ProfileRef: string }

type WorkNextArgs =
    { CommandId: string
      InquiryId: string
      Limit: int }

type WorkSubmitArgs =
    { CommandId: string
      InquiryId: string
      WorkId: string
      Attempt: Attempt
      Fence: Fence
      CanonicalResult: string
      ResultSchema: SchemaRef
      ClusterId: string }

type StatusArgs = { InquiryId: string }

type CancelArgs =
    { CommandId: string
      InquiryId: string
      Reason: string }

[<RequireQualifiedAccess>]
type ExportMode =
    | Summary
    | Full

type ExportArgs = { InquiryId: string; Mode: ExportMode }

type GoalAmendArgs =
    { CommandId: string
      InquiryId: string
      AuthorizedBy: string
      ExpectedRevision: string
      AddedConstraints: string list
      ReplacementText: string option }

[<RequireQualifiedAccess>]
module Tool =

    let private fromWire (error: WireError) : ToolRefusal =
        { Code = error.Code
          Path = error.Path
          Message = error.Message }

    let private fromText (path: string) (message: string) : ToolRefusal =
        { Code = "INVALID_SCHEMA"
          Path = path
          Message = message }

    [<Emit("Object.prototype.hasOwnProperty.call($0, $1)")>]
    let private hasField (raw: obj) (name: string) : bool = jsNative

    [<Emit("$0[$1]")>]
    let private field (raw: obj) (name: string) : obj = jsNative

    /// 当前尚未接通的 Runtime 操作返回具名拒绝。局部 body 分支存在并不能
    /// 证明完整 codec、父链、revision 与 receipt 已可持久化往返。
    let unsupported (tool: string) : ToolRefusal =
        { Code = "RUNTIME_OPERATION_UNSUPPORTED"
          Path = "tool"
          Message =
            sprintf
                "%s is refused: this Runtime operation is not connected to a verified durable transition path. The call changed nothing."
                tool }

    /// An export must state all three hashes, and the trace hash covers the accepted
    /// envelopes in order. This adapter reads published state and has no access to
    /// that envelope sequence, so it refuses rather than presenting a hash of an
    /// empty trace as if it described the inquiry.
    let traceUnavailable (tool: string) : ToolRefusal =
        { Code = "EXPORT_TRACE_UNAVAILABLE"
          Path = "tool"
          Message =
            sprintf
                "%s is refused: this adapter reads published state and cannot enumerate accepted envelopes, so it cannot state the trace hash an export bundle requires. The call changed nothing."
                tool }

    let refusalCode (refusal: ToolRefusal) : string = refusal.Code

    let refusalPath (refusal: ToolRefusal) : string = refusal.Path

    let refusalMessage (refusal: ToolRefusal) : string = refusal.Message

    let decodeStart (raw: obj) : Result<StartArgs, ToolRefusal> =
        Decode.stringField raw "commandId"
        |> Result.mapError fromWire
        |> Result.bind (fun commandId ->
            Decode.stringField raw "goalText"
            |> Result.mapError fromWire
            |> Result.bind (fun goalText ->
                Decode.uniqueStringListField raw "constraints"
                |> Result.mapError fromWire
                |> Result.bind (fun constraints ->
                    Decode.uniqueStringListField raw "materialRefs"
                    |> Result.mapError fromWire
                    |> Result.bind (fun materialRefs ->
                        Decode.stringField raw "authorizationRef"
                        |> Result.mapError fromWire
                        |> Result.bind (fun authorizationRef ->
                            Decode.stringField raw "profileRef"
                            |> Result.mapError fromWire
                            |> Result.map (fun profileRef ->
                                { CommandId = commandId
                                  GoalText = goalText
                                  Constraints = constraints
                                  MaterialRefs = materialRefs
                                  AuthorizationRef = authorizationRef
                                  ProfileRef = profileRef }))))))

    let decodeWorkNext (raw: obj) : Result<WorkNextArgs, ToolRefusal> =
        Decode.stringField raw "commandId"
        |> Result.mapError fromWire
        |> Result.bind (fun commandId ->
            Decode.stringField raw "inquiryId"
            |> Result.mapError fromWire
            |> Result.bind (fun inquiryId ->
                Decode.nonNegativeIntegerField raw "limit"
                |> Result.mapError fromWire
                |> Result.bind (fun limit ->
                    if limit < 1L then
                        Error(fromText "limit" "limit must be at least 1")
                    else
                        Ok
                            { CommandId = commandId
                              InquiryId = inquiryId
                              Limit = int limit })))

    /// The keys a worker must never smuggle in with its answer.
    let private forbiddenResultFields =
        [ "certificatePatches"
          "budgetDebit"
          "events"
          "goalRevision"
          "goalAmendment" ]

    /// 只拒绝明确夹带的变动或命令，不把所有冗余键当作非法入参。
    let private rejectReadMutation (raw: obj) : Result<unit, ToolRefusal> =
        match
            (forbiddenResultFields @ [ "command"; "commands" ])
            |> List.tryFind (fun name -> hasField raw name)
        with
        | Some name ->
            Error(
                fromText
                    name
                    (sprintf
                        "field %s requests an additional mutation or command; status and export only read an inquiry"
                        name)
            )
        | None -> Ok()

    let private decodeResultSchema (raw: obj) : Result<SchemaRef, ToolRefusal> =
        let schema = field raw "resultSchema"

        let isRecord =
            emitJsExpr schema "typeof $0 === 'object' && $0 !== null && !Array.isArray($0)"

        if not isRecord then
            Error(fromText "resultSchema" "resultSchema must be an object with id and hash")
        else
            Decode.stringField schema "id"
            |> Result.mapError fromWire
            |> Result.bind (fun schemaId ->
                Decode.stringField schema "hash"
                |> Result.mapError fromWire
                |> Result.map (fun schemaHash -> { Id = schemaId; Hash = schemaHash }))

    let decodeWorkSubmit (raw: obj) : Result<WorkSubmitArgs, ToolRefusal> =
        match forbiddenResultFields |> List.tryFind (fun name -> hasField raw name) with
        | Some name ->
            Error
                { Code = "WORK_RESULT_EXCEEDS_ROLE"
                  Path = name
                  Message =
                    sprintf
                        "field %s is outside what a work result may carry: a worker submits answers, not certificates, budgets, events or goal revisions"
                        name }
        | None ->
            Decode.stringField raw "commandId"
            |> Result.mapError fromWire
            |> Result.bind (fun commandId ->
                Decode.stringField raw "inquiryId"
                |> Result.mapError fromWire
                |> Result.bind (fun inquiryId ->
                    Decode.stringField raw "workId"
                    |> Result.mapError fromWire
                    |> Result.bind (fun workId ->
                        Decode.nonNegativeIntegerField raw "attempt"
                        |> Result.mapError fromWire
                        |> Result.bind (fun attempt ->
                            Attempt.tryCreate attempt
                            |> Result.mapError (fromText "attempt")
                            |> Result.bind (fun typedAttempt ->
                                Decode.stringField raw "fence"
                                |> Result.mapError fromWire
                                |> Result.bind (fun fence ->
                                    Fence.tryCreate fence
                                    |> Result.mapError (fromText "fence")
                                    |> Result.bind (fun typedFence ->
                                        Decode.stringField raw "canonicalResult"
                                        |> Result.mapError fromWire
                                        |> Result.bind (fun canonicalResult ->
                                            decodeResultSchema raw
                                            |> Result.bind (fun schema ->
                                                Decode.stringField raw "clusterId"
                                                |> Result.mapError fromWire
                                                |> Result.map (fun clusterId ->
                                                    { CommandId = commandId
                                                      InquiryId = inquiryId
                                                      WorkId = workId
                                                      Attempt = typedAttempt
                                                      Fence = typedFence
                                                      CanonicalResult = canonicalResult
                                                      ResultSchema = schema
                                                      ClusterId = clusterId }))))))))))

    let decodeStatus (raw: obj) : Result<StatusArgs, ToolRefusal> =
        rejectReadMutation raw
        |> Result.bind (fun () ->
            Decode.stringField raw "inquiryId"
            |> Result.mapError fromWire
            |> Result.map (fun inquiryId -> { InquiryId = inquiryId }))

    let decodeCancel (raw: obj) : Result<CancelArgs, ToolRefusal> =
        Decode.stringField raw "commandId"
        |> Result.mapError fromWire
        |> Result.bind (fun commandId ->
            Decode.stringField raw "inquiryId"
            |> Result.mapError fromWire
            |> Result.bind (fun inquiryId ->
                Decode.stringField raw "reason"
                |> Result.mapError fromWire
                |> Result.map (fun reason ->
                    { CommandId = commandId
                      InquiryId = inquiryId
                      Reason = reason })))

    let decodeExport (raw: obj) : Result<ExportArgs, ToolRefusal> =
        rejectReadMutation raw
        |> Result.bind (fun () -> Decode.stringField raw "inquiryId" |> Result.mapError fromWire)
        |> Result.bind (fun inquiryId ->
            Decode.stringField raw "mode"
            |> Result.mapError fromWire
            |> Result.bind (fun mode ->
                match mode with
                | "summary" ->
                    Ok
                        { InquiryId = inquiryId
                          Mode = ExportMode.Summary }
                | "full" ->
                    Ok
                        { InquiryId = inquiryId
                          Mode = ExportMode.Full }
                | _ ->
                    Error
                        { Code = "EXPORT_MODE_UNKNOWN"
                          Path = "mode"
                          Message = sprintf "mode must be summary or full, not %s" mode }))

    let decodeGoalAmend (raw: obj) : Result<GoalAmendArgs, ToolRefusal> =
        Decode.stringField raw "commandId"
        |> Result.mapError fromWire
        |> Result.bind (fun commandId ->
            Decode.stringField raw "inquiryId"
            |> Result.mapError fromWire
            |> Result.bind (fun inquiryId ->
                Decode.stringField raw "authorizedBy"
                |> Result.mapError fromWire
                |> Result.bind (fun authorizedBy ->
                    Decode.stringField raw "expectedRevision"
                    |> Result.mapError fromWire
                    |> Result.bind (fun expectedRevision ->
                        Decode.uniqueStringListField raw "addedConstraints"
                        |> Result.mapError fromWire
                        |> Result.bind (fun addedConstraints ->
                            let replacement =
                                if hasField raw "replacementText" then
                                    Decode.stringField raw "replacementText"
                                    |> Result.mapError fromWire
                                    |> Result.map Some
                                else
                                    Ok None

                            replacement
                            |> Result.map (fun replacementText ->
                                { CommandId = commandId
                                  InquiryId = inquiryId
                                  AuthorizedBy = authorizedBy
                                  ExpectedRevision = expectedRevision
                                  AddedConstraints = addedConstraints
                                  ReplacementText = replacementText }))))))
