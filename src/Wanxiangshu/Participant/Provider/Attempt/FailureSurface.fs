namespace Wanxiangshu.Participant.Provider.Attempt

open System
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Foundation.Identity

module FailureSurface =

    [<Emit("typeof $0 === 'string'")>]
    let private isString (value: obj) : bool = jsNative

    let private requiredText name (value: obj) =
        if isNull value || not (isString value) then
            invalidArg name $"missing {name}"

        let text: string = unbox value

        if String.IsNullOrWhiteSpace text then
            invalidArg name $"missing {name}"

        text

    let private requestKindOf value =
        match requiredText "requestKind" value with
        | "WorkMain" -> ProviderRequestKind.WorkMain
        | "InteractionRepair" -> ProviderRequestKind.InteractionRepair
        | other -> invalidArg "requestKind" $"unknown provider request kind '{other}'"

    let private statusOf value =
        match requiredText "status" value with
        | "Transient" -> ProviderFailureStatus.Transient
        | "Permanent" -> ProviderFailureStatus.Permanent
        | other -> invalidArg "status" $"unknown provider failure status '{other}'"

    let private failureLabel =
        function
        | ExecutionFailure.ProviderTransient -> "ProviderTransient"
        | ExecutionFailure.ProviderPermanent -> "ProviderPermanent"
        | ExecutionFailure.StreamInterruptedAfterFirstToken -> "StreamInterruptedAfterFirstToken"
        | ExecutionFailure.LocalInvariant
        | ExecutionFailure.ProtocolRejection
        | ExecutionFailure.AuthorizationDenied
        | ExecutionFailure.UserCancelled
        | ExecutionFailure.Superseded
        | ExecutionFailure.CapacityQueueFull
        | ExecutionFailure.AcceptanceUnknown
        | ExecutionFailure.PersistenceFailure _ -> invalidOp "provider mapper returned a non-provider failure"

    let classify (input: obj) : obj =
        if isNull input then
            invalidArg "input" "missing provider failure evidence"

        let evidence =
            { ProviderRun = ProviderRunIdentity.create (requiredText "providerRun" input?providerRun)
              RequestKind = requestKindOf input?requestKind
              Status = statusOf input?status
              FirstTokenObserved = unbox<bool> input?firstTokenObserved
              Diagnostic =
                if isNull input?diagnostic then
                    ""
                else
                    string input?diagnostic }

        let result = ProviderFailure.classify evidence

        box
            {| failure = failureLabel result.Failure
               providerRun = ProviderRunIdentity.value result.ProviderRun
               requestKind = ProviderRequestKind.label result.RequestKind
               firstTokenObserved = result.FirstTokenObserved
               diagnostic = result.Diagnostic |}
