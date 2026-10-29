namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Host.Ports

/// JS-native observation surface for session Host adapter contracts.
/// Parent lineage is a durable lookup; the physical parent for every child is
/// the resolved family root, never the immediate logical parent. Adapter probes
/// return JSON observations and keep transport capabilities private.
///
/// Probe isolation of the terminal source: every probe in this file builds its
/// own terminal source with `Events.newTerminalSource ()` and therefore keeps
/// its own sticky cache and its own listener list. That is deliberate isolation,
/// not a second production truth. The production truth is the single instance
/// the composition root creates (`OpenCode/Host/PluginHost.fs`) and shares per
/// workspace through `TerminalWiring`; the probes never join that bus, so a
/// probe can neither observe nor disturb a production terminal fact, and a
/// production terminal fact can never leak into a probe's sticky replay.
module SessionsSurface =

    let private hostErrorReason (error: HostError) =
        match error with
        | HostError.Unsupported capability -> sprintf "Host capability %A is unsupported" capability
        | HostError.NotFound what -> what
        | HostError.Refused reason -> reason
        | HostError.Transport reason -> reason
        | HostError.Unknown reason -> reason

    let private discoveryView (result: Result<SessionId list, HostError>) : obj =
        match result with
        | Ok sessions ->
            box
                {| ok = true
                   sessions = sessions |> List.map SessionId.value |> List.toArray
                   error = null |}
        | Error error ->
            box
                {| ok = false
                   sessions = ([||]: string array)
                   error = hostErrorReason error |}

    let observeDeliveryDiscovery (input: obj) (title: string) : Task<obj> =
        match SessionSnapshotPort.createSessionPort (PathsPort.workspaceDirectory input) input with
        | None -> Task.FromResult(discoveryView (Error(HostError.Transport "Session discovery is unavailable")))
        | Some sessions ->
            task {
                let! result = sessions.FindByTitle title
                return discoveryView result
            }

    let private text (value: obj) =
        if isNull value then "" else string value

    let private parentsOf (value: obj) : obj array =
        if isNull value then [||] else unbox<obj array> value

    let familyRoot (parents: obj) (session: string) : string =
        let rec resolve current visited =
            if Set.contains current visited then
                current
            else
                parentsOf parents
                |> Array.tryPick (fun pair ->
                    if text pair?child = current then
                        Some(text pair?parent)
                    else
                        None)
                |> Option.map (fun parent -> resolve parent (Set.add current visited))
                |> Option.defaultValue current

        resolve session Set.empty

    let physicalParents (parents: obj) (children: obj) : string array =
        if isNull children then
            [||]
        else
            (unbox<obj array> children)
            |> Array.map (fun child -> familyRoot parents (text child))

    type private ControlledOpenCodePort(childId: SessionId, rejectAbort: bool) =
        let aborts = ResizeArray<string>()
        let abortTimes = ResizeArray<int>()
        let createParents = ResizeArray<string>()

        let abortStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let rejection =
            TaskCompletionSource<Result<unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

        // DSL-MUTABLE: resource — virtual clock for controlled OpenCode port
        let virtualTime = ref 0

        member _.Aborts = aborts.ToArray()
        member _.AbortStarted = abortStarted.Task
        member _.AbortTimes = abortTimes.ToArray()
        member _.CreateParents = createParents.ToArray()
        member _.VirtualTime = virtualTime.Value
        member _.AdvanceTo(timestamp: int) = virtualTime.Value <- timestamp

        member _.AcceptAbort() =
            AsyncSupport.trySetResult rejection (Ok()) |> ignore

        member _.RejectAbort() =
            AsyncSupport.trySetResult rejection (Error "controlled Host rejected AbortSession")
            |> ignore

        interface IOpenCodePort with
            member _.SendPrompt _ _ _ = Task.FromResult(Fatal "unused")

            member _.AbortSession sessionId =
                aborts.Add(SessionId.value sessionId)
                abortTimes.Add virtualTime.Value
                AsyncSupport.trySetResult abortStarted () |> ignore

                if rejectAbort then
                    rejection.Task
                else
                    Task.FromResult(Ok())

            member _.CreateSession _ _ = Task.FromResult(Error "unused")
            member _.GetSessionParent _ = Task.FromResult(Ok None)

            member _.CreateChildSession parent _ =
                createParents.Add(SessionId.value parent)

                let createdId =
                    if createParents.Count = 1 then
                        childId
                    else
                        SessionId.create (SessionId.value childId + "-" + string createParents.Count)

                Task.FromResult(Ok createdId)

            member _.ListChildren _ = Task.FromResult(Ok [])
            member _.CloseChildSession _ = Task.FromResult(Ok())
            member _.IsRunnerBusy _ _ = Task.FromResult(Ok false)

    /// Exercise the production flattening membrane, not a duplicated family-root
    /// algorithm or the logical-parent argument above the Host adapter.
    let flattenedChildAdapterProbe () : Task<obj> =
        task {
            let root = SessionId.create "flat-sphinx-root"

            let transport =
                ControlledOpenCodePort(SessionId.create "flat-sphinx-engineer", false)

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "standard Engineer"
                  Agent = Some "engineer"
                  Directory = None }

            let! first = sessions.CreateChildSession(root, options)
            let caller = first |> Result.defaultWith invalidOp
            let! second = sessions.CreateChildSession(caller, options)
            let worker = second |> Result.defaultWith invalidOp

            return
                createObj
                    [ "root" ==> SessionId.value root
                      "caller" ==> SessionId.value caller
                      "worker" ==> SessionId.value worker
                      "physicalParents" ==> transport.CreateParents
                      "workerFamily" ==> SessionId.value (sessions.FamilyRootOf worker) ]
        }

    type private AuthoritativeHierarchyPort() =
        let parents = Dictionary<string, Result<string option, string>>()
        let createParents = ResizeArray<string>()
        let listParents = ResizeArray<string>()
        let parentQueryCalls = ResizeArray<string>()
        let createdChildren = ResizeArray<string>()

        member _.SetParent(childId: string, parentOutcome: Result<string option, string>) =
            parents.[childId] <- parentOutcome

        member _.CreateParents = createParents.ToArray()
        member _.ListParents = listParents.ToArray()
        member _.ParentQueryCalls = parentQueryCalls.ToArray()
        member _.CreatedChildren = createdChildren.ToArray()

        interface IOpenCodePort with
            member _.SendPrompt _ _ _ = Task.FromResult(Fatal "unused")
            member _.AbortSession _ = Task.FromResult(Ok())
            member _.CreateSession _ _ = Task.FromResult(Error "unused")

            member _.GetSessionParent sessionId =
                let key = SessionId.value sessionId
                parentQueryCalls.Add key

                match parents.TryGetValue key with
                | true, outcome ->
                    match outcome with
                    | Ok opt -> Task.FromResult(Ok(opt |> Option.map SessionId.create))
                    | Error err -> Task.FromResult(Error err)
                | false, _ -> Task.FromResult(Ok None)

            member _.CreateChildSession parent _ =
                let p = SessionId.value parent
                createParents.Add p
                let child = sprintf "%s-child-%d" p (createParents.Count)
                createdChildren.Add child
                Task.FromResult(Ok(SessionId.create child))

            member _.ListChildren parent =
                let p = SessionId.value parent
                listParents.Add p

                let children: OpenCodeChildInfo list =
                    createdChildren
                    |> Seq.map (fun c ->
                        { SessionId = SessionId.create c
                          ParentSessionId = Some parent
                          Agent = Some "test-agent"
                          Title = Some "test-title" })
                    |> Seq.toList

                Task.FromResult(Ok(children |> List.map ChildSession.toInfo))

            member _.CloseChildSession _ = Task.FromResult(Ok())
            member _.IsRunnerBusy _ _ = Task.FromResult(Ok false)

    /// HOST-015: restored child owner whose process-local family map is empty
    /// resolves physical parent to the authoritative Host family root, inherits
    /// owner language, normalizes ListChildren, and caches ancestry to avoid extra queries.
    let restoredOwnerFlatteningProbe () : Task<obj> =
        task {
            let transport = AuthoritativeHierarchyPort()
            transport.SetParent("restored-child", Ok(Some "intermediate-sub"))
            transport.SetParent("intermediate-sub", Ok(Some "host-family-root"))
            transport.SetParent("host-family-root", Ok None)

            let ownerSession = SessionId.create "restored-child"
            let ownerLang = GlobalProviderLanguage.current ()

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "restored owner worker"
                  Agent = Some "worker"
                  Directory = None }

            let! firstOutcome = sessions.CreateChildSession(ownerSession, options)
            let firstChild = firstOutcome |> Result.defaultWith invalidOp
            let queriesAfterFirst = transport.ParentQueryCalls.Length

            let childLang = string (GlobalProviderLanguage.current ())

            let! listOutcome = sessions.ListChildren(ownerSession)

            let! secondOutcome = sessions.CreateChildSession(ownerSession, options)
            let secondChild = secondOutcome |> Result.defaultWith invalidOp
            let queriesAfterSecond = transport.ParentQueryCalls.Length

            return
                createObj
                    [ "firstOk", box (Result.isOk firstOutcome)
                      "secondOk", box (Result.isOk secondOutcome)
                      "firstChildId", box (SessionId.value firstChild)
                      "secondChildId", box (SessionId.value secondChild)
                      "physicalParents", box transport.CreateParents
                      "listParents", box transport.ListParents
                      "parentQueries", box transport.ParentQueryCalls
                      "queriesAfterFirst", box queriesAfterFirst
                      "queriesAfterSecond", box queriesAfterSecond
                      "ownerLanguage", box (string ownerLang)
                      "childLanguage", box childLang
                      "firstChildFamilyRoot", box (SessionId.value (sessions.FamilyRootOf firstChild))
                      "ownerFamilyRoot", box (SessionId.value (sessions.FamilyRootOf ownerSession)) ]
        }

    /// HOST-015: query failure on session parent query must fail closed,
    /// never guess that the session is a root, and create no session.
    let unknownParentQueryErrorProbe () : Task<obj> =
        task {
            let transport = AuthoritativeHierarchyPort()
            transport.SetParent("broken-owner", Error "Host transport connection reset")

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "failing child"
                  Agent = Some "worker"
                  Directory = None }

            let! createOutcome = sessions.CreateChildSession(SessionId.create "broken-owner", options)
            let! listOutcome = sessions.ListChildren(SessionId.create "broken-owner")

            return
                createObj
                    [ "createOk", box (Result.isOk createOutcome)
                      "createError",
                      box (
                          match createOutcome with
                          | Error err -> err
                          | Ok _ -> ""
                      )
                      "createCalls", box transport.CreateParents.Length
                      "listOk", box (Result.isOk listOutcome)
                      "listError",
                      box (
                          match listOutcome with
                          | Error err -> err
                          | Ok _ -> ""
                      )
                      "listCalls", box transport.ListParents.Length ]
        }

    /// HOST-015: parent cycle in session ancestry must fail closed with cycle
    /// error and refuse creation.
    let ancestryCycleProbe () : Task<obj> =
        task {
            let transport = AuthoritativeHierarchyPort()
            transport.SetParent("cycle-a", Ok(Some "cycle-b"))
            transport.SetParent("cycle-b", Ok(Some "cycle-a"))

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "cyclic child"
                  Agent = Some "worker"
                  Directory = None }

            let! createOutcome = sessions.CreateChildSession(SessionId.create "cycle-a", options)

            return
                createObj
                    [ "createOk", box (Result.isOk createOutcome)
                      "createError",
                      box (
                          match createOutcome with
                          | Error err -> err
                          | Ok _ -> ""
                      )
                      "createCalls", box transport.CreateParents.Length ]
        }

    /// managed-session-lifecycle-016/017: exercise the production session adapter against
    /// a controlled physical Host boundary. The returned view contains values,
    /// never the adapter or its managed-child representation.
    let interruptAttemptAdapterProbe () : Task<obj> =
        task {
            let rootId = SessionId.create "adapter-root"
            let childId = SessionId.create "adapter-child"
            let transport = ControlledOpenCodePort(childId, false)

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "adapter child"
                  Agent = Some "adapter-proof"
                  Directory = None }

            let! created = sessions.CreateChildSession(rootId, options)

            match created with
            | Error error ->
                return
                    createObj
                        [ "created", box false
                          "creationError", box error
                          "rootRejected", box false
                          "rootError", null
                          "transportCallsAfterRoot", box transport.Aborts.Length
                          "childInterrupted", box false
                          "transportCallsAfterChild", box transport.Aborts.Length
                          "abortedSessionIds", box transport.Aborts
                          "childStillManagedAfterInterrupt", box false ]
            | Ok managedChildId ->
                let! rootOutcome = sessions.InterruptAttempt rootId
                let callsAfterRoot = transport.Aborts.Length
                let! childOutcome = sessions.InterruptAttempt managedChildId

                let rootError =
                    match rootOutcome with
                    | Error error -> box error
                    | Ok() -> null

                return
                    createObj
                        [ "created", box true
                          "creationError", null
                          "rootRejected", box (Result.isError rootOutcome)
                          "rootError", rootError
                          "transportCallsAfterRoot", box callsAfterRoot
                          "childInterrupted", box (Result.isOk childOutcome)
                          "transportCallsAfterChild", box transport.Aborts.Length
                          "abortedSessionIds", box transport.Aborts
                          "childStillManagedAfterInterrupt", box (sessions.IsManagedChild managedChildId) ]
        }

    /// managed-session-lifecycle-016: a Host rejection is a typed terminal result for the
    /// single production adapter attempt; the adapter never retries AbortSession.
    let interruptRejectedAdapterProbe () : Task<obj> =
        task {
            let rootId = SessionId.create "adapter-rejected-root"
            let childId = SessionId.create "adapter-rejected-child"
            let transport = ControlledOpenCodePort(childId, true)

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), Events.newTerminalSource ()) :> ISessionHostPort

            let options: ChildSessionOptions =
                { Title = Some "adapter rejected child"
                  Agent = Some "adapter-rejection-proof"
                  Directory = None }

            match! sessions.CreateChildSession(rootId, options) with
            | Error error ->
                return
                    createObj
                        [ "outcome", box "SetupError"
                          "error", box error
                          "abortAttempts", box transport.Aborts.Length
                          "trace", box [||] ]
            | Ok managedChildId ->
                let pending = sessions.InterruptAttempt managedChildId
                let attemptsBeforeRejection = transport.Aborts.Length
                transport.AdvanceTo 10
                transport.RejectAbort()
                let! outcome = pending
                transport.AdvanceTo 1000

                let outcomeName, error =
                    match outcome with
                    | Ok() -> "Ok", ""
                    | Error rejection -> "Error", rejection

                let attemptsAfterQuiescence = transport.Aborts.Length

                let trace =
                    [| sprintf "t=%d AbortSession(%s)" transport.AbortTimes[0] transport.Aborts[0]
                       sprintf "t=10 %s(%s)" outcomeName error
                       sprintf "t=%d quiescent attempts=%d" transport.VirtualTime attemptsAfterQuiescence |]

                return
                    createObj
                        [ "outcome", box outcomeName
                          "error", box error
                          "attemptsBeforeRejection", box attemptsBeforeRejection
                          "abortAttempts", box attemptsAfterQuiescence
                          "abortedSessionIds", box transport.Aborts
                          "virtualTimes", box [| 0; 10; 1000 |]
                          "trace", box trace ]
        }

    let terminationProbe (rejectAbort: bool) : Task<obj> =
        task {
            let childId = SessionId.create "termination-child"
            let rootId = SessionId.create "termination-parent"

            let authority =
                PhysicalUserMessageId.create "physical-authority"
                |> PhysicalUserMessageId.promoteToAuthorityRoot

            let transport = ControlledOpenCodePort(childId, true)
            let terminals = ResizeArray<obj>()
            let cancelled = ResizeArray<string>()

            let cancellationStarted =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let cancellationFinished =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let registry = Events.newTerminalSource ()

            let eventPort =
                { new IEventObservationPort with
                    member _.SubscribeTerminalListener listener =
                        registry.SubscribeTerminalListener(listener)

                    member _.SubscribeFutureTerminalListener listener =
                        registry.SubscribeFutureTerminalListener(listener)

                    member _.NotifyTerminal session outcome =
                        let kind, reason, root =
                            match outcome with
                            | TerminalOutcome.Failed stop -> "Failed", stop.Reason, stop.AuthorityRootUserMessageId
                            | TerminalOutcome.Aborted stop -> "Aborted", stop.Reason, stop.AuthorityRootUserMessageId
                            | TerminalOutcome.Completed result ->
                                "Completed", "", Some result.AuthorityRootUserMessageId

                        terminals.Add(
                            box
                                {| session = SessionId.value session
                                   kind = kind
                                   reason = reason
                                   authority =
                                    root |> Option.map AuthorityRootUserMessageId.value |> Option.defaultValue "" |}
                        )

                        registry.NotifyTerminal session outcome }

            let sessions =
                InjectedSessionPort(Some(transport :> IOpenCodePort), registry) :> ISessionHostPort

            let cancel session : Task =
                cancelled.Add(SessionId.value session)
                AsyncSupport.trySetResult cancellationStarted () |> ignore
                cancellationFinished.Task

            let! rootOutcome =
                ManagedSessionTermination.terminate cancel sessions eventPort rootId authority "refused root"

            let rootEffects = cancelled.Count + transport.Aborts.Length + terminals.Count

            let! created =
                sessions.CreateChildSession(
                    rootId,
                    { Title = None
                      Agent = Some "engineer"
                      Directory = None }
                )

            match created with
            | Error error -> return invalidOp error
            | Ok child ->
                let pending =
                    ManagedSessionTermination.terminate cancel sessions eventPort child authority "no successor"

                do! cancellationStarted.Task
                let beforeDrain = [| transport.Aborts.Length; terminals.Count |]
                AsyncSupport.trySetResult cancellationFinished () |> ignore
                do! transport.AbortStarted
                let beforeAbort = terminals.Count

                if rejectAbort then
                    transport.RejectAbort()
                else
                    transport.AcceptAbort()

                let! outcome = pending

                return
                    box
                        {| rootRejected = Result.isError rootOutcome
                           rootEffects = rootEffects
                           beforeDrain = beforeDrain
                           beforeAbort = beforeAbort
                           cancelled = cancelled.ToArray()
                           aborted = transport.Aborts
                           terminals = terminals.ToArray()
                           ok = Result.isOk outcome
                           error =
                            match outcome with
                            | Ok() -> ""
                            | Error error -> error |}
        }

    /// managed-session-lifecycle-016 already-terminal: a lifecycle-terminated attempt is
    /// permitted to abort on the Host transport, while a
    /// non-terminal non-managed attempt is still rejected with zero transport
    /// calls. The returned view contains values, never the adapter.
    let interruptTerminatedAdapterProbe () : Task<obj> =
        task {
            let terminalId = SessionId.create "term-child"
            let otherId = SessionId.create "other-root"
            let transport = ControlledOpenCodePort(terminalId, false)

            let sessions =
                InjectedSessionPort(
                    Some(transport :> IOpenCodePort),
                    Events.newTerminalSource (),
                    ?isLifecycleTerminated = Some(fun sessionId -> sessionId = terminalId)
                )
                :> ISessionHostPort

            let! terminalOutcome = sessions.InterruptAttempt terminalId
            let abortsAfterTerminal = transport.Aborts.Length
            let! otherOutcome = sessions.InterruptAttempt otherId

            let outcomeName, error =
                match terminalOutcome with
                | Ok() -> "Ok", ""
                | Error rejection -> "Error", rejection

            let otherName, otherError =
                match otherOutcome with
                | Ok() -> "Ok", ""
                | Error rejection -> "Error", rejection

            return
                createObj
                    [ "terminatedOutcome", box outcomeName
                      "terminatedError", box error
                      "abortsAfterTerminal", box abortsAfterTerminal
                      "abortedSessionIds", box transport.Aborts
                      "otherOutcome", box otherName
                      "otherError", box otherError
                      "abortCount", box transport.Aborts.Length ]
        }

    /// HOST-037: InjectedSessionPort.AwaitRunnerIdle over a controlled Host
    /// status. `isBusy` is a JS `(sessionId) => boolean | Promise<boolean>`;
    /// a thrown or rejected read is an unreadable status.
    let awaitRunnerIdle (isBusy: obj) (sessionId: string) : Task =
        let transport =
            { new IOpenCodePort with
                member _.SendPrompt _ _ _ = Task.FromResult(Fatal "unused")
                member _.AbortSession _ = Task.FromResult(Ok())
                member _.CreateSession _ _ = Task.FromResult(Error "unused")
                member _.GetSessionParent _ = Task.FromResult(Ok None)
                member _.CreateChildSession _ _ = Task.FromResult(Error "unused")
                member _.ListChildren _ = Task.FromResult(Ok [])
                member _.CloseChildSession _ = Task.FromResult(Ok())

                member _.IsRunnerBusy session _ =
                    task {
                        try
                            let read: Task<bool> =
                                emitJsExpr (isBusy, SessionId.value session) "Promise.resolve($0($1))"

                            let! busy = read

                            return Ok busy
                        with ex ->
                            return Error ex.Message
                    } }

        let port =
            InjectedSessionPort(Some transport, Events.newTerminalSource ()) :> ISessionHostPort

        port.AwaitRunnerIdle(SessionId.create sessionId, None)
