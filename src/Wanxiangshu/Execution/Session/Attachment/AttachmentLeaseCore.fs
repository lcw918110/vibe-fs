namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Collections.Generic
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// The ONE lifecycle mechanism for every Attached session (managed-session-lifecycle
/// [001]). Each AttachmentKind supplies only its parameters — agent, title, restore
/// hint — and this module performs creation, recovery, registration, single-flight
/// and release for all of them.
///
/// It deliberately depends on nothing from the OpenCode layer: each caller supplies
/// its own child operations as data. That is what lets Companion, SyncDelegate and
/// the read-only replica share a single owner instead of each growing its own.
[<RequireQualifiedAccess>]
module AttachmentLeaseCore =

    /// A child as the Host reports it. Only the fields the lease decision needs.
    type Child =
        { SessionId: string
          Agent: string option
          Title: string option }

    /// What the caller must supply for its own layer. No OpenCode types.
    type Operations =
        { ListChildren: string -> Task<Result<Child list, string>>
          CreateChild: string -> string -> string option -> Task<Result<string, string>>
          AbortChild: string -> Task<Result<unit, string>>
          Link: string -> string -> string -> Task<Result<unit, string>>
          Close: string -> Task<Result<unit, string>> }

    /// Every kind's parameters. This is the whole of what a kind may differ in.
    type Spec =
        {
            Kind: AttachmentKind
            Agent: string
            Title: string
            Directory: string option
            /// The child this kind's durable state says it already has, if any.
            RestoreHint: string option
        }

    type Origin =
        | Created
        | Reused
        | Replacement

    type Lease =
        {
            SessionId: string
            Origin: Origin
            /// The agent this child was established for. A reuse answers with the
            /// agent bound at create time; a later request with a different agent
            /// must not overwrite it, so the lease carries it rather than the caller.
            Agent: string
        }

    /// Stable registry key for a kind. Structural equality on F# unions would work,
    /// but a total string projection keeps the registry independent of how the
    /// union happens to be compiled and makes the key observable in diagnostics.
    let kindKey (kind: AttachmentKind) =
        match kind with
        | AttachmentKind.Companion -> "companion"
        | AttachmentKind.SyncInspector -> "sync-inspector"
        | AttachmentKind.SyncCoder -> "sync-coder"
        | AttachmentKind.SyncEngineer -> "sync-engineer"
        | AttachmentKind.SyncDevOps -> "sync-devops"
        | AttachmentKind.Bookkeeper transactionId -> "bookkeeper\u001f" + transactionId


    let leaseKey (owner: SessionId) (kind: AttachmentKind) =
        SessionId.value owner + "\u001f" + kindKey kind

    /// A restored child is trusted only when it matches the kind's parameters
    /// exactly. Partial matches are ambiguity, not reuse.
    let private exact (spec: Spec) (child: Child) =
        child.Agent = Some spec.Agent && child.Title = Some spec.Title

    /// Resolve which child this kind should use. Never guesses: an exact match is
    /// reused, an explicit restore hint that is absent yields a Replacement (the
    /// caller closes the vanished association), and everything else creates.
    let private resolve
        (operations: Operations)
        (owner: SessionId)
        (spec: Spec)
        (children: Child list)
        : Task<Result<Lease, string>> =
        let ownerText = SessionId.value owner

        let create origin =
            task {
                match! operations.CreateChild ownerText spec.Agent spec.Directory with
                | Ok child ->
                    return
                        Ok
                            { SessionId = child
                              Origin = origin
                              Agent = spec.Agent }
                | Error error -> return Error error
            }

        /// The children the hint's durable association points at: exactly one is the
        /// reused child, none authorizes a Replacement, more is ambiguity.
        let resolveHint (hash: string) (children: Child list) : Task<Result<Lease, string>> =
            match children |> List.filter (fun child -> child.SessionId = hash) with
            | [ child ] when exact spec child ->
                Task.FromResult(
                    Ok
                        { SessionId = child.SessionId
                          Origin = Reused
                          Agent = spec.Agent }
                )
            | [ _ ] ->
                Task.FromResult(
                    Error(
                        sprintf
                            "Conflicting %s recovery for %s: child %s has a different agent or title"
                            (kindKey spec.Kind)
                            ownerText
                            hash
                    )
                )
            | [] -> create Replacement
            | _ ->
                Task.FromResult(
                    Error(
                        sprintf "Ambiguous %s recovery for %s: duplicate child id %s" (kindKey spec.Kind) ownerText hash
                    )
                )

        match spec.RestoreHint with
        // No durable association: `RestoreHint` is the caller's durable state, so its
        // absence is "this kind has no linked child". Recovery never adopts an
        // unassociated child merely because it looks identical
        // (managed-session-lifecycle [003]: 不收养无关联会话) — a duplicate-shaped child
        // is not this kind's association.
        | None -> create Created
        | Some hint -> resolveHint hint children

    /// A lease that was created or replaced must not survive a failed link: the
    /// caller's durable association is what makes a child real, so a link failure
    /// aborts the fresh child instead of leaving an unowned one behind. A reused
    /// lease is never aborted — it was already real.
    let private abortFresh (operations: Operations) (lease: Lease) =
        task {
            match lease.Origin with
            | Reused -> ()
            | Created
            | Replacement ->
                let! _ = operations.AbortChild lease.SessionId
                ()
        }

    let private keepFreshOnlyOnSuccess (operations: Operations) (lease: Lease) operation =
        task {
            match! operation with
            | Ok() -> return Ok()
            | Error error ->
                do! abortFresh operations lease
                return Error error
        }

    /// Establish (or re-establish) the kind's lease and publish its association.
    let ensure (operations: Operations) (owner: SessionId) (spec: Spec) : Task<Result<Lease, string>> =
        taskResult {
            // A replacement is a real transition, not a repoint: close the vanished
            // association before establishing the new one.
            let closeReplaced (lease: Lease) =
                match lease.Origin, spec.RestoreHint with
                | Replacement, Some _ ->
                    operations.Close(SessionId.value owner)
                    |> keepFreshOnlyOnSuccess operations lease
                | _ -> Task.FromResult(Ok())

            let! children = operations.ListChildren(SessionId.value owner)
            let! lease = resolve operations owner spec children
            do! closeReplaced lease

            do!
                operations.Link (SessionId.value owner) lease.SessionId spec.Agent
                |> keepFreshOnlyOnSuccess operations lease

            return lease
        }

    /// Release the kind's lease: the child ends and the association closes. Used
    /// when the owner ends, and by kinds whose lifecycle ends with the call.
    let retire (operations: Operations) (owner: SessionId) : Task<Result<unit, string>> =
        operations.Close(SessionId.value owner)

/// The single lifecycle owner's registry: which child each `(owner, AttachmentKind)`
/// currently holds, and the in-flight ensure so concurrent first requests share one
/// creation instead of racing into two children.
type AttachmentLeaseRegistry() =
    let gate = obj ()
    // DSL-MUTABLE: resource — attached child identity by owner+kind
    let bindings = Dictionary<string, AttachmentLeaseCore.Lease>()
    // DSL-MUTABLE: resource — in-flight ensure by owner+kind
    let flights = Dictionary<string, Task<Result<AttachmentLeaseCore.Lease, string>>>()

    /// The kinds whose key is fully described by its rendered fragment. Bookkeeper
    /// carries a transaction id in a further fragment, so it is parsed separately.
    let fragmentKinds =
        [ AttachmentKind.Companion
          AttachmentKind.SyncInspector
          AttachmentKind.SyncCoder
          AttachmentKind.SyncEngineer
          AttachmentKind.SyncDevOps ]


    /// Re-parse one registry key back into `(owner, kind)`. The registry keeps only
    /// the key, so a reverse lookup recovers the kind from the key's own rendering
    /// instead of holding a second copy of every binding.
    let parseKey (key: string) =
        let parts = key.Split('\u001f')

        if parts.Length < 2 then
            None
        elif parts.Length >= 3 && parts.[1] = "bookkeeper" then
            Some(SessionId.create parts.[0], AttachmentKind.Bookkeeper parts.[2])
        else
            let rendered = parts.[1]

            fragmentKinds
            |> List.tryFind (fun candidate -> AttachmentLeaseCore.kindKey candidate = rendered)
            |> Option.map (fun kind -> SessionId.create parts.[0], kind)

    member _.Bind(owner: SessionId, kind: AttachmentKind, lease: AttachmentLeaseCore.Lease) =
        lock gate (fun () -> bindings.[AttachmentLeaseCore.leaseKey owner kind] <- lease)

    member _.TryFind(owner: SessionId, kind: AttachmentKind) : AttachmentLeaseCore.Lease option =
        lock gate (fun () ->
            match bindings.TryGetValue(AttachmentLeaseCore.leaseKey owner kind) with
            | true, lease -> Some lease
            | false, _ -> None)

    member _.Remove(owner: SessionId, kind: AttachmentKind) : bool =
        lock gate (fun () -> bindings.Remove(AttachmentLeaseCore.leaseKey owner kind))

    /// One kind's lease for one owner. This is the axis the Sync family resolves
    /// its `ReuseScopeId` lookup on: `ReuseScope.ofSession` is the identity wrap of
    /// a SessionId value, so an owner IS its own scope and the scope axis is the
    /// owner axis (see `AttachmentPort`).
    member this.TryFindScoped(owner: SessionId, kind: AttachmentKind) : AttachmentLeaseCore.Lease option =
        this.TryFind(owner, kind)

    /// Reverse lookup: which `(owner, kind)` currently holds this child. Used by
    /// callers that are handed a child id and must find its owner (Sync's
    /// scope-close and deleted-delegate paths).
    member _.TryFindByChild(child: SessionId) : (SessionId * AttachmentKind) list =
        lock gate (fun () ->
            bindings
            |> Seq.choose (fun entry ->
                if entry.Value.SessionId <> SessionId.value child then
                    None
                else
                    parseKey entry.Key)
            |> Seq.toList)

    /// Every binding, for bulk reconciliation on owner/cascade teardown.
    member _.Snapshot() : (SessionId * AttachmentKind) list =
        lock gate (fun () -> bindings |> Seq.choose (fun entry -> parseKey entry.Key) |> Seq.toList)

    /// The child itself is gone: drop whichever binding held it, so no owner keeps a
    /// dead lease.
    member _.RemoveByChild(child: SessionId) : (SessionId * AttachmentKind) list =
        lock gate (fun () ->
            let doomed =
                bindings
                |> Seq.filter (fun entry -> entry.Value.SessionId = SessionId.value child)
                |> Seq.toList

            doomed |> List.iter (fun entry -> bindings.Remove entry.Key |> ignore)

            doomed |> List.choose (fun entry -> parseKey entry.Key))

    /// Share one in-flight ensure per `(owner, kind)`.
    member _.Ensure
        (owner: SessionId, kind: AttachmentKind, start: unit -> Task<Result<AttachmentLeaseCore.Lease, string>>)
        : Task<Result<AttachmentLeaseCore.Lease, string>> =
        lock gate (fun () ->
            let key = AttachmentLeaseCore.leaseKey owner kind

            match flights.TryGetValue key with
            | true, inFlight -> inFlight
            | false, _ ->
                let inFlight = start ()
                flights.[key] <- inFlight
                inFlight)

    member _.FinishEnsure(owner: SessionId, kind: AttachmentKind) =
        lock gate (fun () -> flights.Remove(AttachmentLeaseCore.leaseKey owner kind) |> ignore)

    member _.Clear() =
        lock gate (fun () ->
            bindings.Clear()
            flights.Clear())
