namespace Wanxiangshu.Execution.Delegation.Handle

open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// EXEC-009 durable join payload. Written before `HandleCompleted`; join reads
/// it after a controlled consume. Identity fields that already live on the
/// handle record (`TargetAgent`, `CanonicalRole`, handle id) are not duplicated.
///
/// Clean-break: encode/decode is schemaVersion=2 with finality completed|failed
/// only. Legacy abort blobs decode to `LegacyFalseAbort`, never `RunCompletion`.
module HandleCompletionCodec =

    let private str (value: string) = box value

    /// Encode proven agent terminal as completion blob v2. No aborted branch.
    /// Abandoned is never a durable completion blob (join materialises from lifecycle).
    let encodeOutcome (runId: string) (outcome: AgentCompletionOutcome) : string =
        let fields =
            match outcome with
            | AgentCompleted payload ->
                [ "schemaVersion", box 2
                  "finality", str "completed"
                  "run_id", str runId
                  "view", str payload.View
                  "child_session_id",
                  str (payload.ChildSessionId |> Option.map SessionId.value |> Option.defaultValue "")
                  "authority_root",
                  str (
                      payload.AuthorityRoot
                      |> Option.map AuthorityRootUserMessageId.value
                      |> Option.defaultValue ""
                  )
                  "provider_run",
                  str (
                      payload.ProviderRun
                      |> Option.map ProviderRunIdentity.value
                      |> Option.defaultValue ""
                  )
                  "directory", str (payload.Directory |> Option.defaultValue "") ]
            | AgentFailed payload ->
                [ "schemaVersion", box 2
                  "finality", str "failed"
                  "run_id", str runId
                  "code", str payload.Code
                  "message", str payload.Message
                  "child_session_id",
                  str (payload.ChildSessionId |> Option.map SessionId.value |> Option.defaultValue "") ]
            | AgentAbandoned(agentId, reason) ->
                // Abandoned is not a durable completion blob; keep a minimal failed v2
                // shape only if a caller mistakenly encodes one (should not reach journal).
                [ "schemaVersion", box 2
                  "finality", str "failed"
                  "run_id", str runId
                  "code", str "ABANDONED"
                  "message", str reason
                  "child_session_id", str ""
                  "agent_id", str agentId ]

        Wanxiangshu.Host.CanonicalJson.canonicalJson (createObj fields)

    let private coerceFieldValue (value: obj) : string =
        if isNull value then
            ""
        elif emitJsExpr value "typeof $0 === 'string'" then
            unbox<string> value
        else
            string value

    let private field (raw: obj) (key: string) : string =
        let hasKey: bool =
            emitJsExpr (raw, key) "$0 != null && Object.prototype.hasOwnProperty.call($0, $1)"

        if not hasKey then "" else coerceFieldValue (raw?(key))

    let private fieldOpt (raw: obj) (key: string) : string option =
        let hasKey: bool =
            emitJsExpr (raw, key) "$0 != null && Object.prototype.hasOwnProperty.call($0, $1)"

        if not hasKey then None else Some(field raw key)

    let private optId create value =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(create value)

    let private runIdOrDefault (agentId: string) (runId: string) =
        if String.IsNullOrWhiteSpace runId then
            "run-" + agentId
        else
            runId

    let private directoryOpt (directory: string) =
        if String.IsNullOrWhiteSpace directory then
            None
        else
            Some directory

    let private legacyAbortPayload (runId: string) (json: string) (raw: obj) =
        LegacyFalseAbort
            { Status = "aborted"
              RunId = runId
              Code = field raw "code"
              Message = field raw "message"
              ChildSessionId = field raw "child_session_id"
              RawBody = json }

    let private completedV2 (runId: string) (raw: obj) =
        Current(
            CompletedV2
                { RunId = runId
                  View = field raw "view"
                  ChildSessionId = field raw "child_session_id"
                  AuthorityRoot = field raw "authority_root"
                  ProviderRun = field raw "provider_run"
                  Directory = field raw "directory" }
        )

    let private failedV2 (runId: string) (raw: obj) =
        Current(
            FailedV2
                { RunId = runId
                  Code = field raw "code"
                  Message = field raw "message"
                  ChildSessionId = field raw "child_session_id" }
        )

    let private decodePreV2Status (status: string) (runId: string) (raw: obj) =
        match status with
        | "completed" -> completedV2 runId raw
        | "failed" -> failedV2 runId raw
        | _ -> Invalid(CompletionDecodeError.UnknownFinality status)

    let private decodeV2Finality (finalityField: string option) (runId: string) (raw: obj) (json: string) =
        match finalityField with
        | None -> Invalid CompletionDecodeError.MissingFinality
        | Some "completed" -> completedV2 runId raw
        | Some "failed" -> failedV2 runId raw
        | Some "aborted" -> legacyAbortPayload runId json raw
        | Some other -> Invalid(CompletionDecodeError.UnknownFinality other)

    let private decodeBySchema
        (schemaVersion: string option)
        (status: string)
        (finalityField: string option)
        (runId: string)
        (raw: obj)
        (json: string)
        =
        match schemaVersion with
        | None when status = "completed" || status = "failed" || status = "abandoned" ->
            decodePreV2Status status runId raw
        | None -> Invalid CompletionDecodeError.MissingSchemaVersion
        | Some version when version <> "2" -> Invalid(CompletionDecodeError.UnknownSchemaVersion version)
        | Some _ -> decodeV2Finality finalityField runId raw json

    let private decodeParsed (raw: obj) (json: string) =
        let schemaVersion = fieldOpt raw "schemaVersion"
        let status = field raw "status"
        let finalityField = fieldOpt raw "finality"
        let runId = field raw "run_id"

        if status = "aborted" then
            legacyAbortPayload runId json raw
        else
            decodeBySchema schemaVersion status finalityField runId raw json

    /// Decode blob → DurableCompletionDecode (v2 Current | LegacyFalseAbort | Invalid).
    /// Never constructs RunCompletion for legacy abort.
    let decodeBody (json: string) : DurableCompletionDecode =
        try
            let raw = JS.JSON.parse json
            decodeParsed raw json
        with ex ->
            Invalid(CompletionDecodeError.InvalidJson ex.Message)

    let belongsToWork (work: HandleWorkId) decoded =
        match decoded with
        | CompletedV2 payload ->
            payload.ChildSessionId = SessionId.value work.ChildSessionId
            && payload.AuthorityRoot = AuthorityRootUserMessageId.value work.AuthorityRoot
            && not (String.IsNullOrWhiteSpace payload.ProviderRun)
        | FailedV2 payload -> payload.ChildSessionId = SessionId.value work.ChildSessionId

    /// Materialise RunCompletion only from Current v2 (or pre-v2 completed/failed).
    /// `completedAt` is caller-minted — codec must not invent wall time.
    let tryMaterialiseRunCompletion
        (record: HandleRecord)
        (agentId: string)
        (decoded: DurableAgentCompletionV2)
        (completedAt: DateTimeOffset)
        : RunCompletion =
        let role = record.CanonicalRole

        match decoded with
        | CompletedV2 payload ->
            let runId = runIdOrDefault agentId payload.RunId

            { RunId = runId
              AgentName = record.TargetAgent
              Role = role
              Outcome =
                AgentCompleted
                    { AgentId = agentId
                      ChildSessionId = optId SessionId.create payload.ChildSessionId
                      RunId = runId
                      Role = role
                      AuthorityRoot = optId AuthorityRootUserMessageId.create payload.AuthorityRoot
                      ProviderRun = optId ProviderRunIdentity.create payload.ProviderRun
                      View = payload.View
                      Directory = directoryOpt payload.Directory }
              CompletedAt = completedAt }
        | FailedV2 payload ->
            let runId = runIdOrDefault agentId payload.RunId

            { RunId = runId
              AgentName = record.TargetAgent
              Role = role
              Outcome =
                AgentFailed
                    { AgentId = agentId
                      ChildSessionId = optId SessionId.create payload.ChildSessionId
                      RunId = runId
                      Role = Some role
                      Code = payload.Code
                      Message = payload.Message }
              CompletedAt = completedAt }

    let private decodeErrorReason (err: CompletionDecodeError) =
        match err with
        | CompletionDecodeError.MissingSchemaVersion -> "missing schemaVersion"
        | CompletionDecodeError.UnknownSchemaVersion v -> sprintf "unknown schemaVersion: %s" v
        | CompletionDecodeError.UnknownFinality f -> sprintf "unknown finality: %s" f
        | CompletionDecodeError.MissingFinality -> "missing finality"
        | CompletionDecodeError.InvalidJson r -> sprintf "invalid json: %s" r
        | CompletionDecodeError.IncompletePayload r -> r

    /// Rebuild a `RunCompletion` from durable handle identity + blob body.
    /// Legacy abort → Error (never agent aborted finality).
    /// `completedAt` is caller-minted — codec must not invent wall time.
    let tryDecode
        (record: HandleRecord)
        (agentId: string)
        (json: string)
        (completedAt: DateTimeOffset)
        : Result<RunCompletion, string> =
        match decodeBody json with
        | Current decoded when record.Work |> Option.forall (fun work -> belongsToWork work decoded) ->
            Ok(tryMaterialiseRunCompletion record agentId decoded completedAt)
        | Current _ -> Error "completion body does not belong to its exact work"
        | LegacyFalseAbort _ -> Error "legacy false abort is not a joinable completion"
        | Invalid err -> Error(sprintf "completion blob decode failed: %s" (decodeErrorReason err))

    let private assertBlobDigest (sha256: string -> string) (body: string) (expectedDigest: BlobDigest) =
        if sha256 body <> BlobDigest.value expectedDigest then
            Error(sprintf "completion blob digest mismatch: %s" (BlobDigest.value expectedDigest))
        else
            Ok body

    let private readVerifiedBlob
        (journal: AgentJournalPort)
        (blobRef: BlobRef)
        (expectedDigest: BlobDigest)
        : System.Threading.Tasks.Task<Result<string, string>> =
        taskResult {
            let! body = journal.ReadBlob blobRef
            return! assertBlobDigest journal.Sha256 body expectedDigest
        }

    let private tryReadCompletedAwaiting
        (journal: AgentJournalPort)
        (record: HandleRecord)
        (agentId: string)
        (completedAt: DateTimeOffset)
        (completion: HandleCompletion)
        : System.Threading.Tasks.Task<Result<RunCompletion option, string>> =
        taskResult {
            match completion.CompletionRef, completion.CompletionDigest with
            | None, None -> return None
            | Some blobRef, Some expectedDigest ->
                let! body = readVerifiedBlob journal blobRef expectedDigest
                let! decoded = tryDecode record agentId body completedAt
                return Some decoded
            | Some _, None
            | None, Some _ -> return! Error "completion blob ref/digest pair is incomplete"
        }

    /// Read blob body from journal when the handle carries a completion ref.
    let tryRead
        (journal: AgentJournalPort)
        (record: HandleRecord)
        (agentId: string)
        (completedAt: DateTimeOffset)
        : System.Threading.Tasks.Task<Result<RunCompletion option, string>> =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin completion ->
            tryReadCompletedAwaiting journal record agentId completedAt completion
        | HandleLifecycle.Active
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> System.Threading.Tasks.Task.FromResult(Ok None)

    let private tryReadBodyCompletedAwaiting
        (journal: AgentJournalPort)
        (completion: HandleCompletion)
        : System.Threading.Tasks.Task<Result<string option * BlobRef option * BlobDigest option, string>> =
        taskResult {
            match completion.CompletionRef, completion.CompletionDigest with
            | None, None -> return (None, None, None)
            | Some blobRef, Some expectedDigest ->
                let! body = readVerifiedBlob journal blobRef expectedDigest
                return (Some body, Some blobRef, Some expectedDigest)
            | Some _, None
            | None, Some _ -> return! Error "completion blob ref/digest pair is incomplete"
        }

    /// Read raw blob body for decode-first JoinDrain path.
    let tryReadBody
        (journal: AgentJournalPort)
        (record: HandleRecord)
        : System.Threading.Tasks.Task<Result<string option * BlobRef option * BlobDigest option, string>> =
        match record.Lifecycle with
        | HandleLifecycle.CompletedAwaitingJoin completion -> tryReadBodyCompletedAwaiting journal completion
        | HandleLifecycle.Active
        | HandleLifecycle.Abandoned _
        | HandleLifecycle.Retired -> System.Threading.Tasks.Task.FromResult(Ok(None, None, None))
