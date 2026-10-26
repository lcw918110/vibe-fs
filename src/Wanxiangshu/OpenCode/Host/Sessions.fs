namespace Wanxiangshu.OpenCode

open Wanxiangshu.Execution.Routing

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Collections.Generic
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Participant.Persona
open Fable.Core
open Fable.Core.JsInterop

[<RequireQualifiedAccess>]
module ManagedSessionTermination =

    let private captureResult (operation: unit -> Task<Result<unit, string>>) =
        task {
            try
                return! operation ()
            with ex ->
                return Error ex.Message
        }

    let private captureUnit (operation: unit -> Task) =
        task {
            try
                do! operation ()
                return Ok()
            with ex ->
                return Error ex.Message
        }

    let private combineEffects cancelOutcome abortOutcome =
        match cancelOutcome, abortOutcome with
        | Error error, _ -> Error("descendant cancellation failed: " + error)
        | Ok(), Error error -> Error("Host abort failed: " + error)
        | Ok(), Ok() -> Ok()

    /// managed-session-lifecycle-017: fail-closed termination is one causal CE, not an
    /// attempt-only interrupt followed by a future callback guessing the cause.
    /// Descendant durable cancellation precedes physical teardown; Failed is
    /// published after teardown so the existing fork terminal listener commits
    /// HandleCompleted and pulses the parent join mailbox.
    let terminate
        (cancelSessionChildren: SessionId -> Task)
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (sessionId: SessionId)
        (authorityRoot: AuthorityRootUserMessageId)
        (reason: string)
        : Task<Result<unit, string>> =
        if not (sessionPort.IsManagedChild sessionId) then
            Task.FromResult(
                Error "MANAGED-SESSION-016: user-facing/root session may only be interrupted by the external user"
            )
        else
            task {
                let! cancelOutcome = captureUnit (fun () -> cancelSessionChildren sessionId)
                let! abortOutcome = captureResult (fun () -> sessionPort.AbortSession sessionId)

                eventPort.NotifyTerminal
                    sessionId
                    (TerminalOutcome.Failed(TerminalStop.forAuthority authorityRoot reason))
                |> ignore

                return combineEffects cancelOutcome abortOutcome
            }

module private FamilyTreeInterop =
    [<Import("SessionFamilyTree", "wanxiangli/ownership")>]
    let sessionFamilyTreeClass: obj = jsNative

type InjectedSessionPort
    (
        underlyingPort: IOpenCodePort option,
        terminalSource: ISessionTerminalSource,
        ?familyParent: SessionId -> SessionId option,
        ?isLifecycleTerminated: SessionId -> bool,
        ?isUserQuestionWaiting: SessionId -> bool
    ) =
    let familyTree: obj =
        let parentLookup (sid: string) =
            match familyParent with
            | Some fp -> fp (SessionId.create sid) |> Option.map SessionId.value
            | None -> None
        createNew FamilyTreeInterop.sessionFamilyTreeClass (box parentLookup)

    let resolveFamilyRoot (port: IOpenCodePort) (targetId: SessionId) : Task<Result<SessionId, string>> =
        task {
            let stepQuery (curr: string) =
                task {
                    let! parentRes = port.GetSessionParent (SessionId.create curr)
                    match parentRes with
                    | Ok parentOpt ->
                        return box {| ok = true; parent = parentOpt |> Option.map SessionId.value; error = null |}
                    | Error err ->
                        return box {| ok = false; parent = None; error = err |}
                }

            let! resObj = familyTree?resolveFamilyRoot(SessionId.value targetId, stepQuery)
            if resObj?ok = true then
                let rootStr: string = resObj?root
                return Ok (SessionId.create rootStr)
            else
                let errStr: string = resObj?error
                return Error errStr
        }

    let familyRoot (sessionId: SessionId) =
        let rStr: string = familyTree?familyRootOf(SessionId.value sessionId)
        SessionId.create rStr

    let managedChild (sessionId: SessionId) =
        familyTree?isManagedChild(SessionId.value sessionId) : bool

    let registerChild (parentId: SessionId) (childId: SessionId) =
        familyTree?registerChild(SessionId.value parentId, SessionId.value childId) |> ignore

    let detachChild (childId: SessionId) =
        familyTree?detachChild(SessionId.value childId) |> ignore

    let isAbortOk (res: Result<unit, string>) =
        match res with
        | Ok () -> true
        | Error _ -> false

    let abortOneChild (childId: SessionId) : Task<bool> =
        task {
            match underlyingPort with
            | Some port ->
                let! res = port.AbortSession childId
                return isAbortOk res
            | None -> return false
        }

    let abortChildren (parentId: SessionId) =
        task {
            let parentKey = SessionId.value parentId
            let childrenArray: string array = familyTree?childrenToAbort(parentKey)

            for childKey in childrenArray do
                let childId = SessionId.create childKey
                let! isAborted = abortOneChild childId
                familyTree?settleAbortOne(parentKey, childKey, isAborted) |> ignore

            familyTree?endAbort(parentKey) |> ignore
        }

    let routeSendOptions (_sessionId: SessionId) (opts: SessionPromptOptions) =
        // Dispatch admission is intentionally capacity-free. A session is a
        // reusable container, and the async prompt enqueue must never wait for a
        // provider slot. The sole model/capacity owner is chat.message, where the
        // Host is actually preparing this physical user message for execution.
        Ok opts

    let sendThroughPort
        (port: IOpenCodePort)
        (sessionId: SessionId)
        (text: string)
        (sendOptions: SessionPromptOptions)
        =
        // Pass the outcome through unchanged. This layer knows less about
        // acceptance than the port does; execution identity is handed off by
        // PromptKey at chat.message -> messages.transform, never by this call stack.
        port.SendPrompt sessionId text sendOptions

    let sendAvailablePort sessionId text sendOptions =
        match underlyingPort with
        | Some port -> sendThroughPort port sessionId text sendOptions
        | None -> Task.FromResult(Fatal "No Host transport: plugin input carried no client, serverUrl, baseUrl or port")

    let sendRoutedPrompt sessionId text opts =
        match routeSendOptions sessionId opts with
        | Error error -> Task.FromResult(Fatal error)
        | Ok sendOptions -> sendAvailablePort sessionId text sendOptions

    let bindSiblingExecution (ownerSessionId: SessionId) (laneId: SessionId) (agent: string option) =
        // A fission lane's participant comes from the durable lane/owner projection
        // and its own Accepted evidence; language follows the live global
        // preference, so there is nothing to bind at this boundary.
        ignore agent

    let bindSiblingLane (port: IOpenCodePort) (ownerSessionId: SessionId) (laneId: SessionId) (agent: string option) =
        taskResult {
            try
                bindSiblingExecution ownerSessionId laneId agent
                return laneId
            with ex ->
                do!
                    task {
                        let! _ = port.AbortSession laneId
                        return ()
                    }
                    |> TaskResultCE.ofTask

                return! Error ex.Message
        }

    let createSiblingSession
        (ownerSessionId: SessionId)
        (physicalParentId: SessionId option)
        (options: ChildSessionOptions)
        =
        taskResult {
            let! port =
                underlyingPort
                |> Result.requireSome "No Host transport: cannot create a sibling session"

            let! laneId = port.CreateSession physicalParentId options
            return! bindSiblingLane port ownerSessionId laneId options.Agent
        }

    let createChildSession (parentId: SessionId) (options: ChildSessionOptions) =
        taskResult {
            let! port =
                underlyingPort
                |> Result.requireSome "No Host transport: cannot create a child session"

            let! rootId = resolveFamilyRoot port parentId
            // HOST-015: every managed child is physically parented to the
            // family root — a son's son is a son. Recovery proves ownership
            // by the journal-linked SessionId + agent/title, never by the
            // Host parentID.
            let hostParentId = rootId

            let! childId = port.CreateChildSession hostParentId options
            registerChild rootId childId
            return childId
        }

    let listChildren (parentId: SessionId) =
        taskResult {
            let! port =
                underlyingPort
                |> Result.requireSome "No Host transport: cannot list child sessions"

            let! rootId = resolveFamilyRoot port parentId
            return! port.ListChildren rootId
        }

    let interruptManagedAttempt (sessionId: SessionId) =
        task {
            Diagnostic.emit "session-attempt-interrupt" [ "session_id", SessionId.value sessionId ]
            ModelRouting.cancelUnacquiredExecution sessionId |> ignore

            match underlyingPort with
            | Some port -> return! port.AbortSession sessionId
            | None -> return Error "No Host transport: cannot interrupt attempt"
        }

    let interruptUserQuestion (sessionId: SessionId) =
        task {
            Diagnostic.emit "session-user-question-interrupt" [ "session_id", SessionId.value sessionId ]
            // 只停止提问者的输出；不能取消已入队的人类回复，也不能排空子资源。
            match underlyingPort with
            | Some port -> return! port.AbortSession sessionId
            | None -> return Error "No Host transport: cannot pause for the user question"
        }

    let abortManagedSession (sessionId: SessionId) =
        task {
            Diagnostic.emit "session-abort" [ "session_id", SessionId.value sessionId ]
            ModelRouting.cancelUnacquiredExecution sessionId |> ignore
            detachChild sessionId
            do! abortChildren sessionId

            match underlyingPort with
            | Some port -> return! port.AbortSession sessionId
            | None -> return Error "No Host transport: cannot abort session"
        }

    interface ISessionHostPort with
        member _.AbortChildren(parentId) = abortChildren parentId

        member me.SubscribeTerminal(sessionId, listener) =
            terminalSource.SubscribeTerminal(sessionId, listener)

        member me.SubscribeFutureTerminal(sessionId, listener) =
            terminalSource.SubscribeFutureTerminal(sessionId, listener)

        member me.SendPrompt(sessionId, text, opts) =
            if not (terminalSource.HasSubscriber sessionId) then
                // Fatal, not Retryable: the listener is registered by the caller
                // before it sends, so this is a call-order defect. Retrying the
                // same wrong order cannot fix it.
                Task.FromResult(Fatal "AG-LISTENER-BEFORE-SEND: Listener must be registered before sending prompt")
            else
                sendRoutedPrompt sessionId text opts

        member _.InterruptAttempt sessionId =
            let waitingForUser =
                isUserQuestionWaiting |> Option.exists (fun check -> check sessionId)

            let terminated =
                isLifecycleTerminated |> Option.exists (fun check -> check sessionId)

            if waitingForUser then
                interruptUserQuestion sessionId
            elif not (managedChild sessionId) && not terminated then
                Task.FromResult(
                    Error "MANAGED-SESSION-016: user-facing/root session may only be interrupted by the external user"
                )
            else
                interruptManagedAttempt sessionId

        member _.IsManagedChild(sessionId) = managedChild sessionId

        member me.AbortSession(sessionId) =
            if not (managedChild sessionId) then
                Task.FromResult(
                    Error "MANAGED-SESSION-016: user-facing/root session may only be interrupted by the external user"
                )
            else
                abortManagedSession sessionId

        member _.CreateSiblingSession(ownerSessionId, physicalParentId, options) =
            createSiblingSession ownerSessionId physicalParentId options

        member _.TryGetParentSession(sessionId) =
            match underlyingPort with
            | None -> Task.FromResult(Error "No Host transport: cannot read session parent")
            | Some port -> port.GetSessionParent sessionId

        member me.CreateChildSession(parentId, options) = createChildSession parentId options

        member _.ListChildren(parentId) = listChildren parentId

        member _.FamilyRootOf(sessionId) = familyRoot sessionId
