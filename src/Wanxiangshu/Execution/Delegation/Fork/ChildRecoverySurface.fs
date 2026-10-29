namespace Wanxiangshu.Execution.Delegation.Fork

open Fable.Core.JsInterop
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation.Identity

/// Child-recovery owner surface. Durable/snapshot observations are strings and
/// resolution names; terminal proofs and trace events remain typed internally.
[<RequireQualifiedAccess>]
module ChildRecoverySurface =
    let private text (value: obj) =
        if isNull value then "" else string value

    let private durableOf name handle child : ChildRecovery.DurableHandleEvidence =
        match name with
        | "completed" ->
            let evidence =
                ChildRecovery.TerminalEvidence.completed
                    "agent"
                    (HandleId.Agent(AgentHandleId.create handle))
                    (SessionId.create child)
                    "terminal"

            match ChildRecovery.JoinableCompletion.tryFromProvenTerminal evidence with
            | Ok proof -> ChildRecovery.DurableHandleEvidence.CompletedAwaitingJoin proof
            | Error _ -> ChildRecovery.DurableHandleEvidence.Active
        | "abandoned" -> ChildRecovery.DurableHandleEvidence.Abandoned HandleAbandonReason.ParentCancelled
        | "retired" -> ChildRecovery.DurableHandleEvidence.Retired
        | _ -> ChildRecovery.DurableHandleEvidence.Active

    let private snapshotOf name handle child body : ChildRecovery.ChildSnapshotEvidence =
        match name with
        | "terminal" ->
            ChildRecovery.ChildSnapshotEvidence.Terminal(
                ChildRecovery.TerminalEvidence.completed
                    "agent"
                    (HandleId.Agent(AgentHandleId.create handle))
                    (SessionId.create child)
                    body
            )
        | "unreadable" -> ChildRecovery.ChildSnapshotEvidence.Unreadable "unreadable"
        | "active" -> ChildRecovery.ChildSnapshotEvidence.Active
        | _ -> ChildRecovery.ChildSnapshotEvidence.Missing

    let private observationsOf (values: obj array) =
        values
        |> Array.toList
        |> List.choose (fun value ->
            match text value with
            | "parent-cancelled" -> Some ChildRecovery.HostObservation.ParentCancelled
            | "deadline" -> Some ChildRecovery.HostObservation.DeadlineExceeded
            | "gone" -> Some ChildRecovery.HostObservation.HostSessionGone
            | "active" -> Some ChildRecovery.HostObservation.SessionActive
            | "restore" -> Some ChildRecovery.HostObservation.RecoveryInFlight
            
                
            | _ -> None)

    let resolve (durable: string) (snapshot: string) (observations: obj array) (body: string) : obj =
        let result =
            ChildRecovery.resolveChild
                (durableOf durable "h1" "child")
                (snapshotOf snapshot "h1" "child" body)
                (observationsOf observations)

        let name, reason =
            match result with
            | ChildRecovery.ChildResolution.RecoveredTerminal _ -> "RecoveredTerminal", ""
            | ChildRecovery.ChildResolution.RecoveredAbandoned _ -> "RecoveredAbandoned", ""
            | ChildRecovery.ChildResolution.RecoveredActive -> "RecoveredActive", ""
            | ChildRecovery.ChildResolution.RecoveryIncomplete -> "RecoveryIncomplete", ""
            | ChildRecovery.ChildResolution.RecoveryBlocked reason -> "RecoveryBlocked", reason

        box {| result = name; reason = reason |}

    let provenTerminal (body: string) : obj =
        let evidence =
            ChildRecovery.TerminalEvidence.completed
                "agent"
                (HandleId.Agent(AgentHandleId.create "h1"))
                (SessionId.create "child")
                body

        match ChildRecovery.JoinableCompletion.tryFromProvenTerminal evidence with
        | Ok proof ->
            box
                {| ok = true
                   finality = "Succeeded"
                   body = ChildRecovery.JoinableCompletion.body proof |}
        | Error error -> box {| ok = false; error = error |}

    let trace (events: obj array) : bool =
        let typed =
            events
            |> Array.toList
            |> List.map (fun value ->
                match text (value?kind) with
                | "RawAbortObserved" ->
                    ChildRecovery.JoinRecoveryTrace.RawAbortObserved(SessionId.create (text (value?session)))
                | "ChildRecoveryStarted" ->
                    ChildRecovery.JoinRecoveryTrace.ChildRecoveryStarted(SessionId.create (text (value?session)))
                | "TerminalProofIssued" -> ChildRecovery.JoinRecoveryTrace.TerminalProofIssued(text (value?agent))
                | "HandleCompletionCommitted" ->
                    ChildRecovery.JoinRecoveryTrace.HandleCompletionCommitted(text (value?agent))
                | _ ->
                    ChildRecovery.JoinRecoveryTrace.JoinReturned(
                        text (value?agent),
                        ChildRecovery.ChildFinality.Succeeded "body"
                    ))

        ChildRecovery.joinReturnedImpliesProofBeforeCommit typed
