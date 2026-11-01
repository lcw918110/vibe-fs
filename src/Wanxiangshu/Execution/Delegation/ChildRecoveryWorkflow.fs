namespace Wanxiangshu.Execution.Delegation


open System
open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Foundation.HostSession
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

    /// delegation-029: the recovery runtime consumes a narrow capability, never
    /// the durable journal handle. The reader answers "does a delivery witness
    /// exist for this child session, provider run and Authority Root?"; the
    /// durable composition owns the concrete journal read behind it.
    type DeliveryWitnessReader = SessionId -> ProviderRunIdentity -> AuthorityRootUserMessageId -> bool

    /// The durable chat-execution acceptance of one physical user message.
    /// A retry lands as a new physical user message under the original
    /// Authority Root, so the physical id must never be promoted to a root.
    type AcceptedRoot =
        { PhysicalUserMessageId: PhysicalUserMessageId
          AuthorityRoot: AuthorityRootUserMessageId }

    /// Durable Accepted lookup by physical message. None = not observed.
    type AcceptedRootReader = SessionId -> PhysicalUserMessageId -> AcceptedRoot option

    /// DSL-state-combination: physical — optional Journal/Snapshot capabilities
    /// are injected infrastructure ports; the remaining fields are one recovery
    /// invocation's identities and observations, not a stored program counter.
    /// DSL-class: PhysicalHandle — HOST recovery invocation ports and wake handle; owner CHILD-RECOVERY, law FLOW-001, proof direct-ce-contract.
    type Ports =
        {
            Journal: AgentJournalPort option
            /// Durable delivery-witness reader; None = no witness source, so a
            /// restored terminal is not proven.
            DeliveryWitness: DeliveryWitnessReader option
            /// Durable Accepted reader; None = no acceptance source, so the
            /// restored terminal's true root cannot be observed.
            AcceptedRoot: AcceptedRootReader option
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

    /// A user message after the last assistant means the child has a newer
    /// pending turn; the older assistant is not this child's terminal.
    let private userAfterLastAssistant (messages: SessionMessage list) =
        let rec scan (messages: SessionMessage list) =
            match messages with
            | [] -> false
            | message :: rest when message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) -> false
            | message :: rest -> message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) || scan rest

        scan (List.rev messages)

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

    let private deliveryWitnessed
        (ports: Ports)
        (authorityRoot: AuthorityRootUserMessageId)
        (assistant: SessionMessage)
        : bool =
        let providerRun = ProviderRunIdentity.create assistant.Id

        ports.DeliveryWitness
        |> Option.exists (fun reader -> reader ports.ChildSession providerRun authorityRoot)

    let private acceptedRootForPhysical
        (ports: Ports)
        (physical: PhysicalUserMessageId)
        : Result<AuthorityRootUserMessageId, string> =
        match ports.AcceptedRoot with
        | None -> Error "restart terminal has no durable Accepted source"
        | Some read ->
            match read ports.ChildSession physical with
            | None -> Error "restart terminal has no durable Accepted for its exact physical message"
            | Some accepted when accepted.PhysicalUserMessageId <> physical ->
                Error "durable Accepted does not answer the assistant's exact physical message"
            | Some accepted -> Ok accepted.AuthorityRoot

    let private admittedRoot (ports: Ports) (authorityRoot: AuthorityRootUserMessageId) : Result<unit, string> =
        let work =
            { Handle = ports.Handle
              ChildSessionId = ports.ChildSession
              AuthorityRoot = authorityRoot }

        match ports.Journal with
        | None -> Error "restart terminal has no durable journal to prove its canonical work"
        | Some journal ->
            match HandleProjection.tryAdmittedWork work (journal.HandleProjection ports.ParentId) with
            | Ok _ -> Ok()
            | Error _ -> Error "durable Accepted root has no matching canonical admitted work"

    let private terminalFromMessages (ports: Ports) (assistant: SessionMessage) : ChildSnapshotEvidence =
        // The assistant's parent is the exact physical message it answers.
        // The last user message may be a retry whose physical id is not the
        // Authority Root; only the durable Accepted record knows the root.
        match assistant.ParentId with
        | None -> ChildSnapshotEvidence.Unreadable "terminal assistant has no exact physical parent message"
        | Some parentId ->
            let physical = PhysicalUserMessageId.create parentId

            match acceptedRootForPhysical ports physical with
            | Error reason -> ChildSnapshotEvidence.Unreadable reason
            | Ok authorityRoot ->
                match admittedRoot ports authorityRoot with
                | Error reason -> ChildSnapshotEvidence.Unreadable reason
                | Ok() when not (deliveryWitnessed ports authorityRoot assistant) ->
                    ChildSnapshotEvidence.Unreadable "restart terminal has no durable delivery witness"
                | Ok() ->
                    let runId = "run-restored-" + ports.AgentId
                    let view = textOfParts assistant.Parts

                    let agentOutcome =
                        AgentCompletion.completed
                            ports.AgentId
                            ports.ChildSession
                            runId
                            ports.Role
                            authorityRoot
                            (ProviderRunIdentity.create assistant.Id)
                            view
                            None

                    let body = HandleCompletionCodec.encodeOutcome runId agentOutcome

                    ChildSnapshotEvidence.Terminal(
                        TerminalEvidence.completed ports.AgentId ports.Handle ports.ChildSession body
                    )

    let private snapshotFromMessages (ports: Ports) (messages: SessionMessage list) : ChildSnapshotEvidence =
        match lastByRole messages "assistant" with
        | None -> ChildSnapshotEvidence.Active
        | Some assistant when isTerminalCompleted assistant && not (userAfterLastAssistant messages) ->
            terminalFromMessages ports assistant
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

    let private commitAdmittedTerminal journal parentId proof (durable: AgentJournalPort) (work: HandleWorkId) =
        let projection = durable.HandleProjection parentId

        match HandleProjection.tryAdmittedWork work projection, HandleProjection.tryWork work projection with
        | Ok admitted, _ -> HandleController.recordWorkCompletion journal parentId admitted proof
        | _, Some existing when
            existing.Lifecycle <> Active
            && not (Set.contains work.Handle projection.LegacyWorkHandles)
            ->
            Task.FromResult(Ok())
        | _ -> Task.FromResult(Error "historical or unmatched terminal has no scoped admission")

    let private commitDecodedTerminal journal parentId proof durable =
        match JoinableCompletion.body proof |> Option.map HandleCompletionCodec.decodeBody with
        | Some(Current(CompletedV2 payload)) ->
            let work =
                { Handle = JoinableCompletion.handle proof
                  ChildSessionId = JoinableCompletion.childSession proof
                  AuthorityRoot = AuthorityRootUserMessageId.create payload.AuthorityRoot }

            commitAdmittedTerminal journal parentId proof durable work
        | _ -> Task.FromResult(Error "recovery terminal requires its exact admitted Root; no legacy write fallback")

    /// Snapshot recovery must match an existing canonical work; it cannot reopen history.
    let commitJoinable
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (proof: JoinableCompletion)
        : Task<Result<unit, string>> =
        match journal with
        | None -> Task.FromResult(Error "completion requires an exact canonical admission")
        | Some durable -> commitDecodedTerminal journal parentId proof durable

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
