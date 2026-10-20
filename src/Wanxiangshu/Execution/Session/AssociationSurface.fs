namespace Wanxiangshu.Execution.Session

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// Plain keyed-lookup surface for the durable Work ↔ Companion association.
/// The projection map and its typed association records remain production-owned.
[<RequireQualifiedAccess>]
module AssociationSurface =

    let private text (value: obj) =
        if isNull value then "" else string value

    let private optionalText (value: obj) : string option =
        let value = text value

        if System.String.IsNullOrWhiteSpace value then
            None
        else
            Some value

    let private pairCompanion (pair: obj) : string option = optionalText (pair?companion)

    let private pairsOf (state: obj) : obj array =
        if isNull state then [||] else unbox<obj array> state

    let private mapOf (state: obj) : Map<SessionId, SessionAssociation> =
        pairsOf state
        |> Array.fold
            (fun current pair ->
                let main = SessionId.create (text (pair?main))
                let parent = optionalText (pair?parent) |> Option.map SessionId.create

                match pairCompanion pair with
                | Some companionId ->
                    match SessionAssociationProjection.link main (SessionId.create companionId) parent current with
                    | Ok next -> next
                    | Error rejection -> failwith (SessionAssociationProjection.describe rejection)
                | None ->
                    Map.add
                        main
                        { SessionId = main
                          Kind = ManagedSessionKind.WorkSession
                          ParentSessionId = parent }
                        current)
            Map.empty

    let private optionObj (value: string option) : obj =
        value |> Option.map box |> Option.defaultValue null

    let private stateOf (current: Map<SessionId, SessionAssociation>) : obj =
        current
        |> Map.toList
        |> List.choose (fun (_, entry) ->
            match entry.Kind with
            | ManagedSessionKind.WorkSession ->
                let companion =
                    SessionAssociationProjection.tryCompanionOf entry.SessionId current
                    |> Option.map SessionId.value

                Some(
                    box
                        {| main = SessionId.value entry.SessionId
                           companion = companion |> optionObj
                           parent = entry.ParentSessionId |> Option.map SessionId.value |> optionObj |}
                )
            | ManagedSessionKind.SatelliteSession _ -> None)
        |> List.toArray
        |> box

    let private entryToJs (entry: SessionAssociation) : obj =
        let kind, owner, satelliteKind =
            match entry.Kind with
            | ManagedSessionKind.WorkSession -> "WorkSession", None, None
            | ManagedSessionKind.SatelliteSession(owner, SatelliteKind.Companion) ->
                "SatelliteSession", Some(SessionId.value owner), Some "Companion"

        box
            {| kind = kind
               mainSessionId = owner |> Option.defaultValue null
               satelliteKind = satelliteKind |> Option.defaultValue null
               parent = entry.ParentSessionId |> Option.map SessionId.value |> Option.defaultValue null |}

    let private attachmentLabel (kind: AttachmentKind) : string * obj =
        match kind with
        | AttachmentKind.Companion -> "Companion", null
        | AttachmentKind.SyncInspector -> "SyncInspector", null
        | AttachmentKind.SyncCoder -> "SyncCoder", null
        | AttachmentKind.SyncEngineer -> "SyncEngineer", null
        | AttachmentKind.SyncDevOps -> "SyncDevOps", null
        | AttachmentKind.Bookkeeper transactionId -> "Bookkeeper", box transactionId


    let private ownershipToJs (ownership: SessionOwnership) : obj =
        match ownership with
        | SessionOwnership.Root ->
            box
                {| kind = "Root"
                   owner = null
                   attachment = null
                   transactionId = null |}
        | SessionOwnership.Attached(owner, attachment) ->
            let kind, transactionId = attachmentLabel attachment

            box
                {| kind = "Attached"
                   owner = SessionId.value owner
                   attachment = kind
                   transactionId = transactionId |}

    let private executionClassLabel (value: SessionExecutionClass) : string =
        match value with
        | SessionExecutionClass.Work -> "Work"
        | SessionExecutionClass.InternalLeaf -> "InternalLeaf"

    let empty: obj = [||]

    let private rejectionLabel (rejection: AssociationRejection) : string =
        match rejection with
        | AssociationRejection.CompanionWouldRecurse _ -> "CompanionWouldRecurse"
        | AssociationRejection.SelfLink _ -> "SelfLink"
        | AssociationRejection.AlreadyLinkedToOther _ -> "AlreadyLinkedToOther"
        | AssociationRejection.CompanionClaimedByOther _ -> "CompanionClaimedByOther"
        | AssociationRejection.SatelliteKindConflict _ -> "SatelliteKindConflict"

    let link (pair: obj) (state: obj) : obj =
        let main = SessionId.create (text (pair?main))

        let companionText =
            match pairCompanion pair with
            | Some c -> c
            | None -> ""

        let companionId = SessionId.create companionText
        let parent = optionalText (pair?parent) |> Option.map SessionId.create

        match SessionAssociationProjection.link main companionId parent (mapOf state) with
        | Ok next ->
            box
                {| ok = true
                   value = stateOf next
                   error = ""
                   message = "" |}
        | Error rejection ->
            let message = SessionAssociationProjection.describe rejection

            box
                {| ok = false
                   value = null
                   error = rejectionLabel rejection
                   message = message |}

    let unlink (mainSessionId: string) (state: obj) : obj =
        SessionAssociationProjection.unlink (SessionId.create mainSessionId) (mapOf state)
        |> stateOf

    let entry (sessionId: string) (state: obj) : obj =
        SessionAssociationProjection.tryFind (SessionId.create sessionId) (mapOf state)
        |> Option.map entryToJs
        |> Option.defaultValue null

    let classify (sessionId: string) (state: obj) : obj =
        SessionOwnershipClassification.tryClassify (SessionId.create sessionId) (mapOf state)
        |> Option.map (fun (executionClass, ownership) ->
            box
                {| executionClass = executionClassLabel executionClass
                   ownership = ownership |> Option.map ownershipToJs |> Option.defaultValue null |})
        |> Option.defaultValue null

    let ids (state: obj) : string array =
        pairsOf state
        |> Array.collect (fun pair ->
            [| text (pair?main)
               match pairCompanion pair with
               | Some value -> value
               | None -> "" |])
        |> Array.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Array.distinct


    let mainSessionOf (sessionId: string) (state: obj) : obj =
        SessionAssociationProjection.tryMainSessionOf (SessionId.create sessionId) (mapOf state)
        |> Option.map (fun value -> box (SessionId.value value))
        |> Option.defaultValue null

    let isCompanion (sessionId: string) (state: obj) : bool =
        SessionAssociationProjection.isCompanion (SessionId.create sessionId) (mapOf state)

    let executionClass (kind: string) : obj =
        let value =
            if kind = "InternalLeaf" then
                SessionExecutionClass.InternalLeaf
            else
                SessionExecutionClass.Work

        box
            {| name = executionClassLabel value
               isWork = SessionExecutionClass.isWork value
               isInternalLeaf = SessionExecutionClass.isInternalLeaf value |}

    let ownershipRoot: obj = ownershipToJs SessionOwnership.Root

    let ownershipAttached (owner: string) (attachment: string) : obj =
        let kind =
            match attachment with
            | "SyncInspector" -> AttachmentKind.SyncInspector
            | "SyncCoder" -> AttachmentKind.SyncCoder
            | "SyncEngineer" -> AttachmentKind.SyncEngineer
            | "SyncDevOps" -> AttachmentKind.SyncDevOps
            | "Bookkeeper" -> AttachmentKind.Bookkeeper ""

            | _ -> AttachmentKind.Companion

        ownershipToJs (SessionOwnership.Attached(SessionId.create owner, kind))

    let attachment (kind: string) : obj =
        let value =
            match kind with
            | "SyncInspector" -> AttachmentKind.SyncInspector
            | "SyncCoder" -> AttachmentKind.SyncCoder
            | "SyncEngineer" -> AttachmentKind.SyncEngineer
            | "SyncDevOps" -> AttachmentKind.SyncDevOps
            | "Bookkeeper" -> AttachmentKind.Bookkeeper ""

            | _ -> AttachmentKind.Companion

        let label, transactionId = attachmentLabel value

        box
            {| name = label
               transactionId = transactionId |}

    let bookkeeperAttachment (transactionId: string) : obj =
        let name, transaction = attachmentLabel (AttachmentKind.Bookkeeper transactionId)

        box
            {| name = name
               transactionId = transaction |}

    let dedicatedExecutionClass: string =
        executionClassLabel SyncDelegateAssociationHints.dedicatedExecutionClass

    let private dedicatedRole (role: string) =
        match role with
        | "Coder" -> SyncDelegateRole.Coder
        | "Engineer" -> SyncDelegateRole.Engineer
        | "DevOps" -> SyncDelegateRole.DevOps
        | _ -> SyncDelegateRole.Inspector

    let dedicatedOwnership (owner: string) (role: string) : obj =
        SyncDelegateAssociationHints.dedicatedOwnership (SessionId.create owner) (dedicatedRole role)
        |> ownershipToJs

    let dedicatedAttachment (role: string) : string =
        dedicatedRole role
        |> SyncDelegateRole.toAttachmentKind
        |> attachmentLabel
        |> fst
