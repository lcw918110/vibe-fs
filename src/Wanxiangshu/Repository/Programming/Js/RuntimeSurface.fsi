namespace Wanxiangshu.Repository.Programming.Js

open System.Threading.Tasks

/// JS runtime owner boundary for bindings and sandbox execution. The mutable
/// as-is write ledger (persisted at each edit/write call) and sandbox failure
/// union remain opaque; observations cross as plain result objects.
[<RequireQualifiedAccess>]
module JsRuntimeSurface =

    val createApi: root: string -> obj

    /// Bindings over an explicit JS capability-label set; unknown labels grant nothing.
    val createApiFor: root: string -> permissionLabels: string array -> obj

    val api: handle: obj -> obj

    /// Test hook: install a process-live delegation host for one session.
    /// Each callback returns a data envelope object (never prose).
    val installDelegationHost:
        sessionId: string -> fork: obj -> join: obj -> fission: obj -> horizon: obj -> advisor: obj -> unit

    val clearDelegationHost: sessionId: string -> unit
    val clearDelegationHosts: unit -> unit
    /// Test hook: the raw value a program handed over through `js.deliver`.
    val recordDeliveredView: sessionId: string -> value: obj -> view: obj -> unit
    val deliveredReturned: sessionId: string -> string array
    val clearDeliveredReturned: sessionId: string -> unit
    val takeDeliveredHandoffWith: sessionId: string -> assistantProse: string option -> obj
    val takeDeliveredHandoff: sessionId: string -> obj
    val deliveredValue: sessionId: string -> obj
    val clearDeliveredValue: sessionId: string -> unit
    val stagedCount: handle: obj -> int
    val stagedKinds: handle: obj -> string array
    val readPaths: handle: obj -> string array

    val run:
        baseClassSource: string ->
        modelSource: string ->
        apiValue: obj ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
            Task<obj>

    /// Start a sandbox program without awaiting it to completion, exposing the
    /// in-flight result promise and a mutable deadline reset. Returns
    /// `{ promise, resetDeadline }`: `promise` resolves to the same
    /// `{ ok, value | code, reason }` shape as `run`; `resetDeadline(epochMs)`
    /// re-arms the async deadline of the in-flight program.
    val start:
        baseClassSource: string ->
        modelSource: string ->
        apiValue: obj ->
        deadlineMs: int ->
        deadlineEpochMs: int64 ->
        outputBoundBytes: int ->
            Task<obj>
