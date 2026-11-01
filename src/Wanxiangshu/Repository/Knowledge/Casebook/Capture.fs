namespace Wanxiangshu.Repository.Knowledge.Casebook

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Host
open Wanxiangshu.Repository.Programming.Js
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

type private NativeFileBaseline =
    abstract capture: paths: string array -> Task<obj>
    abstract compare: paths: string array * baseline: string -> Task<obj>

/// CASE-003 / KR-003 / KR-014: typed observation capture and substantive access.
module CasebookCapture =

    type MaintenanceCapture =
        { DiffSummary: string
          Refresh: bool
          TargetState: string }

    [<Import("FileDiff", "wanxiangli/file-baseline")>]
    let private maintenanceLawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private maintenanceLaw: obj = construct maintenanceLawClass

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

    /// knowledge-reuse-014: head/tail truncation is FileDiff.truncate.
    let truncateDiffForBudget (diff: string) (budget: int) : obj = maintenanceLaw?truncate (diff, budget)

    [<Import("FileBaseline", "wanxiangli/file-baseline")>]
    let private baselineClass: obj = jsNative

    [<Emit("new $0($1)")>]
    let private constructBaseline (_constructor: obj) (_options: obj) : NativeFileBaseline = jsNative

    let private createBaseline (store: IEventStore) workspaceRoot =
        let read reference =
            task {
                match! store.ReadPayload(PayloadRef.create reference) with
                | Error error -> return box {| ok = false; error = error |}
                | Ok bytes -> return box {| ok = true; value = Option.toObj bytes |}
            }

        let write (bytes: byte[]) =
            task {
                match! store.WritePayload bytes with
                | Error error -> return box {| ok = false; error = error |}
                | Ok reference -> return box {| ok = true; value = PayloadRef.value reference |}
            }

        constructBaseline
            baselineClass
            (box
                {| resolvePath = JsMutationFs.resolveToolPath workspaceRoot
                   payloads = {| read = read; write = write |} |})

    let private result (decode: obj -> 'T) (value: obj) : Result<'T, string> =
        if value?ok then Ok(decode value?value) else Error(value?error)

    let freezeCompletionState
        (store: IEventStore)
        (workspaceRoot: string)
        (paths: string list)
        : Task<Result<string, string>> =
        task {
            let service = createBaseline store workspaceRoot
            let! captured = service.capture (List.toArray paths)
            return result unbox<string> captured
        }

    let computeMaintenanceDiff
        (store: IEventStore)
        (workspaceRoot: string)
        (paths: string list)
        (baseline: string)
        : Task<Result<MaintenanceCapture, string>> =
        task {
            let service = createBaseline store workspaceRoot
            let! compared = service.compare (List.toArray paths, baseline)

            return
                result
                    (fun value ->
                        { DiffSummary = value?diffSummary
                          Refresh = value?refresh
                          TargetState = value?targetState })
                    compared
        }
