namespace Wanxiangshu.Persistence.Journal

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// Journal operations for the relay lifecycle, plus the retired obligation-ledger
/// read boundary.
///
/// obligation-ledger-007: the append is gone, so the only honest answer refuses and
/// names the retirement. The relay lifecycle functions stay because real callers use
/// them; they are relay code that historically sat beside the old ledger route.
[<RequireQualifiedAccess>]
module ObligationJournalSurface =

    let private streamOfSession (sessionId: string) =
        StreamId.Session(SessionId.create sessionId)

    let private appendResult result =
        match result with
        | Ok _ -> box {| ok = true |}
        | Error failure ->
            box
                {| ok = false
                   error = JournalAppendFailure.describe failure |}

    /// Retired. A refusal, so a migrated caller learns why instead of inferring it.
    let appendMagicTodo (handle: JournalHandle) : obj =
        ignore handle

        box
            {| ok = false
               error =
                "appendMagicTodo is retired (obligation-ledger-007); Host-native todowrite owns the list and successful calls append TodoCheckpointCommitted only for context compression" |}

    /// Retired alongside the projection it would have returned.
    let snapshotMagicTodo (handle: JournalHandle) : obj =
        ignore handle
        null

    let openIncumbency (handle: JournalHandle) (sessionId: string) (incumbencyId: string) : Task<obj> =
        task {
            let roadId = Wanxiangshu.Mission.Relay.RoadId.create sessionId
            let incId = Wanxiangshu.Mission.Relay.IncumbencyId.create incumbencyId
            let snapId = Wanxiangshu.Mission.Relay.WorkspaceSnapshotId.create "snapshot-root"
            let authRev = Wanxiangshu.Mission.Relay.AuthorityRevision.create "rev-1"
            let physUser = Wanxiangshu.Mission.Relay.PhysicalUserMessageId.create "user-root"

            let events =
                [ Wanxiangshu.Mission.Relay.RelayEvent.RoadOpened(roadId, authRev, physUser)
                  Wanxiangshu.Mission.Relay.RelayEvent.IncumbencyOpened(incId, snapId) ]

            match Wanxiangshu.Mission.Relay.RelayTransaction.create events with
            | Error err -> return box {| ok = false; error = err |}
            | Ok tx ->
                let fact =
                    AgentFact.Relay(
                        Wanxiangshu.Mission.Relay.RelayFactCases.TransactionCommitted
                            {| RoadId = roadId; Transaction = tx |}
                    )

                let! result = AgentJournal.appendAgent (streamOfSession sessionId) None fact handle.Journal

                return appendResult result
        }

    /// Idempotent accepted-assessment commit on a session that may already hold an
    /// open road: an already accepted assessment is left untouched, an open road
    /// contributes its active incumbency/snapshot/authority revision to the
    /// assessment, and a road that is not open yet is opened first.
    let grantWorkOwned (handle: JournalHandle) (sessionId: string) (incumbencyId: string) : Task<obj> =
        task {
            let roadId = Wanxiangshu.Mission.Relay.RoadId.create sessionId
            let incId = Wanxiangshu.Mission.Relay.IncumbencyId.create incumbencyId
            let snapId = Wanxiangshu.Mission.Relay.WorkspaceSnapshotId.create "snapshot-root"
            let authRev = Wanxiangshu.Mission.Relay.AuthorityRevision.create "rev-1"
            let physUser = Wanxiangshu.Mission.Relay.PhysicalUserMessageId.create "user-root"
            let assessId = Wanxiangshu.Mission.Relay.AssessmentId.create ("assess-" + sessionId)

            let existingRoad =
                AgentProjection.tryFind
                    (SessionId.create sessionId)
                    (AgentJournal.snapshot handle.Journal).AgentProjections
                |> Option.bind (fun session -> session.Relay)
                |> Option.bind (fun relay -> Wanxiangshu.Mission.Relay.Fold.view relay roadId)

            let accepted =
                existingRoad
                |> Option.bind (fun road -> road.AcceptedAssessmentTransport)
                |> Option.isSome

            let binding: Wanxiangshu.Mission.Relay.AssessmentBinding =
                { PhysicalUserMessageId = "user-root"
                  ProviderRunId = "run-test"
                  ToolCallId = "tool-test"
                  NarrativeDigest = "digest-narrative"
                  PayloadDigest = "digest-payload"
                  RootRequestDigest = "digest-root"
                  RequirementSetDigest = "digest-req"
                  EvidenceFrontierDigest = "digest-evidence" }

            let findings =
                Wanxiangshu.Mission.Relay.AssessmentFindings.tryCreate
                    [ { Wanxiangshu.Mission.Relay.AssessmentFinding.AcceptanceCriteria =
                          "the delivery reaches the requested target state"
                        Wanxiangshu.Mission.Relay.AssessmentFinding.WorkPlan =
                          "close the remaining gap before the next review" } ]
                |> Result.defaultWith (fun _ -> failwith "findings")

            let assessment incumbent snapshot revision =
                Wanxiangshu.Mission.Relay.RelayEvent.AssessmentCommitted(
                    assessId,
                    incumbent,
                    binding,
                    snapshot,
                    revision,
                    findings
                )

            let pending =
                if accepted then
                    []
                else
                    match existingRoad with
                    | None ->
                        [ Wanxiangshu.Mission.Relay.RelayEvent.RoadOpened(roadId, authRev, physUser)
                          Wanxiangshu.Mission.Relay.RelayEvent.IncumbencyOpened(incId, snapId)
                          assessment incId snapId authRev ]
                    | Some road ->
                        match road.ActiveIncumbency, road.ActiveSnapshotId, road.ActiveAuthorityRevision with
                        | Some activeIncumbency, Some activeSnapshot, Some activeRevision ->
                            [ assessment activeIncumbency activeSnapshot activeRevision ]
                        | _ ->
                            let currentRevision =
                                if road.AuthorityRevisions.IsEmpty then
                                    authRev
                                else
                                    road.AuthorityRevision

                            let roadOpened =
                                if road.AuthorityRevisions.IsEmpty then
                                    [ Wanxiangshu.Mission.Relay.RelayEvent.RoadOpened(roadId, currentRevision, physUser) ]
                                else
                                    []

                            roadOpened
                            @ [ Wanxiangshu.Mission.Relay.RelayEvent.IncumbencyOpened(incId, snapId)
                                assessment incId snapId currentRevision ]

            match pending with
            | [] -> return box {| ok = true |}
            | events ->
                match Wanxiangshu.Mission.Relay.RelayTransaction.create events with
                | Error err -> return box {| ok = false; error = err |}
                | Ok tx ->
                    let fact =
                        AgentFact.Relay(
                            Wanxiangshu.Mission.Relay.RelayFactCases.TransactionCommitted
                                {| RoadId = roadId; Transaction = tx |}
                        )

                    let! result = AgentJournal.appendAgent (streamOfSession sessionId) None fact handle.Journal

                    return appendResult result
        }

    let appendManagerLifecycle (handle: JournalHandle) (sessionId: string) (action: string) (payload: obj) : Task<obj> =
        match action with
        | "LifeOpened" -> openIncumbency handle sessionId sessionId
        | "LifeCompleted" ->
            task {
                let roadId = Wanxiangshu.Mission.Relay.RoadId.create sessionId
                let incId = Wanxiangshu.Mission.Relay.IncumbencyId.create sessionId
                let snapId = Wanxiangshu.Mission.Relay.WorkspaceSnapshotId.create "snapshot-root"
                let authRev = Wanxiangshu.Mission.Relay.AuthorityRevision.create "rev-1"

                let assessId =
                    Wanxiangshu.Mission.Relay.AssessmentId.create ("assess-terminal-" + sessionId)

                let retId = Wanxiangshu.Mission.Relay.RetirementId.create ("ret-" + sessionId)

                let binding: Wanxiangshu.Mission.Relay.AssessmentBinding =
                    { PhysicalUserMessageId = "user-root"
                      ProviderRunId = "run-terminal"
                      ToolCallId = "tool-terminal"
                      NarrativeDigest = "digest-narrative"
                      PayloadDigest = "digest-payload"
                      RootRequestDigest = "digest-root"
                      RequirementSetDigest = "digest-req"
                      EvidenceFrontierDigest = "digest-evidence" }

                let findings =
                    Wanxiangshu.Mission.Relay.AssessmentFindings.tryCreate []
                    |> Result.defaultWith (fun _ -> failwith "findings")

                let certificateId =
                    Wanxiangshu.Mission.Relay.QualityCertificateId.create (
                        "certificate:" + Wanxiangshu.Mission.Relay.AssessmentId.value assessId
                    )

                let cut: Wanxiangshu.Mission.Relay.ProjectionCut =
                    { ProviderRunId = "run-terminal"
                      ToolCallId = "tool-terminal" }

                let summary: Wanxiangshu.Mission.Relay.RetirementSummary =
                    { Id = retId
                      IncumbencyId = incId
                      SnapshotId = snapId
                      AuthorityRevision = authRev
                      ProjectionCut = cut
                      Outcome = Wanxiangshu.Mission.Relay.RetirementOutcome.Accepted certificateId }

                let events =
                    [ Wanxiangshu.Mission.Relay.RelayEvent.AssessmentCommitted(
                          assessId,
                          incId,
                          binding,
                          snapId,
                          authRev,
                          findings
                      )
                      Wanxiangshu.Mission.Relay.RelayEvent.RetirementCommitted summary ]

                match Wanxiangshu.Mission.Relay.RelayTransaction.create events with
                | Error err -> return box {| ok = false; error = err |}
                | Ok tx ->
                    let fact =
                        AgentFact.Relay(
                            Wanxiangshu.Mission.Relay.RelayFactCases.TransactionCommitted
                                {| RoadId = roadId; Transaction = tx |}
                        )

                    let! result = AgentJournal.appendAgent (streamOfSession sessionId) None fact handle.Journal

                    // concern-routing-006: a completed owner life retires its
                    // mailboxes before any later publish. The retirement fact
                    // replays idempotently, so an append failure rejects the
                    // lifecycle call and the caller retries instead of silently
                    // leaving a live mailbox for a terminated participant.
                    match result with
                    | Error _ -> return appendResult result
                    | Ok _ ->
                        let! retired =
                            AttentionConcernJournalAdapter.retireMailboxesOf
                                handle.Journal
                                (SessionId.create sessionId)
                                None

                        match retired with
                        | Ok() -> return box {| ok = true |}
                        | Error reason -> return box {| ok = false; error = reason |}
            }
        | _ -> Task.FromResult(box {| ok = true |})
