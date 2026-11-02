namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

module internal DelegateValues =
    [<Import("DelegateInvocationRuntime", "wanxiangli/delegate-batch")>]
    let runtimeConstructor: obj = jsNative

    [<Emit("$0.invoke($1,$2)")>]
    let invoke (_runtime: obj) (_request: obj) (_effects: obj) : Task<obj> = jsNative

    [<Emit("$0.handleTurn($1,$2)")>]
    let handleTurn (_runtime: obj) (_observation: obj) (_effects: obj) : Task<bool> = jsNative

    [<Emit("$0.settleCompleted($1,$2)")>]
    let settle (_runtime: obj) (_observation: obj) (_effects: obj) : bool = jsNative

    [<Emit("$0.invokeResponse($1,$2)")>]
    let invokeResponse (_runtime: obj) (_request: obj) (_effects: obj) : Task<obj> = jsNative

    [<Emit("$0.invokeObserved($1,$2,$3)")>]
    let invokeObserved (_runtime: obj) (_request: obj) (_effects: obj) (_notDispatched: string -> obj) : obj = jsNative

    let optional make (value: obj) =
        if isNullOrUndefined value then
            None
        else
            Some(make (unbox value))

    let nullable map =
        Option.map map >> Option.map box >> Option.defaultValue null

    let roleName role =
        match role with
        | SyncDelegateRole.Inspector -> "Inspector"
        | SyncDelegateRole.Coder -> "Coder"
        | SyncDelegateRole.Engineer -> "Engineer"
        | SyncDelegateRole.DevOps -> "DevOps"

    let roleOf name =
        match name with
        | "Inspector" -> SyncDelegateRole.Inspector
        | "Coder" -> SyncDelegateRole.Coder
        | "Engineer" -> SyncDelegateRole.Engineer
        | _ -> SyncDelegateRole.DevOps

    let resultToNative result =
        match result with
        | Ok value -> createObj [ "ok" ==> true; "value" ==> value ]
        | Error error -> createObj [ "ok" ==> false; "error" ==> error ]

    let resultFromNative (value: obj) : Result<string, string> =
        if unbox<bool> value?ok then
            Ok(unbox value?value)
        else
            Error(unbox value?error)

    let invocationFromNative (value: obj) =
        if not (unbox<bool> value?ok) then
            Error(unbox value?error)
        elif not (isNullOrUndefined value?mergedInto) then
            Ok(SyncDelegateInvocationResult.MergedInto(ToolCallId.create (unbox value?mergedInto)))
        else
            Ok(SyncDelegateInvocationResult.View(unbox value?value))

    let invocationToNative result =
        match result with
        | Ok(SyncDelegateInvocationResult.MergedInto key) ->
            createObj [ "ok" ==> true; "mergedInto" ==> ToolCallId.value key ]
        | Ok(SyncDelegateInvocationResult.View view) -> resultToNative (Ok view)
        | Error reason -> resultToNative (Error reason: Result<string, string>)

type internal SyncDelegateTerminalFailureScope =
    | FreshAuthorityRoot of AuthorityRootUserMessageId
    | ExistingAuthorityContinuation of PhysicalUserMessageId

type internal SyncDelegateInvocation(raw: obj) =
    let request: obj = raw?request
    member _.Raw = raw

    member _.ObserveAdmission: (SyncDelegateObservedAdmission -> unit) option =
        DelegateValues.optional id request?observeAdmission

    member _.IsCancelled: unit -> bool = request?isCancelled

    member _.StartCursor
        with get (): int64 option = DelegateValues.optional id raw?cursor
        and set (value: int64 option) = raw?cursor <- DelegateValues.nullable id value

type internal SyncDelegateCallAcceptance(raw: obj) =
    member _.AttachDisposable(registration: IDisposable) : unit =
        raw?attach (fun () -> registration.Dispose())

    member _.Accept(physical: PhysicalUserMessageId, notify: unit -> unit) : unit =
        raw?accept (PhysicalUserMessageId.value physical, notify)

    member _.Owns(physical: PhysicalUserMessageId) : bool =
        raw?owns (PhysicalUserMessageId.value physical)

    member _.Close() : unit = raw?close ()

type internal SyncDelegateCall(raw: obj) =
    member _.Raw = raw
    member _.Owner = SessionId.create (unbox raw?owner)
    member _.OwnerScope = ReuseScopeId.create (unbox raw?scope)
    member _.Role = DelegateValues.roleOf (unbox raw?role)
    member _.Delegate = SessionId.create (unbox raw?session)
    member _.Agent: string = raw?agent

    member _.Invocations =
        (unbox<obj array> raw?invocations)
        |> Array.map SyncDelegateInvocation
        |> Array.toList

    member _.AdmissionRoot: Task<AuthorityRootUserMessageId option> =
        task {
            let! value = unbox<Task<obj>> raw?admission
            return DelegateValues.optional AuthorityRootUserMessageId.create value
        }

    member _.Admit(root: AuthorityRootUserMessageId) : unit =
        raw?admit (AuthorityRootUserMessageId.value root)

    member _.Acceptance = SyncDelegateCallAcceptance raw

    member _.AcceptedPhysical
        with get () = DelegateValues.optional PhysicalUserMessageId.create raw?acceptedPhysical
        and set value = raw?acceptedPhysical <- DelegateValues.nullable PhysicalUserMessageId.value value

    member _.AcceptedAuthorityRoot
        with get () = DelegateValues.optional AuthorityRootUserMessageId.create raw?acceptedRoot
        and set value = raw?acceptedRoot <- DelegateValues.nullable AuthorityRootUserMessageId.value value

    member _.TerminalFailureScope
        with get (): SyncDelegateTerminalFailureScope option =
            let value: obj = raw?failureScope

            if isNullOrUndefined value then
                None
            elif unbox<string> value?kind = "root" then
                Some(FreshAuthorityRoot(AuthorityRootUserMessageId.create (unbox value?root)))
            else
                Some(ExistingAuthorityContinuation(PhysicalUserMessageId.create (unbox value?physical)))
        and set value =
            raw?failureScope <-
                match value with
                | None -> null
                | Some(FreshAuthorityRoot root) ->
                    createObj [ "kind" ==> "root"; "root" ==> AuthorityRootUserMessageId.value root ]
                | Some(ExistingAuthorityContinuation physical) ->
                    createObj
                        [ "kind" ==> "continuation"
                          "physical" ==> PhysicalUserMessageId.value physical ]

type internal SyncDelegateCallStore() =
    let runtime = createNew DelegateValues.runtimeConstructor ()

    let callOption raw =
        if isNullOrUndefined raw then
            None
        else
            Some(SyncDelegateCall raw)

    member _.Raw = runtime

    member _.ObserveProviderToolCall
        (owner: SessionId, run: ProviderRunIdentity, role: SyncDelegateRole, call: ToolCallId)
        : unit =
        runtime?observeProviderToolCall (
            SessionId.value owner,
            ProviderRunIdentity.value run,
            DelegateValues.roleName role,
            ToolCallId.value call
        )

    member _.TryObservedBatch(owner: SessionId, run: ProviderRunIdentity, role: SyncDelegateRole, call: ToolCallId) =
        let order: obj =
            runtime?tryObservedBatch (
                SessionId.value owner,
                ProviderRunIdentity.value run,
                DelegateValues.roleName role,
                ToolCallId.value call
            )

        DelegateValues.optional
            (fun (keys: string array) ->
                { ProviderRun = run
                  CallOrder = keys |> Array.map ToolCallId.create |> Array.toList
                  CurrentCall = call })
            order

    member _.TryPeekCallByDelegate(session: SessionId) =
        runtime?peek (SessionId.value session) |> callOption

    member _.TryPopCallByDelegate(session: SessionId) =
        runtime?pop (SessionId.value session) |> callOption

    member _.TryPopExactCall(call: SyncDelegateCall) =
        runtime?popExact (call.Raw) |> callOption

    member _.FailCall(call: SyncDelegateCall, reason: string) : unit = runtime?fail (call.Raw, reason)

    member _.CancelScope(scope: ReuseScopeId) : unit =
        runtime?cancelScope (ReuseScopeId.value scope)

    member _.TryTakeDeletedDelegate(scope: ReuseScopeId) =
        runtime?takeRetired (ReuseScopeId.value scope)
        |> DelegateValues.optional SessionId.create

    member _.TryGetDeletedDelegate(scope: ReuseScopeId) =
        runtime?getRetired (ReuseScopeId.value scope)
        |> DelegateValues.optional SessionId.create

    member _.PutDeletedDelegate(scope: ReuseScopeId, session: SessionId) =
        runtime?stageRetired (ReuseScopeId.value scope, SessionId.value session)
        |> DelegateValues.optional SessionId.create

    member _.ClearDeletedDelegate(scope: ReuseScopeId) =
        runtime?clearRetired (ReuseScopeId.value scope)
        |> DelegateValues.optional SessionId.create

    member _.ClearAll() : SessionId list =
        (unbox<string array> (runtime?clear ()))
        |> Array.map SessionId.create
        |> Array.toList
