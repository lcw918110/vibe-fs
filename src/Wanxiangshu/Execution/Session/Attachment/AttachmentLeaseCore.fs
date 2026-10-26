namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
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

    /// CUT-12 sub-loop 3: which child the kind's lease uses (reuse / conflict /
    /// replacement / ambiguity / create) and whether a fresh child is aborted on a
    /// failed close or link have one owner, wanxiangli/attachment-lease. This module
    /// lists the children, performs the operations and maps the verdict back.
    [<Import("AttachmentLeaseLaw", "wanxiangli/attachment-lease")>]
    let private lawClass: obj = jsNative

    [<Emit("new $0()")>]
    let private construct (cls: obj) : obj = jsNative

    let private law: obj = construct lawClass

    let private optionalText (value: string option) : obj =
        match value with
        | Some text -> box text
        | None -> null

    let private originName (origin: Origin) =
        match origin with
        | Created -> "Created"
        | Reused -> "Reused"
        | Replacement -> "Replacement"

    let private createdOriginOf (value: obj) : Origin =
        match unbox<string> value with
        | "Created" -> Created
        | "Replacement" -> Replacement
        | other -> invalidOp ("wanxiangli/attachment-lease returned an unknown origin: " + other)

    let private rejectionMessage (kind: AttachmentKind) (ownerText: string) (hash: string) (rejection: obj) : string =
        match unbox<string> rejection with
        | "Conflicting" ->
            sprintf
                "Conflicting %s recovery for %s: child %s has a different agent or title"
                (kindKey kind)
                ownerText
                hash
        | "Ambiguous" -> sprintf "Ambiguous %s recovery for %s: duplicate child id %s" (kindKey kind) ownerText hash
        | other -> invalidOp ("wanxiangli/attachment-lease returned an unknown rejection: " + other)

    /// Resolve which child this kind should use. Never guesses: the library decides,
    /// this side creates the child a `create` verdict asks for.
    let private resolve
        (operations: Operations)
        (owner: SessionId)
        (spec: Spec)
        (children: Child list)
        : Task<Result<Lease * bool, string>> =
        let ownerText = SessionId.value owner

        let create origin closeReplacedBeforeLink =
            task {
                match! operations.CreateChild ownerText spec.Agent spec.Directory with
                | Ok child ->
                    return
                        Ok(
                            { SessionId = child
                              Origin = origin
                              Agent = spec.Agent },
                            closeReplacedBeforeLink
                        )
                | Error error -> return Error error
            }

        let verdict: obj =
            law?resolve (
                createObj
                    [ "restoreHint" ==> optionalText spec.RestoreHint
                      "agent" ==> spec.Agent
                      "title" ==> spec.Title
                      "children"
                      ==> (children
                           |> List.map (fun child ->
                               createObj
                                   [ "sessionId" ==> child.SessionId
                                     "agent" ==> optionalText child.Agent
                                     "title" ==> optionalText child.Title ])
                           |> List.toArray) ]
            )

        match unbox<string> verdict?verdict with
        | "reuse" ->
            Task.FromResult(
                Ok(
                    { SessionId = unbox<string> verdict?child
                      Origin = Reused
                      Agent = spec.Agent },
                    false
                )
            )
        | "create" ->
            let origin = createdOriginOf verdict?origin
            create origin (unbox<bool> verdict?closeReplacedBeforeLink)
        | "reject" ->
            Task.FromResult(Error(rejectionMessage spec.Kind ownerText (unbox<string> verdict?child) verdict?rejection))
        | other -> invalidOp ("wanxiangli/attachment-lease returned an unknown verdict: " + other)

    /// A lease that was created or replaced must not survive a failed link: the
    /// caller's durable association is what makes a child real. Which origins are
    /// fresh is the library's answer.
    let private abortFresh (operations: Operations) (lease: Lease) =
        task {
            if unbox<bool> (law?abortOnFailure (originName lease.Origin)) then
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
            let closeReplaced (lease: Lease) closeFirst =
                if closeFirst then
                    operations.Close(SessionId.value owner)
                    |> keepFreshOnlyOnSuccess operations lease
                else
                    Task.FromResult(Ok())

            let! children = operations.ListChildren(SessionId.value owner)
            let! lease, closeFirst = resolve operations owner spec children
            do! closeReplaced lease closeFirst

            do!
                operations.Link (SessionId.value owner) lease.SessionId spec.Agent
                |> keepFreshOnlyOnSuccess operations lease

            return lease
        }

    /// Release the kind's lease: the child ends and the association closes. Used
    /// when the owner ends, and by kinds whose lifecycle ends with the call.
    let retire (operations: Operations) (owner: SessionId) : Task<Result<unit, string>> =
        operations.Close(SessionId.value owner)

module private RegistryMapping =

    [<Import("AttachmentLeaseRegistry", "wanxiangli/attachment-lease")>]
    let registryClass: obj = jsNative

    let fragmentKinds =
        [ AttachmentKind.Companion
          AttachmentKind.SyncInspector
          AttachmentKind.SyncCoder
          AttachmentKind.SyncEngineer
          AttachmentKind.SyncDevOps ]

    let parseKind (kindText: string) : AttachmentKind option =
        match kindText.StartsWith("bookkeeper\u001f") with
        | true ->
            let txId = kindText.Substring("bookkeeper\u001f".Length)
            Some(AttachmentKind.Bookkeeper txId)
        | false ->
            fragmentKinds
            |> List.tryFind (fun candidate -> AttachmentLeaseCore.kindKey candidate = kindText)

    let decodeOrigin (originText: string) : AttachmentLeaseCore.Origin =
        match originText with
        | "Reused" -> AttachmentLeaseCore.Origin.Reused
        | "Replacement" -> AttachmentLeaseCore.Origin.Replacement
        | _ -> AttachmentLeaseCore.Origin.Created

    let originToString (origin: AttachmentLeaseCore.Origin) : string =
        match origin with
        | AttachmentLeaseCore.Origin.Created -> "Created"
        | AttachmentLeaseCore.Origin.Reused -> "Reused"
        | AttachmentLeaseCore.Origin.Replacement -> "Replacement"

    let encodeLease (lease: AttachmentLeaseCore.Lease) : obj =
        createObj
            [ "sessionId" ==> lease.SessionId
              "origin" ==> originToString lease.Origin
              "agent" ==> lease.Agent ]

    let decodeLease (item: obj) : AttachmentLeaseCore.Lease =
        { SessionId = unbox<string> item?sessionId
          Origin = decodeOrigin (unbox<string> item?origin)
          Agent = unbox<string> item?agent }

    let decodeOwnerPair (item: obj) : (SessionId * AttachmentKind) option =
        let ownerText = unbox<string> item?owner
        let kindText = unbox<string> item?kind
        parseKind kindText
        |> Option.map (fun kind -> SessionId.create ownerText, kind)

    let decodeOwnerPairs (items: obj array) : (SessionId * AttachmentKind) list =
        items
        |> Array.choose decodeOwnerPair
        |> Array.toList

open RegistryMapping

/// The single lifecycle owner's registry: which child each `(owner, AttachmentKind)`
/// currently holds, and the in-flight ensure so concurrent first requests share one
/// creation instead of racing into two children.
///
/// Package-21: State ownership migrated to wanxiangli/attachment-lease (AttachmentLeaseRegistry).
/// Host holds zero mutable collections and delegates 100% to library autonomous store.
type AttachmentLeaseRegistry() =
    let store: obj = emitJsExpr registryClass "new $0()"

    member _.Bind(owner: SessionId, kind: AttachmentKind, lease: AttachmentLeaseCore.Lease) : unit =
        store?bind(SessionId.value owner, AttachmentLeaseCore.kindKey kind, encodeLease lease)

    member _.TryFind(owner: SessionId, kind: AttachmentKind) : AttachmentLeaseCore.Lease option =
        let found: obj = store?tryFind(SessionId.value owner, AttachmentLeaseCore.kindKey kind)
        match box found with
        | null -> None
        | _ -> Some(decodeLease found)

    member _.Remove(owner: SessionId, kind: AttachmentKind) : bool =
        unbox<bool> (store?remove(SessionId.value owner, AttachmentLeaseCore.kindKey kind))

    member _.TryFindByChild(child: SessionId) : (SessionId * AttachmentKind) list =
        let items: obj array = store?tryFindByChild(SessionId.value child)
        decodeOwnerPairs items

    member _.Snapshot() : (SessionId * AttachmentKind) list =
        let items: obj array = store?snapshot()
        decodeOwnerPairs items

    member _.RemoveByChild(child: SessionId) : (SessionId * AttachmentKind) list =
        let items: obj array = store?removeByChild(SessionId.value child)
        decodeOwnerPairs items

    member _.Ensure
        (owner: SessionId, kind: AttachmentKind, start: unit -> Task<Result<AttachmentLeaseCore.Lease, string>>)
        : Task<Result<AttachmentLeaseCore.Lease, string>> =
        let inFlight: obj = store?ensure(SessionId.value owner, AttachmentLeaseCore.kindKey kind, start)
        unbox<Task<Result<AttachmentLeaseCore.Lease, string>>> inFlight

    member _.FinishEnsure(owner: SessionId, kind: AttachmentKind) : unit =
        store?finishEnsure(SessionId.value owner, AttachmentLeaseCore.kindKey kind)

    member _.Clear() : unit =
        store?clear()
