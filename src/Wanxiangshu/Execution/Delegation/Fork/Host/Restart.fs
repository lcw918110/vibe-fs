namespace Wanxiangshu.Execution.Delegation.Fork.Host

open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Session.Recovery.SessionRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Process

/// Restart recovery for linked children. Terminal path: ChildRecoveryWorkflow
/// → ChildRecoveryResult → recordCompletion → PulseAgentHandle. Fail closed on proof.
/// Clean-break: legacy abort blobs never publish; retired false terminals refuse (no replacement).
/// GREEN-4: returns HandleFamilyRecovery (query result), never option/missing-port.
/// EXEC-024: agent mailbox is wake-only (PulseAgentHandle); no agent completion payload path.
module HostForkRestart =

    let private ports
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournalPort option)
        (parentId: SessionId)
        (agentId: string)
        (childSessionId: SessionId)
        (role: Role)
        (agent: string)
        : ChildRecoveryWorkflow.Ports =
        { Journal = journal
          ParentId = parentId
          Snapshot = snapshot
          AgentId = agentId
          Handle = HandleController.agentHandle agentId
          ChildSession = childSessionId
          Role = role
          Agent = agent
          // After Restore+Bind the child is live in this process. SessionActive makes
          // resolveChild → RecoveredActive (recovery work done; child continues).
          // Do not inject RecoveryInFlight — that forced RecoveryIncomplete and blocked permit.
          Observations = [ HostObservation.SessionActive ]
          Pulse = Some(fun () -> runtime.PulseAgentHandle(AgentHandleId.create agentId))
          Clock = NodeTiming.nodeClockPort () }

    let private blockReasons
        (blocks: Wanxiangshu.Execution.Delegation.Fork.ChildRecovery.NonEmpty<ChildRecoveryBlock>)
        =
        Wanxiangshu.Execution.Delegation.Fork.ChildRecovery.NonEmpty.toList blocks
        |> List.map (function
            | ChildRecoveryBlock.Reason r -> r
            | ChildRecoveryBlock.SnapshotUnreadable(_, r) -> r)
        |> String.concat "; "

    let private recoveryDependencyReason (dep: RecoveryDependency) =
        match dep with
        | RecoveryDependency.AwaitingTerminalEvidence _ -> "awaiting terminal evidence"
        | RecoveryDependency.HostRestoreInFlight _ -> "host restore in flight"

    let private applyOkRecovery (runtime: ForkRuntime) (agentId: string) (result: ChildRecoveryResult) =
        match result with
        | ChildRecoveryResult.RecoveredTerminal _
        | ChildRecoveryResult.RecoveredAbandoned _
        | ChildRecoveryResult.RecoveredActive _ -> result
        | ChildRecoveryResult.RecoveryIncomplete _ ->
            runtime.MarkInterrupted(agentId, "host restart: awaiting terminal evidence")
            result
        | ChildRecoveryResult.RecoveryBlocked blocks ->
            runtime.MarkInterrupted(agentId, sprintf "host restart: %s" (blockReasons blocks))
            result

    let private applyResolvedRecovery
        (runtime: ForkRuntime)
        (agentId: string)
        (resolved: Result<ChildRecoveryResult, string>)
        =
        match resolved with
        | Ok result -> applyOkRecovery runtime agentId result
        | Error reason ->
            runtime.MarkInterrupted(agentId, sprintf "host restart: %s" reason)

            ChildRecoveryResult.RecoveryBlocked(
                Wanxiangshu.Execution.Delegation.Fork.ChildRecovery.NonEmpty.one (ChildRecoveryBlock.Reason reason)
            )

    /// Active handle: Domain recoverChild via production interpreter.
    let recoverChild
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal option)
        (parentId: SessionId)
        (agentId: string)
        (childSessionId: SessionId)
        (role: Role)
        (agent: string)
        : Task<ChildRecoveryResult> =
        task {
            let journalPort = journal |> Option.map AgentJournalPortAdapter.fromAgentJournal
            runtime.Restore(agentId, role, agent)
            runtime.BindChildSession(agentId, childSessionId)

            let p =
                ports runtime snapshot journalPort parentId agentId childSessionId role agent

            let! resolved = ChildRecoveryWorkflow.resolveAndCommit p
            return applyResolvedRecovery runtime agentId resolved
        }

    let private recoveredHandle (agentHandle: AgentHandleId) (child: SessionId) (kind: string) : RecoveredHandle =
        { Handle = agentHandle
          ChildSession = child
          Kind = kind }

    let private fromChildResult
        (agentHandle: AgentHandleId)
        (child: SessionId)
        (result: ChildRecoveryResult)
        : Choice<RecoveredHandle, HandleRecoveryWait, HandleRecoveryBlock> =
        match result with
        | ChildRecoveryResult.RecoveredTerminal _ -> Choice1Of3(recoveredHandle agentHandle child "terminal")
        | ChildRecoveryResult.RecoveredAbandoned _ -> Choice1Of3(recoveredHandle agentHandle child "abandoned")
        | ChildRecoveryResult.RecoveredActive _ -> Choice1Of3(recoveredHandle agentHandle child "active")
        | ChildRecoveryResult.RecoveryIncomplete dep ->
            Choice2Of3
                { Handle = agentHandle
                  ChildSession = child
                  Reason = recoveryDependencyReason dep }
        | ChildRecoveryResult.RecoveryBlocked blocks ->
            Choice3Of3
                { Handle = agentHandle
                  ChildSession = child
                  Reason = blockReasons blocks }

    let private accumulateChoice
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (choice: Choice<RecoveredHandle, HandleRecoveryWait, HandleRecoveryBlock>)
        =
        match choice with
        | Choice1Of3 h -> recovered.Add h
        | Choice2Of3 w -> waiting.Add w
        | Choice3Of3 b -> blocked.Add b

    let private addBlocked
        (runtime: ForkRuntime)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (child: SessionId)
        (reason: string)
        =
        runtime.MarkInterrupted(agentId, sprintf "host restart: %s" reason)

        blocked.Add
            { Handle = agentHandle
              ChildSession = child
              Reason = reason }

    let private addWaiting
        (runtime: ForkRuntime)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (child: SessionId)
        (message: string)
        (reason: string)
        =
        runtime.MarkInterrupted(agentId, message)

        waiting.Add
            { Handle = agentHandle
              ChildSession = child
              Reason = reason }

    let private bindChildIntoRuntime
        (runtime: ForkRuntime)
        (children: Dictionary<string, SessionId>)
        (childCreatedDir: string -> SessionId -> string option -> unit)
        (directoryOf: string -> string option)
        (agentId: string)
        (record: HandleRecord)
        (role: Role)
        =
        children.[agentId] <- record.ChildSessionId
        childCreatedDir agentId record.ChildSessionId (directoryOf agentId)
        runtime.Restore(agentId, role, record.TargetAgent)
        runtime.BindChildSession(agentId, record.ChildSessionId)

    let private publishCurrentCompletion
        (runtime: ForkRuntime)
        (recovered: ResizeArray<RecoveredHandle>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        (body: string)
        (decoded: DurableAgentCompletionV2)
        =
        ignore (JoinableCompletion.fromDecoded agentId record.Handle record.ChildSessionId decoded body)

        // GREEN-5: wake only; JoinDrain re-reads Journal for payload.
        runtime.PulseAgentHandle agentHandle
        recovered.Add(recoveredHandle agentHandle record.ChildSessionId "terminal")

    let private rejectLegacyFalseAbort
        (runtime: ForkRuntime)
        (journal: AgentJournal)
        (parentId: SessionId)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        =
        task {
            match!
                AgentJournal.appendAgent
                    (StreamId.Session parentId)
                    None
                    (ExecutionFact.HandleFalseCompletionRejected
                        {| ParentSessionId = parentId
                           Handle = record.Handle
                           ExpectedCompletionRef = blobRef
                           ExpectedCompletionDigest = blobDigest
                           Reason = FalseCompletionReason.LegacyAbortWasObservation |})
                    journal
            with
            | Ok _ ->
                addWaiting
                    runtime
                    waiting
                    agentId
                    agentHandle
                    record.ChildSessionId
                    "host restart: legacy false abort rejected"
                    "legacy false abort rejected"
            | Error failure ->
                addBlocked
                    runtime
                    blocked
                    agentId
                    agentHandle
                    record.ChildSessionId
                    (JournalAppendFailure.describe failure)
        }

    let private restoreDecodedCompletion
        (runtime: ForkRuntime)
        (journal: AgentJournal)
        (parentId: SessionId)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        (body: string)
        (blobRef: BlobRef)
        (blobDigest: BlobDigest)
        =
        match HandleCompletionCodec.decodeBody body with
        | Current decoded when
            record.Work
            |> Option.forall (fun work -> HandleCompletionCodec.belongsToWork work decoded)
            ->
            publishCurrentCompletion runtime recovered agentId agentHandle record body decoded
            task { return () }
        | Current _ ->
            addBlocked
                runtime
                blocked
                agentId
                agentHandle
                record.ChildSessionId
                "completion does not belong to the exact child work"

            Task.FromResult(())
        | LegacyFalseAbort _ ->
            rejectLegacyFalseAbort
                runtime
                journal
                parentId
                waiting
                blocked
                agentId
                agentHandle
                record
                blobRef
                blobDigest
        | Invalid _ ->
            // EXEC-022: Invalid blob = wait (not hard block). Align JoinDrain
            // (Invalid → None / no consume) and ChildRecovery Incomplete.
            addWaiting
                runtime
                waiting
                agentId
                agentHandle
                record.ChildSessionId
                "host restart: invalid completion blob; waiting"
                "invalid completion blob"

            task { return () }

    let private restoreFromRecoverChild
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        (role: Role)
        =
        task {
            let! result =
                recoverChild
                    runtime
                    snapshot
                    (Some journal)
                    parentId
                    agentId
                    record.ChildSessionId
                    role
                    record.TargetAgent

            accumulateChoice recovered waiting blocked (fromChildResult agentHandle record.ChildSessionId result)
        }

    let private restoreCompletedBody
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentId: string)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        (role: Role)
        (read: Result<string option * BlobRef option * BlobDigest option, string>)
        =
        match read with
        | Ok(Some body, Some blobRef, Some blobDigest) ->
            restoreDecodedCompletion
                runtime
                journal
                parentId
                recovered
                waiting
                blocked
                agentId
                agentHandle
                record
                body
                blobRef
                blobDigest
        | Ok(Some _, _, _) ->
            addBlocked
                runtime
                blocked
                agentId
                agentHandle
                record.ChildSessionId
                "completion blob ref/digest pair is incomplete"

            task { return () }
        | Ok(None, _, _) ->
            restoreFromRecoverChild
                runtime
                snapshot
                journal
                parentId
                recovered
                waiting
                blocked
                agentId
                agentHandle
                record
                role
        | Error reason ->
            addBlocked runtime blocked agentId agentHandle record.ChildSessionId reason
            task { return () }

    let private restoreCompletedAwaitingJoin
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (children: Dictionary<string, SessionId>)
        (childCreatedDir: string -> SessionId -> string option -> unit)
        (directoryOf: string -> string option)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        =
        task {
            let agentId = AgentHandleId.value agentHandle
            let role = record.CanonicalRole

            bindChildIntoRuntime runtime children childCreatedDir directoryOf agentId record role

            let journalPort = AgentJournalPortAdapter.fromAgentJournal journal
            let! read = HandleCompletionCodec.tryReadBody journalPort record

            do!
                restoreCompletedBody
                    runtime
                    snapshot
                    journal
                    parentId
                    recovered
                    waiting
                    blocked
                    agentId
                    agentHandle
                    record
                    role
                    read
        }

    let private restoreActiveHandle
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (children: Dictionary<string, SessionId>)
        (childCreatedDir: string -> SessionId -> string option -> unit)
        (directoryOf: string -> string option)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (agentHandle: AgentHandleId)
        (record: HandleRecord)
        =
        let agentId = AgentHandleId.value agentHandle
        let role = record.CanonicalRole

        children.[agentId] <- record.ChildSessionId
        childCreatedDir agentId record.ChildSessionId (directoryOf agentId)

        restoreFromRecoverChild
            runtime
            snapshot
            journal
            parentId
            recovered
            waiting
            blocked
            agentId
            agentHandle
            record
            role

    let private restoreOneRecord
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (children: Dictionary<string, SessionId>)
        (childCreatedDir: string -> SessionId -> string option -> unit)
        (directoryOf: string -> string option)
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        (record: HandleRecord)
        =
        match record.Lifecycle, HandleId.tryAgent record.Handle with
        | HandleLifecycle.Abandoned _, Some agentHandle ->
            recovered.Add(recoveredHandle agentHandle record.ChildSessionId "abandoned")
            task { return () }
        | HandleLifecycle.Abandoned _, None
        | _, None -> task { return () }
        | HandleLifecycle.Retired, Some agentHandle ->
            // Retired handles are terminal tombstones — no migration, no replacement.
            // JoinDrain.reconcileFalseAborts handles fail-closed refuse for any
            // retired handle whose LastCompletion was a LegacyFalseAbort.
            recovered.Add(recoveredHandle agentHandle record.ChildSessionId "retired")
            task { return () }
        | HandleLifecycle.CompletedAwaitingJoin _, Some agentHandle ->
            restoreCompletedAwaitingJoin
                runtime
                snapshot
                journal
                parentId
                children
                childCreatedDir
                directoryOf
                recovered
                waiting
                blocked
                agentHandle
                record
        | HandleLifecycle.Active, Some agentHandle ->
            restoreActiveHandle
                runtime
                snapshot
                journal
                parentId
                children
                childCreatedDir
                directoryOf
                recovered
                waiting
                blocked
                agentHandle
                record

    let private familyFromNonEmpty
        (someCase: Wanxiangshu.Execution.Session.Recovery.SessionRecovery.NonEmpty<'a> -> HandleFamilyRecovery)
        (items: ResizeArray<'a>)
        =
        match Wanxiangshu.Execution.Session.Recovery.SessionRecovery.NonEmpty.ofList (List.ofSeq items) with
        | Some ne -> someCase ne
        | None -> HandleFamilyRecovery.NoLinkedHandles

    let private concludeFamilyRecovery
        (recovered: ResizeArray<RecoveredHandle>)
        (waiting: ResizeArray<HandleRecoveryWait>)
        (blocked: ResizeArray<HandleRecoveryBlock>)
        =
        if recovered.Count = 0 && waiting.Count = 0 && blocked.Count = 0 then
            HandleFamilyRecovery.NoLinkedHandles
        elif blocked.Count > 0 then
            familyFromNonEmpty HandleFamilyRecovery.HandlesBlocked blocked
        elif waiting.Count > 0 then
            familyFromNonEmpty HandleFamilyRecovery.HandlesWaiting waiting
        else
            familyFromNonEmpty HandleFamilyRecovery.HandlesRecovered recovered

    /// EXEC-009 restart recovery: rebuild parent join mailbox from durable handles.
    /// Returns HandleFamilyRecovery for SessionRecovery RestoreHandles (GREEN-4).
    let restoreLinkedChildren
        (runtime: ForkRuntime)
        (snapshot: ISessionSnapshotPort option)
        (journal: AgentJournal)
        (parentId: SessionId)
        (children: Dictionary<string, SessionId>)
        (childCreatedDir: string -> SessionId -> string option -> unit)
        (directoryOf: string -> string option)
        : Task<HandleFamilyRecovery> =
        task {
            // GLORY-002 / SURFACE-006: HostOwnedHidden handles belong to the Host-owned workflow,
            // not to this parent.
            // Restoring one into the parent's runtime would resurrect it inside
            // the parent's list/join/guard. The Host workflow re-adopts its
            // own sessions from its durable enlistment facts instead.
            let records =
                AgentProjection.tryFind parentId (AgentJournal.snapshot journal).AgentProjections
                |> Option.bind (fun session -> session.Handles)
                |> Option.map (fun handles ->
                    let pending =
                        HandleProjection.joinable handles @ HandleProjection.reportableAbandoned handles

                    let bindings =
                        HandleProjection.linkedChildren handles
                        |> List.filter (fun record ->
                            not (
                                pending
                                |> List.exists (fun candidate ->
                                    candidate.Handle = record.Handle && candidate.Work = record.Work)
                            ))

                    pending @ bindings)
                |> Option.map (
                    List.filter (fun record ->
                        match record.Ownership with
                        | HandleOwnership.DurableParentHandle -> true
                        | HandleOwnership.HostOwnedHidden -> false)
                )
                |> Option.defaultValue []

            // DSL-MUTABLE: algorithm-scratch — recovered handle accumulator
            let recovered = ResizeArray<RecoveredHandle>()
            // DSL-MUTABLE: algorithm-scratch — waiting handle accumulator
            let waiting = ResizeArray<HandleRecoveryWait>()
            // DSL-MUTABLE: algorithm-scratch — blocked handle accumulator
            let blocked = ResizeArray<HandleRecoveryBlock>()

            for record in records do
                do!
                    restoreOneRecord
                        runtime
                        snapshot
                        journal
                        parentId
                        children
                        childCreatedDir
                        directoryOf
                        recovered
                        waiting
                        blocked
                        record

            // HandleFamilyRecovery carries Domain.SessionRecovery.NonEmpty.
            return concludeFamilyRecovery recovered waiting blocked
        }
