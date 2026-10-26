namespace Wanxiangshu.Composition.Turn

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession

/// Root / physical / continuation bindings for a logical run.
///
/// State ownership (userMessageBindings, activeBindings, continuationMessageIds)
/// and merge algorithms are 100% owned by wanxiangli/association (Package-14).
/// Store delegates all state and domain logic to the library instance.
module TurnBinding =

    [<Import("TurnBindingStore", "wanxiangli/association")>]
    let private turnBindingStoreClass: obj = jsNative

    [<Emit("new $0()")>]
    let private createTurnBindingStore (cls: obj) : obj = jsNative

    [<Emit("$0 === null || $0 === undefined")>]
    let private isAbsent (x: obj) : bool = jsNative

    let private optString (v: obj) : string option =
        if isAbsent v then None else Some (unbox<string> v)

    let private optConts (v: obj) : Set<string> =
        if isAbsent v then Set.empty else Set.ofArray (unbox<string array> v)

    let private collectUserMessageBindings (entries: obj array) : Dictionary<string, PhysicalUserMessageId> =
        let dict = Dictionary<string, PhysicalUserMessageId>()
        let safeEntries = if isAbsent entries then [||] else entries
        safeEntries
        |> Array.iter (fun e ->
            let sid = string e?sessionId
            let phys = string e?physical
            let valid = not (String.IsNullOrEmpty sid) && not (String.IsNullOrEmpty phys)
            if valid then dict.[sid] <- PhysicalUserMessageId.create phys)
        dict

    let private extractProjectedSummary (sessionId: SessionId) (proj: AgentProjectionSet) : obj option =
        let directRun =
            Map.tryFind sessionId proj.Sessions
            |> Option.bind (fun session -> session.PromptAuthority)
            |> Option.bind (fun authority -> authority.ActiveLogicalRun)

        let inheritedProfile =
            PromptAuthorityProjectionQueries.inheritedOwnerProfile sessionId proj

        match directRun, inheritedProfile with
        | Some run, _ ->
            createObj
                [ "activeRootId" ==> box (AuthorityRootUserMessageId.value run.AuthorityRootUserMessageId)
                  "canonicalRole" ==> box (Roles.roleLabel run.CanonicalRole)
                  "inheritedOwnerSessionId" ==> null
                  "inheritedOwnerRootId" ==> null
                  "inheritedOwnerRole" ==> null
                  "lanePhysicalId" ==> null ]
            |> Some
        | None, Some profile ->
            let owner = PromptAuthorityProjectionQueries.laneOwnerSessionId sessionId proj
            let lanePhysicals = PromptAuthorityProjectionQueries.ownContinuationPhysicals sessionId proj
            let lanePhys = lanePhysicals |> List.tryLast |> Option.map PhysicalUserMessageId.value
            createObj
                [ "activeRootId" ==> null
                  "canonicalRole" ==> null
                  "inheritedOwnerSessionId" ==> (match owner with Some o -> box (SessionId.value o) | None -> null)
                  "inheritedOwnerRootId" ==> box (AuthorityRootUserMessageId.value profile.AuthorityRootUserMessageId)
                  "inheritedOwnerRole" ==> box (Roles.roleLabel profile.CanonicalRole)
                  "lanePhysicalId" ==> (match lanePhys with Some p -> box p | None -> null) ]
            |> Some
        | None, None -> None

    let private decodeActiveRunBinding (sessionId: SessionId) (obj': obj) : ActiveRunBinding option =
        if isAbsent obj' then None
        else
            Some
                { SessionId = sessionId
                  RunId = optString obj'?runId
                  AuthorityRootUserMessageId = optString obj'?authorityRootUserMessageId |> Option.map AuthorityRootUserMessageId.create
                  PhysicalUserMessageId = optString obj'?physicalUserMessageId |> Option.map PhysicalUserMessageId.create
                  ContinuationMessageIds = optConts obj'?continuationMessageIds
                  Role = optString obj'?role |> Option.bind Roles.tryParseRole
                  Directory = optString obj'?directory
                  InheritedAuthorityOwner = optString obj'?inheritedAuthorityOwner |> Option.map SessionId.create }

    /// Thin delegation store. All state and merge algorithms reside in wanxiangli/association.
    type Store private (inner: obj) =
        new() = Store(createTurnBindingStore turnBindingStoreClass)

        member _.UserMessageBindings: Dictionary<string, PhysicalUserMessageId> =
            let entries: obj array = inner?allUserMessageBindings()
            collectUserMessageBindings entries

        member _.BindUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId, ?agentRole: Role) =
            let roleStr = agentRole |> Option.map Roles.roleLabel
            inner?bindUserMessage(SessionId.value sessionId, PhysicalUserMessageId.value physical, roleStr) |> ignore

        member _.BindContinuationUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId) =
            inner?bindContinuationUserMessage(SessionId.value sessionId, PhysicalUserMessageId.value physical) |> ignore

        member _.BindPhysicalUserMaterial(sessionId: SessionId, physical: PhysicalUserMessageId) =
            inner?bindPhysicalUserMaterial(SessionId.value sessionId, PhysicalUserMessageId.value physical) |> ignore

        member _.BindActiveRun(binding: ActiveRunBinding) =
            let roleStr = binding.Role |> Option.map Roles.roleLabel
            let rootStr = binding.AuthorityRootUserMessageId |> Option.map AuthorityRootUserMessageId.value
            let physStr = binding.PhysicalUserMessageId |> Option.map PhysicalUserMessageId.value
            let ownerStr = binding.InheritedAuthorityOwner |> Option.map SessionId.value
            let conts = binding.ContinuationMessageIds |> Set.toArray

            let recordObj =
                createObj
                    [ "sessionId" ==> box (SessionId.value binding.SessionId)
                      "runId" ==> (match binding.RunId with Some r -> box r | None -> null)
                      "authorityRootUserMessageId" ==> (match rootStr with Some r -> box r | None -> null)
                      "physicalUserMessageId" ==> (match physStr with Some p -> box p | None -> null)
                      "continuationMessageIds" ==> box conts
                      "role" ==> (match roleStr with Some r -> box r | None -> null)
                      "directory" ==> (match binding.Directory with Some d -> box d | None -> null)
                      "inheritedAuthorityOwner" ==> (match ownerStr with Some o -> box o | None -> null) ]

            inner?bindActiveRun(recordObj) |> ignore

        member _.ActiveRunBinding(sessionId: SessionId, ?projection: AgentProjectionSet) : ActiveRunBinding option =
            let summaryObj = projection |> Option.bind (extractProjectedSummary sessionId)
            let res: obj = inner?activeRunBinding(SessionId.value sessionId, summaryObj)
            decodeActiveRunBinding sessionId res

        member _.TryPhysicalUserMessage(sessionId: SessionId) : PhysicalUserMessageId option =
            let phys: obj = inner?tryPhysicalUserMessage(SessionId.value sessionId)
            if isAbsent phys then None
            else Some (PhysicalUserMessageId.create (unbox<string> phys))

        member _.ClearSession(sessionId: SessionId) =
            inner?clearSession(SessionId.value sessionId) |> ignore
