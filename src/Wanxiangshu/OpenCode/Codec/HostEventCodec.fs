namespace Wanxiangshu.OpenCode

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Execution.Session.ChatExecution

/// Outputs typed `HostSignal` for the coarse signals:
///   - session.status idle / session.idle
///   - session.status retry
///   - session.deleted
///   - session.error as a non-durable failure wakeup
/// All other raw payloads return None.
module HostEventCodec =

    let private retrySignal (sessionId: SessionId) (raw: obj) : RetrySignal option =
        let properties = if isNull raw then null else raw?properties
        let status = if isNull properties then null else properties?status

        if
            isNull status
            || isNull status?``type``
            || unbox<string> status?``type`` <> "retry"
        then
            None
        else
            let attempt =
                if isNull status?attempt then
                    "unknown"
                else
                    string status?attempt

            let reason =
                if isNull status?message then
                    "provider retry"
                else
                    unbox<string> status?message

            // ARCH-002: the event's `messageID` is deliberately not read. It was
            // taken as the failed assistant message and written into the fallback
            // cursor, which derives a domain fact from an event field. The failed
            // provider run comes from the reconciled snapshot (HOST-004).
            Some
                { SessionId = sessionId
                  Attempt = attempt
                  Failure = ExecutionFailure.ProviderTransient
                  Diagnostic = reason }

    let private statusTypeOf (raw: obj) : string option =
        let properties = raw?properties
        let status = if isNull properties then null else properties?status

        if isNull status || isNull status?``type`` then
            None
        else
            Some(unbox<string> status?``type``)

    let private decodeSessionStatus (raw: obj) : HostSignal option =
        match HostEventEnvelope.trySessionId raw, statusTypeOf raw with
        | None, _ -> None
        | Some sessionId, Some "idle" -> Some(SessionIdle sessionId)
        | Some sessionId, Some "retry" -> retrySignal sessionId raw |> Option.map ProviderRetry
        | _ -> None

    let private parentSessionIdOf (raw: obj) : SessionId option =
        let properties = raw?properties
        let info = if isNull properties then null else properties?info

        let fromInfo =
            if isNull info || isNull info?parentID then
                None
            else
                Some(SessionId.create (unbox<string> info?parentID))

        let fromProperties =
            if isNull properties then
                None
            elif not (isNull properties?parentID) then
                Some(SessionId.create (unbox<string> properties?parentID))
            elif not (isNull properties?parentId) then
                Some(SessionId.create (unbox<string> properties?parentId))
            else
                None

        fromInfo |> Option.orElse fromProperties

    let private decodeSessionDeleted (raw: obj) : HostSignal option =
        match HostEventEnvelope.trySessionId raw with
        | Some sessionId -> Some(SessionDeleted(sessionId, parentSessionIdOf raw))
        | None -> None

    let private errorNameOf (error: obj) : string =
        if isNull error || isNull error?name then
            ""
        else
            unbox<string> error?name

    let private failureReasonOf (error: obj) : string =
        if not (isNull error) && not (isNull error?message) then
            unbox<string> error?message
        elif not (isNull error) && not (isNull error?data) && not (isNull error?data?message) then
            unbox<string> error?data?message
        elif not (isNull error) && emitJsExpr error "typeof $0 === 'string'" then
            unbox<string> error
        else
            "provider failure"


    let private failureOf (error: obj) : ExecutionFailure =
        // Operator decision: every Host-reported error is a provider error.
        // Wanxiangshu classifies nothing at this boundary — retry, budget and
        // context replacement are owned by the provider recovery path. Only two
        // typed control signals stay distinct: operator abort (HOST-002/004,
        // crash-reconciliation-008) and supersede (a newer attempt already owns the fact).
        // Semantic failures such as tool-call errors never reach here: OpenCode
        // resolves them inside its own loop and reports parts, not session errors.
        let errName = errorNameOf error
        let reason = failureReasonOf error
        let isUpstreamAbort = reason.ToLowerInvariant().Contains("upstream_error")

        let fromName () =
            match errName with
            | "MessageAbortedError"
            | "AbortError" -> ExecutionFailure.UserCancelled
            | "TimeoutError" -> ExecutionFailure.ProviderTransient
            | "SupersededError" -> ExecutionFailure.Superseded
            | _ when reason.ToLowerInvariant().Contains("abort") -> ExecutionFailure.UserCancelled
            | _ -> ExecutionFailure.ProviderTransient

        match isUpstreamAbort with
        | true -> ExecutionFailure.ProviderTransient
        | false -> fromName ()

    let private decodeSessionErrorFor (sessionId: SessionId) (raw: obj) : HostSignal option =
        let properties = raw?properties
        let error = if isNull properties then null else properties?error
        let failure = failureOf error

        let observation =
            { SessionId = sessionId
              Failure = failure
              Diagnostic = failureReasonOf error }

        match failure with
        | ExecutionFailure.UserCancelled ->
            // HOST-002/004: operator abort is a typed signal, not a
            // dropped event. It revokes the attempt's idle-derived
            // continuation capability; it must never be mistaken for
            // ProviderFailure (which would wrongly advance fallback).
            Some(AttemptAborted observation)
        | _ -> Some(ProviderFailure observation)

    let private decodeSessionError (raw: obj) : HostSignal option =
        match HostEventEnvelope.trySessionId raw with
        | Some sessionId -> decodeSessionErrorFor sessionId raw
        | None -> None

    let private decodeHostSignal (raw: obj) : HostSignal option =
        match HostEventEnvelope.eventTypeOf raw with
        | "session.status" -> decodeSessionStatus raw
        | "session.idle" -> HostEventEnvelope.trySessionId raw |> Option.map SessionIdle
        | "session.deleted" -> decodeSessionDeleted raw
        | "session.error" -> decodeSessionError raw
        | _ -> None

    let tryDecode (rawInput: obj) : HostSignal option =
        let raw = HostEventEnvelope.unwrap rawInput

        if isNull raw then None else decodeHostSignal raw

    let private fieldText (value: obj) (name: string) =
        if isNull value || isNull value?(name) then
            ""
        else
            string value?(name)

    let private nonEmptyFieldText value name =
        let text = fieldText value name
        if String.IsNullOrWhiteSpace text then None else Some text

    let private messageInfo (raw: obj) =
        let properties = if isNull raw then null else raw?properties
        if isNull properties then null else properties?info

    let private providerStepTerminalInfo (info: obj) =
        let assistant = fieldText info "role" = "assistant"
        let time = if isNull info then null else info?time
        let completed = not (isNull time) && not (isNull time?completed)
        let failed = not (isNull info) && not (isNull info?error)
        assistant && (failed || completed)

    let private physicalParentId (info: obj) =
        nonEmptyFieldText info "parentID" |> Option.map PhysicalUserMessageId.create

    let private providerRunId (info: obj) =
        nonEmptyFieldText info "id" |> Option.map ProviderRunIdentity.create

    let private hasStartedState (info: obj) =
        let time = if isNull info then null else info?time
        not (isNull time) && not (isNull time?created) && not (providerStepTerminalInfo info)

    let private completionFromFinish =
        function
        | Some "stop" -> Some HostProviderCompletion.Stop
        | Some "length" -> Some HostProviderCompletion.Length
        | Some "content-filter" -> Some HostProviderCompletion.ContentFiltered
        | _ -> None

    let tryDecodeExactProviderStart (rawInput: obj) : ExactProviderStartObservation option =
        let raw = HostEventEnvelope.unwrap rawInput
        let info = messageInfo raw

        let sessionId = HostEventEnvelope.tryMessageSessionId raw

        match
            not (isNull raw)
            && HostEventEnvelope.eventTypeOf raw = "message.updated"
            && fieldText info "role" = "assistant"
            && hasStartedState info,
            sessionId,
            physicalParentId info,
            providerRunId info
        with
        | true, Some session, Some physical, Some providerRun ->
            Some
                { SessionId = session
                  PhysicalUserMessageId = physical
                  ProviderRun = providerRun }
        | _ -> None

    let private completionOf (info: obj) : HostProviderCompletion option =
        let time = if isNull info then null else info?time
        let completed = not (isNull time) && not (isNull time?completed)

        nonEmptyFieldText info "finish"
        |> Option.filter (fun _ -> completed && isNull info?error)
        |> completionFromFinish

    let private failureTerminalOutcome failure =
        match failure with
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded ->
            Some(HostProviderTerminalOutcome.Cancelled failure, Some ChatExecutionTerminalDisposition.Cancelled)
        | _ -> Some(HostProviderTerminalOutcome.ProviderFailure failure, None)

    let private terminalOutcomeOf
        (info: obj)
        : (HostProviderTerminalOutcome * ChatExecutionTerminalDisposition option) option =
        match completionOf info with
        | Some completion ->
            Some(HostProviderTerminalOutcome.Completed completion, Some ChatExecutionTerminalDisposition.Completed)
        | None when isNull info || isNull info?error -> None
        | None -> failureOf info?error |> failureTerminalOutcome

    let tryDecodeExactProviderTerminal (rawInput: obj) : ExactProviderTerminalObservation option =
        let raw = HostEventEnvelope.unwrap rawInput
        let info = messageInfo raw

        let sessionId = HostEventEnvelope.tryMessageSessionId raw

        match
            not (isNull raw)
            && HostEventEnvelope.eventTypeOf raw = "message.updated"
            && fieldText info "role" = "assistant",
            sessionId,
            physicalParentId info,
            providerRunId info,
            terminalOutcomeOf info
        with
        | true, Some session, Some physical, Some providerRun, Some(outcome, disposition) ->
            Some
                { SessionId = session
                  PhysicalUserMessageId = physical
                  ProviderRun = providerRun
                  Outcome = outcome
                  Disposition = disposition }
        | _ -> None

    let tryDecodeProviderStepEnd (rawInput: obj) : (SessionId * PhysicalUserMessageId * ProviderRunIdentity) option =
        let raw = HostEventEnvelope.unwrap rawInput
        let info = messageInfo raw

        let isMessageUpdated =
            not (isNull raw) && HostEventEnvelope.eventTypeOf raw = "message.updated"

        let sessionId = HostEventEnvelope.tryMessageSessionId raw

        match isMessageUpdated, providerStepTerminalInfo info, sessionId, physicalParentId info, providerRunId info with
        | true, true, Some sessionId, Some physical, Some providerRun -> Some(sessionId, physical, providerRun)
        | _ -> None

    /// execution-model-routing-007: physical capacity release needs exact execution identity. The
    /// coarse SessionIdle signal has only SessionId and can arrive after a newer
    /// chat.message admission, so it cannot safely release model occupancy.
    /// A terminal assistant message carries parentID = the exact physical user
    /// message that caused that provider execution.
    let tryDecodePhysicalExecutionEnd (rawInput: obj) : (SessionId * PhysicalUserMessageId) option =
        tryDecodeExactProviderTerminal rawInput
        |> Option.bind (fun observation ->
            match observation.Outcome with
            | HostProviderTerminalOutcome.Completed _ -> Some(observation.SessionId, observation.PhysicalUserMessageId)
            | HostProviderTerminalOutcome.Cancelled _
            | HostProviderTerminalOutcome.Interrupted _
            | HostProviderTerminalOutcome.ProviderFailure _ -> None)
