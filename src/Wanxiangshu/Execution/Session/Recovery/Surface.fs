namespace Wanxiangshu.Execution.Session.Recovery

open Fable.Core.JsInterop
open Wanxiangshu.Foundation.Identity

/// Recovery-owned semantic boundary. Closure, family and permit laws cross as
/// strings, arrays and plain objects; typed recovery unions stay private.
[<RequireQualifiedAccess>]
module RecoverySurface =
    let private text (value: obj) =
        if isNull value then "" else string value

    let private nodeOf (value: obj) : SessionRecovery.RecoveryNode =
        match text (value?kind) with
        | "child" ->
            SessionRecovery.RecoveryNode.AgentChild(
                SessionId.create (text (value?parent)),
                SessionId.create (text (value?child)),
                AgentHandleId.create (text (value?handle))
            )
        | "companion" ->
            SessionRecovery.RecoveryNode.Companion(
                SessionId.create (text (value?main)),
                SessionId.create (text (value?companion))
            )
        | "managerJob" ->
            SessionRecovery.RecoveryNode.ManagerJob(
                ManagerJobId.create (text (value?job)),
                SessionId.create (text (value?manager))
            )
        | _ -> SessionRecovery.RecoveryNode.WorkSession(SessionId.create (text (value?session)))

    let token (value: obj) : string =
        SessionRecovery.RecoveryNode.token (nodeOf value)

    let validateClosure (root: string) (nodes: obj array) : obj =
        let closure: SessionRecovery.RecoveryClosure =
            { Root = SessionId.create root
              Nodes = nodes |> Array.toList |> List.map nodeOf
              Digest = "surface"
              JournalSequence = 1L }

        match SessionRecovery.validateClosurePure closure with
        | Ok valid ->
            box
                {| ok = true
                   members =
                    SessionRecovery.RecoveryClosure.members (SessionRecovery.ValidatedClosure.value valid)
                    |> Set.toArray |}
        | Error blocks ->
            let first = blocks.Head

            box
                {| ok = false
                   error =
                    match first with
                    | SessionRecovery.RecoveryBlock.RecoveryCycle _ -> "RecoveryCycle"
                    | _ -> "RecoveryBlock" |}

    let missingMembers (permitMembers: string array) (currentMembers: string array) : string array =
        let permit = Set.ofArray permitMembers
        Set.difference permit (Set.ofArray currentMembers) |> Set.toArray

    let private receipt id sequence =
        SessionRecovery.RecoveryReceipt.create (SessionId.create id) sequence None [] []

    let private outcome name =
        match name with
        | "Blocked" ->
            SessionRecovery.SessionRecovery.Blocked(
                SessionRecovery.NonEmpty.one (SessionRecovery.RecoveryBlock.MissingSession(SessionId.create "blocked"))
            )
        | "Waiting" ->
            SessionRecovery.SessionRecovery.Waiting(
                SessionRecovery.NonEmpty.one (SessionRecovery.RecoveryBlock.MissingSession(SessionId.create "waiting"))
            )
        | "Recovered" -> SessionRecovery.SessionRecovery.Recovered(receipt "recovered" 1L)
        | "NoRecoveryRequired" -> SessionRecovery.SessionRecovery.NoRecoveryRequired(receipt "none" 1L)
        | _ -> SessionRecovery.SessionRecovery.NoRecoveryRequired(receipt "none" 1L)

    let private outcomeName value =
        match value with
        | SessionRecovery.SessionRecovery.Blocked _ -> "Blocked"
        | SessionRecovery.SessionRecovery.Waiting _ -> "Waiting"
        | SessionRecovery.SessionRecovery.Recovered _ -> "Recovered"
        | SessionRecovery.SessionRecovery.NoRecoveryRequired _ -> "NoRecoveryRequired"

    let combine (names: string array) : string =
        names
        |> Array.toList
        |> List.map outcome
        |> SessionRecovery.combine
        |> outcomeName

    let handleFamily (branch: string) : obj =
        let family =
            match branch with
            | "recovered" ->
                SessionRecovery.HandleFamilyRecovery.HandlesRecovered(
                    SessionRecovery.NonEmpty.one
                        { Handle = AgentHandleId.create "h1"
                          ChildSession = SessionId.create "c1"
                          Kind = "terminal" }
                )
            | "waiting" ->
                SessionRecovery.HandleFamilyRecovery.HandlesWaiting(
                    SessionRecovery.NonEmpty.one
                        { Handle = AgentHandleId.create "h2"
                          ChildSession = SessionId.create "c2"
                          Reason = "still running" }
                )
            | "blocked" ->
                SessionRecovery.HandleFamilyRecovery.HandlesBlocked(
                    SessionRecovery.NonEmpty.one
                        { Handle = AgentHandleId.create "h3"
                          ChildSession = SessionId.create "c3"
                          Reason = "linkage conflict" }
                )
            | _ -> SessionRecovery.HandleFamilyRecovery.NoLinkedHandles

        let mapped =
            SessionRecovery.sessionRecoveryOfHandleFamily (SessionId.create "s1") 1L family

        box
            {| state = outcomeName mapped
               restoredHandles =
                match mapped with
                | SessionRecovery.SessionRecovery.Recovered receipt ->
                    SessionRecovery.RecoveryReceipt.restoredHandles receipt
                    |> List.map AgentHandleId.value
                    |> List.toArray
                | _ -> [||]
               reason =
                match mapped with
                | SessionRecovery.SessionRecovery.Waiting blocks
                | SessionRecovery.SessionRecovery.Blocked blocks ->
                    match blocks.Head with
                    | SessionRecovery.RecoveryBlock.ChildRecoveryFailed(_, reason) -> reason
                    | _ -> ""
                | _ -> "" |}

    let jobFamily (branch: string) : obj =
        let family =
            match branch with
            | "recovered" ->
                SessionRecovery.JobFamilyRecovery.JobsRecovered(SessionRecovery.NonEmpty.one (ManagerJobId.create "j1"))
            | "waiting" -> SessionRecovery.JobFamilyRecovery.JobRecoveryUnknown(ManagerJobId.create "j2", "no evidence")
            | "blocked" ->
                SessionRecovery.JobFamilyRecovery.JobsBlocked(
                    SessionRecovery.NonEmpty.one (SessionRecovery.RecoveryBlock.MissingSession(SessionId.create "c9"))
                )
            | _ -> SessionRecovery.JobFamilyRecovery.NoRelatedJobs

        let mapped =
            SessionRecovery.sessionRecoveryOfJobFamily (SessionId.create "s1") 1L family

        box {| state = outcomeName mapped |}

    let authorize (root: string) (sequence: int) (results: obj array) : obj =
        let typed =
            results
            |> Array.toList
            |> List.map (fun value ->
                let id = SessionId.create (text (value?session))
                let result = outcome (text (value?state))
                id, result)
            |> Map.ofList

        let closure: SessionRecovery.RecoveryClosure =
            { Root = SessionId.create root
              Nodes = []
              Digest = "surface"
              JournalSequence = int64 sequence }

        let recovered: SessionRecovery.RecoveredClosure =
            { Closure = closure; Results = typed }

        let result =
            SessionRecovery.authorizeFamilyResume (SessionId.create root) (int64 sequence) recovered

        match result with
        | SessionRecovery.FamilyRecovery.FamilyReady permit ->
            box
                {| state = "FamilyReady"
                   root = SessionId.value (SessionRecovery.FamilyRecoveryPermit.root permit)
                   sequence = int (SessionRecovery.FamilyRecoveryPermit.journalSequence permit)
                   members = SessionRecovery.FamilyRecoveryPermit.closureMembers permit |> Set.toArray |}
        | SessionRecovery.FamilyRecovery.FamilyWaiting _ -> box {| state = "FamilyWaiting" |}
        | SessionRecovery.FamilyRecovery.FamilyBlocked _ -> box {| state = "FamilyBlocked" |}

    let receiptView (id: string) (sequence: int) : obj =
        let value = receipt id (int64 sequence)

        box
            {| session = SessionId.value (SessionRecovery.RecoveryReceipt.sessionId value)
               sequence = int (SessionRecovery.RecoveryReceipt.journalSequence value)
               snapshotDigest = null
               resolvedClaims = [||]
               restoredHandles = [||] |}
