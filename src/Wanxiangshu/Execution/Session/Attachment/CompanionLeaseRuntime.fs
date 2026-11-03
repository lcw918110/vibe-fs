namespace Wanxiangshu.Execution.Session.Attachment

open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession

type SatelliteOrigin =
    | Created
    | Reused
    | Replacement

type SatelliteLease =
    { SessionId: SessionId
      Origin: SatelliteOrigin }

type SatelliteSpec =
    { Kind: SatelliteKind
      Agent: string
      Title: string
      Directory: string option
      RestoredSessionId: SessionId option
      Link: SessionId -> SessionId -> string -> Task<Result<unit, string>>
      Close: SessionId -> Task<Result<unit, string>> }

type CompanionLeaseRuntime(sessions: ISessionHostPort, registry: AttachmentLeaseRegistry) =
    let attached = registry.Sessions
    let selfKind = AttachmentLeaseCore.kindKey AttachmentKind.Companion

    let childValue (child: ChildSessionInfo) : obj =
        createObj
            [ "sessionId" ==> SessionId.value child.SessionId
              "agent" ==> AttachmentLeaseCore.optionalText child.Agent
              "title" ==> AttachmentLeaseCore.optionalText child.Title ]

    let operations (spec: SatelliteSpec) : obj =
        createObj
            [ "list"
              ==> Func<obj, Task<obj>>(fun request ->
                  task {
                      let owner = SessionId.create (unbox<string> request?owner)
                      let root = sessions.FamilyRootOf owner
                      let! rootChildren = sessions.ListChildren root

                      let! ownerChildren =
                          if root = owner then
                              Task.FromResult(Ok [])
                          else
                              sessions.ListChildren owner

                      let children =
                          match rootChildren, ownerChildren with
                          | Ok rootList, Ok ownerList ->
                              Ok(
                                  (rootList @ ownerList)
                                  |> List.distinctBy (fun child -> SessionId.value child.SessionId)
                                  |> List.map childValue
                                  |> List.toArray
                              )
                          | Error error, _
                          | _, Error error -> Error(sprintf "Cannot recover companion satellite: %s" error)

                      return AttachmentLeaseCore.resultToNative box children
                  })
              "create"
              ==> Func<obj, Task<obj>>(fun request ->
                  task {
                      let! created =
                          sessions.CreateChildSession(
                              SessionId.create (unbox<string> request?owner),
                              { Title = Some spec.Title
                                Agent = Some spec.Agent
                                Directory = Option.ofObj (unbox<string> request?directory) }
                          )

                      return
                          created
                          |> AttachmentLeaseCore.resultToNative (fun child -> box (SessionId.value child))
                  })
              "abort"
              ==> Func<obj, Task<obj>>(fun request ->
                  task {
                      let! result = sessions.AbortSession(SessionId.create (unbox<string> request?sessionId))
                      return AttachmentLeaseCore.resultToNative box result
                  })
              "link"
              ==> Func<obj, Task<obj>>(fun request ->
                  task {
                      let! result =
                          spec.Link
                              (SessionId.create (unbox<string> request?owner))
                              (SessionId.create (unbox<string> request?sessionId))
                              (unbox<string> request?agent)

                      return AttachmentLeaseCore.resultToNative box result
                  })
              "close"
              ==> Func<obj, Task<obj>>(fun request ->
                  task {
                      let! result = spec.Close(SessionId.create (unbox<string> request?owner))
                      return AttachmentLeaseCore.resultToNative box result
                  })
              "rejection"
              ==> Func<obj, string>(fun request ->
                  let kind = unbox<string> request?kind
                  let owner = unbox<string> request?owner
                  let child = unbox<string> request?child

                  match unbox<string> request?rejection with
                  | "Conflicting" ->
                      sprintf
                          "Conflicting %s recovery for %s: child %s has a different agent or title"
                          kind
                          owner
                          child
                  | "Ambiguous" -> sprintf "Ambiguous %s recovery for %s: duplicate child id %s" kind owner child
                  | other -> invalidOp ("wanxiangli/attachment-lease returned an unknown rejection: " + other)) ]

    let leaseValue (lease: obj) : SatelliteLease =
        let origin =
            match unbox<string> lease?origin with
            | "Created" -> SatelliteOrigin.Created
            | "Reused" -> SatelliteOrigin.Reused
            | "Replacement" -> SatelliteOrigin.Replacement
            | other -> invalidOp ("wanxiangli/attachment-lease returned an unknown origin: " + other)

        { SessionId = SessionId.create (unbox<string> lease?sessionId)
          Origin = origin }

    member _.Ensure(owner: SessionId, spec: SatelliteSpec) : Task<Result<SatelliteLease, string>> =
        let request =
            createObj
                [ "owner" ==> SessionId.value owner
                  "kind" ==> selfKind
                  "agent" ==> spec.Agent
                  "title" ==> spec.Title
                  "directory" ==> AttachmentLeaseCore.optionalText spec.Directory
                  "restoreHint"
                  ==> (spec.RestoredSessionId
                       |> Option.map SessionId.value
                       |> AttachmentLeaseCore.optionalText) ]

        task {
            let! result = unbox<Task<obj>> (attached?ensureAssociated (request, operations spec))
            return AttachmentLeaseCore.resultFromNative leaseValue result
        }

    member _.Retire(owner: SessionId, spec: SatelliteSpec) : Task<Result<unit, string>> =
        task {
            let! result =
                unbox<Task<obj>> (attached?retireAssociated (SessionId.value owner, selfKind, operations spec))

            return AttachmentLeaseCore.resultFromNative ignore result
        }
