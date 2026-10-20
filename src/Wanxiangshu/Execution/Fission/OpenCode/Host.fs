namespace Wanxiangshu.Execution.Fission.OpenCode

open Wanxiangshu.Persistence.Journal.JournalOutcome
open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

[<RequireQualifiedAccess>]
module FissionHostRequestProjection =

    let private requireOutputMessage output =
        let message = if isNull output then null else output?message

        if isNull message then
            invalidOp "INTRA-PARTICIPANT-PARALLELISM-013: fission request projection has no mutable output.message"

        message

    let private ensureToolsObject (message: obj) : obj =
        if isNull message?tools then createObj [] else message?tools

    let private projectVisibility hasPhysicalParent output =
        if FissionRequestProjection.apply hasPhysicalParent then
            let message = requireOutputMessage output
            let tools = ensureToolsObject message
            tools?fission <- box false
            message?tools <- tools

    let projectExternalManaged
        (hasPhysicalParent: SessionId -> bool)
        (intent: ChatAdmissionIntent.Decision)
        (output: obj)
        =
        match intent with
        | ChatAdmissionIntent.Decision.ExternalRootIntent evidence ->
            projectVisibility (hasPhysicalParent evidence.Key.SessionId) output
        | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent evidence ->
            projectVisibility (hasPhysicalParent evidence.Key.SessionId) output
        | ChatAdmissionIntent.Decision.NoManagedExecution _
        | ChatAdmissionIntent.Decision.PendingPromptIntent _
        | ChatAdmissionIntent.Decision.AcceptedInputIntent _
        | ChatAdmissionIntent.Decision.HostInternal _
        | ChatAdmissionIntent.Decision.Reject _ -> ()

    let projectPendingManaged
        (hasPhysicalParent: SessionId -> bool)
        (intent: ChatAdmissionIntent.Decision)
        (output: obj)
        =
        match intent with
        | ChatAdmissionIntent.Decision.PendingPromptIntent evidence ->
            projectVisibility (hasPhysicalParent evidence.Key.SessionId) output
        | ChatAdmissionIntent.Decision.AcceptedInputIntent evidence ->
            projectVisibility (hasPhysicalParent evidence.SessionId) output
        | ChatAdmissionIntent.Decision.ExternalRootIntent _
        | ChatAdmissionIntent.Decision.ActiveHumanContinuationIntent _
        | ChatAdmissionIntent.Decision.NoManagedExecution _
        | ChatAdmissionIntent.Decision.HostInternal _
        | ChatAdmissionIntent.Decision.Reject _ -> ()

/// Host-side terminal bridge for same-participant Fission lanes. A physical lane
/// terminal is not a parent-visible agent completion. It materializes one keyed
/// lane view, and only the durable group convergence publishes a terminal on the
/// old logical owner's SessionId/completion cell.
module FissionHost =

    /// A Fission owner replacement physically aborts the old present without
    /// cancelling logical-owner resources. Fission owns that distinction; the
    /// Host root supplies the two published continuations only.
    let routeAttemptAborted (sessionId: SessionId) (onSilentReplacement: unit -> unit) (onOrdinaryAbort: unit -> unit) =
        if FissionRuntime.isSilentInterrupt sessionId then
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

    let private ensureRetiredAfterConsume (durablePort: AgentJournalPort) durable ownerSessionId handle =
        task {
            match! HandleController.consume durablePort ownerSessionId handle with
            | Ok _ -> return true
            | Error _ -> return isHandleRetired durable ownerSessionId handle
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

    let private allLaneWorkPresent (group: FissionGroupProjection) =
        group.LaneWork.Count = group.LaneCount
        && group.LaneProviderRuns.Count = group.LaneCount

    let private allSharedCompletionsDelivered (group: FissionGroupProjection) =
        group.PreFissionCompletionIds
        |> Set.forall (fun completionId ->
            let delivered =
                group.CompletionDeliveries
                |> Map.tryFind completionId
                |> Option.defaultValue Set.empty

            Map.containsKey completionId group.CapturedCompletions
            && delivered = Set.ofList [ 0 .. group.LaneCount - 1 ])

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
        (sessionPort: ISessionHostPort)
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
            | Error _ -> return false
            | Ok promptKey ->
                match! appendTakeoverClaimed durable group aggregateBlob laneIndex laneSessionId promptKey with
                | Error _ -> return false
                | Ok() -> return true
        }

    let private sendTakeoverWithAggregateBlob
        (sessionPort: ISessionHostPort)
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
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        aggregate
        =
        task {
            match! durable.WriteBlob aggregate with
            | Error _ -> return false
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
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        =
        task {
            match! aggregateView durable group with
            | Error _ -> return false
            | Ok aggregate ->
                return! writeAggregateAndTakeover sessionPort durable directoryFor group authority aggregate
        }

    let private runClaimedTakeover
        (sessionPort: ISessionHostPort)
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
                FissionRuntime.endTakeover group.GroupId
        }

    let private startTakeover
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        if FissionRuntime.tryBeginTakeover group.GroupId then
            runClaimedTakeover sessionPort durable directoryFor group
        else
            task { return false }

    let private startTakeoverIfPreConsumed
        (sessionPort: ISessionHostPort)
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
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (group: FissionGroupProjection)
        =
        task {
            if group.Takeover.IsSome then
                return true
            elif not (allLaneWorkPresent group) || not (allSharedCompletionsDelivered group) then
                return false
            else
                let! preConsumed = consumePreFissionAgents durable group
                return! startTakeoverIfPreConsumed sessionPort durable directoryFor group preConsumed
        }

    let private tryConvergeGroup
        (sessionPort: ISessionHostPort)
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
        (sessionPort: ISessionHostPort)
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

    let private appendDeliveryFact (durable: AgentJournal) groupId ownerSessionId completionId laneIndex =
        appendFission
            durable
            ownerSessionId
            None
            (FissionFact.FissionCompletionDelivered
                {| GroupId = groupId
                   OwnerSessionId = ownerSessionId
                   CompletionId = completionId
                   LaneIndex = laneIndex |})

    let private sendSharedCompletionContinuation
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        laneSessionId
        completionId
        (payload: string)
        =
        let prompt =
            LlmFacing.instructions
                [ "A completion that was already outstanding before Fission now belongs to every present of this same participant."
                  "Treat the completion payload below as part of your current responsibility, not as newly delegated work."
                  payload ]
            |> LlmFacing.withData [ LlmFacing.Data.stringField "completion_id" completionId ]
            |> LlmFacing.render

        let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

        dispatcher.SendContinuation
            (DispatchSessionPort.ofSessionPort sessionPort)
            laneSessionId
            prompt
            PromptAuthority.ContinuationKind.FissionHandoff
            authority
            (directoryFor laneSessionId)
            PromptDispatcher.AwaitMode.Detached
            None

    let private sendThenMarkDelivery
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        groupId
        completionId
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (now: FissionGroupProjection)
        laneIndex
        laneSessionId
        (payload: string)
        =
        task {
            match!
                sendSharedCompletionContinuation
                    sessionPort
                    durable
                    directoryFor
                    authority
                    laneSessionId
                    completionId
                    payload
            with
            | Error _ -> return ()
            | Ok _ ->
                let! _ = appendDeliveryFact durable groupId now.OwnerSessionId completionId laneIndex
                return ()
        }

    let private deliverOrMarkLane
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        groupId
        completionId
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (now: FissionGroupProjection)
        laneIndex
        laneSessionId
        (payload: string)
        =
        if Map.containsKey laneIndex now.LaneWork then
            task {
                let! _ = appendDeliveryFact durable groupId now.OwnerSessionId completionId laneIndex
                return ()
            }
        else
            sendThenMarkDelivery
                sessionPort
                durable
                directoryFor
                groupId
                completionId
                authority
                now
                laneIndex
                laneSessionId
                payload

    let private deliverOneLaneSession
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        groupId
        completionId
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (now: FissionGroupProjection)
        laneIndex
        (payload: string)
        =
        match Map.tryFind laneIndex now.LaneSessions with
        | None -> task { return () }
        | Some laneSessionId ->
            deliverOrMarkLane
                sessionPort
                durable
                directoryFor
                groupId
                completionId
                authority
                now
                laneIndex
                laneSessionId
                payload

    let private deliverOneLane
        (sessionPort: ISessionHostPort)
        (durable: AgentJournal)
        (directoryFor: SessionId -> string option)
        groupId
        completionId
        laneIndex
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (payload: string)
        =
        let current =
            FissionProjection.tryGroup groupId (AgentJournal.snapshot durable).AgentProjections.Fission

        match current with
        | None -> task { return () }
        | Some now when completionDeliveredTo laneIndex completionId now -> task { return () }
        | Some now ->
            deliverOneLaneSession sessionPort durable directoryFor groupId completionId authority now laneIndex payload

    let private abortAllLaneSessions (sessionPort: ISessionHostPort) (group: FissionGroupProjection) =
        task {
            for KeyValue(_, laneSessionId) in group.LaneSessions do
                let! _ = sessionPort.AbortSession laneSessionId
                ()
        }

    let private failOpenGroup
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
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
            | Error _ -> return ()
            | Ok() ->
                do! abortAllLaneSessions sessionPort group

                eventPort.NotifyTerminal group.OwnerSessionId (TerminalOutcome.Failed(TerminalStop.session reason))
                |> ignore

                FissionAdmission.releaseOwner group.OwnerSessionId
                FissionRuntime.clearOwner group.OwnerSessionId
        }

    let private failGroup
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
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
        (eventPort: IEventObservationPort)
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

            match! TerminalReporter.completeWithEvidence eventPort tracePort ownerTurn with
            | XTraceTerminalCompletion.Published published when
                published.SessionId = result.SessionId
                && published.ProviderRun = result.ProviderRun
                ->
                FissionAdmission.releaseOwner group.OwnerSessionId
                FissionRuntime.clearOwner group.OwnerSessionId
                return Ok true
            | XTraceTerminalCompletion.Published _ ->
                return Error "Fission takeover terminal reporter changed the physical owner or provider run"
            | XTraceTerminalCompletion.CaptureFailed error ->
                return Error(sprintf "Fission takeover terminal trace capture failed: %A" error)
            | XTraceTerminalCompletion.RejectedMissingRole ->
                return Error "Fission takeover terminal reporter rejected the owner role"
            | XTraceTerminalCompletion.RejectedEmptyOutput ->
                return Error "Fission takeover completed with empty terminal output"
        }

    let private publishConvergedTakeover
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (turn: ReconciledTurn)
        (result: AgentRunResult)
        (role: Role)
        (convergence: Result<unit, string>)
        =
        task {
            let! outcome =
                match convergence with
                | Error error -> Task.FromResult(Error error)
                | Ok() -> publishOwnerTakeover eventPort durable group turn result role

            match outcome with
            | Ok published -> return published
            | Error error ->
                do! failGroup sessionPort eventPort durable group error
                return true
        }

    let private persistAndPublishTakeover
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (turn: ReconciledTurn)
        (result: AgentRunResult)
        (role: Role)
        =
        task {
            let! convergence = appendConvergedFromTakeover durable group takeover turn

            return! publishConvergedTakeover sessionPort eventPort durable group turn result role convergence
        }

    let private completeTakeover
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
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
                return! persistAndPublishTakeover sessionPort eventPort durable group takeover turn result role
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
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
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
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
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

    let private materializeCompletedLane
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (turn: ReconciledTurn)
        =
        task {
            match
                Wanxiangshu.Repository.Programming.Js.JsDeliveredValues.tryPeekRendered (SessionId.value turn.SessionId)
            with
            | None ->
                do! failGroup sessionPort eventPort durable group "Fission lane completed without a delivered view"

                return true
            | Some delivered ->
                return! writeLaneWorkAndMaterialize sessionPort eventPort durable group laneIndex turn delivered
        }

    let private observeOpenLaneCompletion
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
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
                let! _ =
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

                return true
            | false, _, false ->
                // The shared completion broadcaster will send a same-run
                // continuation when the missing fact arrives.
                return true
            | false, _, true -> return! materializeCompletedLane sessionPort eventPort durable group laneIndex turn
        }

    let private observeCompletedLane
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
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

    let private settlementObservation abortCause outcome =
        match outcome, abortCause with
        | ReconcileProgram.TurnInProgress, _ -> FissionSettlementObservation.OngoingExecution
        | ReconcileProgram.TurnNeedsContinuation _, _ -> FissionSettlementObservation.NeedsContinuation
        | ReconcileProgram.TurnFailed _, _ -> FissionSettlementObservation.ProviderFailed
        | ReconcileProgram.TurnAborted _, AbortCause.DegenerationGuard _ ->
            FissionSettlementObservation.DegenerationInterrupted
        | ReconcileProgram.TurnAborted reason, AbortCause.External -> FissionSettlementObservation.ExternalAbort reason
        | ReconcileProgram.TurnCompleted, _ -> FissionSettlementObservation.Completed

    let private settlementObservationOfTurn abortCause (turn: ReconciledTurn) =
        settlementObservation abortCause turn.Outcome

    let private failSettlement sessionPort eventPort durable group reason =
        task {
            do! failGroup sessionPort eventPort durable group reason
            return true
        }

    let private observeCurrentTakeoverOutcome
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        =
        match FissionSettlement.decideTakeover (settlementObservationOfTurn abortCause turn) with
        | FissionTakeoverSettlementDecision.YieldToTurnWorkflow -> task { return false }
        | FissionTakeoverSettlementDecision.FailGroup reason ->
            failSettlement sessionPort eventPort durable group reason
        | FissionTakeoverSettlementDecision.CompleteOwner ->
            completeTakeover sessionPort eventPort durable group takeover turn

    let private observeTakeoverOutcome
        (sessionPort: ISessionHostPort)
        (eventPort: IEventObservationPort)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        (takeover: FissionTakeoverProjection)
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        =
        if turn.SessionId = takeover.LaneSessionId then
            // Composition/Turn has already reconciled this observation against
            // the session's current physical user message. The takeover owns the
            // lane across every same-run continuation; re-freezing it to the first
            // FissionHandoff physical id would strand nudge/NEEDHELP/AABB successors.
            observeCurrentTakeoverOutcome sessionPort eventPort durable group takeover abortCause turn
        else
            // Once takeover is claimed, all non-terminal lanes are historical
            // physical presents and cannot regain settlement ownership.
            task { return true }

    let private observeOpenLaneOutcome
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        =
        match FissionSettlement.decideLane (settlementObservationOfTurn abortCause turn) with
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
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (group: FissionGroupProjection)
        laneIndex
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        =
        match group.Terminal, group.Takeover with
        | FissionGroupTerminal.Converged _, _
        | FissionGroupTerminal.Failed _, _ -> task { return true }
        | FissionGroupTerminal.Open, Some takeover ->
            observeTakeoverOutcome sessionPort eventPort durable group takeover abortCause turn
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
                abortCause
                turn

    let private observeDurableLaneTurn
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (durable: AgentJournal)
        (abortCause: AbortCause)
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
                abortCause
                turn

    /// Returns true when this turn belongs to Fission and its terminal semantics
    /// were consumed here. Retired owner sessions are absorbed; non-terminal lane
    /// turns return false so ordinary repair/recovery behavior still runs.
    let observeLaneTurn
        (sessionPort: ISessionHostPort)
        (rootWorkspace: IRootWorkspaceReader)
        (eventPort: IEventObservationPort)
        (journal: AgentJournal option)
        (joinGuardNudges: HashSet<string>)
        (quiescence: ISessionQuiescenceGate)
        (permit: QuiescencePermit option)
        (abortCause: AbortCause)
        (turn: ReconciledTurn)
        : Task<bool> =
        task {
            let isOwnerReplaced =
                FissionRuntime.isSilentInterrupt turn.SessionId
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
                        abortCause
                        turn
        }
