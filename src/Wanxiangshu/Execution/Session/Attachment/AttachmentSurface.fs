namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// JS-native boundary for the attached-session owner. The lease registry and every
/// kind adapter stay opaque; callers observe binding snapshots or drive the ONE
/// lifecycle owner through controlled ports.
module AttachmentSurface =
    let private roleOf =
        function
        | "Engineer" -> SyncDelegateRole.Engineer
        | "Coder" -> SyncDelegateRole.Coder
        | "Inspector" -> SyncDelegateRole.Inspector
        | "DevOps" -> SyncDelegateRole.DevOps
        | _ -> invalidArg "role" "Unknown attachment role"

    let createOwner () : obj = box (AttachedSessionRuntime())

    let getOrCreate
        (runtime: obj)
        (owner: string)
        (role: string)
        (agent: string)
        (createChild: string -> string -> string -> string -> Task<string>)
        (bindChild: string -> string -> string -> unit)
        : Task<obj> =
        task {
            let observe (_: SessionId) (_: ReuseScopeId) (_: SyncDelegateRole) (_: string) =
                Task.FromResult(Ok AttachedChildObservation.Missing)

            let create ownerId scope roleValue agentName (_: string option) =
                task {
                    let! child =
                        createChild
                            (SessionId.value ownerId)
                            (ReuseScopeId.value scope)
                            (SyncDelegate.roleLabel roleValue)
                            agentName

                    return Ok(SessionId.create child)
                }

            let bind ownerId childId agentName =
                bindChild (SessionId.value ownerId) (SessionId.value childId) agentName

            let! result =
                (runtime :?> AttachedSessionRuntime)
                    .GetOrCreate(
                        SessionId.create owner,
                        roleOf role,
                        agent,
                        None,
                        observe,
                        create,
                        bind,
                        (fun _ _ -> ())
                    )

            return
                match result with
                | Ok(child, boundAgent) ->
                    box
                        {| child = SessionId.value child
                           agent = boundAgent |}
                | Error detail -> box {| error = detail |}
        }

    let tryFind (runtime: obj) (owner: string) (role: string) : string option =
        (runtime :?> AttachedSessionRuntime)
            .TryFind(SessionId.create owner, roleOf role)
        |> Option.map SessionId.value

    let remove (runtime: obj) (owner: string) (role: string) : bool =
        (runtime :?> AttachedSessionRuntime).Remove(SessionId.create owner, roleOf role)

    let clear (runtime: obj) : unit =
        (runtime :?> AttachedSessionRuntime).Clear()

    let scenario
        (owner: string)
        (role: string)
        (firstAgent: string)
        (secondAgent: string)
        (retainBinding: bool)
        : Task<obj> =
        task {
            let roleValue = roleOf role

            // DSL-MUTABLE: algorithm-scratch — attachment id counter
            let next = ref 0
            let runtime = AttachedSessionRuntime()

            let createChild
                (_: SessionId)
                (_: ReuseScopeId)
                (_: SyncDelegateRole)
                (_agent: string)
                (_directory: string option)
                : Task<Result<SessionId, string>> =
                task {
                    next.Value <- next.Value + 1
                    return Ok(SessionId.create (sprintf "child-%d" next.Value))
                }

            let observeChild (_: SessionId) (_: ReuseScopeId) (_: SyncDelegateRole) (_agent: string) =
                Task.FromResult(Ok AttachedChildObservation.Missing)

            let bindChild (_: SessionId) (_: SessionId) (_agent: string) = ()
            let onReady (_: SessionId) (_agent: string) = ()

            let! first =
                runtime.GetOrCreate(
                    SessionId.create owner,
                    roleValue,
                    firstAgent,
                    None,
                    observeChild,
                    createChild,
                    bindChild,
                    onReady
                )

            if not retainBinding then
                runtime.Remove(SessionId.create owner, roleValue) |> ignore

            let! second =
                runtime.GetOrCreate(
                    SessionId.create owner,
                    roleValue,
                    secondAgent,
                    None,
                    observeChild,
                    createChild,
                    bindChild,
                    onReady
                )

            match first, second with
            | Ok firstValue, Ok secondValue ->
                return
                    box
                        {| owner = owner
                           role = SyncDelegate.roleLabel roleValue
                           firstChild = SessionId.value (fst firstValue)
                           firstAgent = snd firstValue
                           secondChild = SessionId.value (fst secondValue)
                           secondAgent = snd secondValue
                           created = next.Value |}
            | Error firstError, _ ->
                return
                    box
                        {| owner = owner
                           role = SyncDelegate.roleLabel roleValue
                           error = firstError
                           created = next.Value |}
            | _, Error secondError ->
                return
                    box
                        {| owner = owner
                           role = SyncDelegate.roleLabel roleValue
                           error = secondError
                           created = next.Value |}
        }

    /// managed-session-lifecycle-001 (GAP-133): drive every AttachmentKind through the
    /// ONE lifecycle owner against one controlled Host, and read from the shared
    /// registry which `(owner, kind)` holds each child. Companion, SyncInspector and
    /// SyncCoder are all established by the same core — through their kind adapters — with
    /// one registry recording them all, so an attachment kind is only ever parameters
    /// and terminal policy.
    let everyKindScenario (owner: string) : Task<obj> =
        task {
            // DSL-MUTABLE: algorithm-scratch — controlled Host child allocation
            let children = ResizeArray<SessionId>()
            // DSL-MUTABLE: algorithm-scratch — durable link/close journal
            let linked = ResizeArray<string array>()
            let closed = ResizeArray<string>()

            let ownerId = SessionId.create owner
            let registry = AttachmentLeaseRegistry()
            let sync = AttachedSessionRuntime(registry = registry)

            let host: ISessionHostPort =
                { new ISessionHostPort with
                    member _.SubscribeTerminal(_, _) =
                        { new IDisposable with
                            member _.Dispose() = () }

                    member _.SubscribeFutureTerminal(_, _) =
                        { new IDisposable with
                            member _.Dispose() = () }

                    member _.SendPrompt(_, _, _) = Task.FromResult(Unchecked.defaultof<_>)

                    member _.AbortSession _ = Task.FromResult(Ok())
                    member _.InterruptAttempt _ = Task.FromResult(Ok())
                    member _.IsManagedChild _ = true
                    member _.AbortChildren _ = Task.FromResult()
                    member _.CreateSiblingSession(_, _, _) = Task.FromResult(Error "unused")
                    member _.TryGetParentSession _ = Task.FromResult(Ok None)

                    member _.CreateChildSession(_, _) =
                        let id = SessionId.create (sprintf "child-%d" (children.Count + 1))
                        children.Add id
                        Task.FromResult(Ok id)

                    // The Host reports the children it really allocated, with the
                    // Companion kind's exact agent+title, so a hinted reuse is a real
                    // reuse rather than a replacement.
                    member _.ListChildren parent =
                        Task.FromResult(
                            Ok(
                                children
                                |> Seq.filter (fun child ->
                                    child = ownerId || SessionId.value parent = SessionId.value ownerId)
                                |> Seq.map (fun child ->
                                    { SessionId = child
                                      ParentSessionId = Some ownerId
                                      Agent = Some "companion"
                                      Title = Some "Companion" })
                                |> Seq.toList
                            )
                        )

                    member _.FamilyRootOf _ = ownerId }

            let companion = CompanionLeaseRuntime(host, registry)

            // The Sync kind's own ports: its observation decision is this kind's
            // exactness rule, its factory is this kind's child shape.
            // DSL-MUTABLE: algorithm-scratch — the child each Sync kind already
            // established in this scenario (its durable association)
            let syncChildren = Dictionary<string, SessionId>()

            let syncObserve (_: SessionId) (_: ReuseScopeId) (role: SyncDelegateRole) (agent: string) =
                let key = sprintf "%s/%s" agent (SyncDelegate.roleLabel role)

                Task.FromResult(
                    Ok(
                        match syncChildren.TryGetValue key with
                        | true, child -> AttachedChildObservation.Matching child
                        | false, _ -> AttachedChildObservation.Missing
                    )
                )

            let syncCreate
                (_: SessionId)
                (_: ReuseScopeId)
                (role: SyncDelegateRole)
                (agent: string)
                (_: string option)
                =
                let id = SessionId.create (sprintf "sync-child-%d" (children.Count + 1))
                children.Add id
                syncChildren.[sprintf "%s/%s" agent (SyncDelegate.roleLabel role)] <- id
                Task.FromResult(Ok id)

            let noBind (_: SessionId) (_: SessionId) (_: string) = ()
            let noReady (_: SessionId) (_: string) = ()

            let! syncInspector =
                sync.GetOrCreate(
                    ownerId,
                    SyncDelegateRole.Inspector,
                    "inspector",
                    None,
                    syncObserve,
                    syncCreate,
                    noBind,
                    noReady
                )

            let! syncCoder =
                sync.GetOrCreate(
                    ownerId,
                    SyncDelegateRole.Coder,
                    "coder",
                    None,
                    syncObserve,
                    syncCreate,
                    noBind,
                    noReady
                )

            let! companionLease =
                companion.Ensure(
                    ownerId,
                    { Kind = SatelliteKind.Companion
                      Agent = "companion"
                      Title = "Companion"
                      Directory = None
                      RestoredSessionId = None
                      Link =
                        fun o c a ->
                            linked.Add [| SessionId.value o; SessionId.value c; a |]
                            Task.FromResult(Ok())
                      Close =
                        fun o ->
                            closed.Add(SessionId.value o)
                            Task.FromResult(Ok()) }
                )

            let childOf =
                function
                | Ok(child: SessionId, _) -> Some(SessionId.value child)
                | _ -> None

            let inspectorChild = childOf syncInspector
            let coderChild = childOf syncCoder

            let companionChild =
                companionLease
                |> Result.map (fun l -> SessionId.value l.SessionId)
                |> Result.toOption

            let bindingOf child =
                registry.TryFindByChild(SessionId.create child)
                |> List.map (fun (boundOwner, kind) -> SessionId.value boundOwner, AttachmentLeaseCore.kindKey kind)

            let bindings =
                [ inspectorChild; coderChild; companionChild ]

                |> List.choose id
                |> List.map (fun child -> bindingOf child |> List.toArray)
                |> List.toArray

            // Scope isolation: a different scope is a different owner and sees nothing.
            let otherScope = ReuseScopeId.create "other-owner"

            // Reuse must answer with the agent bound at create time (Sync) and must
            // not mint a second child (Companion).
            let! coderReuse =
                sync.GetOrCreate(
                    ownerId,
                    SyncDelegateRole.Coder,
                    "different-agent",
                    None,
                    syncObserve,
                    syncCreate,
                    noBind,
                    noReady
                )

            let! companionReuse =
                companion.Ensure(
                    ownerId,
                    { Kind = SatelliteKind.Companion
                      Agent = "companion"
                      Title = "Companion"
                      Directory = None
                      // The Companion host always passes the durable association it
                      // holds; that hint is what makes this a reuse.
                      RestoredSessionId = companionChild |> Option.map SessionId.create
                      Link = (fun _ _ _ -> Task.FromResult(Ok()))
                      Close = (fun _ -> Task.FromResult(Ok())) }
                )

            return
                box
                    {| owner = owner
                       inspectorChild = Option.defaultValue "" inspectorChild
                       coderChild = Option.defaultValue "" coderChild
                       companionChild = Option.defaultValue "" companionChild
                       bindings = bindings

                       children = children |> Seq.map SessionId.value |> Seq.toArray
                       companionOrigin =
                        companionLease
                        |> Result.map (fun l -> sprintf "%A" l.Origin)
                        |> Result.defaultValue "Error"
                       companionReuseOrigin =
                        companionReuse
                        |> Result.map (fun l -> sprintf "%A" l.Origin)
                        |> Result.defaultValue "Error"
                       companionReuseChild =
                        companionReuse
                        |> Result.map (fun l -> SessionId.value l.SessionId)
                        |> Result.defaultValue ""
                       companionReuseLinked = linked.Count
                       companionClosedCount = closed.Count
                       coderReuseChild = coderReuse |> Result.map (fst >> SessionId.value) |> Result.defaultValue ""
                       coderReuseAgent = coderReuse |> Result.map snd |> Result.defaultValue ""
                       syncInspectorScopeChild =
                        sync.TryFindByScope(ReuseScope.ofSession ownerId, SyncDelegateRole.Inspector)
                        |> Option.map SessionId.value
                        |> Option.defaultValue ""
                       otherScopeChild =
                        sync.TryFindByScope(otherScope, SyncDelegateRole.Inspector)
                        |> Option.map SessionId.value
                        |> Option.defaultValue ""
                       registrySize = registry.Snapshot() |> List.length |}

        }
