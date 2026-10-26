namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Repository.Programming.Js
open Thoth.Json
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Persistence.EventStore

/// knowledge-reuse-003: which accesses count and the relatedPaths normal form are
/// wanxiangli/casebook-access `CasebookAccessLaw` (CUT-27); this keeps only the
/// per-session accumulator.
module private CasebookAccessLawInstance =
    [<Import("CasebookAccessLaw", "wanxiangli/casebook-access")>]
    let private accessLawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let law: obj = construct accessLawClass

    let accessPaths (access: obj) : string array =
        unbox<string array> (law?accessPaths access)

[<Import("AccessTracker", "wanxiangli/casebook-access")>]
type private LibraryAccessTracker() =
    member _.recordRead(path: string, contentHash: string) : unit = jsNative
    member _.recordCreate(path: string) : unit = jsNative
    member _.recordEdit(path: string) : unit = jsNative
    member _.recordDelete(path: string) : unit = jsNative
    member _.recordMove(source: string, destination: string) : unit = jsNative
    member _.recordGrep(pattern: string, path: string) : unit = jsNative
    member _.recordGlob(pattern: string) : unit = jsNative
    member _.recordAttemptedMutation(path: string, committed: bool) : unit = jsNative
    member _.getRelatedPaths() : string array = jsNative

    member _.RecordRead(path: string, contentHash: string) : unit = jsNative
    member _.RecordCreate(path: string) : unit = jsNative
    member _.RecordEdit(path: string) : unit = jsNative
    member _.RecordDelete(path: string) : unit = jsNative
    member _.RecordMove(source: string, destination: string) : unit = jsNative
    member _.RecordGrep(pattern: string, path: string) : unit = jsNative
    member _.RecordGlob(pattern: string) : unit = jsNative
    member _.RecordAttemptedMutation(path: string, committed: bool) : unit = jsNative
    member _.GetRelatedPaths() : string array = jsNative

/// Access tracker for substantive file access collection (thin proxy to wanxiangli/casebook-access).
type AccessTracker() =
    let inner = LibraryAccessTracker()

    member _.recordRead(path: string, contentHash: string) : unit = inner.RecordRead(path, contentHash)
    member _.recordCreate(path: string) : unit = inner.RecordCreate(path)
    member _.recordEdit(path: string) : unit = inner.RecordEdit(path)
    member _.recordDelete(path: string) : unit = inner.RecordDelete(path)
    member _.recordMove(source: string, destination: string) : unit = inner.RecordMove(source, destination)
    member _.recordGrep(pattern: string, path: string) : unit = inner.RecordGrep(pattern, path)
    member _.recordGlob(pattern: string) : unit = inner.RecordGlob(pattern)
    member _.recordAttemptedMutation(path: string, committed: bool) : unit = inner.RecordAttemptedMutation(path, committed)
    member _.getRelatedPaths() : string array = inner.GetRelatedPaths()

    member _.RecordRead(path: string, contentHash: string) : unit = inner.RecordRead(path, contentHash)
    member _.RecordCreate(path: string) : unit = inner.RecordCreate(path)
    member _.RecordEdit(path: string) : unit = inner.RecordEdit(path)
    member _.RecordDelete(path: string) : unit = inner.RecordDelete(path)
    member _.RecordMove(source: string, destination: string) : unit = inner.RecordMove(source, destination)
    member _.RecordGrep(pattern: string, path: string) : unit = inner.RecordGrep(pattern, path)
    member _.RecordGlob(pattern: string) : unit = inner.RecordGlob(pattern)
    member _.RecordAttemptedMutation(path: string, committed: bool) : unit = inner.RecordAttemptedMutation(path, committed)
    member _.GetRelatedPaths() : string list = inner.GetRelatedPaths() |> Array.toList

/// CASE-003 / KR-003 / KR-014: typed observation capture and substantive access.
module CasebookCapture =

    type MaintenanceCapture =
        { DiffSummary: string
          Refresh: bool
          TargetState: string }

    [<Import("CasebookMaintenanceLaw", "wanxiangli/casebook-maintenance")>]
    let private maintenanceLawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private maintenanceLaw: obj = construct maintenanceLawClass

    [<RequireQualifiedAccess>]
    type private StoredFileState =
        | Missing
        | Present of sha256: string * payload: PayloadRef

    type private CapturedFile =
        { Bytes: byte[]
          Text: string
          Sha256: string }

    /// Stable content fingerprint for FileRead observations (CASE-003).
    let contentHash (text: string) : string =
        if isNull text then "" else HostDigest.sha256Hex text

    let private text (value: obj) : string option =
        if isNull value || value = null then
            None
        else
            Some(string value)

    /// §63 executor reads (cat/head/tail/sed single-file reads) are
    /// wanxiangli/casebook-access `execRead`.
    let ofExecCommand (command: string) : Observation option =
        if isNull command then
            None
        else
            CasebookAccessLawInstance.law?execRead command
            |> unbox<string>
            |> Option.ofObj
            |> Option.map (fun file -> Observation.FileRead(file, contentHash ""))

    // ---- Substantive Access & Dual Baselines (KR-003, KR-004, KR-010, KR-014) ----

    let isSubstantiveTool (toolName: string) : bool =
        unbox<bool> (CasebookAccessLawInstance.law?isSubstantiveTool (string toolName))

    let createAccessTracker () : AccessTracker = AccessTracker()

    /// Tool results → paths (committed create/rewrite/delete/move only) is
    /// wanxiangli/casebook-access `toolPaths`.
    let recordSubstantiveAccess (tracker: AccessTracker) (toolName: string) (args: obj) (committed: bool) : unit =
        for path in unbox<string array> (CasebookAccessLawInstance.law?toolPaths (string toolName, args, committed)) do
            tracker.RecordAttemptedMutation(path, true)

    let mergeFissionSubstantiveAccess (preFission: string list) (laneAccesses: string list list) : string list =
        unbox<string array> (
            CasebookAccessLawInstance.law?relatedPaths (Seq.append preFission (Seq.concat laneAccesses) |> Seq.toArray)
        )
        |> Array.toList

    let caseIdentityForInvocation (sessionId: string) (invocationId: string) : string =
        sprintf "%s:%s" sessionId invocationId

    /// knowledge-reuse-014: head/tail truncation is wanxiangli/casebook-maintenance `truncate`.
    let truncateDiffForBudget (diff: string) (budget: int) : obj = maintenanceLaw?truncate (diff, budget)

    [<Import("readFileSync", "node:fs")>]
    let private readFileBytes (path: string) : byte[] = jsNative

    [<Emit("new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode($0)")>]
    let private decodeFileUtf8 (bytes: byte[]) : string = jsNative

    let private capturedFileContent path (bytes: byte[]) (text: string) : Result<CapturedFile, string> =
        if System.Text.Encoding.UTF8.GetBytes text <> bytes then
            Error(sprintf "file %s did not preserve its UTF-8 bytes" path)
        else
            Ok
                { Bytes = bytes
                  Text = text
                  Sha256 = contentHash text }

    let private decodeFileContent path (bytes: byte[]) : Result<CapturedFile, string> =
        try
            decodeFileUtf8 bytes |> capturedFileContent path bytes
        with _ ->
            Error(sprintf "failed to read file %s: invalid UTF-8" path)

    /// knowledge-reuse-016: only ENOENT is Missing (wanxiangli/casebook-maintenance `readFailure`).
    let private classifyFileReadFailure path (error: exn) =
        if unbox<string> (maintenanceLaw?readFailure (error?code)) = "missing" then
            Ok None
        else
            Error(sprintf "failed to read file %s: %s" path error.Message)

    let private readFileState (workspaceRoot: string) (relPath: string) : Result<CapturedFile option, string> =
        let fullPath = JsMutationFs.resolveToolPath workspaceRoot relPath

        try
            readFileBytes fullPath |> decodeFileContent relPath |> Result.map Some
        with ex ->
            classifyFileReadFailure relPath ex

    let private writePayloadEntry
        (store: IEventStore)
        (relPath: string)
        (content: CapturedFile)
        : Task<Result<string * JsonValue, string>> =
        task {
            match! store.WritePayload content.Bytes with
            | Error err -> return Error(sprintf "failed to write payload for %s: %s" relPath err)
            | Ok payloadRef ->
                let entryObj =
                    Encode.object
                        [ "kind", Encode.string "Present"
                          "payloadRef", Encode.string (PayloadRef.value payloadRef)
                          "payload_ref", Encode.string (PayloadRef.value payloadRef)
                          "sha256", Encode.string content.Sha256
                          "contentHash", Encode.string content.Sha256 ]

                return Ok(relPath, entryObj)
        }

    let private writeFileState (store: IEventStore) (relPath: string) (content: CapturedFile option) =
        match content with
        | None -> Task.FromResult(Ok(relPath, Encode.object [ "kind", Encode.string "Missing" ]))
        | Some captured -> writePayloadEntry store relPath captured

    let private freezeSinglePath
        (store: IEventStore)
        (workspaceRoot: string)
        (relPath: string)
        : Task<Result<string * JsonValue, string>> =
        match readFileState workspaceRoot relPath with
        | Error err -> Task.FromResult(Error err)
        | Ok content -> writeFileState store relPath content

    let rec private continueFreezeLoop store workspaceRoot rest (entries: ResizeArray<string * JsonValue>) =
        function
        | Error err -> Task.FromResult(Error err)
        | Ok entry ->
            entries.Add entry
            freezePathsLoop store workspaceRoot rest entries

    and private freezePathsLoop
        (store: IEventStore)
        (workspaceRoot: string)
        (paths: string list)
        (entries: ResizeArray<string * JsonValue>)
        : Task<Result<string, string>> =
        match paths with
        | [] ->
            let canonicalJson =
                entries |> Seq.sortBy fst |> Seq.toList |> Encode.object |> Encode.toString 0

            Task.FromResult(Ok canonicalJson)
        | relPath :: rest ->
            task {
                let! step = freezeSinglePath store workspaceRoot relPath
                return! continueFreezeLoop store workspaceRoot rest entries step
            }

    let freezeCompletionState
        (store: IEventStore)
        (workspaceRoot: string)
        (paths: string list)
        : Task<Result<string, string>> =
        let distinctPaths = paths |> List.distinct |> List.sort
        let entries = ResizeArray<string * JsonValue>()
        freezePathsLoop store workspaceRoot distinctPaths entries

    let private storedEntryDecoder: Decoder<StoredFileState> =
        Decode.field "kind" Decode.string
        |> Decode.andThen (function
            | "Missing" -> Decode.succeed StoredFileState.Missing
            | "Present" ->
                Decode.map2
                    (fun hash payload -> StoredFileState.Present(hash, PayloadRef.create payload))
                    (Decode.field "sha256" Decode.string)
                    (Decode.field "payloadRef" Decode.string)
            | kind -> Decode.fail (sprintf "unknown file state %s" kind))

    let private decodeBaseline paths baseline =
        match Decode.fromString (Decode.keyValuePairs storedEntryDecoder) baseline with
        | Error err -> Error("invalid maintenance baseline: " + err)
        | Ok entries when Set.ofList (List.map fst entries) <> Set.ofList paths ->
            Error "maintenance baseline does not cover the related paths"
        | Ok entries -> Ok(List.sortBy fst entries)

    let private requireStoredPayload path =
        function
        | None -> Error(sprintf "missing baseline payload for %s" path)
        | Some bytes -> Ok bytes

    let private validateStoredHash path hash (content: CapturedFile) =
        if content.Sha256 = hash then
            Ok()
        else
            Error(sprintf "baseline payload digest mismatch for %s" path)

    let private readPresentContent (store: IEventStore) path hash payload =
        taskResult {
            let! stored = store.ReadPayload payload
            let! bytes = requireStoredPayload path stored |> Task.FromResult
            let! content = decodeFileContent path bytes |> Task.FromResult
            do! validateStoredHash path hash content |> Task.FromResult
            return Some content.Text
        }

    let private readStoredContent store path =
        function
        | StoredFileState.Missing -> Task.FromResult(Ok None)
        | StoredFileState.Present(hash, payload) -> readPresentContent store path hash payload

    /// knowledge-reuse-005/015/016: the B→T file diff is wanxiangli/casebook-maintenance
    /// `CasebookMaintenanceLaw.fileDiff` (CUT-27); null is Missing.
    let private fileDiff (path: string) (before: string option) (after: string option) : string =
        unbox<string> (maintenanceLaw?fileDiff (path, Option.toObj before, Option.toObj after))

    let rec private captureMaintenancePaths store workspaceRoot entries captured diffs =
        taskResult {
            match entries with
            | [] ->
                let summary = maintenanceLaw?summarize (diffs |> List.rev |> List.toArray)

                return
                    { DiffSummary = unbox<string> summary?diffSummary
                      Refresh = unbox<bool> summary?refresh
                      TargetState = captured |> List.rev |> Encode.object |> Encode.toString 0 }
            | (path, state) :: rest ->
                let! before = readStoredContent store path state
                let! target = readFileState workspaceRoot path |> Task.FromResult
                let! stored = writeFileState store path target
                let diff = fileDiff path before (target |> Option.map (fun content -> content.Text))
                return! captureMaintenancePaths store workspaceRoot rest (stored :: captured) (diff :: diffs)
        }

    let computeMaintenanceDiff
        (store: IEventStore)
        (workspaceRoot: string)
        (paths: string list)
        (baseline: string)
        : Task<Result<MaintenanceCapture, string>> =
        taskResult {
            let! entries = decodeBaseline paths baseline |> Task.FromResult
            return! captureMaintenancePaths store workspaceRoot entries [] []
        }
