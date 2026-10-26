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
    /// The delegate callback takes `(name, payload, carry)`; every
    /// other callback returns a data envelope object (never prose).
    val installDelegationHost:
        sessionId: string -> delegation: obj -> fission: obj -> horizon: obj -> unit

    val clearDelegationHost: sessionId: string -> unit
    /// Test hook: the raw value handed over through delivery.
    val recordDeliveredView: sessionId: string -> view: obj -> unit
    /// Test hook: record a delivery carrying the exact call fence that produced it.
    val recordFencedDeliveredView: sessionId: string -> providerRun: string -> toolCallId: string -> view: obj -> unit
    val deliveredReturned: sessionId: string -> string array
    val clearDeliveredReturned: sessionId: string -> unit
    val takeDeliveredHandoffWith: sessionId: string -> assistantProse: string option -> obj

    /// Test hook: consume the handoff only when the cell carries this exact
    /// fence — the provider run AND the tool call id.
    val takeDeliveredHandoffForCall:
        sessionId: string -> providerRun: string -> toolCallId: string -> assistantProse: string option -> obj

    val takeDeliveredHandoff: sessionId: string -> obj
    /// Test hook: observe whether the current cell belongs to this very call.
    val deliveredCellIsMine: sessionId: string -> providerRun: string -> toolCallId: string -> bool
    val deliveredValue: sessionId: string -> obj
    val deliveredViewOf: sessionId: string -> obj
    val deliveredFenceOf: sessionId: string -> obj
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
