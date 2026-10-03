namespace Wanxiangshu.Execution.Delegation


open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Session
open Wanxiangshu.Participant.Persona

/// Direct-CE child recovery (FLOW-001 / P0-1).
/// Recovery consumes canonical admission and commits only exact scoped completions.
/// GREEN-5: after durable commit, Pulse agent handle only (wake); Journal is fact source.
module ChildRecoveryWorkflow =

    /// DSL-state-combination: physical — optional Journal/Snapshot capabilities
    /// are injected infrastructure ports; the remaining fields are one recovery
    /// invocation's identities and observations, not a stored program counter.
    /// DSL-class: PhysicalHandle — HOST recovery invocation ports and wake handle; owner CHILD-RECOVERY, law FLOW-001, proof direct-ce-contract.
    type Ports =
        {
            Journal: AgentJournalPort option
            ParentId: SessionId
            Snapshot: ISessionSnapshotPort option
            AgentId: string
            Handle: HandleId
            ChildSession: SessionId
            Role: Role
            Agent: string
            Observations: HostObservation list
            /// Process-local agent wake after durable commit. None = durable only.
            Pulse: (unit -> unit) option
            /// Injectable wall clock (rabbit §15 / G4R-CE S11) — no raw UtcNow.
            Clock: IClockPort
        }

    let private textOfParts (parts: MessagePart array) =
        if isNull parts then
            ""
        else
            parts
            |> Array.choose (function
                | MessagePart.Text text -> Some text
                | _ -> None)
            |> String.concat ""

    let private lastByRole (messages: SessionMessage list) (role: string) =
        messages
        |> List.rev
        |> List.tryFind (fun message -> message.Role.Equals(role, StringComparison.OrdinalIgnoreCase))

    let private isTerminalCompleted (assistant: SessionMessage) =
        match assistant.Finish with
        | Some finish when finish.Equals("stop", StringComparison.OrdinalIgnoreCase) ->
            not (String.IsNullOrWhiteSpace(textOfParts assistant.Parts))
        | _ -> false

    let private evidenceFromDecodedBody (ports: Ports) (body: string) : DurableHandleEvidence =
        match HandleCompletionCodec.decodeBody body with
        | Current decoded when
            ports.Journal
            |> Option.exists (fun journal ->
                HandleProjection.tryFind ports.Handle (journal.HandleProjection ports.ParentId)
                |> Option.exists (fun record ->
                    record.Work
                    |> Option.exists (fun work -> HandleCompletionCodec.belongsToWork work decoded)))
            ->
            let proof =
                JoinableCompletion.fromDecoded ports.AgentId ports.Handle ports.ChildSession decoded body

            DurableHandleEvidence.CompletedAwaitingJoin proof
        | Current _ -> DurableHandleEvidence.Unknown
        | LegacyFalseAbort _ -> DurableHandleEvidence.Active
        | Invalid _ -> DurableHandleEvidence.Unknown

    let private evidenceFromAwaitingJoin
        (ports: Ports)
        (journal: AgentJournalPort)
        (record: HandleRecord)
        : Task<DurableHandleEvidence> =
        task {
            match! HandleCompletionCodec.tryReadBody journal record with
            | Ok(Some body, _, _) -> return evidenceFromDecodedBody ports body
            | Ok(None, _, _) -> return DurableHandleEvidence.Active
            | Error _ -> return DurableHandleEvidence.Unknown
        }

    let private evidenceFromLifecycle
        (ports: Ports)
        (journal: AgentJournalPort)
        (record: HandleRecord)
        : Task<DurableHandleEvidence> =
        match record.Lifecycle with
        | HandleLifecycle.Active -> Task.FromResult DurableHandleEvidence.Active
        | HandleLifecycle.Retired -> Task.FromResult DurableHandleEvidence.Retired
        | HandleLifecycle.Abandoned reason -> Task.FromResult(DurableHandleEvidence.Abandoned reason)
        | HandleLifecycle.CompletedAwaitingJoin _ -> evidenceFromAwaitingJoin ports journal record

    let private readDurableFromJournal (ports: Ports) (journal: AgentJournalPort) : Task<DurableHandleEvidence> =
        let projection = journal.HandleProjection ports.ParentId

        match HandleProjection.tryFind ports.Handle projection with
        | None -> Task.FromResult DurableHandleEvidence.Unknown
        | Some record -> evidenceFromLifecycle ports journal record

    let private readDurableEvidence (ports: Ports) : Task<DurableHandleEvidence> =
        match ports.Journal with
        | None -> Task.FromResult DurableHandleEvidence.Unknown
        | Some journal -> readDurableFromJournal ports journal

    let private terminalFromMessages
        (ports: Ports)
        (messages: SessionMessage list)
        (assistant: SessionMessage)
        : ChildSnapshotEvidence =
        match lastByRole messages "user" with
        | None -> ChildSnapshotEvidence.Unreadable "host restart: terminal child has no user message"
        | Some user when assistant.ParentId <> Some user.Id ->
            ChildSnapshotEvidence.Unreadable "terminal assistant does not answer the exact accepted physical message"
        | Some user ->
            let runId = "run-restored-" + ports.AgentId
            let workRecord = textOfParts assistant.Parts

            let agentOutcome =
                AgentCompletion.completed
                    ports.AgentId
                    ports.ChildSession
                    runId
                    ports.Role
                    (PhysicalUserMessageId.promoteToAuthorityRoot (PhysicalUserMessageId.create user.Id))
                    (ProviderRunIdentity.create assistant.Id)
                    workRecord
                    None

            let body = HandleCompletionCodec.encodeOutcome runId agentOutcome

            ChildSnapshotEvidence.Terminal(
                TerminalEvidence.completed ports.AgentId ports.Handle ports.ChildSession body
            )

    let private snapshotFromMessages (ports: Ports) (messages: SessionMessage list) : ChildSnapshotEvidence =
        match lastByRole messages "assistant" with
        | None -> ChildSnapshotEvidence.Active
        | Some assistant when isTerminalCompleted assistant -> terminalFromMessages ports messages assistant
        | Some _assistant ->
            // Mid-turn / non-terminal assistant: stream readable → still running.
            ChildSnapshotEvidence.Active

    let private readSnapshotFromPort (ports: Ports) (port: ISessionSnapshotPort) : Task<ChildSnapshotEvidence> =
        task {
            match! port.GetMessages ports.ChildSession with
            | Error reason -> return ChildSnapshotEvidence.Unreadable reason
            | Ok messages -> return snapshotFromMessages ports messages
        }

    let private readSnapshotEvidence (ports: Ports) : Task<ChildSnapshotEvidence> =
        match ports.Snapshot with
        | None -> Task.FromResult ChildSnapshotEvidence.Missing
        | Some port -> readSnapshotFromPort ports port

    let private pulseAfterCommit (ports: Ports) : unit =
        match ports.Pulse with
        | Some pulse -> pulse ()
        | None -> ()

    let private commitRecoveredWork
        (journal: AgentJournalPort option)
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (proof: JoinableCompletion)
        (payload: CompletedPayload)
        : Task<Result<unit, string>> =
        let work =
            { Handle = JoinableCompletion.handle proof
              ChildSessionId = JoinableCompletion.childSession proof
              AuthorityRoot = AuthorityRootUserMessageId.create payload.AuthorityRoot }

        let projection = durable.HandleProjection parentId

        match HandleProjection.tryAdmittedWork work projection, HandleProjection.tryWork work projection with
        | Ok admitted, _ -> HandleController.recordWorkCompletion journal parentId admitted proof
        | _, Some existing when
            existing.Lifecycle <> Active
            && not (Set.contains work.Handle projection.LegacyWorkHandles)
            ->
            Task.FromResult(Ok())
        | _ -> Task.FromResult(Error "historical or unmatched terminal has no scoped admission")

    let private commitDecodedJoinable
        (journal: AgentJournalPort option)
        (durable: AgentJournalPort)
        (parentId: SessionId)
        (proof: JoinableCompletion)
        : Task<Result<unit, string>> =
        match JoinableCompletion.body proof |> Option.map HandleCompletionCodec.decodeBody with
        | Some(Current(CompletedV2 payload)) -> commitRecoveredWork journal durable parentId proof payload
        | _ -> Task.FromResult(Error "recovery terminal requires its exact admitted Root; no legacy write fallback")

    /// Snapshot recovery must match an existing canonical work; it cannot reopen history.
    let commitJoinable
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (proof: JoinableCompletion)
        : Task<Result<unit, string>> =
        match journal with
        | None -> Task.FromResult(Error "completion requires an exact canonical admission")
        | Some durable -> commitDecodedJoinable journal durable parentId proof

    let private commitAbandon
        (ports: Ports)
        (handle: HandleId)
        (reason: HandleAbandonReason)
        : Task<Result<unit, string>> =
        let agentId =
            match HandleId.tryAgent handle with
            | Some id -> AgentHandleId.value id
            | None -> ports.AgentId

        HandleController.recordAbandon ports.Journal ports.ParentId agentId reason (ports.Clock.UtcNow())

    /// Resolve one child and commit through the single write entry.
    /// GREEN-4: ChildRecoveryResult (RecoveredActive ≠ RecoveryIncomplete).
    let resolveAndCommit (ports: Ports) : Task<Result<ChildRecoveryResult, string>> =
        taskResult {
            let! durable = readDurableEvidence ports |> TaskResultCE.ofTask
            let! snapshot = readSnapshotEvidence ports |> TaskResultCE.ofTask
            let resolution = resolveChild durable snapshot ports.Observations

            match resolution with
            | ChildResolution.RecoveredTerminal proof ->
                do! commitJoinable ports.Journal ports.ParentId proof
                pulseAfterCommit ports
                return ChildRecoveryResult.RecoveredTerminal proof
            | ChildResolution.RecoveredAbandoned reason ->
                do! commitAbandon ports ports.Handle reason

                return
                    ChildRecoveryResult.RecoveredAbandoned
                        { Handle = ports.Handle
                          Reason = reason }
            | ChildResolution.RecoveredActive ->
                return
                    ChildRecoveryResult.RecoveredActive
                        { Handle = ports.Handle
                          ChildSession = ports.ChildSession }
            | ChildResolution.RecoveryIncomplete ->
                return
                    ChildRecoveryResult.RecoveryIncomplete(
                        RecoveryDependency.AwaitingTerminalEvidence(ports.Handle, ports.ChildSession)
                    )
            | ChildResolution.RecoveryBlocked reason ->
                return ChildRecoveryResult.RecoveryBlocked(NonEmpty.one (ChildRecoveryBlock.Reason reason))
        }
