namespace Wanxiangshu.Execution.Delegation.Handle

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// CompletionRecord owns the schema 4 protocol. This module adapts F# identities,
/// supplies journal callbacks and selects records whose lifecycle permits reading.
module HandleCompletionCodec =
    [<Import("CompletionRecord", "wanxiangli/handle-lifecycle")>]
    let private recordClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private library = construct recordClass

    let private optionalValue map value =
        value |> Option.map (map >> box) |> Option.defaultValue null

    let private optionalIdentity create (value: obj) =
        if isNull value then None else Some(create (unbox<string> value))

    let private workValue (work: HandleWorkId) =
        createObj
            [ "childSessionId" ==> SessionId.value work.ChildSessionId
              "authorityRoot" ==> AuthorityRootUserMessageId.value work.AuthorityRoot ]

    let encodeOutcome (runId: string) (outcome: AgentCompletionOutcome) : string =
        let value =
            match outcome with
            | AgentCompleted payload ->
                createObj
                    [ "kind" ==> "completed"
                      "runId" ==> runId
                      "view" ==> payload.View
                      "childSessionId" ==> optionalValue SessionId.value payload.ChildSessionId
                      "authorityRoot" ==> optionalValue AuthorityRootUserMessageId.value payload.AuthorityRoot
                      "providerRun" ==> optionalValue ProviderRunIdentity.value payload.ProviderRun
                      "directory" ==> optionalValue id payload.Directory ]
            | AgentFailed payload ->
                createObj
                    [ "kind" ==> "failed"
                      "runId" ==> runId
                      "code" ==> payload.Code
                      "message" ==> payload.Message
                      "childSessionId" ==> optionalValue SessionId.value payload.ChildSessionId ]
            | AgentAbandoned(agentId, reason) ->
                createObj
                    [ "kind" ==> "abandoned"
                      "runId" ==> runId
                      "agentId" ==> agentId
                      "reason" ==> reason ]

        library?encode(value)

    let private payloadValue (decoded: DurableAgentCompletionV2) =
        match decoded with
        | CompletedV2 payload ->
            createObj
                [ "kind" ==> "completed"
                  "runId" ==> payload.RunId
                  "view" ==> payload.View
                  "childSessionId" ==> payload.ChildSessionId
                  "authorityRoot" ==> payload.AuthorityRoot
                  "providerRun" ==> payload.ProviderRun
                  "directory" ==> payload.Directory ]
        | FailedV2 payload ->
            createObj
                [ "kind" ==> "failed"
                  "runId" ==> payload.RunId
                  "code" ==> payload.Code
                  "message" ==> payload.Message
                  "childSessionId" ==> payload.ChildSessionId ]

    let private payloadOf (value: obj) =
        match unbox<string> value?kind with
        | "completed" ->
            CompletedV2
                { RunId = unbox value?runId
                  View = unbox value?view
                  ChildSessionId = unbox value?childSessionId
                  AuthorityRoot = unbox value?authorityRoot
                  ProviderRun = unbox value?providerRun
                  Directory = unbox value?directory }
        | "failed" ->
            FailedV2
                { RunId = unbox value?runId
                  Code = unbox value?code
                  Message = unbox value?message
                  ChildSessionId = unbox value?childSessionId }
        | kind -> invalidOp ("Unknown completion payload kind: " + kind)

    let private decodeErrorOf (error: obj) =
        match unbox<string> error?kind with
        | "MissingSchemaVersion" -> CompletionDecodeError.MissingSchemaVersion
        | "UnknownSchemaVersion" -> CompletionDecodeError.UnknownSchemaVersion(unbox error?reason)
        | "UnknownFinality" -> CompletionDecodeError.UnknownFinality(unbox error?reason)
        | "MissingFinality" -> CompletionDecodeError.MissingFinality
        | "InvalidJson" -> CompletionDecodeError.InvalidJson(unbox error?reason)
        | "IncompletePayload" -> CompletionDecodeError.IncompletePayload(unbox error?reason)
        | kind -> invalidOp ("Unknown completion decode error: " + kind)

    let decodeBody (json: string) : DurableCompletionDecode =
        let decoded: obj = library?decode(json)

        match unbox<string> decoded?kind with
        | "current" -> Current(payloadOf decoded?payload)
        | "legacy-false-abort" ->
            let payload: obj = decoded?payload

            LegacyFalseAbort
                { Status = unbox payload?status
                  RunId = unbox payload?runId
                  Code = unbox payload?code
                  Message = unbox payload?message
                  ChildSessionId = unbox payload?childSessionId
                  RawBody = unbox payload?rawBody }
        | "invalid" -> Invalid(decodeErrorOf decoded?error)
        | kind -> invalidOp ("Unknown completion decode kind: " + kind)

    let belongsToWork (work: HandleWorkId) (decoded: DurableAgentCompletionV2) : bool =
        library?belongsToWork(workValue work, payloadValue decoded)

    let private runCompletionOf
        (record: HandleRecord)
        (agentId: string)
        (completedAt: DateTimeOffset)
        (payload: obj)
        : RunCompletion =
        let runId: string = unbox payload?runId
        let role = record.CanonicalRole

        let outcome =
            match unbox<string> payload?kind with
            | "completed" ->
                AgentCompleted
                    { AgentId = agentId
                      ChildSessionId = optionalIdentity SessionId.create payload?childSessionId
                      RunId = runId
                      Role = role
                      AuthorityRoot = optionalIdentity AuthorityRootUserMessageId.create payload?authorityRoot
                      ProviderRun = optionalIdentity ProviderRunIdentity.create payload?providerRun
                      View = unbox payload?view
                      Directory = optionalIdentity id payload?directory }
            | "failed" ->
                AgentFailed
                    { AgentId = agentId
                      ChildSessionId = optionalIdentity SessionId.create payload?childSessionId
                      RunId = runId
                      Role = Some role
                      Code = unbox payload?code
                      Message = unbox payload?message }
            | kind -> invalidOp ("Unknown materialized completion kind: " + kind)

        { RunId = runId
          AgentName = record.TargetAgent
          Role = role
          Outcome = outcome
          CompletedAt = completedAt }

    let tryMaterialiseRunCompletion
        (record: HandleRecord)
        (agentId: string)
        (decoded: DurableAgentCompletionV2)
        (completedAt: DateTimeOffset)
        : RunCompletion =
        library?materialize(agentId, payloadValue decoded)
        |> runCompletionOf record agentId completedAt

    let tryDecode
        (record: HandleRecord)
        (agentId: string)
        (json: string)
        (completedAt: DateTimeOffset)
        : Result<RunCompletion, string> =
        let result: obj = library?decodeForWork(optionalValue workValue record.Work, agentId, json)

        if unbox<bool> result?ok then
            Ok(runCompletionOf record agentId completedAt result?value)
        else
            Error(unbox result?error)

    let private readCompletionBody
        (journal: AgentJournalPort)
        (completion: HandleCompletion)
        : Task<Result<obj option, string>> =
        let reference =
            createObj
                [ "ref" ==> optionalValue BlobRef.value completion.CompletionRef
                  "digest" ==> optionalValue BlobDigest.value completion.CompletionDigest ]

        let readBlob (reference: string) : Task<obj> =
            task {
                let! result = journal.ReadBlob(BlobRef.create reference)

                return
                    match result with
                    | Ok body -> box {| ok = true; value = body |}
                    | Error reason -> box {| ok = false; error = reason |}
            }

        task {
            let! result = unbox<Task<obj>> (library?read(reference, readBlob, journal.Sha256))

            if unbox<bool> result?ok then
                let value: obj = result?value
                return Ok(if isNull value then None else Some value)
            else
                return Error(unbox result?error)
        }

    let tryRead
        (journal: AgentJournalPort)
        (record: HandleRecord)
        (agentId: string)
        (completedAt: DateTimeOffset)
        : Task<Result<RunCompletion option, string>> =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin completion ->
            taskResult {
                let! body = readCompletionBody journal completion

                match body with
                | None -> return None
                | Some value ->
                    let! decoded = tryDecode record agentId (unbox value?body) completedAt
                    return Some decoded
            }
        | HandleLifecycle.Active
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> Task.FromResult(Ok None)

    let tryReadBody
        (journal: AgentJournalPort)
        (record: HandleRecord)
        : Task<Result<string option * BlobRef option * BlobDigest option, string>> =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin completion ->
            taskResult {
                let! body = readCompletionBody journal completion

                return
                    match body with
                    | None -> None, None, None
                    | Some value ->
                        Some(unbox value?body),
                        Some(BlobRef.create (unbox value?ref)),
                        Some(BlobDigest.create (unbox value?digest))
            }
        | HandleLifecycle.Active
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> Task.FromResult(Ok(None, None, None))
