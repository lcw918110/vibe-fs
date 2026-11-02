namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

type ObserveAttachedChild =
    SessionId -> ReuseScopeId -> SyncDelegateRole -> string -> Task<Result<AttachedChildObservation, string>>

type CreateAttachedChild =
    SessionId -> ReuseScopeId -> SyncDelegateRole -> string -> string option -> Task<Result<SessionId, string>>

type AttachedSessionRuntime
    (?registry: AttachmentLeaseRegistry, ?registerParent: SessionId -> SessionId -> unit, ?isUsable: SessionId -> bool)
    =
    let leases = defaultArg registry (AttachmentLeaseRegistry())
    let sessions = leases.Sessions
    let register = defaultArg registerParent (fun _ _ -> ())
    let usable = defaultArg isUsable (fun _ -> true)
    let canUse = Func<string, bool>(fun child -> usable (SessionId.create child))

    let kindKey (role: SyncDelegateRole) =
        role |> SyncDelegateRole.toAttachmentKind |> AttachmentLeaseCore.kindKey

    let ownKinds =
        [| AttachmentKind.SyncInspector
           AttachmentKind.SyncCoder
           AttachmentKind.SyncEngineer
           AttachmentKind.SyncDevOps |]
        |> Array.map AttachmentLeaseCore.kindKey

    let leaseValue (lease: obj) : SessionId * string =
        SessionId.create (unbox<string> lease?sessionId), unbox<string> lease?agent

    let observeHint (observed: Result<AttachedChildObservation, string>) : Result<string option, string> =
        match observed with
        | Error error -> Error error
        | Ok(AttachedChildObservation.Matching childId) -> Ok(Some(SessionId.value childId))
        | Ok AttachedChildObservation.Missing -> Ok None
        | Ok(AttachedChildObservation.Conflicting children) ->
            Error(
                sprintf
                    "sync delegate child observation conflicted: %s"
                    (children |> List.map SessionId.value |> String.concat ", ")
            )

    member _.GetOrCreate
        (
            ownerSessionId: SessionId,
            role: SyncDelegateRole,
            agentName: string,
            directory: string option,
            observeChild: ObserveAttachedChild,
            createChild: CreateAttachedChild,
            bindChild: SessionId -> SessionId -> string -> unit,
            onReady: SessionId -> string -> unit
        ) : Task<Result<SessionId * string, string>> =
        let scope = ReuseScope.ofSession ownerSessionId

        let spec =
            createObj
                [ "owner" ==> SessionId.value ownerSessionId
                  "kind" ==> kindKey role
                  "agent" ==> agentName
                  "directory" ==> AttachmentLeaseCore.optionalText directory ]

        let effects =
            createObj
                [ "isUsable" ==> canUse
                  "observe"
                  ==> Func<obj, Task<obj>>(fun _ ->
                      task {
                          let! observed = observeChild ownerSessionId scope role agentName

                          return
                              observeHint observed
                              |> AttachmentLeaseCore.resultToNative AttachmentLeaseCore.optionalText
                      })
                  "create"
                  ==> Func<obj, Task<obj>>(fun request ->
                      task {
                          let directory = Option.ofObj (unbox<string> request?directory)
                          let! created = createChild ownerSessionId scope role agentName directory

                          return
                              created
                              |> AttachmentLeaseCore.resultToNative (fun child -> box (SessionId.value child))
                      })
                  "link"
                  ==> Func<obj, Task<obj>>(fun request ->
                      let child = SessionId.create (unbox<string> request?sessionId)
                      let agent = unbox<string> request?agent
                      register ownerSessionId child
                      bindChild ownerSessionId child agent
                      onReady child agent
                      Task.FromResult(AttachmentLeaseCore.resultToNative box (Ok()))) ]

        let pending = unbox<Task<obj>> (sessions?getOrCreate (spec, effects))

        task {
            let! result = pending
            return AttachmentLeaseCore.resultFromNative leaseValue result
        }

    member _.TryFind(ownerSessionId: SessionId, role: SyncDelegateRole) : SessionId option =
        let lease: obj =
            sessions?tryFind (SessionId.value ownerSessionId, kindKey role, canUse)

        match box lease with
        | null -> None
        | _ -> Some(leaseValue lease |> fst)

    member _.TryFindByScope(scope: ReuseScopeId, role: SyncDelegateRole) : SessionId option =
        let lease: obj = sessions?tryFind (ReuseScopeId.value scope, kindKey role, canUse)

        match box lease with
        | null -> None
        | _ -> Some(leaseValue lease |> fst)

    member _.TryFindOwner(delegateSessionId: SessionId, role: SyncDelegateRole) : SessionId option =
        let owner: string =
            sessions?tryFindOwner (SessionId.value delegateSessionId, kindKey role)

        Option.ofObj owner |> Option.map SessionId.create

    member _.Remove(ownerSessionId: SessionId, role: SyncDelegateRole) : bool =
        unbox<bool> (sessions?remove (SessionId.value ownerSessionId, kindKey role))

    member _.RemoveByDelegateSession(delegateSessionId: SessionId) : bool =
        unbox<bool> (sessions?removeByChild (SessionId.value delegateSessionId))

    member _.Clear() =
        sessions?clearKinds (ownKinds) |> ignore

    interface IAttachedSessionPort with
        member this.TryFind(ownerSessionId, role) = this.TryFind(ownerSessionId, role)
        member this.TryFindByScope(scope, role) = this.TryFindByScope(scope, role)

        member this.TryFindOwner(delegateSessionId, role) =
            this.TryFindOwner(delegateSessionId, role)

        member this.Remove(ownerSessionId, role) = this.Remove(ownerSessionId, role)

        member this.RemoveByDelegateSession(delegateSessionId) =
            this.RemoveByDelegateSession(delegateSessionId)

        member this.GetOrCreate
            (ownerSessionId, role, agentName, directory, observeChild, createChild, bindChild, onReady)
            =
            this.GetOrCreate(ownerSessionId, role, agentName, directory, observeChild, createChild, bindChild, onReady)

        member this.Clear() = this.Clear()
