namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Collections.Generic
open System.Threading.Tasks
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

type private PendingBatch =
    { ProviderRun: ProviderRunIdentity
      CallOrder: ToolCallId list
      Items: Dictionary<string, SyncDelegateInvocation> }

type private ObservedProviderRun =
    { Calls: ResizeArray<SyncDelegateRole * ToolCallId>
      Seen: HashSet<string> }

[<RequireQualifiedAccess>]
type internal SyncDelegateAdmission =
    | Waiting
    | Ready of SyncDelegateInvocation list
    | Rejected of string

module private SyncDelegateStoreOps =
    let sessionKey (sessionId: SessionId) = SessionId.value sessionId
    let scopeKey (scope: ReuseScopeId) = ReuseScopeId.value scope
    let keyOf (scope: ReuseScopeId) role = scopeKey scope, role
    let callKey (callId: ToolCallId) = ToolCallId.value callId
    let providerRunKey (providerRun: ProviderRunIdentity) = ProviderRunIdentity.value providerRun

    let observedKey (owner: SessionId) (providerRun: ProviderRunIdentity) =
        sessionKey owner, providerRunKey providerRun

    let sameCallOrder left right =
        List.map callKey left = List.map callKey right

    let failInvocation (invocation: SyncDelegateInvocation) error =
        AsyncSupport.trySetResult invocation.Completion (Error error) |> ignore

    let failInvocations (invocations: seq<SyncDelegateInvocation>) error =
        for invocation in invocations do
            failInvocation invocation error

    let failCalls (failCall: SyncDelegateCall -> string -> unit) (calls: SyncDelegateCall list) error =
        for call in calls do
            failCall call error

    let removeOwnerListIfEmpty
        (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>)
        ownerKey
        (list: ResizeArray<SyncDelegateCall>)
        =
        if list.Count = 0 then
            callsByOwnerScope.Remove ownerKey |> ignore

    let removeOwnerCall
        (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>)
        ownerKey
        (call: SyncDelegateCall)
        =
        match callsByOwnerScope.TryGetValue ownerKey with
        | true, list ->
            list.Remove call |> ignore
            removeOwnerListIfEmpty callsByOwnerScope ownerKey list
        | false, _ -> ()

    let tryRemoveOwnerCall
        (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>)
        ownerKey
        (call: SyncDelegateCall)
        =
        match callsByOwnerScope.TryGetValue ownerKey with
        | true, list ->
            let wasRemoved = list.Remove call
            removeOwnerListIfEmpty callsByOwnerScope ownerKey list
            wasRemoved
        | false, _ -> false

    let removeDelegateIfCurrent
        (callsByDelegate: Dictionary<string, SyncDelegateCall>)
        delegateKey
        (call: SyncDelegateCall)
        =
        match callsByDelegate.TryGetValue delegateKey with
        | true, current when Object.ReferenceEquals(current.Answer, call.Answer) ->
            callsByDelegate.Remove delegateKey |> ignore
        | _ -> ()

    let ownerCallsOf (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>) ownerKey =
        match callsByOwnerScope.TryGetValue ownerKey with
        | true, list -> list
        | false, _ ->
            // DSL-MUTABLE: algorithm-scratch — new call list for dictionary insert
            let list = ResizeArray<SyncDelegateCall>()
            callsByOwnerScope.[ownerKey] <- list
            list

    let cancelPendingBatch (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>) key error =
        failInvocations pendingBatches.[key].Items.Values error
        pendingBatches.Remove key |> ignore

    let cancelActiveBatch
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        key
        error
        =
        failInvocations activeBatches.[key] error
        activeBatches.Remove key |> ignore

    let detachDelegates (callsByDelegate: Dictionary<string, SyncDelegateCall>) (calls: SyncDelegateCall list) =
        for call in calls do
            callsByDelegate.Remove(sessionKey call.Delegate) |> ignore

    let failOwnerScopeCalls
        (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>)
        (callsByDelegate: Dictionary<string, SyncDelegateCall>)
        (failCall: SyncDelegateCall -> string -> unit)
        sKey
        error
        =
        match callsByOwnerScope.TryGetValue sKey with
        | true, list ->
            let calls = list |> Seq.toList
            callsByOwnerScope.Remove sKey |> ignore
            detachDelegates callsByDelegate calls
            failCalls failCall calls error
        | false, _ -> ()

    let batchOfObservedRun providerRun role currentCall (observed: ObservedProviderRun) =
        let callOrder =
            observed.Calls
            |> Seq.choose (fun (observedRole, callId) -> if observedRole = role then Some callId else None)
            |> Seq.toList

        if List.contains currentCall callOrder then
            Some
                { ProviderRun = providerRun
                  CallOrder = callOrder
                  CurrentCall = currentCall }
        else
            None

    let admitSingle
        (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>)
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        key
        (invocation: SyncDelegateInvocation)
        =
        if pendingBatches.ContainsKey key then
            SyncDelegateAdmission.Rejected "sync delegate rejected: semantic batch already pending"
        else
            activeBatches.[key] <- [ invocation ]
            SyncDelegateAdmission.Ready [ invocation ]

    let resolvePendingBatch
        (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>)
        key
        (batch: SyncDelegateBatch)
        =
        match pendingBatches.TryGetValue key with
        | true, existing when
            ProviderRunIdentity.value existing.ProviderRun = ProviderRunIdentity.value batch.ProviderRun
            && sameCallOrder existing.CallOrder batch.CallOrder
            ->
            Ok existing
        | true, _ -> Error "sync delegate rejected: another ProviderRun batch is pending"
        | false, _ ->
            let created =
                { ProviderRun = batch.ProviderRun
                  CallOrder = batch.CallOrder
                  Items = Dictionary<string, SyncDelegateInvocation>() }

            pendingBatches.[key] <- created
            Ok created

    let completeOrWaitBatch
        (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>)
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        key
        (batchState: PendingBatch)
        currentKey
        (invocation: SyncDelegateInvocation)
        =
        batchState.Items.[currentKey] <- invocation

        if batchState.Items.Count <> batchState.CallOrder.Length then
            SyncDelegateAdmission.Waiting
        else
            let ordered =
                batchState.CallOrder
                |> List.map (fun callId -> batchState.Items.[callKey callId])

            pendingBatches.Remove key |> ignore
            activeBatches.[key] <- ordered
            SyncDelegateAdmission.Ready ordered

    let admitBatched
        (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>)
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        key
        (invocation: SyncDelegateInvocation)
        (batch: SyncDelegateBatch)
        =
        let orderedKeys = batch.CallOrder |> List.map callKey
        let currentKey = callKey batch.CurrentCall

        if
            List.isEmpty orderedKeys
            || Set.count (Set.ofList orderedKeys) <> List.length orderedKeys
            || not (List.contains currentKey orderedKeys)
        then
            SyncDelegateAdmission.Rejected "sync delegate rejected: invalid ProviderRun batch"
        else
            match resolvePendingBatch pendingBatches key batch with
            | Error error -> SyncDelegateAdmission.Rejected error
            | Ok batchState when batchState.Items.ContainsKey currentKey ->
                SyncDelegateAdmission.Rejected "sync delegate rejected: duplicate ToolCallId in batch"
            | Ok batchState -> completeOrWaitBatch pendingBatches activeBatches key batchState currentKey invocation

    let admitByBatch
        (pendingBatches: Dictionary<string * SyncDelegateRole, PendingBatch>)
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        key
        (invocation: SyncDelegateInvocation)
        =
        match invocation.Batch with
        | None -> admitSingle pendingBatches activeBatches key invocation
        | Some batch -> admitBatched pendingBatches activeBatches key invocation batch

    let disposeCallRegistration
        gate
        (activeBatches: Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>)
        activeKey
        (callsByOwnerScope: Dictionary<string, ResizeArray<SyncDelegateCall>>)
        ownerKey
        (call: SyncDelegateCall)
        (callsByDelegate: Dictionary<string, SyncDelegateCall>)
        delegateKey
        (failIfOwned: unit -> unit)
        =
        let stillOwned =
            lock gate (fun () ->
                match activeBatches.TryGetValue activeKey with
                | true, current when Object.ReferenceEquals(current, call.Invocations) ->
                    activeBatches.Remove activeKey |> ignore
                | _ -> ()

                let removed = tryRemoveOwnerCall callsByOwnerScope ownerKey call
                removeDelegateIfCurrent callsByDelegate delegateKey call
                removed)

        call.Acceptance.Close()

        if stillOwned then
            failIfOwned ()

open SyncDelegateStoreOps

/// EXEC-026/031: process-local SyncDelegateRuntime state. Semantic batches are
/// defined by ProviderRun + provider tool-call order, never scheduler timing.
type internal SyncDelegateCallStore() as this =
    let gate = obj ()
    /// DSL-cross-callback-proof: physical resource — live call/rendezvous ownership by caller scope
    // DSL-MUTABLE: resource — live calls by owner scope key
    let callsByOwnerScope = Dictionary<string, ResizeArray<SyncDelegateCall>>()
    /// DSL-cross-callback-proof: physical single-flight — dedicated delegate session owns at most one live call
    // DSL-MUTABLE: resource — at most one live call per dedicated delegate session
    let callsByDelegate = Dictionary<string, SyncDelegateCall>()
    /// DSL-cross-callback-proof: physical — retired child identity retained only for draft/session cleanup
    // DSL-MUTABLE: resource — retired delegate ids staged between child and owner SessionDeleted
    let deletedDelegatesByOwnerScope = Dictionary<string, SessionId>()
    /// DSL-cross-callback-proof: physical waiter — rendezvous buffer until the Host-declared ProviderRun call set is complete
    // DSL-MUTABLE: resource — incomplete semantic batches by (scope, role)
    let pendingBatches = Dictionary<string * SyncDelegateRole, PendingBatch>()
    // Host event projection: provider tool parts accumulate in Host order and
    // complement the independently lagging session snapshot view.
    /// DSL-cross-callback-proof: physical resource — accumulated Host tool-call observation for one ProviderRun
    // DSL-MUTABLE: resource — host event projection by (owner, providerRun).
    let observedProviderRuns = Dictionary<string * string, ObservedProviderRun>()
    // DSL-MUTABLE: single-flight — complete/admitted batch through ordinary completion
    let activeBatches =
        Dictionary<string * SyncDelegateRole, SyncDelegateInvocation list>()

    member _.ObserveProviderToolCall
        (owner: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, callId: ToolCallId)
        =
        lock gate (fun () ->
            let key = observedKey owner providerRun

            let observed =
                match observedProviderRuns.TryGetValue key with
                | true, current -> current
                | false, _ ->
                    let created =
                        { Calls = ResizeArray<SyncDelegateRole * ToolCallId>()
                          Seen = HashSet<string>() }

                    observedProviderRuns.[key] <- created
                    created

            if observed.Seen.Add(callKey callId) then
                observed.Calls.Add(role, callId))

    member _.TryObservedBatch
        (owner: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, currentCall: ToolCallId)
        : SyncDelegateBatch option =
        lock gate (fun () ->
            match observedProviderRuns.TryGetValue(observedKey owner providerRun) with
            | false, _ -> None
            | true, observed -> batchOfObservedRun providerRun role currentCall observed)

    member _.TryPeekCallByDelegate(delegateSession: SessionId) : SyncDelegateCall option =
        lock gate (fun () ->
            match callsByDelegate.TryGetValue(sessionKey delegateSession) with
            | true, call -> Some call
            | false, _ -> None)

    member _.TryPopCallByDelegate(delegateSession: SessionId) : SyncDelegateCall option =
        lock gate (fun () ->
            let delegateKey = sessionKey delegateSession

            match callsByDelegate.TryGetValue delegateKey with
            | true, call ->
                callsByDelegate.Remove delegateKey |> ignore
                removeOwnerCall callsByOwnerScope (scopeKey call.OwnerScope) call
                call.Acceptance.Close()
                Some call
            | false, _ -> None)

    member _.TryPopExactCall(call: SyncDelegateCall) : SyncDelegateCall option =
        lock gate (fun () ->
            let delegateKey = sessionKey call.Delegate

            match callsByDelegate.TryGetValue delegateKey with
            | true, current when Object.ReferenceEquals(current, call) ->
                callsByDelegate.Remove delegateKey |> ignore
                removeOwnerCall callsByOwnerScope (scopeKey call.OwnerScope) call
                call.Acceptance.Close()
                Some call
            | _ -> None)

    member _.FailCall(call: SyncDelegateCall, error: string) =
        call.Acceptance.Close()
        AsyncSupport.trySetResult call.Answer (Error error) |> ignore

    member _.Admit(invocation: SyncDelegateInvocation) : SyncDelegateAdmission =
        lock gate (fun () ->
            let key = keyOf invocation.OwnerScope invocation.Role

            if activeBatches.ContainsKey key then
                SyncDelegateAdmission.Rejected "sync delegate rejected: dedicated delegate already has an active batch"
            else
                admitByBatch pendingBatches activeBatches key invocation)

    member _.ReleaseAdmission(ownerScope: ReuseScopeId, role: SyncDelegateRole) =
        lock gate (fun () -> activeBatches.Remove(keyOf ownerScope role) |> ignore)

    member _.CancelScope(scope: ReuseScopeId) =
        lock gate (fun () ->
            let sKey = scopeKey scope
            let cancelError = "Sync delegate call was cancelled"

            let pendingKeys =
                pendingBatches.Keys |> Seq.filter (fun (scope, _) -> scope = sKey) |> Seq.toList

            for key in pendingKeys do
                cancelPendingBatch pendingBatches key cancelError

            let activeKeys =
                activeBatches.Keys |> Seq.filter (fun (scope, _) -> scope = sKey) |> Seq.toList

            for key in activeKeys do
                cancelActiveBatch activeBatches key cancelError

            let observedKeys =
                observedProviderRuns.Keys
                |> Seq.filter (fun (owner, _) -> owner = sKey)
                |> Seq.toList

            for key in observedKeys do
                observedProviderRuns.Remove key |> ignore

            failOwnerScopeCalls
                callsByOwnerScope
                callsByDelegate
                (fun call error -> this.FailCall(call, error))
                sKey
                cancelError)

    member _.BeginCall
        (
            owner: SessionId,
            ownerScope: ReuseScopeId,
            role: SyncDelegateRole,
            delegateSession: SessionId,
            agent: string,
            invocations: SyncDelegateInvocation list
        ) : Result<SyncDelegateCall * IDisposable, string> =
        let ownerKey = scopeKey ownerScope
        let delegateKey = sessionKey delegateSession
        let activeKey = keyOf ownerScope role

        lock gate (fun () ->
            if not (activeBatches.ContainsKey activeKey) then
                Error "sync delegate call was cancelled before dispatch"
            elif callsByDelegate.ContainsKey delegateKey then
                Error "sync delegate rejected: dedicated delegate already has an in-flight call"
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

                (ownerCallsOf callsByOwnerScope ownerKey).Add call
                callsByDelegate.[delegateKey] <- call

                let registration =
                    { new IDisposable with
                        member _.Dispose() =
                            disposeCallRegistration
                                gate
                                activeBatches
                                activeKey
                                callsByOwnerScope
                                ownerKey
                                call
                                callsByDelegate
                                delegateKey
                                (fun () -> this.FailCall(call, "Sync delegate call scope disposed")) }

                Ok(call, registration))

    member _.TryTakeDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        lock gate (fun () ->
            let key = scopeKey scope

            match deletedDelegatesByOwnerScope.TryGetValue key with
            | true, sessionId ->
                deletedDelegatesByOwnerScope.Remove key |> ignore
                Some sessionId
            | false, _ -> None)

    member _.TryGetDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        lock gate (fun () ->
            match deletedDelegatesByOwnerScope.TryGetValue(scopeKey scope) with
            | true, sessionId -> Some sessionId
            | false, _ -> None)

    member _.PutDeletedDelegate(scope: ReuseScopeId, delegateSessionId: SessionId) : SessionId option =
        lock gate (fun () ->
            let key = scopeKey scope

            let previous =
                match deletedDelegatesByOwnerScope.TryGetValue key with
                | true, sessionId -> Some sessionId
                | false, _ -> None

            deletedDelegatesByOwnerScope.[key] <- delegateSessionId
            previous)

    member _.ClearDeletedDelegate(scope: ReuseScopeId) : SessionId option =
        lock gate (fun () ->
            let key = scopeKey scope

            match deletedDelegatesByOwnerScope.TryGetValue key with
            | true, delegateId ->
                deletedDelegatesByOwnerScope.Remove key |> ignore
                Some delegateId
            | false, _ -> None)

    /// Dispose path: fail every live/pending invocation and clear all indexes.
    member _.ClearAll() : SessionId list =
        lock gate (fun () ->
            let disposedError = "SyncDelegate runtime disposed"

            for pending in pendingBatches.Values do
                failInvocations pending.Items.Values disposedError

            for invocations in activeBatches.Values do
                failInvocations invocations disposedError

            for list in callsByOwnerScope.Values do
                failCalls (fun call error -> this.FailCall(call, error)) (list |> Seq.toList) disposedError

            let retiredDelegates = deletedDelegatesByOwnerScope.Values |> Seq.toList

            pendingBatches.Clear()
            activeBatches.Clear()
            observedProviderRuns.Clear()
            callsByOwnerScope.Clear()
            callsByDelegate.Clear()
            deletedDelegatesByOwnerScope.Clear()
            retiredDelegates)
