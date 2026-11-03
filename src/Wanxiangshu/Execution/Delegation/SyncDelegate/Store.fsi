namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation.Identity

module internal DelegateValues =
    [<Import("DelegateInvocationRuntime", "wanxiangli/delegate-batch")>]
    val runtimeConstructor: obj

    [<Emit("$0.invoke($1,$2)")>]
    val invoke: _runtime: obj -> _request: obj -> _effects: obj -> Task<obj>

    [<Emit("$0.handleTurn($1,$2)")>]
    val handleTurn: _runtime: obj -> _observation: obj -> _effects: obj -> Task<bool>

    [<Emit("$0.settleCompleted($1,$2)")>]
    val settle: _runtime: obj -> _observation: obj -> _effects: obj -> bool

    [<Emit("$0.invokeResponse($1,$2)")>]
    val invokeResponse: _runtime: obj -> _request: obj -> _effects: obj -> Task<obj>

    [<Emit("$0.invokeObserved($1,$2,$3)")>]
    val invokeObserved: _runtime: obj -> _request: obj -> _effects: obj -> _notDispatched: (string -> obj) -> obj

    val optional: make: ('a -> 'b) -> value: obj -> 'b option
    val nullable: map: ('a -> 'b) -> ('a option -> obj)
    val roleName: role: SyncDelegateRole -> string
    val roleOf: name: string -> SyncDelegateRole
    val resultToNative: result: Result<'a, 'b> -> obj
    val resultFromNative: value: obj -> Result<string, string>
    val invocationFromNative: value: obj -> Result<SyncDelegateInvocationResult, 'a>
    val invocationToNative: result: Result<SyncDelegateInvocationResult, string> -> obj

type internal SyncDelegateTerminalFailureScope =
    | FreshAuthorityRoot of AuthorityRootUserMessageId
    | ExistingAuthorityContinuation of PhysicalUserMessageId

type internal SyncDelegateInvocation =
    new: raw: obj -> SyncDelegateInvocation
    member Raw: obj
    member ObserveAdmission: (SyncDelegateObservedAdmission -> unit) option
    member IsCancelled: (unit -> bool)
    member StartCursor: int64 option with get, set

type internal SyncDelegateCallAcceptance =
    new: raw: obj -> SyncDelegateCallAcceptance
    member AttachDisposable: registration: IDisposable -> unit
    member Accept: physical: PhysicalUserMessageId * notify: (unit -> unit) -> unit
    member Owns: physical: PhysicalUserMessageId -> bool
    member Close: unit -> unit

type internal SyncDelegateCall =
    new: raw: obj -> SyncDelegateCall
    member Raw: obj
    member Owner: SessionId
    member OwnerScope: ReuseScopeId
    member Role: SyncDelegateRole
    member Delegate: SessionId
    member Agent: string
    member Invocations: SyncDelegateInvocation list
    member AdmissionRoot: Task<AuthorityRootUserMessageId option>
    member Admit: root: AuthorityRootUserMessageId -> unit
    member Acceptance: SyncDelegateCallAcceptance
    member AcceptedPhysical: PhysicalUserMessageId option with get, set
    member AcceptedAuthorityRoot: AuthorityRootUserMessageId option with get, set
    member TerminalFailureScope: SyncDelegateTerminalFailureScope option with get, set

type internal SyncDelegateCallStore =
    new: unit -> SyncDelegateCallStore
    member Raw: obj

    member ObserveProviderToolCall:
        owner: SessionId * run: ProviderRunIdentity * role: SyncDelegateRole * call: ToolCallId -> unit

    member TryObservedBatch:
        owner: SessionId * run: ProviderRunIdentity * role: SyncDelegateRole * call: ToolCallId ->
            SyncDelegateBatch option

    member TryPeekCallByDelegate: session: SessionId -> SyncDelegateCall option
    member TryPopCallByDelegate: session: SessionId -> SyncDelegateCall option
    member TryPopExactCall: call: SyncDelegateCall -> SyncDelegateCall option
    member FailCall: call: SyncDelegateCall * reason: string -> unit
    member CancelScope: scope: ReuseScopeId -> unit
    member TryTakeDeletedDelegate: scope: ReuseScopeId -> SessionId option
    member TryGetDeletedDelegate: scope: ReuseScopeId -> SessionId option
    member PutDeletedDelegate: scope: ReuseScopeId * session: SessionId -> SessionId option
    member ClearDeletedDelegate: scope: ReuseScopeId -> SessionId option
    member ClearAll: unit -> SessionId list
