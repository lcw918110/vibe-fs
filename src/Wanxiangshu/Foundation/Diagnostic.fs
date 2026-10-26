namespace Wanxiangshu.Foundation

open System
open System.Text.RegularExpressions
open Fable.Core

/// CTX-014 / HOST-007 runtime diagnostics — two severities only:
///
///   emit  — expected / best-effort. No console. Never a recovery decision.
///   fatal — unexpected invariant break. Print once, then kill the process.
///
/// A log line must never become a recovery protocol (HOST-007). Classification
/// is at the call site: if the world can still continue, call `emit` (or nothing);
/// if the plugin is out of contract, call `fatal`.
module Diagnostic =

    /// HNI P-12: the neutral diagnostic sink. The product's own diagnostic
    /// path stays here; the host supplies a sink that receives the same
    /// records through the neutral `IDiagnosticSink` port.
    type Sink = string -> (string * string) list -> unit

    // DSL-MUTABLE: resource — HNI P-12 的全局诊断汇注册位；宿主在组合期装入自身 sink。
    let mutable private activeSink: Sink option = None

    let installSink (sink: Sink) : unit = activeSink <- Some sink

    let clearSink () : unit = activeSink <- None

    /// CTX-014 允许字段清单。新增字段必须同时登记到白名单与
    /// `tests/unit/Context/ctx014.test.mjs` 的正向用例。
    let AllowedFields =
        set
            [ "session_id"
              "logical_run_id"
              "authority_root_user_message_id"
              "physical_user_message_id"
              "prompt_key"
              "provider_run_identity"
              "provider_run"
              "effective_agent"
              "role"
              "transition_from"
              "transition_to"
              "failure_class"
              "retry_decision"
              "fallback_decision"
              "capacity_state"
              "capacity_fence"
              "recovery_decision"
              "persistence_commitment"
              "hook"
              "policy_class"
              "operation"
              "request_kind"
              "offset"
              "side"
              "armed"
              "squash_attempted"
              "squash_committed"
              "frame_count_before"
              "frame_count_after"
              "cutoff_before"
              "cutoff_after"
              "delta_bytes"
              "result"
              "provider_error"
              "duration"
              // LOOP-010
              "weighted_distinct_token_count"
              "detector_step"
              // negotiate answer delivery (negotiate-steer-delivery)
              "receipt"
              "recipient"
              "delegator"
              "stage"
              "reason" ]

    /// An operation name is one non-blank line (moved from `ReliabilityDiagnostics`, CUT-34).
    let validateOperation (operation: string) =
        not (String.IsNullOrWhiteSpace operation)
        && not (operation.Contains '\n')
        && not (operation.Contains '\r')

    /// `WANXIANGSHU_DIAG=1` makes expected diagnostics observable (moved from `ReliabilityDiagnosticsSurface`, CUT-34).
    let diagnosticsVisible () : bool =
        Environment.GetEnvironmentVariable "WANXIANGSHU_DIAG" = "1"

    /// One line, credentials and absolute paths redacted (moved from `ReliabilityDiagnosticsSurface`, CUT-34).
    let redactText (value: string) =
        let oneLine = Regex.Replace(value, "[\\r\\n].*$", "")

        oneLine
        |> fun text -> Regex.Replace(text, "\\bBearer\\s+[^\\s]+", "Bearer [REDACTED]", RegexOptions.IgnoreCase)
        |> fun text ->
            Regex.Replace(
                text,
                "\\b(api[_-]?key|token|password|cookie|credential)\\s*[:=]\\s*[^\\s,;]+",
                "$1=[REDACTED]",
                RegexOptions.IgnoreCase
            )
        |> fun text -> Regex.Replace(text, "(?:[A-Za-z]:\\\\|/)(?:[^\\s/\\\\]+[/\\\\])+[^\\s,;]+", "[REDACTED]")

    [<Emit("console.error($0)")>]
    let private error (message: string) : unit = jsNative

    [<Emit("JSON.stringify($0)")>]
    let private stringify (value: obj) : string = jsNative

    let private shouldEmit operation =
        validateOperation operation && diagnosticsVisible ()

    let private redactFields (fields: (string * string) list) : (string * string) list =
        fields |> List.map (fun (name, value) -> name, redactText value)

    /// Decides the sink call itself: a sink that throws is dropped, because a
    /// diagnostic must never break product code.
    let private invokeSink (sink: Sink) (operation: string) (fields: (string * string) list) : unit =
        try
            sink operation (redactFields fields)
        with _ ->
            ()

    /// Route one record to the installed sink, if any.
    let private routeToSink (operation: string) (fields: (string * string) list) : unit =
        match activeSink with
        | None -> ()
        | Some sink -> invokeSink sink operation fields

    let private validate (fields: (string * string) list) : unit =
        let illegal =
            fields
            |> List.map fst
            |> List.filter (fun name -> not (Set.contains name AllowedFields))

        if not (List.isEmpty illegal) then
            failwith (sprintf "CTX-014: 诊断字段不在白名单: %A" illegal)

    /// One JSON object: the operation plus its whitelisted fields.
    ///
    /// `Object.fromEntries` over explicit pairs rather than `createObj` over a tuple list: the
    /// shape of what gets printed is the one thing a diagnostic cannot afford to get wrong, and
    /// this spelling is checked by `CTX_014_diagnostic_records_carry_their_fields`.
    [<Emit("Object.fromEntries([['operation', $0]].concat($1))")>]
    let private payloadObject (operation: string) (entries: string array array) : obj = jsNative

    let private payload (operation: string) (fields: (string * string) list) : obj =
        fields
        |> List.map (fun (name, value) -> [| name; redactText value |])
        |> List.toArray
        |> payloadObject operation

    /// Decides whether this record is observable at all: only a valid,
    /// diagnostics-visible operation reaches the console and the sink.
    let private emitWhenVisible (operation: string) (render: unit -> unit) : unit =
        if shouldEmit operation then
            render ()

    /// Expected / best-effort. Validates the CTX-014 whitelist.
    ///
    /// Silent by default, and that default is the clause: HOST-007 forbids a log line from
    /// becoming a recovery protocol, and code that prints on the happy path invites exactly that.
    /// `WANXIANGSHU_DIAG=1` makes the same records observable without changing a single decision —
    /// which is what a stalled run needs, because the alternative measured in practice is a
    /// session parked with no explanation anywhere: every branch that could explain it emitted
    /// into silence.
    let emit (operation: string) (fields: (string * string) list) : unit =
        try
            validate fields

            emitWhenVisible operation (fun () ->
                payload operation fields |> stringify |> error
                routeToSink operation fields)
        with _ ->
            ()

    let private reportFatal (operation: string) (fields: (string * string) list) : unit =
        try
            validate fields
            error (stringify (payload operation fields))
        with _ ->
            error "{\"operation\":\"diagnostic-fatal-render-failed\",\"result\":\"[REDACTED]\"}"

    /// Unexpected invariant break. Reporting failure must not bypass termination.
    let fatal (operation: string) (fields: (string * string) list) : unit =
        try
            reportFatal operation fields
        finally
            FatalProcess.kill ()
