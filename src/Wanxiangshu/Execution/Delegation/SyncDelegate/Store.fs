namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type private CallAcceptanceState =
    | Open of ResizeArray<IDisposable> * HashSet<PhysicalUserMessageId>
    | Closed

type internal SyncDelegateCallAcceptance(admissionRoot: TaskCompletionSource<AuthorityRootUserMessageId option>) =
    let gate = obj ()
    // DSL-MUTABLE: resource — one call owns its registrations and actual accepted physical identities.
    let mutable state =
        Open(ResizeArray<IDisposable>(), HashSet<PhysicalUserMessageId>())

    member _.AttachDisposable(registration: IDisposable) =
        let release =
            lock gate (fun () ->
                match state with
                | Closed -> true
                | Open(resources, _) ->
                    resources.Add registration
                    false)

        if release then
            registration.Dispose()

    member _.Accept(physical: PhysicalUserMessageId, notify: unit -> unit) =
        lock gate (fun () ->
            match state with
            | Closed -> ()
            | Open(_, accepted) ->
                accepted.Add physical |> ignore
                notify ())

    member _.Owns(physical: PhysicalUserMessageId) =
        lock gate (fun () ->
            match state with
            | Closed -> false
            | Open(_, accepted) -> accepted.Contains physical)

    member _.Close() =
        let resources =
            lock gate (fun () ->
                match state with
                | Closed -> [||]
                | Open(resources, _) ->
                    state <- Closed
                    resources.ToArray())

        AsyncSupport.trySetResult admissionRoot None |> ignore

        for resource in resources do
            resource.Dispose()

/// Authority evidence capable of failing one in-flight managed assignment.
type internal SyncDelegateTerminalFailureScope =
    | FreshAuthorityRoot of AuthorityRootUserMessageId
    | ExistingAuthorityContinuation of PhysicalUserMessageId

/// Coarse terminal notifications are authoritative only for a fresh root.
/// Reused continuations require their exact physical prompt on ReconciledTurn.
/// DSL-state-combination: physical — live call resource couples accepted root and exact terminal scope for causal settlement.
and internal SyncDelegateCall =
    {
        Owner: SessionId
        OwnerScope: ReuseScopeId
        Role: SyncDelegateRole
        Delegate: SessionId
        Agent: string
        Invocations: SyncDelegateInvocation list
        AdmissionRoot: TaskCompletionSource<AuthorityRootUserMessageId option>
        Acceptance: SyncDelegateCallAcceptance
        /// Exact physical prompt of this invocation's accepted attempt; retry
        /// attempts of the same call are its ProviderRetryAttempt continuations.
        mutable AcceptedPhysical: PhysicalUserMessageId option
        mutable AcceptedAuthorityRoot: AuthorityRootUserMessageId option
        mutable TerminalFailureScope: SyncDelegateTerminalFailureScope option
        Answer: TaskCompletionSource<Result<string, string>>
    }

/// A single caller's pending invocation to a sync delegate.
/// DSL-state-combination: physical — StartCursor is a process-local resource coordinate fixed after the delegate session exists.
and internal SyncDelegateInvocation =
    {
        Owner: SessionId
        OwnerScope: ReuseScopeId
        Role: SyncDelegateRole
        Charge: string
        ExpectedToolCalls: int option
        PrepareProviderPrompt: unit -> Task<LlmFacing.Document>
        ObserveAdmission: (SyncDelegateObservedAdmission -> unit) option
        CaptureResponse: (SyncDelegateTerminalResponse -> unit) option
        IsCancelled: unit -> bool
        Batch: SyncDelegateBatch option
        Completion: TaskCompletionSource<Result<SyncDelegateInvocationResult, string>>
        /// EXEC-031: XTrace head (one-past last part, 0 when empty) captured at
        /// send. Inclusive start of the per-batch view range; the
        /// exclusive end is the same head captured at completion.
        mutable StartCursor: int64 option
    }

[<RequireQualifiedAccess>]
type internal SyncDelegateAdmission =
    | Waiting
    | Ready of SyncDelegateInvocation list
    | Rejected of string

module private SyncDelegateStoreHelpers =
    [<Emit("$0 === null || $0 === undefined")>]
    let isAbsent (_value: obj) : bool = jsNative

    let sessionKey (sessionId: SessionId) = SessionId.value sessionId
    let scopeKey (scope: ReuseScopeId) = ReuseScopeId.value scope
    let callKey (callId: ToolCallId) = ToolCallId.value callId
    let providerRunKey (providerRun: ProviderRunIdentity) = ProviderRunIdentity.value providerRun

    let roleName (role: SyncDelegateRole) =
        match role with
        | SyncDelegateRole.Inspector -> "Inspector"
        | SyncDelegateRole.Coder -> "Coder"
        | SyncDelegateRole.Engineer -> "Engineer"
        | SyncDelegateRole.DevOps -> "DevOps"

    let callKeys (calls: ToolCallId list) =
        calls |> List.map callKey |> List.toArray

    let rejection (reason: string) =
        match reason with
        | "ActiveBatch" -> "sync delegate rejected: dedicated delegate already has an active batch"
        | "BatchPending" -> "sync delegate rejected: semantic batch already pending"
        | "InvalidBatch" -> "sync delegate rejected: invalid ProviderRun batch"
        | "OtherRunPending" -> "sync delegate rejected: another ProviderRun batch is pending"
        | "DuplicateCall" -> "sync delegate rejected: duplicate ToolCallId in batch"
        | other -> "sync delegate rejected: " + other

open SyncDelegateStoreHelpers

/// EXEC-026/031: process-local SyncDelegate adapter over wanxiangli/delegate-batch.
/// All in-process batch state, dual-index in-flight calls, observations, and retired
/// delegate staging are 100% owned by wanxiangli/delegate-batch autonomous objects.
/// The host retains only a thin resource table mapping pure keys to TCS/IDisposable.
module private SyncDelegatePyramidHelpers =
    [<Emit("$0 === null || $0 === undefined")>]
    let isAbsentOrNull (_v: obj) : bool = jsNative

    let parseDispatchError (resObj: obj) : string =
        if isAbsentOrNull resObj then
            "sync delegate dispatch failed"
        else
            unbox<string> resObj?error

open SyncDelegatePyramidHelpers

module private SyncDelegateBatchInterop =
    [<Import("syncDelegateBatchStore", "wanxiangli/delegate-batch")>]
    let batchStore: obj = jsNative

    [<Import("retiredDelegateRegistry", "wanxiangli/delegate-batch")>]
    let retiredRegistry: obj = jsNative

open SyncDelegateBatchInterop

type internal SyncDelegateCallStore() =
    let gate = obj ()

    let callResources = Dictionary<string, SyncDelegateCall>()
    let callToId = Dictionary<SyncDelegateCall, string>(HashIdentity.Reference)
    let invocationResources = Dictionary<string, SyncDelegateInvocation>()
    let invocationToKey = Dictionary<SyncDelegateInvocation, string>(HashIdentity.Reference)

    let cleanKey (s: string) =
        if isNull s then "" else s.Trim()

    let failInvocationByKey (invKey: string) (error: string) =
        match invocationResources.TryGetValue invKey with
        | true, inv ->
            invocationResources.Remove invKey |> ignore
            invocationToKey.Remove inv |> ignore
            AsyncSupport.trySetResult inv.Completion (Error error) |> ignore
        | false, _ -> ()

    let failCallByKey (cKey: string) (error: string) =
        match callResources.TryGetValue cKey with
        | true, call ->
            callResources.Remove cKey |> ignore
            callToId.Remove call |> ignore
            call.Acceptance.Close()
            AsyncSupport.trySetResult call.Answer (Error error) |> ignore
        | false, _ -> ()

    let lookupInvocations (keys: string array) : SyncDelegateInvocation list =
        lock gate (fun () ->
            keys
            |> Array.toList
            |> List.choose (fun k ->
                match invocationResources.TryGetValue k with
                | true, inv -> Some inv
                | false, _ -> None))

    let removeOwnedCall (cKey: string) (call: SyncDelegateCall) : bool =
        lock gate (fun () ->
            if callResources.ContainsKey cKey then
                callResources.Remove cKey |> ignore
                callToId.Remove call |> ignore
                true
            else
                false)

    let findCall (cKey: string) : SyncDelegateCall option =
        lock gate (fun () ->
            match callResources.TryGetValue cKey with
            | true, call -> Some call
            | false, _ -> None)

    let popCallResource (cKey: string) : SyncDelegateCall option =
        lock gate (fun () ->
            match callResources.TryGetValue cKey with
            | true, call ->
                callResources.Remove cKey |> ignore
                callToId.Remove call |> ignore
                call.Acceptance.Close()
                Some call
            | false, _ -> None)

    member _.ObserveProviderToolCall
        (owner: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, callId: ToolCallId)
        : unit =
        batchStore?observeProviderToolCall(sessionKey owner, providerRunKey providerRun, roleName role, callKey callId)

    member _.TryObservedBatch
        (owner: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, currentCall: ToolCallId)
        : SyncDelegateBatch option =
        let orderObj: obj =
            batchStore?tryObservedBatch(sessionKey owner, providerRunKey providerRun, roleName role, callKey currentCall)

        if isNull orderObj || isAbsent orderObj then
            None
        else
            let order = unbox<string array> orderObj
            Some
                { ProviderRun = providerRun
                  CallOrder = order |> Array.toList |> List.map ToolCallId.create
                  CurrentCall = currentCall }

    member _.TryPeekCallByDelegate(delegateSession: SessionId) : SyncDelegateCall option =
        let callIdObj: obj = batchStore?tryPeekCallByDelegate(sessionKey delegateSession)
        if isAbsentOrNull callIdObj then None
        else findCall (unbox<string> callIdObj)

    member _.TryPopCallByDelegate(delegateSession: SessionId) : SyncDelegateCall option =
        let callIdObj: obj = batchStore?tryPopCallByDelegate(sessionKey delegateSession)
        if isAbsentOrNull callIdObj then None
        else popCallResource (unbox<string> callIdObj)

    member _.TryPopExactCall(call: SyncDelegateCall) : SyncDelegateCall option =
        let cKey =
            lock gate (fun () ->
                match callToId.TryGetValue call with
                | true, k -> k
                | false, _ -> "")

        if String.IsNullOrEmpty cKey then
            None
        else
            let callIdObj: obj = batchStore?tryPopExactCall(sessionKey call.Delegate, cKey)
            if isAbsentOrNull callIdObj then None
            else
                lock gate (fun () ->
                    callResources.Remove cKey |> ignore
                    callToId.Remove call |> ignore)
                call.Acceptance.Close()
                Some call

    member _.FailCall(call: SyncDelegateCall, error: string) : unit =
        call.Acceptance.Close()
        let cKey =
            lock gate (fun () ->
                match callToId.TryGetValue call with
                | true, k ->
                    callResources.Remove k |> ignore
                    callToId.Remove call |> ignore
                    k
                | false, _ -> "")
        if not (String.IsNullOrEmpty cKey) then
            batchStore?tryPopExactCall(sessionKey call.Delegate, cKey) |> ignore
        AsyncSupport.trySetResult call.Answer (Error error) |> ignore

    member _.Admit(invocation: SyncDelegateInvocation) : SyncDelegateAdmission =
        let invKey =
            lock gate (fun () ->
                match invocationToKey.TryGetValue invocation with
                | true, k -> k
                | false, _ ->
                    let k = Guid.NewGuid().ToString("N")
                    invocationToKey.[invocation] <- k
                    invocationResources.[k] <- invocation
                    k)

        let batchObj: obj =
            match invocation.Batch with
            | Some b ->
                createObj
                    [ "providerRun" ==> providerRunKey b.ProviderRun
                      "callOrder" ==> callKeys b.CallOrder
                      "currentCall" ==> callKey b.CurrentCall ]
            | None -> null

        let verdictObj: obj =
            batchStore?admit(scopeKey invocation.OwnerScope, roleName invocation.Role, invKey, batchObj)

        let verdictKind = unbox<string> verdictObj?verdict

        match verdictKind with
        | "reject" ->
            lock gate (fun () ->
                invocationResources.Remove invKey |> ignore
                invocationToKey.Remove invocation |> ignore)
            SyncDelegateAdmission.Rejected(rejection (unbox<string> verdictObj?reason))
        | "single" ->
            SyncDelegateAdmission.Ready [ invocation ]
        | "wait" ->
            SyncDelegateAdmission.Waiting
        | "ready" ->
            let orderedKeys = unbox<string array> verdictObj?invocations
            let orderedInvocations = lookupInvocations orderedKeys
            SyncDelegateAdmission.Ready orderedInvocations
        | other ->
            invalidOp ("wanxiangli/delegate-batch returned an unexpected admission: " + other)

    member _.ReleaseAdmission(ownerScope: ReuseScopeId, role: SyncDelegateRole) : unit =
        batchStore?releaseAdmission(scopeKey ownerScope, roleName role)

    member _.CancelScope(scope: ReuseScopeId) : unit =
        let sKey = scopeKey scope
        let settlement: obj = batchStore?cancelScope sKey

        let cancelledCalls = unbox<string array> settlement?cancelledCalls
        let cancelledInvocations = unbox<string array> settlement?cancelledInvocations

        lock gate (fun () ->
            for invKey in cancelledInvocations do
                failInvocationByKey invKey "Sync delegate call was cancelled"
            for cKey in cancelledCalls do
                failCallByKey cKey "Sync delegate call was cancelled")

    member this.BeginCall
        (
            owner: SessionId,
            ownerScope: ReuseScopeId,
            role: SyncDelegateRole,
            delegateSession: SessionId,
            agent: string,
            invocations: SyncDelegateInvocation list
        ) : Result<SyncDelegateCall * IDisposable, string> =
        let cKey = Guid.NewGuid().ToString("N")
        let invKeys =
            lock gate (fun () ->
                invocations
                |> List.map (fun inv ->
                    match invocationToKey.TryGetValue inv with
                    | true, k -> k
                    | false, _ ->
                        let k = Guid.NewGuid().ToString("N")
                        invocationToKey.[inv] <- k
                        invocationResources.[k] <- inv
                        k))

        let resObj: obj =
            batchStore?beginCall(
                sessionKey owner,
                scopeKey ownerScope,
                roleName role,
                sessionKey delegateSession,
                agent,
                (invKeys |> List.toArray),
                cKey
            )

        let isOk =
            if isAbsentOrNull resObj then false
            else
                match resObj?ok with
                | null -> false
                | v -> unbox<bool> v

        if not isOk then
            Error(parseDispatchError resObj)
        else
            let answer =
                TaskCompletionSource<Result<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

            let admissionRoot =
                TaskCompletionSource<AuthorityRootUserMessageId option>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let call =
                { Owner = owner
                  OwnerScope = ownerScope
                  Role = role
                  Delegate = delegateSession
                  Agent = agent
                  Invocations = invocations
                  AdmissionRoot = admissionRoot
                  Acceptance = SyncDelegateCallAcceptance(admissionRoot)
                  AcceptedPhysical = None
                  AcceptedAuthorityRoot = None
                  TerminalFailureScope = None
                  Answer = answer }

            lock gate (fun () ->
                callResources.[cKey] <- call
                callToId.[call] <- cKey)

            let registration =
                { new IDisposable with
                    member _.Dispose() =
                        let stillOwned = removeOwnedCall cKey call
                        batchStore?tryPopExactCall(sessionKey delegateSession, cKey) |> ignore
                        call.Acceptance.Close()
                        if stillOwned then
                            this.FailCall(call, "Sync delegate call scope disposed") }

            Ok(call, registration)

    member _.TryTakeDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        let sidObj: obj = retiredRegistry?tryTake(scopeKey scope)
        if isNull sidObj || isAbsent sidObj then
            None
        else
            Some (SessionId.create (unbox<string> sidObj))

    member _.TryGetDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        let sidObj: obj = retiredRegistry?tryGet(scopeKey scope)
        if isNull sidObj || isAbsent sidObj then
            None
        else
            Some (SessionId.create (unbox<string> sidObj))

    member _.PutDeletedDelegate(scope: ReuseScopeId, delegateSessionId: SessionId) : SessionId option =
        let prevObj: obj = retiredRegistry?stage(scopeKey scope, sessionKey delegateSessionId)
        if isNull prevObj || isAbsent prevObj then
            None
        else
            Some (SessionId.create (unbox<string> prevObj))

    member _.ClearDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        let sidObj: obj = retiredRegistry?clear(scopeKey scope)
        if isNull sidObj || isAbsent sidObj then
            None
        else
            Some (SessionId.create (unbox<string> sidObj))

    member _.ClearAll() : SessionId list =
        let settlement: obj = batchStore?clearAll()

        let cancelledCalls = unbox<string array> settlement?cancelledCalls
        let cancelledInvocations = unbox<string array> settlement?cancelledInvocations

        lock gate (fun () ->
            for invKey in cancelledInvocations do
                failInvocationByKey invKey "SyncDelegate runtime disposed"
            for cKey in cancelledCalls do
                failCallByKey cKey "SyncDelegate runtime disposed")

        let retiredArr: string array = unbox<string array> (retiredRegistry?clearAll())
        retiredArr |> Array.toList |> List.map SessionId.create
