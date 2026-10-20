namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

type private SatelliteSessionPort(physical: bool, conflict: bool, queryError: bool) =
    let created = ResizeArray<string>()
    let closed = ResizeArray<string>()
    let linkedFacts = ResizeArray<string array>()
    // DSL-MUTABLE: resource — controlled Host child allocation sequence
    let mutable createCount = 0

    let existing: OpenCodeChildInfo list =
        if not physical then
            []
        else
            [ { SessionId = SessionId.create "companion-1"
                ParentSessionId = Some(SessionId.create "work")
                Agent = Some(if conflict then "wrong-agent" else "companion")
                Title = Some(if conflict then "wrong-title" else "Companion") } ]

    member _.Created = created.ToArray()
    member _.Closed = closed.ToArray()
    member _.LinkedFacts = linkedFacts.ToArray()

    member _.Link owner child agent =
        linkedFacts.Add [| SessionId.value owner; SessionId.value child; agent |]
        Task.FromResult(Ok())

    member _.Close owner =
        closed.Add(SessionId.value owner)
        Task.FromResult(Ok())

    interface ISessionHostPort with
        member _.SubscribeTerminal(_, _) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.SubscribeFutureTerminal(_, _) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.SendPrompt(_, _, _) =
            task { return raise (InvalidOperationException "SatelliteSurface does not send prompts") }

        member _.AbortSession _ = Task.FromResult(Ok())
        member _.InterruptAttempt _ = Task.FromResult(Ok())
        member _.IsManagedChild _ = true
        member _.AbortChildren _ = Task.FromResult()
        member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "unused")
        member _.TryGetParentSession _ = Task.FromResult(Ok None)

        member _.CreateChildSession(_, _) =
            createCount <- createCount + 1
            let child = $"created-{createCount}"
            created.Add child
            Task.FromResult(Ok(SessionId.create child))

        member _.ListChildren _ =
            if queryError then
                Task.FromResult(Error "children unavailable")
            else
                Task.FromResult(Ok existing)

        member _.FamilyRootOf _ = SessionId.create "work"

module SatelliteSurface =
    [<Emit("Promise.all($0)")>]
    let private promiseAll (promises: Task<'T> array) : Task<'T array> = jsNative

    let private spec (port: SatelliteSessionPort) linked =
        { Kind = SatelliteKind.Companion
          Agent = "companion"
          Title = "Companion"
          Directory = None
          RestoredSessionId =
            if linked then
                Some(SessionId.create "companion-1")
            else
                None
          Link = port.Link
          Close = port.Close }

    let private originName origin =
        match origin with
        | SatelliteOrigin.Created -> "Created"
        | SatelliteOrigin.Reused -> "Reused"
        | SatelliteOrigin.Replacement -> "Replacement"

    let scenario (linked: bool) (physical: bool) (conflict: bool) (queryError: bool) : Task<obj> =
        task {
            let port = SatelliteSessionPort(physical, conflict, queryError)
            let runtime = CompanionLeaseRuntime(port, AttachmentLeaseRegistry())
            let! outcome = runtime.Ensure(SessionId.create "work", spec port linked)

            return
                match outcome with
                | Ok lease ->
                    box
                        {| ok = true
                           origin = originName lease.Origin
                           child = SessionId.value lease.SessionId
                           created = port.Created
                           closed = port.Closed
                           linked = port.LinkedFacts |}
                | Error error ->
                    box
                        {| ok = false
                           error = error
                           created = port.Created
                           closed = port.Closed
                           linked = port.LinkedFacts |}
        }

    let concurrent () : Task<obj> =
        task {
            let port = SatelliteSessionPort(false, false, false)
            let runtime = CompanionLeaseRuntime(port, AttachmentLeaseRegistry())
            let first = runtime.Ensure(SessionId.create "work", spec port false)
            let second = runtime.Ensure(SessionId.create "work", spec port false)
            let! outcomes = promiseAll [| first; second |]

            let children =
                outcomes
                |> Array.choose (function
                    | Ok lease -> Some(SessionId.value lease.SessionId)
                    | Error _ -> None)

            return
                box
                    {| children = children
                       created = port.Created |}
        }
