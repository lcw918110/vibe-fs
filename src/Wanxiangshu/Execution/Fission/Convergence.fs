namespace Wanxiangshu.Execution.Fission

open Fable.Core
open Fable.Core.JsInterop

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Collections.Generic
open System.Threading.Tasks
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.TerminalSignal
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

/// Host-side terminal bridge for same-participant Fission lanes. A physical lane
/// terminal is not a parent-visible agent completion. It materializes one keyed
/// lane view, and only the durable group convergence publishes a terminal on the
/// old logical owner's SessionId/completion cell.
module FissionHost =

    [<Import("fissionRuntime", "wanxiangli/fission-group")>]
    let private fissionRuntime: obj = jsNative

    /// A Fission owner replacement physically aborts the old present without
    /// cancelling logical-owner resources. Fission owns that distinction; the
    /// Host root supplies the two published continuations only.
    let routeAttemptAborted (sessionId: SessionId) (onSilentReplacement: unit -> unit) (onOrdinaryAbort: unit -> unit) =
        if fissionRuntime?isSilentInterrupt (SessionId.value sessionId) then
            onSilentReplacement ()
        else
            onOrdinaryAbort ()

    /// intra-participant-parallelism-009: an exact physical terminal is only
    /// a reconciliation occasion for the durable Fission lane that still owns
    /// that exact current physical material. The Host root supplies observation
    /// and wake capabilities; Fission owns the membership/currentness decision.
    let observePhysicalExecutionEnd
        (tryCurrentPhysical: SessionId -> PhysicalUserMessageId option)
        (durable: AgentJournal option)
        (kick: SessionId -> unit)
        (sessionId: SessionId)
        (physicalUserMessageId: PhysicalUserMessageId)
        =
        let isCurrentPhysical =
            tryCurrentPhysical sessionId |> Option.exists ((=) physicalUserMessageId)

        let isFissionLane =
            durable
            |> Option.exists (fun journal ->
                FissionProjection.tryMembershipOfLane
                    sessionId
                    (AgentJournal.snapshot journal).AgentProjections.Fission
                |> Option.isSome)

        if isCurrentPhysical && isFissionLane then
            kick sessionId

    let private appendFission durable owner providerRun fact =
        task {
            match! AgentJournal.appendAgent (StreamId.Session owner) providerRun fact durable with
            | Ok _ -> return Ok()
            | Error failure -> return Error(JournalAppendFailure.describe failure)
        }

    let private readVerified (durable: AgentJournal) (blobRef: BlobRef) (digest: BlobDigest) =
        task {
            match! durable.Writer.BlobWriter.Read blobRef with
            | Ok text when HostDigest.sha256Hex text = BlobDigest.value digest -> return Ok text
            | Ok _ -> return Error "Fission aggregate blob digest mismatch"
            | Error error -> return Error error
        }

    let private completionDeliveredTo laneIndex completionId (group: FissionGroupProjection) =
        group.CompletionDeliveries
        |> Map.tryFind completionId
        |> Option.defaultValue Set.empty
        |> Set.contains laneIndex

    let private sharedInputsAccounted laneIndex (group: FissionGroupProjection) =
        group.PreFissionCompletionIds
        |> Set.forall (fun completionId -> completionDeliveredTo laneIndex completionId group)

    let private agentIdOfExternal (externalId: string) =
        if externalId.StartsWith("agent:", StringComparison.Ordinal) then
            Some(externalId.Substring("agent:".Length))
        else
            None

    let private handleStillOutstanding handles agentId =
        match HandleProjection.tryFind (HandleController.agentHandle agentId) handles with
        | Some { Lifecycle = HandleLifecycle.Retired } -> false
        | Some _
        | None -> true

    let private externalStillOutstanding handles externalId =
        match agentIdOfExternal externalId with
        | None -> true
        | Some agentId -> handleStillOutstanding handles agentId

    let private affinityOutstanding handles laneIndex externalId affinity =
        affinity = laneIndex && externalStillOutstanding handles externalId

    let private laneHasOutstandingExternal durable laneIndex (group: FissionGroupProjection) =
        let handles = AgentJournal.handleProjection durable group.OwnerSessionId

        group.ExternalAffinities
        |> Map.exists (fun externalId affinity -> affinityOutstanding handles laneIndex externalId affinity)

    let private isHandleRetired durable ownerSessionId handle =
        match HandleProjection.tryFind handle (AgentJournal.handleProjection durable ownerSessionId) with
        | Some { Lifecycle = HandleLifecycle.Retired } -> true
        | _ -> false

    /// A refused consume still counts when the handle is already retired;
    /// otherwise the refusal is reported and the handle stays unconsumed.
    let private retiredAfterRefusedConsume durable ownerSessionId handle err =
        let retired = isHandleRetired durable ownerSessionId handle

        if not retired then
            Diagnostic.emit
                "fission-handle-consume-failed"
                [ "session_id", SessionId.value ownerSessionId
                  "result", sprintf "handle consume failed: %A" err ]

        retired

    let private ensureRetiredAfterConsume (durablePort: AgentJournalPort) durable ownerSessionId handle =
        task {
            match! HandleController.consume durablePort ownerSessionId handle with
            | Ok _ -> return true
            | Error err -> return retiredAfterRefusedConsume durable ownerSessionId handle err
        }

    let private tryConsumeCompletedHandle (durablePort: AgentJournalPort) durable ownerSessionId handle =
        match HandleProjection.tryFind handle (AgentJournal.handleProjection durable ownerSessionId) with
        | Some { Lifecycle = HandleLifecycle.Retired } -> task { return true }
        | Some { Lifecycle = HandleLifecycle.CompletedAwaitingJoin _ }
        | Some { Lifecycle = HandleLifecycle.Abandoned _ } ->
            ensureRetiredAfterConsume durablePort durable ownerSessionId handle
        | Some { Lifecycle = HandleLifecycle.Active }
        | None -> task { return false }

    let private consumeOnePreFission
        (durablePort: AgentJournalPort)
        durable
        (group: FissionGroupProjection)
        completionId
        =
        match agentIdOfExternal completionId with
        | None -> task { return true }
        | Some agentId ->
            tryConsumeCompletedHandle durablePort durable group.OwnerSessionId (HandleController.agentHandle agentId)

    let private continuePreFissionConsume ok rest consume =
        if ok then consume rest else task { return false }

    let private consumePreFissionAgents durable (group: FissionGroupProjection) =
        let durablePort =
            Wanxiangshu.Composition.Durable.AgentJournalPortAdapter.fromAgentJournal durable

        let rec consume completionIds =
            task {
                match completionIds with
                | [] -> return true
                | completionId :: rest ->
                    let! ok = consumeOnePreFission durablePort durable group completionId
                    return! continuePreFissionConsume ok rest consume
            }

        group.PreFissionCompletionIds |> Set.toList |> List.sort |> consume

    let private appendOneLaneWork
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (blocks: ResizeArray<string>)
        laneIndex
        =
        taskResult {
            match Map.tryFind laneIndex group.LaneWork with
            | None -> return! Error(sprintf "missing lane work %d" laneIndex)
            | Some(workRef, workDigest) ->
                let! record = readVerified durable workRef workDigest
                blocks.Add ""
                blocks.Add(sprintf "[lane.%d]" laneIndex)
                blocks.Add record
                return ()
        }

    let rec private appendLaneWorkBlocks
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (blocks: ResizeArray<string>)
        laneIndexes
        =
        taskResult {
            match laneIndexes with
            | [] -> return ()
            | laneIndex :: rest ->
                do! appendOneLaneWork durable group blocks laneIndex
                return! appendLaneWorkBlocks durable group blocks rest
        }

    let private appendOneSharedCompletion
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (blocks: ResizeArray<string>)
        completionId
        =
        taskResult {
            match Map.tryFind completionId group.CapturedCompletions with
            | None -> return! Error("missing captured shared completion: " + completionId)
            | Some(payloadRef, payloadDigest) ->
                let! payload = readVerified durable payloadRef payloadDigest
                blocks.Add ""
                blocks.Add("[shared_completion.\"" + completionId.Replace("\"", "\\\"") + "\"]")
                blocks.Add payload
                return ()
        }

    let rec private appendSharedCompletionBlocks
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (blocks: ResizeArray<string>)
        completionIds
        =
        taskResult {
            match completionIds with
            | [] -> return ()
            | completionId :: rest ->
                do! appendOneSharedCompletion durable group blocks completionId
                return! appendSharedCompletionBlocks durable group blocks rest
        }

    let private aggregateView (durable: AgentJournal) (group: FissionGroupProjection) =
        taskResult {
            let! ownerView = readVerified durable group.OwnerViewRef group.OwnerViewDigest
            // DSL-MUTABLE: algorithm-scratch — view block accumulator
            let blocks = ResizeArray<string>()
            blocks.Add "[[fission_convergence]]"
            blocks.Add(sprintf "lane_count = %d" group.LaneCount)
            blocks.Add ""
            blocks.Add "[owner_before_fission]"
            blocks.Add ownerView
            do! appendLaneWorkBlocks durable group blocks (FissionRing.mergeOrder group.LaneCount)

            do!
                group.PreFissionCompletionIds
                |> Set.toList
                |> List.sort
                |> appendSharedCompletionBlocks durable group blocks

            return String.concat "\n" (blocks |> Seq.toList)
        }

    let private ownerAuthority projections ownerSessionId =
        PromptAuthorityProjectionQueries.activeProfile ownerSessionId projections
        |> Option.orElseWith (fun () ->
            PromptAuthorityProjectionQueries.lastAuthorityProfile ownerSessionId projections)

    let private takeoverPrompt aggregate =
        LlmFacing.renderInstructions
            [ "The Fission ring has collected every lane's delivered view and returned the complete handoff to this final present."
              "Continue as the same logical participant. Integrate the handoff below and now produce the ordinary final report to your commissioner."
              "Do not report lane mechanics, physical session ids, or the internal Fission topology."
              aggregate ]

    let private appendTakeoverClaimed
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (aggregateBlob: BlobWriteReceipt)
        laneIndex
        laneSessionId
        promptKey
        =
        appendFission
            durable
            group.OwnerSessionId
            None
            (FissionFact.FissionTakeoverClaimed
                {| GroupId = group.GroupId
                   OwnerSessionId = group.OwnerSessionId
                   LaneIndex = laneIndex
                   LaneSessionId = laneSessionId
                   PromptKey = promptKey
                   AggregateViewRef = aggregateBlob.BlobRef
                   AggregateViewDigest = aggregateBlob.BlobDigest |})

    let private sendTakeover
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (aggregateBlob: BlobWriteReceipt)
        aggregate
        laneIndex
        laneSessionId
        =
        task {
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            match!
                dispatcher.SendContinuation
                    (DispatchSessionPort.ofSessionPort sessionPort)
                    laneSessionId
                    (takeoverPrompt aggregate)
                    PromptAuthority.ContinuationKind.FissionHandoff
                    authority
                    (directoryFor laneSessionId)
                    PromptDispatcher.AwaitMode.Await
                    None
            with
            | Error reason ->
                Diagnostic.emit
                    "fission-send-failed"
                    [ "session_id", SessionId.value laneSessionId
                      "result",
                      sprintf
                          "fission takeover prompt send failed: group=%s lane=%d reason=%s"
                          group.GroupId
                          laneIndex
                          reason ]

                return false
            | Ok promptKey ->
                match! appendTakeoverClaimed durable group aggregateBlob laneIndex laneSessionId promptKey with
                | Error failure ->
                    Diagnostic.emit
                        "fission-takeover-claimed-append-failed"
                        [ "session_id", SessionId.value laneSessionId
                          "result",
                          sprintf
                              "fission takeover claim append failed: group=%s lane=%d reason=%s"
                              group.GroupId
                              laneIndex
                              failure ]

                    return false
                | Ok() -> return true
        }

    let private sendTakeoverWithAggregateBlob
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (aggregateBlob: BlobWriteReceipt)
        aggregate
        =
        let target =
            FissionRing.finalLane group.LaneCount
            |> Option.bind (fun laneIndex ->
                Map.tryFind laneIndex group.LaneSessions
                |> Option.map (fun laneSessionId -> laneIndex, laneSessionId))

        match target with
        | None -> task { return false }
        | Some(laneIndex, laneSessionId) ->
            sendTakeover
                sessionPort
                durable
                directoryFor
                group
                authority
                aggregateBlob
                aggregate
                laneIndex
                laneSessionId

    let private writeAggregateAndTakeover
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        aggregate
        =
        task {
            match! durable.WriteBlob aggregate with
            | Error reason ->
                Diagnostic.emit
                    "fission-aggregate-write-failed"
                    [ "session_id", SessionId.value group.OwnerSessionId
                      "result", sprintf "fission aggregate blob write failed: group=%s reason=%s" group.GroupId reason ]

                return false
            | Ok aggregateBlob ->
                return!
                    sendTakeoverWithAggregateBlob
                        sessionPort
                        durable
                        directoryFor
                        group
                        authority
                        aggregateBlob
                        aggregate
        }

    let private startTakeoverWithAuthority
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        =
        task {
            match! aggregateView durable group with
            | Error reason ->
                Diagnostic.emit
                    "fission-aggregate-view-read-failed"
                    [ "session_id", SessionId.value group.OwnerSessionId
                      "result", sprintf "fission aggregate view read failed: group=%s reason=%s" group.GroupId reason ]

                return false
            | Ok aggregate ->
                return! writeAggregateAndTakeover sessionPort durable directoryFor group authority aggregate
        }

    let private runClaimedTakeover
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        task {
            try
                let projections = (AgentJournal.snapshot durable).AgentProjections

                return!
                    ownerAuthority projections group.OwnerSessionId
                    |> Option.map (startTakeoverWithAuthority sessionPort durable directoryFor group)
                    |> Option.defaultValue (task { return false })
            finally
                fissionRuntime?endTakeover group.GroupId
        }

    let private startTakeover
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        if fissionRuntime?tryBeginTakeover group.GroupId then
            runClaimedTakeover sessionPort durable directoryFor group
        else
            task { return false }

    let private startTakeoverIfPreConsumed
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        preConsumed
        =
        if preConsumed then
            startTakeover sessionPort durable directoryFor group
        else
            task { return false }

    let private tryConvergeOpen
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        task {
            if group.Takeover.IsSome then
                return true
            elif not (FissionProjection.convergenceReady group) then
                return false
            else
                let! preConsumed = consumePreFissionAgents durable group
                return! startTakeoverIfPreConsumed sessionPort durable directoryFor group preConsumed
        }

    let private tryConvergeGroup
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        match group.Terminal with
        | FissionGroupTerminal.Failed _ -> task { return false }
        | FissionGroupTerminal.Converged _ -> task { return true }
        | FissionGroupTerminal.Open -> tryConvergeOpen sessionPort durable directoryFor group

    /// Once every lane record and shared completion is accounted for, route the
    /// complete ring bundle back into the final physical present. The logical
    /// owner remains open until that continuation itself reaches an ordinary
    /// terminal turn.
    let tryConverge
        (sessionPort: FissionSessionPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (owner: SessionId)
        =
        task {
            match
                FissionProjection.tryLatestForOwner owner (AgentJournal.snapshot durable).AgentProjections.Fission
            with
            | None -> return false
            | Some group -> return! tryConvergeGroup sessionPort durable directoryFor group
        }

    let private reportLaneAbort (group: FissionGroupProjection) (laneSessionId: SessionId) aborted =
        match aborted with
        | Ok() -> ()
        | Error reason ->
            Diagnostic.emit
                "fission-lane-abort-failed"
                [ "session_id", SessionId.value laneSessionId
                  "result", sprintf "fission lane abort failed: group=%s reason=%s" group.GroupId reason ]

    let private abortAllLaneSessions (sessionPort: FissionSessionPort) (group: FissionGroupProjection) =
        task {
            for KeyValue(_, laneSessionId) in group.LaneSessions do
                let! aborted = sessionPort.AbortSession laneSessionId
                reportLaneAbort group laneSessionId aborted
        }

    let private failOpenGroup
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        reason
        =
        task {
            match!
                appendFission
                    durable
                    group.OwnerSessionId
                    None
                    (FissionFact.FissionFailed
                        {| GroupId = group.GroupId
                           OwnerSessionId = group.OwnerSessionId
                           Reason = reason |})
            with
            | Error failure ->
                Diagnostic.emit
                    "fission-failed-fact-append-failed"
                    [ "session_id", SessionId.value group.OwnerSessionId
                      "result", sprintf "fission failed fact append failed: group=%s reason=%s" group.GroupId failure ]

                return ()
            | Ok() ->
                do! abortAllLaneSessions sessionPort group

                let notified =
                    eventPort.NotifyTerminal group.OwnerSessionId (TerminalOutcome.Failed(TerminalStop.session reason))

                if not notified then
                    Diagnostic.emit
                        "fission-failed-terminal-unobserved"
                        [ "session_id", SessionId.value group.OwnerSessionId
                          "result",
                          sprintf "fission failed terminal was a duplicate or found no listener: group=%s" group.GroupId ]

                FissionAdmission.releaseOwner group.OwnerSessionId
                fissionRuntime?clearOwner (SessionId.value group.OwnerSessionId) |> ignore
        }

    let private failGroup
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        reason
        =
        task {
            match group.Terminal with
            | FissionGroupTerminal.Open -> do! failOpenGroup sessionPort eventPort durable group reason
            | _ -> ()
        }

    let private validateOwnerRunResult (result: AgentRunResult) =
        if result.IsValid then
            Ok result
        else
            Error "Fission takeover completed with empty terminal output"

    let private ownerRunResult (durable: AgentJournal) (group: FissionGroupProjection) (turn: ReconciledTurn) =
        let projections = (AgentJournal.snapshot durable).AgentProjections

        match ownerAuthority projections group.OwnerSessionId with
        | None -> Error "Fission takeover completed without owner authority"
        | Some authority ->
            let result =
                { SessionId = group.OwnerSessionId
                  AuthorityRootUserMessageId = authority.AuthorityRootUserMessageId
                  ProviderRun = turn.ProviderRun
                  Role = authority.CanonicalRole
                  Directory = turn.Directory
                  TerminalText = CompletedTurnClassifier.partsSessionText turn.Parts
                  TurnFormalText = CompletedTurnClassifier.partsText turn.Parts }

            validateOwnerRunResult result
            |> Result.map (fun valid -> valid, authority.CanonicalRole)

    let private appendConvergedFromTakeover
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        =
        appendFission
            durable
            group.OwnerSessionId
            (Some turn.ProviderRun)
            (FissionFact.FissionConverged
                {| GroupId = group.GroupId
                   OwnerSessionId = group.OwnerSessionId
                   TerminalLaneSessionId = turn.SessionId
                   TerminalProviderRun = turn.ProviderRun
                   AggregateViewRef = takeover.AggregateViewRef
                   AggregateViewDigest = takeover.AggregateViewDigest |})

    let private publishOwnerTakeover
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (turn: ReconciledTurn)
        (result: AgentRunResult)
        (role: Role)
        =
        task {
            let ownerTurn =
                { turn with
                    SessionId = group.OwnerSessionId
                    AuthorityRootUserMessageId = result.AuthorityRootUserMessageId
                    Role = Some role }

            let tracePort = TerminalTracePort.forJournal durable |> Some

            let deliveryWitness =
                // 围栏感知读取：同一 (SessionId, ProviderRun) 下可能并存多条见证
                // （同轮重试换了 toolCallId），粗粒度读取会把证据整体丢掉。
                // 这里只知道 (AuthorityRoot, PhysicalUserMessageId)，
                // 交给围栏子集入口按围栏字段筛选，材料不一致时仍然拒绝。
                TaskDeliveryJournal.tryFindByFenceFields
                    durable
                    turn.SessionId
                    turn.ProviderRun
                    None
                    (Some turn.AuthorityRootUserMessageId)
                    (Some turn.PhysicalUserMessageId)
                    // 本次只有子集围栏：这一轮 turn 不携带逻辑运行 id，
                    // 也不携带交付任务；缺失的两位（逻辑运行 / 交付任务）
                    // 由交付见证的写入方（settle 侧按 activeProfile 与
                    // deliveryTaskOf 登记）另行核对。
                    None
                    None
                |> Option.isSome

            match! TerminalReporter.completeWithEvidence eventPort tracePort ownerTurn deliveryWitness with
            | XTraceTerminalCompletion.Published published when
                published.SessionId = result.SessionId
                && published.ProviderRun = result.ProviderRun
                ->
                return Ok true
            | XTraceTerminalCompletion.Published _ ->
                return Error "Fission takeover terminal reporter changed the physical owner or provider run"
            | XTraceTerminalCompletion.CaptureFailed error ->
                return Error(sprintf "Fission takeover terminal trace capture failed: %A" error)
            | XTraceTerminalCompletion.RejectedMissingRole ->
                return Error "Fission takeover terminal reporter rejected the owner role"
            | XTraceTerminalCompletion.RejectedEmptyOutput ->
                return Error "Fission takeover completed with empty terminal output"
            | XTraceTerminalCompletion.RejectedUndelivered ->
                return Error "Fission takeover completed without a delivery witness"
        }

    /// Publish the owner terminal before the group is recorded as converged.
    ///
    /// The durable group terminal is single-shot: once FissionConverged is
    /// folded, the group refuses FissionFailed (TerminalAlreadySet). Appending
    /// the convergence fact first would therefore make a rejected owner
    /// terminal unfailable — the lanes would stay alive, the owner would never
    /// be notified and the owner admission reservation would stay held.
    /// Publishing first keeps the failure path available: a rejected
    /// publication appends no FissionConverged fact, so the group is still open
    /// and can be failed, its lanes aborted, the owner notified and the owner
    /// reservation released.
    let private settleTakeoverConvergence
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        convergence
        =
        task {
            match convergence with
            | Ok() ->
                FissionAdmission.releaseOwner group.OwnerSessionId
                fissionRuntime?clearOwner (SessionId.value group.OwnerSessionId) |> ignore
                return true
            | Error error ->
                do! failGroup sessionPort eventPort durable group error
                return true
        }

    let private publishConvergedTakeover
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        (result: AgentRunResult)
        (role: Role)
        =
        task {
            let! published = publishOwnerTakeover eventPort durable group turn result role

            match published with
            | Error error ->
                // The owner terminal was not published, so no convergence fact
                // exists and the group is still open: the failure path can
                // still fail it, abort the lanes, notify the owner and release
                // the owner reservation.
                do! failGroup sessionPort eventPort durable group error
                return true
            | Ok _ ->
                let! convergence = appendConvergedFromTakeover durable group takeover turn
                return! settleTakeoverConvergence sessionPort eventPort durable group convergence
        }

    let private completeTakeover
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        =
        task {
            match ownerRunResult durable group turn with
            | Error error ->
                do! failGroup sessionPort eventPort durable group error
                return true
            | Ok(result, role) ->
                return! publishConvergedTakeover sessionPort eventPort durable group takeover turn result role
        }

    let private appendLaneMaterialized
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        (workBlob: BlobWriteReceipt)
        =
        appendFission
            durable
            group.OwnerSessionId
            (Some turn.ProviderRun)
            (FissionFact.FissionLaneMaterialized
                {| GroupId = group.GroupId
                   OwnerSessionId = group.OwnerSessionId
                   LaneIndex = laneIndex
                   LaneSessionId = turn.SessionId
                   ProviderRun = turn.ProviderRun
                   ViewRef = workBlob.BlobRef
                   ViewDigest = workBlob.BlobDigest |})

    let private afterLaneMaterializedAppend
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (directoryFor: SessionId -> string option)
        (outcome: Result<unit, string>)
        =
        task {
            match outcome with
            | Error error ->
                do! failGroup sessionPort eventPort durable group error
                return true
            | Ok() ->
                let! _ = tryConverge sessionPort durable directoryFor group.OwnerSessionId
                return true
        }

    let private directoryForTurn (turn: ReconciledTurn) laneSessionId =
        if laneSessionId = turn.SessionId then
            turn.Directory
        else
            None

    let private writeLaneWorkAndMaterialize
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        (view: string)
        =
        task {
            match! durable.WriteBlob view with
            | Error error ->
                do! failGroup sessionPort eventPort durable group error
                return true
            | Ok workBlob ->
                let! outcome = appendLaneMaterialized durable group laneIndex turn workBlob
                return! afterLaneMaterializedAppend sessionPort eventPort durable group (directoryForTurn turn) outcome
        }

    let private nudgeUndeliveredLane
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        task {
            // A stop without delivery keeps the lane alive in the same run.
            let! nudge =
                HostJoinGuard.nudge
                    sessionPort
                    rootWorkspace
                    (Some(AgentJournalPortAdapter.forHostJoinGuard durable))
                    (Some durable)
                    joinGuardNudges
                    (fun () ->
                        match permit with
                        | Some idlePermit -> quiescence.TryConsume idlePermit
                        | None -> Error QuiescencePermitFailure.NoFreshIdle)
                    (fun () ->
                        match permit with
                        | Some idlePermit -> quiescence.TryRelease idlePermit
                        | None -> Error QuiescencePermitFailure.NoFreshIdle)
                    turn.SessionId
                    turn.ProviderRun
                    turn.Directory

            match nudge with
            | HostJoinGuard.JoinGuardNudgeOutcome.Failed reason ->
                do!
                    failGroup
                        sessionPort
                        eventPort
                        durable
                        group
                        (sprintf "fission undelivered lane nudge failed: %s" reason)
            | HostJoinGuard.JoinGuardNudgeOutcome.NotSent ->
                Diagnostic.emit
                    "fission-join-nudge-unsent"
                    [ "session_id", SessionId.value turn.SessionId
                      "result",
                      sprintf
                          "fission undelivered lane nudge unsent: group=%s lane=%d reason=not-sent"
                          group.GroupId
                          laneIndex ]
            | HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected failure ->
                Diagnostic.emit
                    "fission-join-nudge-unsent"
                    [ "session_id", SessionId.value turn.SessionId
                      "result",
                      sprintf
                          "fission undelivered lane nudge admission rejected: group=%s lane=%d reason=%A"
                          group.GroupId
                          laneIndex
                          failure ]
            | HostJoinGuard.JoinGuardNudgeOutcome.Sent _
            | HostJoinGuard.JoinGuardNudgeOutcome.AlreadyOutstanding
            | HostJoinGuard.JoinGuardNudgeOutcome.Superseded -> ()

            return true
        }

    let private materializeCompletedLane
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        task {
            let material =
                TaskDeliveryJournal.tryFindByFenceFields
                    durable
                    turn.SessionId
                    turn.ProviderRun
                    None
                    (Some turn.AuthorityRootUserMessageId)
                    (Some turn.PhysicalUserMessageId)
                    None
                    None
                |> Option.map (fun witness -> witness.View)
                |> Option.orElseWith (fun () ->
                    Wanxiangshu.Repository.Programming.Js.JsDeliveredValues.tryPeekMaterialWith
                        (SessionId.value turn.SessionId)
                        (Some(CompletedTurnClassifier.partsSessionText turn.Parts)))

            match material with
            | None ->
                return!
                    nudgeUndeliveredLane
                        sessionPort
                        rootWorkspace
                        eventPort
                        joinGuardNudges
                        quiescence
                        permit
                        durable
                        group
                        laneIndex
                        turn
            | Some delivered ->
                return!
                    writeLaneWorkAndMaterialize
                        sessionPort
                        eventPort
                        durable
                        group
                        laneIndex
                        turn
                        (ReturnedMaterial.render delivered)
        }

    let private reportOutstandingExternalNudge (turn: ReconciledTurn) nudge =
        match nudge with
        | HostJoinGuard.JoinGuardNudgeOutcome.Failed reason ->
            Diagnostic.emit
                "fission-join-nudge-failed"
                [ "session_id", SessionId.value turn.SessionId
                  "result", sprintf "fission outstanding external nudge failed: %s" reason ]
        | HostJoinGuard.JoinGuardNudgeOutcome.NotSent ->
            Diagnostic.emit
                "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId
                  "result", "fission outstanding external nudge unsent: not-sent" ]
        | HostJoinGuard.JoinGuardNudgeOutcome.AdmissionRejected failure ->
            Diagnostic.emit
                "fission-join-nudge-unsent"
                [ "session_id", SessionId.value turn.SessionId
                  "result", sprintf "fission outstanding external nudge admission rejected: %A" failure ]
        | HostJoinGuard.JoinGuardNudgeOutcome.Sent _
        | HostJoinGuard.JoinGuardNudgeOutcome.AlreadyOutstanding
        | HostJoinGuard.JoinGuardNudgeOutcome.Superseded -> ()

    let private observeOpenLaneCompletion
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        task {
            match laneHasOutstandingExternal durable laneIndex group, permit, sharedInputsAccounted laneIndex group with
            | true, None, _ -> return true
            | true, Some idlePermit, _ ->
                let! nudge =
                    HostJoinGuard.nudge
                        sessionPort
                        rootWorkspace
                        (journal
                         |> Option.map Wanxiangshu.Composition.Durable.AgentJournalPortAdapter.forHostJoinGuard)
                        journal
                        joinGuardNudges
                        (fun () -> quiescence.TryConsume idlePermit)
                        (fun () -> quiescence.TryRelease idlePermit)
                        turn.SessionId
                        turn.ProviderRun
                        turn.Directory

                reportOutstandingExternalNudge turn nudge

                return true
            | false, _, false ->
                // The shared completion broadcaster will send a same-run
                // continuation when the missing fact arrives.
                return true
            | false, _, true ->
                return!
                    materializeCompletedLane
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        joinGuardNudges
                        quiescence
                        permit
                        durable
                        group
                        laneIndex
                        turn
        }

    let private observeCompletedLane
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        match group.Terminal with
        | FissionGroupTerminal.Converged _
        | FissionGroupTerminal.Failed _ -> task { return true }
        | FissionGroupTerminal.Open ->
            observeOpenLaneCompletion
                sessionPort
                rootWorkspace
                eventPort
                journal
                joinGuardNudges
                quiescence
                permit
                durable
                group
                laneIndex
                turn

    let private settlementObservation outcome =
        match outcome with
        | ReconcileProgram.TurnInProgress -> FissionSettlementObservation.OngoingExecution
        | ReconcileProgram.TurnNeedsContinuation _ -> FissionSettlementObservation.NeedsContinuation
        | ReconcileProgram.TurnFailed _ -> FissionSettlementObservation.ProviderFailed
        | ReconcileProgram.TurnAborted reason -> FissionSettlementObservation.ExternalAbort reason
        | ReconcileProgram.TurnCompleted -> FissionSettlementObservation.Completed

    let private settlementObservationOfTurn (turn: ReconciledTurn) = settlementObservation turn.Outcome

    let private failSettlement sessionPort eventPort durable group reason =
        task {
            do! failGroup sessionPort eventPort durable group reason
            return true
        }

    let private observeCurrentTakeoverOutcome
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        =
        match FissionSettlement.decideTakeover (settlementObservationOfTurn turn) with
        | FissionTakeoverSettlementDecision.YieldToTurnWorkflow -> task { return false }
        | FissionTakeoverSettlementDecision.FailGroup reason ->
            failSettlement sessionPort eventPort durable group reason
        | FissionTakeoverSettlementDecision.CompleteOwner ->
            // LC-011: the lane's local delivery is integration material only —
            // it already travelled into the takeover prompt via the aggregate,
            // so it is neither the owner delivery cell's content nor a witness.
            // The owner's own integrated delivery is judged by publishOwnerTakeover's
            // durable TaskDelivery witness.
            completeTakeover sessionPort eventPort durable group takeover turn

    let private observeTakeoverOutcome
        (sessionPort: FissionSessionPort)
        (eventPort: FissionEventPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        =
        if turn.SessionId = takeover.LaneSessionId then
            // Composition/Turn has already reconciled this observation against
            // session's current physical user message. The takeover owns the
            // lane across every same-run continuation; re-freezing it to the first
            // FissionHandoff physical id would strand nudge/NEEDHELP/AABB successors.
            observeCurrentTakeoverOutcome sessionPort eventPort durable group takeover turn
        else
            // Once takeover is claimed, all non-terminal lanes are historical
            // physical presents and cannot regain settlement ownership.
            task { return true }

    let private observeOpenLaneOutcome
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        match FissionSettlement.decideLane (settlementObservationOfTurn turn) with
        | FissionLaneSettlementDecision.YieldToTurnWorkflow -> task { return false }
        | FissionLaneSettlementDecision.FailGroup reason -> failSettlement sessionPort eventPort durable group reason
        | FissionLaneSettlementDecision.MaterializeLane ->
            observeCompletedLane
                sessionPort
                rootWorkspace
                eventPort
                journal
                joinGuardNudges
                quiescence
                permit
                durable
                group
                laneIndex
                turn

    let private observeLaneOutcome
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        match group.Terminal, group.Takeover with
        | FissionGroupTerminal.Converged _, _
        | FissionGroupTerminal.Failed _, _ -> task { return true }
        | FissionGroupTerminal.Open, Some takeover ->
            observeTakeoverOutcome sessionPort eventPort durable group takeover turn
        | FissionGroupTerminal.Open, None ->
            observeOpenLaneOutcome
                sessionPort
                rootWorkspace
                eventPort
                journal
                joinGuardNudges
                quiescence
                permit
                durable
                group
                laneIndex
                turn

    let private observeDurableLaneTurn
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (turn: ReconciledTurn)
        =
        match
            FissionProjection.tryMembershipOfLane
                turn.SessionId
                (AgentJournal.snapshot durable).AgentProjections.Fission
        with
        | None -> task { return false }
        | Some(group, laneIndex) ->
            observeLaneOutcome
                sessionPort
                rootWorkspace
                eventPort
                journal
                joinGuardNudges
                quiescence
                permit
                durable
                group
                laneIndex
                turn

    /// Returns true when this turn belongs to Fission and its terminal semantics
    /// were consumed here. Retired owner sessions are absorbed; non-terminal lane
    /// turns return false so ordinary repair/recovery behavior still runs.
    let observeLaneTurn
        (sessionPort: FissionSessionPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: FissionEventPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (turn: ReconciledTurn)
        : Task<bool> =
        task {
            let isOwnerReplaced =
                fissionRuntime?isSilentInterrupt (SessionId.value turn.SessionId)
                || (journal
                    |> Option.exists (fun durable ->
                        FissionProjection.tryActiveForOwner
                            turn.SessionId
                            (AgentJournal.snapshot durable).AgentProjections.Fission
                        |> Option.isSome))

            match isOwnerReplaced, journal with
            | true, _ ->
                // Old caller physical present was replaced and retired by Fission.
                // Absorb all turn observations silently; do not cascade or continue.
                return true
            | false, None -> return false
            | false, Some durable ->
                return!
                    observeDurableLaneTurn
                        sessionPort
                        rootWorkspace
                        eventPort
                        journal
                        joinGuardNudges
                        quiescence
                        permit
                        durable
                        turn
        }
