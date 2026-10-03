namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System
open System.Text
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.OpenCode
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Process

[<RequireQualifiedAccess>]
module HostForkPtySurface =
    let private callView command =
        match command with
        | PtyCommand.Write bytes ->
            Some(
                box
                    {| kind = "write"
                       text = Encoding.UTF8.GetString bytes |}
            )
        | PtyCommand.Signal signal ->
            Some(
                box
                    {| kind = "signal"
                       signal = PtySupervisor.signalName signal |}
            )
        | _ -> None

    let private outcomeView calls owned known =
        function
        | Ok read ->
            box
                {| ok = true
                   id = read.Id.Value
                   output = read.Output
                   closed = read.Closed
                   owned = owned
                   known = known
                   calls = calls |}
        | Error error ->
            box
                {| ok = false
                   error = error
                   owned = owned
                   known = known
                   calls = calls |}

    let private forkView (calls: obj array) (runtime: HostForkRuntime) (port: PtyPort) =
        function
        | Ok(id: PtyId) ->
            box
                {| ok = true
                   id = id.Value
                   owned = runtime.OwnsPty id
                   known = port.Known id
                   calls = calls |}
        | Error error ->
            box
                {| ok = false
                   error = error
                   owned = runtime.SnapshotOutstandingPtyRuns() |> List.isEmpty |> not
                   calls = calls |}

    let scenario (action: string) (input: string) (failure: string) : Task<obj> =
        task {
            let calls = ResizeArray<obj>()
            // DSL-MUTABLE: resource — controlled backend read-completion port
            let mutable portRef: PtyPort option = None

            let handler id command =
                if action = "fork-error" then
                    match command with
                    | PtyCommand.Spawn _ -> raise (InvalidOperationException failure)
                    | _ -> ()

                callView command |> Option.iter calls.Add

                match command, portRef with
                | PtyCommand.Read, Some port -> port.ReadResult(id, "terminal text", true)
                | _ -> ()

                if String.IsNullOrEmpty failure then
                    Task.FromResult(Ok())
                else
                    Task.FromResult(Error failure)

            let port = PtyPort(handler = handler)
            portRef <- Some port

            let runtime =
                HostForkRuntime(
                    SessionId.create "pty-owner",
                    Unchecked.defaultof<ISessionHostPort>,
                    (fun _ _ _ -> Task.FromResult None),
                    CompletionMailboxRuntime.create,
                    NodeTiming.nodeClockPort (),
                    NodeTiming.raceExit,
                    ptyPort = port
                )

            let agent = ManagedAgent.make Role.DevOps

            match action with
            | "lookup-unknown" ->
                return
                    box
                        {| ok = true
                           known = runtime.TryPty(input).IsSome
                           calls = calls.ToArray() |}
            | "send-unowned" ->
                let id = PtyId.Create "foreign"
                let! outcome = runtime.SendPty(id, input, None)
                return outcomeView (calls.ToArray()) (runtime.OwnsPty id) (port.Known id) outcome
            | "track-untrack" ->
                let id = PtyId.Create input
                runtime.TrackPtyRun id
                let ownedBefore = runtime.OwnsPty id
                runtime.UntrackPtyRun id.Value

                return
                    box
                        {| ok = true
                           ownedBefore = ownedBefore
                           ownedAfter = runtime.OwnsPty id
                           calls = calls.ToArray() |}
            | "exit-cleanup" ->
                match! runtime.ForkPty("shell", agent) with
                | Error error ->
                    return
                        box
                            {| ok = false
                               error = error
                               calls = calls.ToArray() |}
                | Ok id ->
                    let termName = "term1"
                    let bindResult = runtime.TryBindTerminalName(termName, id)
                    let outstandingBefore = runtime.SnapshotOutstandingPtyRuns() |> List.toArray
                    let ownedBefore = runtime.OwnsPty id
                    let byNameBefore = runtime.TryPtyByName(termName)

                    // Deliver physical backend exit
                    port.Complete id

                    let outstandingAfter = runtime.SnapshotOutstandingPtyRuns() |> List.toArray
                    let ownedAfter = runtime.OwnsPty id
                    let byNameAfter = runtime.TryPtyByName(termName)

                    return
                        box
                            {| ok = true
                               id = id.Value
                               bindOk = Result.isOk bindResult
                               outstandingBefore = outstandingBefore
                               ownedBefore = ownedBefore
                               byNameBefore = byNameBefore |> Option.map (fun (p: PtyId) -> p.Value)
                               outstandingAfter = outstandingAfter
                               ownedAfter = ownedAfter
                               byNameAfter = byNameAfter |> Option.map (fun (p: PtyId) -> p.Value)
                               calls = calls.ToArray() |}
            | "devops-return-drain" ->
                // Setup child runtime representing DevOps session
                let devopsSessionId = SessionId.create "devops-session"
                let devopsPort = PtyPort(handler = handler)

                let devopsRuntime =
                    HostForkRuntime(
                        devopsSessionId,
                        Unchecked.defaultof<ISessionHostPort>,
                        (fun _ _ _ -> Task.FromResult None),
                        CompletionMailboxRuntime.create,
                        NodeTiming.nodeClockPort (),
                        NodeTiming.raceExit,
                        ptyPort = devopsPort
                    )
                // DevOps opens a PTY in its own session
                let! devopsPtyResult = devopsRuntime.ForkPty("long-running-devops-task", agent)

                let devopsPtyId =
                    match devopsPtyResult with
                    | Ok id -> id
                    | Error e -> failwith e

                // Another session (e.g. engineer session) opens its own PTY
                let engineerSessionId = SessionId.create "engineer-session"
                let engineerPort = PtyPort(handler = handler)

                let engineerRuntime =
                    HostForkRuntime(
                        engineerSessionId,
                        Unchecked.defaultof<ISessionHostPort>,
                        (fun _ _ _ -> Task.FromResult None),
                        CompletionMailboxRuntime.create,
                        NodeTiming.nodeClockPort (),
                        NodeTiming.raceExit,
                        ptyPort = engineerPort
                    )

                let! engineerPtyResult = engineerRuntime.ForkPty("engineer-task", ManagedAgent.make Role.Engineer)

                let engineerPtyId =
                    match engineerPtyResult with
                    | Ok id -> id
                    | Error e -> failwith e

                let! journal = HostForkRunLifecycle.openTemporaryJournal ()

                // DSL-MUTABLE: resource — surface-only physical message counter
                let physicalSequence = ref 0

                // Manager runtime configured with drainChildPtys capability
                let drainChildPtys (sid: SessionId) =
                    task {
                        if sid = devopsSessionId then
                            do! devopsRuntime.CloseOwnedPtys()
                    }

                let dummySessions =
                    { new ISessionHostPort with
                        member _.SubscribeTerminal(_, _) =
                            { new IDisposable with
                                member _.Dispose() = () }

                        member _.SubscribeFutureTerminal(_, _) =
                            { new IDisposable with
                                member _.Dispose() = () }

                        member _.SendPrompt(_, _, _) =
                            physicalSequence.Value <- physicalSequence.Value + 1

                            let physicalId = sprintf "pty-surface-physical:%d" physicalSequence.Value

                            Task.FromResult(
                                Outcome.SendOutcome.AdmittedWithPhysicalMessage(PhysicalUserMessageId.create physicalId)
                            )

                        member _.AbortSession _ = Task.FromResult(Ok())
                        member _.InterruptAttempt _ = Task.FromResult(Ok())
                        member _.IsManagedChild _ = true
                        member _.AbortChildren _ = Task.FromResult(()) :> Task
                        member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "dummy")
                        member _.TryGetParentSession _ = Task.FromResult(Ok None)
                        member _.CreateChildSession(_, _) = Task.FromResult(Error "dummy")
                        member _.ListChildren _ = Task.FromResult(Ok [])
                        member _.FamilyRootOf sessionId = sessionId }

                let managerRuntime =
                    HostForkRuntime(
                        SessionId.create "manager-session",
                        dummySessions,
                        (fun _ _ _ -> Task.FromResult None),
                        CompletionMailboxRuntime.create,
                        NodeTiming.nodeClockPort (),
                        NodeTiming.raceExit,
                        journal = journal,
                        drainChildPtys = drainChildPtys
                    )

                let! devopsAdmitted =
                    HostForkRunLifecycle.admitPendingAgentWork
                        journal
                        dummySessions
                        (SessionId.create "manager-session")
                        "manager"
                        "devops"
                        devopsSessionId
                        Role.DevOps

                let authorityRoot =
                    match devopsAdmitted with
                    | Ok root -> root
                    | Error reason -> failwith reason

                let run =
                    managerRuntime.InstallRun("devops", devopsSessionId, Role.DevOps, authorityRoot)

                let devopsPtysBefore = devopsRuntime.SnapshotOutstandingPtyRuns() |> List.toArray

                let engineerPtysBefore =
                    engineerRuntime.SnapshotOutstandingPtyRuns() |> List.toArray

                // Complete DevOps run (settlement upon return)
                let outcome =
                    TerminalOutcome.Completed
                        { SessionId = devopsSessionId
                          Role = Role.DevOps
                          ProviderRun = ProviderRunIdentity.create "prov-run"
                          AuthorityRootUserMessageId = authorityRoot
                          Directory = None
                          TerminalText = "devops finished"
                          TurnFormalText = "devops finished" }

                managerRuntime.Complete(run, outcome)
                // Drain owned background work in manager runtime to ensure drainChildPtys has finished
                do! managerRuntime.DrainOwnedWork()

                let devopsPtysAfter = devopsRuntime.SnapshotOutstandingPtyRuns() |> List.toArray
                let engineerPtysAfter = engineerRuntime.SnapshotOutstandingPtyRuns() |> List.toArray

                return
                    box
                        {| ok = true
                           devopsPtyId = devopsPtyId.Value
                           engineerPtyId = engineerPtyId.Value
                           devopsBefore = devopsPtysBefore
                           devopsAfter = devopsPtysAfter
                           engineerBefore = engineerPtysBefore
                           engineerAfter = engineerPtysAfter
                           calls = calls.ToArray() |}
            | "blank-fork"
            | "fork"
            | "fork-error" ->
                let! outcome = runtime.ForkPty(input, agent)
                return forkView (calls.ToArray()) runtime port outcome
            | _ ->
                match! runtime.ForkPty("shell", agent) with
                | Error error ->
                    return
                        box
                            {| ok = false
                               error = error
                               calls = calls.ToArray() |}
                | Ok id ->
                    if action = "send-closed" then
                        port.Complete id

                    let signal =
                        if action = "signal" then
                            PtySignal.tryParse input |> Result.toOption
                        else
                            None

                    let prompt =
                        if action = "write" || action = "send-closed" then
                            input
                        else
                            ""

                    let! outcome = runtime.SendPty(id, prompt, signal)
                    return outcomeView (calls.ToArray()) (runtime.OwnsPty id) (port.Known id) outcome
        }
