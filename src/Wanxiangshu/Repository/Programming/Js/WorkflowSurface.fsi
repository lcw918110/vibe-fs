namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks

/// Opaque workflow outcome. The semantic observation functions expose the
/// commit report and failure algebra without leaking JsToolOutcome's union.
[<RequireQualifiedAccess>]
module JsWorkflowSurface =

    val run:
        workspaceRoot: string ->
        role: string ->
        language: string ->
        program: string ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
        store: obj ->
            Task<obj>

    val runWithRecord:
        workspaceRoot: string ->
        role: string ->
        language: string ->
        program: string ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
        record: (string -> string -> Task<obj>) ->
            Task<obj>

    val runObserved:
        workspaceRoot: string ->
        role: string ->
        language: string ->
        program: string ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
        store: obj ->
        fileAccessObservation: obj ->
            Task<obj>

    /// Execute under the three-state input protocol. `input` is one of
    /// `{ program } | { unpark: { handle, value } } | { abandon: { handle } }`.
    val execute:
        workspaceRoot: string ->
        role: string ->
        language: string ->
        sessionId: string ->
        input: obj ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
        store: obj ->
            Task<obj>

    val caseName: value: obj -> string
    val parkedName: value: obj -> obj
    val parkedPayload: value: obj -> obj
    val parkedSource: value: obj -> obj
    val rewritten: value: obj -> string array
    val created: value: obj -> string array
    val removed: value: obj -> string array
    val moved: value: obj -> string array
    val failureCode: value: obj -> obj
    val failureReason: value: obj -> obj
    val persistenceFailure: value: obj -> obj
    val render: value: obj -> string

    /// Test hook: clear the process-live parked-site table.
    val closePrograms: unit -> Task<unit>
