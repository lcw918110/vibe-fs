namespace Wanxiangshu.Composition.Turn

open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession

module ReconcilePass =

    [<Emit("console.error($0, $1)")>]
    let private logError (prefix: string) (message: string) : unit = jsNative

    let private publishTurnOf (turn: ReconciledTurn) : ReconcileProgram.PublishTurn =
        { SessionId = turn.SessionId
          PhysicalUserMessageId = turn.PhysicalUserMessageId
          ProviderRun = turn.ProviderRun
          Outcome = turn.Outcome }

    let private classifyOutcome
        (outcome: ReconcileProgram.TurnOutcome)
        (observed: ReconcileProgram.ObservedTurn)
        : ReconcileProgram.ReconcileEvidence =
        match outcome with
        | ReconcileProgram.TurnCompleted
        | ReconcileProgram.TurnAborted _
        | ReconcileProgram.TurnFailed _ -> ReconcileProgram.ReconcileEvidence.Terminal observed
        | ReconcileProgram.TurnInProgress
        | ReconcileProgram.TurnNeedsContinuation _ -> ReconcileProgram.ReconcileEvidence.Provisional observed

    let private evidenceFromObserved (value: ReconciledTurn) : ReconcileProgram.ReconcileEvidence =
        match value.Observation with
        | Some ReconcileProgram.TurnUnknown ->
            // HOST-004 / rabbit §7: finish=None is SnapshotObservation evidence
            // (Unknown), wake-gated in decideStep. PublishTurn carries the
            // placeholder Outcome only — never TurnUnknown — so publishDecision
            // can seal/dedupe the handoff to TurnWorkflow / InteractionRepair.
            ReconcileProgram.ReconcileEvidence.Unknown(Some(ReconcileProgram.observedTurn (publishTurnOf value)))
        | None -> classifyOutcome value.Outcome (ReconcileProgram.observedTurn (publishTurnOf value))

    let private evidenceOf (turn: ReconciledTurn option) : ReconcileProgram.ReconcileEvidence =
        match turn with
        | None -> ReconcileProgram.ReconcileEvidence.NoTurn
        | Some value -> evidenceFromObserved value

    let private deliveryOf
        (shouldPublish: bool)
        (wake: ReconcileProgram.ReconcileWake)
        : ReconciledTurnDelivery option =
        match shouldPublish, wake with
        | true, _ -> Some ReconciledTurnDelivery.Observation
        | false, ReconcileProgram.ReconcileWake.IdleWake _
        | false, ReconcileProgram.ReconcileWake.IdleFailureWake _ -> Some ReconciledTurnDelivery.IdleRevisit
        | false, ReconcileProgram.ReconcileWake.RetryWake
        | false, ReconcileProgram.ReconcileWake.FailureWake _
        | false, ReconcileProgram.ReconcileWake.AbortWake -> None

    let private quiescenceOf (wake: ReconcileProgram.ReconcileWake) =
        match wake with
        | ReconcileProgram.ReconcileWake.IdleWake permit
        | ReconcileProgram.ReconcileWake.IdleFailureWake(permit, _, _, _, _) -> Some permit
        | ReconcileProgram.ReconcileWake.RetryWake
        | ReconcileProgram.ReconcileWake.FailureWake _
        | ReconcileProgram.ReconcileWake.AbortWake -> None

    let private materializeFailureWitness
        (wake: ReconcileProgram.ReconcileWake)
        (turn: ReconciledTurn option)
        : ReconciledTurn option =
        turn
        |> Option.map (fun observed ->
            let publish = publishTurnOf observed

            match ReconcileProgram.tryFailureWitness wake publish with
            | Some(failure, reason) when
                ReconcileProgram.failureWitnessMintsTerminal
                    failure
                    (not (CompletedTurnClassifier.formalContentUnusable observed.Parts))
                ->
                { observed with
                    Outcome = ReconcileProgram.TurnFailed reason
                    Observation = None }
            | _ -> observed)

    let private observeIfPresent
        (isCurrent: SessionId -> int -> bool)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (sessionId: SessionId)
        (generation: int)
        (lastSnapshot: SessionMessage list option)
        : Task =
        task {
            match lastSnapshot, isCurrent sessionId generation with
            | Some messages, true -> do! observeSnapshot sessionId messages
            | _ -> ()
        }

    let private maybeRecordMaps
        (shouldPublish: bool)
        (maps: ReconcileProgram.PublishMaps)
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (sessionId: SessionId)
        (generation: int)
        : unit =
        if shouldPublish && isCurrent sessionId generation then
            recordMaps sessionId maps

    let private publishResolvedTurn
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (shouldPublish: bool)
        (maps: ReconcileProgram.PublishMaps)
        (value: ReconcileProgram.PublishTurn)
        (turns: Map<string, ReconciledTurn>)
        (delivery: ReconciledTurnDelivery)
        : Task =
        task {
            match Map.tryFind (ReconcileProgram.consumeKey value) turns with
            | None -> logError "RECONCILE-PUBLISH" "publish token missing from observed turns"
            | Some reconciled ->
                do!
                    onTurn
                        { Turn = reconciled
                          Failure = ReconcileProgram.tryFailureWitness wake value |> Option.map fst
                          Quiescence = quiescenceOf wake
                          Delivery = delivery }

                maybeRecordMaps shouldPublish maps isCurrent recordMaps sessionId generation
        }

    let private publishPresentTurn
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (maps: ReconcileProgram.PublishMaps)
        (value: ReconcileProgram.PublishTurn)
        (turns: Map<string, ReconciledTurn>)
        (lastSnapshot: SessionMessage list option)
        : Task =
        task {
            let decision = ReconcileProgram.publishDecision maps value
            let delivery = deliveryOf decision.shouldPublish wake

            match delivery, isCurrent sessionId generation with
            | Some resolved, true ->
                do!
                    publishResolvedTurn
                        isCurrent
                        recordMaps
                        onTurn
                        sessionId
                        generation
                        wake
                        decision.shouldPublish
                        decision.maps
                        value
                        turns
                        resolved
            | _ -> ()

            do! observeIfPresent isCurrent observeSnapshot sessionId generation lastSnapshot
        }

    let private publishIfAllowed
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (maps: ReconcileProgram.PublishMaps)
        (turn: ReconcileProgram.PublishTurn option)
        (turns: Map<string, ReconciledTurn>)
        (lastSnapshot: SessionMessage list option)
        : Task =
        task {
            match turn with
            | None -> do! observeIfPresent isCurrent observeSnapshot sessionId generation lastSnapshot
            | Some value ->
                do!
                    publishPresentTurn
                        isCurrent
                        recordMaps
                        observeSnapshot
                        onTurn
                        sessionId
                        generation
                        wake
                        maps
                        value
                        turns
                        lastSnapshot
        }

    let private publishableOf (evidence: ReconcileProgram.ReconcileEvidence) : ReconcileProgram.PublishTurn option =
        match evidence with
        | ReconcileProgram.ReconcileEvidence.Terminal observed -> observed.PublishTurn
        | ReconcileProgram.ReconcileEvidence.Unknown(Some observed) -> observed.PublishTurn
        | ReconcileProgram.ReconcileEvidence.Provisional observed -> observed.PublishTurn
        | _ -> None

    let private observedTurnsOf (turn: ReconciledTurn option) : Map<string, ReconciledTurn> =
        match turn with
        | Some value -> Map.empty |> Map.add (ReconcileProgram.consumeKey (publishTurnOf value)) value
        | None -> Map.empty

    let private handleSnapshotSuccess
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (maps: ReconcileProgram.PublishMaps)
        (activeBinding: ActiveRunBinding)
        (messages: SessionMessage list)
        : Task =
        let turn =
            TurnReconcile.reconcile messages activeBinding |> materializeFailureWitness wake

        let evidence = evidenceOf turn
        let observedTurns = observedTurnsOf turn
        let decision = ReconcileProgram.decideStep wake evidence

        match decision with
        | ReconcileProgram.ReconcileDecision.Publish ->
            // Stable observation handoff only (rabbit §7).
            // Unknown under IdleWake publishes the observed turn as-is;
            // TurnWorkflow / InteractionRepair owns missing-final-report repair
            // (GLORY-070), gated on this pass's quiescence evidence.
            publishIfAllowed
                isCurrent
                recordMaps
                observeSnapshot
                onTurn
                sessionId
                generation
                wake
                maps
                (publishableOf evidence)
                observedTurns
                (Some messages)
        | ReconcileProgram.ReconcileDecision.StopPass ->
            observeIfPresent isCurrent observeSnapshot sessionId generation (Some messages)

    let private resumeFromSnapshot
        (isCurrent: SessionId -> int -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (maps: ReconcileProgram.PublishMaps)
        (activeBinding: ActiveRunBinding)
        (result: Result<SessionMessage list, string>)
        : Task =
        task {
            match isCurrent sessionId generation, result with
            | false, _ -> ()
            | true, Error error -> logError "RECONCILE-SNAPSHOT" (sprintf "snapshot failed: %s" (string error))
            | true, Ok messages ->
                do!
                    handleSnapshotSuccess
                        isCurrent
                        recordMaps
                        observeSnapshot
                        onTurn
                        sessionId
                        generation
                        wake
                        maps
                        activeBinding
                        messages
        }

    let private materializeActive
        (snapshot: ISessionSnapshotPort)
        (isCurrent: SessionId -> int -> bool)
        (isCleared: SessionId -> bool)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (sessionId: SessionId)
        (generation: int)
        (wake: ReconcileProgram.ReconcileWake)
        (maps: ReconcileProgram.PublishMaps)
        (activeBinding: ActiveRunBinding)
        : Task =
        task {
            if isCurrent sessionId generation && not (isCleared sessionId) then
                let! result = snapshot.GetMessages sessionId

                return!
                    resumeFromSnapshot
                        isCurrent
                        recordMaps
                        observeSnapshot
                        onTurn
                        sessionId
                        generation
                        wake
                        maps
                        activeBinding
                        result
        }

    let private observeIdleSnapshot
        (snapshot: ISessionSnapshotPort)
        (isCurrent: SessionId -> int -> bool)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (sessionId: SessionId)
        (generation: int)
        : Task =
        task {
            let! result = snapshot.GetMessages sessionId

            match isCurrent sessionId generation, result with
            | false, _ -> ()
            | true, Ok messages -> do! observeSnapshot sessionId messages
            | true, Error error -> logError "RECONCILE-SNAPSHOT" (sprintf "snapshot failed: %s" (string error))
        }

    let private runWithBinding
        (snapshot: ISessionSnapshotPort)
        (isCurrent: SessionId -> int -> bool)
        (isCleared: SessionId -> bool)
        (mapsFor: SessionId -> ReconcileProgram.PublishMaps)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (wake: ReconcileProgram.ReconcileWake)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (activeBinding: ActiveRunBinding option)
        (sessionId: SessionId)
        (generation: int)
        : Task =
        match activeBinding with
        | None ->
            // HOST-006: still read + observe when no active run.
            observeIdleSnapshot snapshot isCurrent observeSnapshot sessionId generation
        | Some bound ->
            materializeActive
                snapshot
                isCurrent
                isCleared
                recordMaps
                observeSnapshot
                onTurn
                sessionId
                generation
                wake
                (mapsFor sessionId)
                bound

    /// Run one reconcile pass for a session generation.
    /// HOST-006: no active run still observes snapshot.
    let run
        (snapshot: ISessionSnapshotPort)
        (isCurrent: SessionId -> int -> bool)
        (isCleared: SessionId -> bool)
        (mapsFor: SessionId -> ReconcileProgram.PublishMaps)
        (recordMaps: SessionId -> ReconcileProgram.PublishMaps -> unit)
        (wake: ReconcileProgram.ReconcileWake)
        (observeSnapshot: SessionId -> SessionMessage list -> Task)
        (onTurn: ReconciledTurnContext -> Task)
        (activeBinding: ActiveRunBinding option)
        (sessionId: SessionId)
        (generation: int)
        : Task =
        task {
            if not (isCurrent sessionId generation) || isCleared sessionId then
                return ()
            else
                return!
                    runWithBinding
                        snapshot
                        isCurrent
                        isCleared
                        mapsFor
                        recordMaps
                        wake
                        observeSnapshot
                        onTurn
                        activeBinding
                        sessionId
                        generation
        }
