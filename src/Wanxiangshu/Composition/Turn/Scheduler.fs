namespace Wanxiangshu.Composition.Turn

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Wanxiangshu.Composition.Durable
open Fable.Core.JsInterop
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.HostSession

/// Direct-CE reconcile scheduler (FLOW-001 / PR4).
/// Owns queue / generation / single-flight / clear-session runtime state.
/// Pass body lives in ReconcilePass — Scheduler only drains and dispatches.
module Reconciler =

    [<RequireQualifiedAccess>]
    type private Dispatch =
        | Start of generation: int
        | Enqueued
        | Stopped

    [<RequireQualifiedAccess>]
    type private ProjectionPassDecision =
        | Settled of edgeEpoch: int64
        | ConsumeEdgeAndQueue of edgeEpoch: int64
        | Park of consumedEpoch: int64
        | Ignore

    [<Emit("console.error($0, $1)")>]
    let private logError (prefix: string) (message: string) : unit = jsNative

    type Scheduler
        (
            snapshot: ISessionSnapshotPort,
            binding: TurnBinding.Store,
            onTurn: ReconciledTurnContext -> Task,
            ?onDeleted: SessionId -> unit,
            ?projection: (SessionId -> AgentProjectionSet option),
            ?onSnapshot: SessionId -> SessionMessage list -> Task,
            ?durableUnavailable: unit -> bool
        ) as this =

        let gate = obj ()
        // When a signal arrives while a drain is active, the work is queued
        // here; releaseAfterPass checks it to re-drain after the current pass.
        // Same single-flight model as runningDrains (global task count) but
        // scoped per session.
        /// DSL-cross-callback-proof: physical single-flight — pending drain signal count, not semantic continuation
        // DSL-MUTABLE: resource — per-session single-flight admission queue.
        let queued = Dictionary<string, int>()
        // (HasFlight guard). startOrEnqueue checks active.ContainsKey to decide
        // whether to start a new drain task or enqueue. Only one drain task per
        // session at a time — the per-session equivalent of runningDrains.
        /// DSL-cross-callback-proof: physical single-flight — live drain ownership by session
        // DSL-MUTABLE: resource — per-session single-flight admission latch.
        let active = Dictionary<string, int>()
        // Set by ClearSession, cleared by BindUserMessage/BindActiveRun.
        // RunPass polls isCleared to skip drains for cleared sessions.
        /// DSL-cross-callback-proof: physical cancellation-token — deletion invalidates in-flight drain work
        // DSL-MUTABLE: resource — per-session cleared-session flag.
        let cleared = HashSet<string>()
        // store. ClearSession bumps the generation to invalidate in-flight
        // drains; isCurrent (polled deep inside ReconcilePass.run) compares the
        // generation captured at drain start against the current value here and
        // aborts stale drains. The generation value itself already flows as a CE
        // recursion parameter through Drain/RunPass/DrainAfterPass — this
        // Dictionary is the external invalidation authority that ClearSession
        // mutates, not a drain-pass program counter. Without it, ClearSession
        // would have no mechanism to cancel running drains.
        /// DSL-cross-callback-proof: physical cancellation-token — generation invalidates stale snapshot work
        // DSL-MUTABLE: resource — per-session cooperative cancellation token store.
        let generations = Dictionary<string, int>()
        /// DSL-cross-callback-proof: physical resource — last published Host projection cache for re-drain
        // DSL-MUTABLE: resource — per-session published reconcile maps cache.
        // recordMaps stores publish output; mapsFor retrieves it for re-drain.
        let published = Dictionary<string, ReconcileProgram.PublishMaps>()
        // DSL-MUTABLE: resource — shutdown admission latch.
        let mutable accepting = true
        // DSL-MUTABLE: resource — count of already-started physical drain tasks.
        let mutable runningDrains = 0
        // DSL-MUTABLE: single-flight — shared waiter for scheduler shutdown drain.
        let mutable stopWaiter: TaskCompletionSource<unit> option = None
        // Never consumed: a drain-resume re-run needs the same wake, and a newer
        // signal simply overwrites it. A session with no recorded wake defaults
        // to RetryWake (no idle rights — the safe side).
        /// DSL-cross-callback-proof: physical resource — last Host wake observation replayed to a drain
        // DSL-MUTABLE: resource — per-session last-dispatch wake.
        let wakes = Dictionary<string, ReconcileProgram.ReconcileWake>()
        // host-boundary-005: exact terminal message.updated is a projection
        // visibility edge, never a business HostSignal. Keep one monotonic edge
        // version per session/current physical user so a pass can park without
        // losing an edge that races the snapshot read.
        /// DSL-cross-callback-proof: physical resource — monotonic Host projection visibility edge
        // DSL-MUTABLE: resource — latest projection-change edge per session.
        let projectionEdges = Dictionary<string, struct (string * int64)>()
        // Last projection edge already spent to authorize a snapshot read for
        // this physical user. This makes reads edge-counted rather than
        // time/budget-counted: one edge can cause at most one extra read.
        /// DSL-cross-callback-proof: physical resource — edge-consumption watermark, not workflow position
        // DSL-MUTABLE: resource — consumed projection edge per session.
        let projectionConsumed = Dictionary<string, struct (string * int64)>()
        /// DSL-cross-callback-proof: physical waiter — snapshot read parked on an exact projection edge
        // DSL-MUTABLE: resource — parked reconcile occasion per session.
        let projectionWaits = Dictionary<string, struct (string * int64)>()
        let resolveProjection = defaultArg projection (fun _ -> None)
        let onDeleted = defaultArg onDeleted ignore
        let isDurableUnavailable = defaultArg durableUnavailable (fun () -> false)

        let observeSnapshot =
            defaultArg onSnapshot (fun _ _ -> AsyncSupport.completedTask ())

        let isCleared (sessionId: SessionId) =
            lock gate (fun () -> cleared.Contains(SessionId.value sessionId))

        let currentGeneration (sessionId: SessionId) =
            lock gate (fun () ->
                match generations.TryGetValue(SessionId.value sessionId) with
                | true, value -> value
                | false, _ -> 0)

        let isCurrent (sessionId: SessionId) (generation: int) =
            currentGeneration sessionId = generation

        let startOrEnqueue (key: string) (generation: int) =
            if active.ContainsKey key then
                Dispatch.Enqueued
            else
                active.[key] <- generation
                runningDrains <- runningDrains + 1
                Dispatch.Start generation

        let closeAdmission () =
            accepting <- false
            queued.Clear()

        let admittedWakeFor (sessionId: SessionId) (key: string) wake =
            match wakes.TryGetValue key with
            | true, previous -> ReconcileProgram.mergeWake (binding.TryPhysicalUserMessage sessionId) previous wake
            | false, _ -> wake

        let dispatch (sessionId: SessionId) (wake: ReconcileProgram.ReconcileWake) =
            lock gate (fun () ->
                if not accepting || isDurableUnavailable () then
                    closeAdmission ()
                    Dispatch.Stopped
                else
                    let key = SessionId.value sessionId
                    let generation = currentGeneration sessionId
                    let admittedWake = admittedWakeFor sessionId key wake

                    projectionWaits.Remove(key) |> ignore
                    wakes.[key] <- admittedWake
                    queued.[key] <- generation
                    startOrEnqueue key generation)

        let projectionSettledForWake (wake: ReconcileProgram.ReconcileWake) (turn: ReconciledTurn option) =
            match turn, wake with
            | None, _ -> false
            | Some _, ReconcileProgram.ReconcileWake.IdleWake _
            | Some _, ReconcileProgram.ReconcileWake.IdleFailureWake _ -> true
            | Some observed, ReconcileProgram.ReconcileWake.FailureWake(Some physical, _, _, _) when
                observed.PhysicalUserMessageId = physical
                ->
                true
            | Some observed,
              (ReconcileProgram.ReconcileWake.RetryWake | ReconcileProgram.ReconcileWake.FailureWake _ | ReconcileProgram.ReconcileWake.AbortWake) ->
                Option.isNone observed.Observation
                && ReconcileProgram.isTerminalOutcome observed.Outcome

        let projectionEpochFor
            (source: Dictionary<string, struct (string * int64)>)
            (key: string)
            (physicalKey: string)
            =
            match source.TryGetValue key with
            | true, struct (storedPhysical, epoch) when storedPhysical = physicalKey -> epoch
            | _ -> 0L

        let decideProjectionPass settled current edgeEpoch consumedEpoch =
            if settled then
                ProjectionPassDecision.Settled edgeEpoch
            elif not current then
                ProjectionPassDecision.Ignore
            elif edgeEpoch > consumedEpoch then
                ProjectionPassDecision.ConsumeEdgeAndQueue edgeEpoch
            else
                ProjectionPassDecision.Park consumedEpoch

        let settleProjectionPass
            (sessionId: SessionId)
            (generation: int)
            (physical: PhysicalUserMessageId)
            (settled: bool)
            =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                let physicalKey = PhysicalUserMessageId.value physical
                let edgeEpoch = projectionEpochFor projectionEdges key physicalKey
                let consumedEpoch = projectionEpochFor projectionConsumed key physicalKey

                let decision =
                    decideProjectionPass settled (isCurrent sessionId generation) edgeEpoch consumedEpoch

                match decision with
                | ProjectionPassDecision.Settled epoch ->
                    projectionConsumed.[key] <- struct (physicalKey, epoch)
                    projectionWaits.Remove(key) |> ignore
                | ProjectionPassDecision.ConsumeEdgeAndQueue epoch ->
                    // The pass was driven by a coarse signal while at least
                    // one exact projection edge remained unconsumed, OR an
                    // edge raced this read. Spend the newest edge once and
                    // queue exactly one more read. If that read still cannot
                    // see the assistant, it parks until a genuinely newer edge.
                    projectionConsumed.[key] <- struct (physicalKey, epoch)
                    projectionWaits.Remove(key) |> ignore
                    queued.[key] <- generation
                | ProjectionPassDecision.Park epoch -> projectionWaits.[key] <- struct (physicalKey, epoch)
                | ProjectionPassDecision.Ignore -> ())

        let projectionWaitMatches key physicalKey =
            match projectionWaits.TryGetValue key with
            | true, struct (waitingPhysical, _) when waitingPhysical = physicalKey -> true
            | _ -> false

        let armProjectionPass (sessionId: SessionId) (generation: int) (physical: PhysicalUserMessageId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                let physicalKey = PhysicalUserMessageId.value physical
                let edgeEpoch = projectionEpochFor projectionEdges key physicalKey
                let consumedEpoch = projectionEpochFor projectionConsumed key physicalKey
                let hasUnconsumedEdge = edgeEpoch > consumedEpoch
                let nextConsumed = if hasUnconsumedEdge then edgeEpoch else consumedEpoch

                projectionWaits.[key] <- struct (physicalKey, nextConsumed)

                if hasUnconsumedEdge then
                    projectionConsumed.[key] <- struct (physicalKey, edgeEpoch)
                    queued.[key] <- generation)

        let recordProjectionEdge (sessionId: SessionId) physicalKey =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                let admitted = accepting && not (isDurableUnavailable ())
                let nextEpoch = projectionEpochFor projectionEdges key physicalKey + 1L
                let waiting = projectionWaitMatches key physicalKey

                match admitted, waiting with
                | false, _ -> false
                | true, false ->
                    projectionEdges.[key] <- struct (physicalKey, nextEpoch)
                    false
                | true, true ->
                    projectionEdges.[key] <- struct (physicalKey, nextEpoch)
                    // This read is now owned by exactly this edge. Mark it
                    // consumed before queueing so the pass cannot spin on the
                    // same event if projection is still not visible.
                    projectionConsumed.[key] <- struct (physicalKey, nextEpoch)
                    projectionWaits.Remove(key) |> ignore
                    true)

        let invalidateProjectionWait (sessionId: SessionId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId
                projectionWaits.Remove(key) |> ignore
                projectionEdges.Remove(key) |> ignore
                projectionConsumed.Remove(key) |> ignore)

        let finishDrain () =
            lock gate (fun () ->
                runningDrains <- runningDrains - 1

                if not accepting && runningDrains = 0 then
                    stopWaiter
                    |> Option.iter (fun waiter -> AsyncSupport.trySetResult waiter () |> ignore))

        let currentWake (sessionId: SessionId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId

                match wakes.TryGetValue key with
                | true, wake -> wake
                | false, _ -> ReconcileProgram.ReconcileWake.RetryWake)

        let takeQueuedWork (sessionId: SessionId) (generation: int) =
            let key = SessionId.value sessionId

            let isActive =
                match active.TryGetValue key with
                | true, activeGeneration -> activeGeneration = generation
                | false, _ -> false

            let isQueued =
                match queued.TryGetValue key with
                | true, queuedGeneration -> queuedGeneration = generation
                | false, _ -> false

            if isCurrent sessionId generation && isActive && isQueued then
                queued.Remove(key) |> ignore
                true
            else
                false

        let takeWork (sessionId: SessionId) (generation: int) =
            lock gate (fun () ->
                if isDurableUnavailable () then
                    closeAdmission ()
                    false
                else
                    takeQueuedWork sessionId generation)

        let releaseActive key generation =
            match active.TryGetValue key with
            | true, activeGeneration when activeGeneration = generation -> active.Remove(key) |> ignore
            | _ -> ()

        let releaseAvailableAfterPass (sessionId: SessionId) (generation: int) =
            let key = SessionId.value sessionId
            let activeState = active.TryGetValue key
            let queuedState = queued.TryGetValue key

            match activeState, queuedState with
            | (true, activeGeneration), (true, queuedGeneration) when
                activeGeneration = generation
                && queuedGeneration = generation
                && isCurrent sessionId generation
                ->
                true
            | (true, activeGeneration), _ when activeGeneration = generation ->
                active.Remove(key) |> ignore
                false
            | _ -> false

        let releaseAfterPass (sessionId: SessionId) (generation: int) =
            lock gate (fun () ->
                if isDurableUnavailable () then
                    closeAdmission ()
                    releaseActive (SessionId.value sessionId) generation
                    false
                else
                    releaseAvailableAfterPass sessionId generation)

        let mapsFor (sessionId: SessionId) =
            lock gate (fun () ->
                let key = SessionId.value sessionId

                match published.TryGetValue key with
                | true, maps -> maps
                | false, _ -> ReconcileProgram.publishMapsEmpty ())

        let recordMaps (sessionId: SessionId) (maps: ReconcileProgram.PublishMaps) =
            lock gate (fun () -> published.[SessionId.value sessionId] <- maps)

        member private _.RunPass(sessionId: SessionId, generation: int) : Task =
            task {
                if
                    isDurableUnavailable ()
                    || not (isCurrent sessionId generation)
                    || isCleared sessionId
                then
                    return ()
                else
                    let wake = currentWake sessionId

                    let activeBinding =
                        binding.ActiveRunBinding(sessionId, ?projection = resolveProjection sessionId)

                    let currentPhysical =
                        activeBinding |> Option.bind (fun bound -> bound.PhysicalUserMessageId)

                    let isCurrentSnapshot observedSession observedGeneration =
                        isCurrent observedSession observedGeneration
                        && binding.TryPhysicalUserMessage observedSession = currentPhysical

                    currentPhysical |> Option.iter (armProjectionPass sessionId generation)

                    let observeProjectionSnapshot observedSession messages =
                        let settled =
                            match activeBinding with
                            | None -> true
                            | Some bound -> TurnReconcile.reconcile messages bound |> projectionSettledForWake wake

                        currentPhysical
                        |> Option.iter (fun physical -> settleProjectionPass sessionId generation physical settled)

                        observeSnapshot observedSession messages

                    do!
                        ReconcilePass.run
                            snapshot
                            isCurrentSnapshot
                            isCleared
                            mapsFor
                            recordMaps
                            wake
                            observeProjectionSnapshot
                            onTurn
                            activeBinding
                            sessionId
                            generation
            }

        member private this.DrainAfterPass(sessionId: SessionId, generation: int) : Task =
            task {
                if isCurrent sessionId generation then
                    return! this.Drain(sessionId, generation)
                else
                    return ()
            }

        member private this.Drain(sessionId: SessionId, generation: int) : Task =
            task {
                if takeWork sessionId generation then
                    do! this.RunPass(sessionId, generation)
                    return! this.DrainAfterPass(sessionId, generation)
                else
                    return ()
            }

        member private this.Run(sessionId: SessionId, generation: int) : Task =
            task {
                try
                    do! this.Drain(sessionId, generation)
                with error ->
                    logError "RECONCILE-SCHEDULER" error.Message

                if releaseAfterPass sessionId generation then
                    return! this.Run(sessionId, generation)
                else
                    return ()
            }

        member private this.RunTracked(sessionId: SessionId, generation: int) : Task =
            task {
                try
                    do! this.Run(sessionId, generation)
                finally
                    finishDrain ()
            }

        member _.Kick(sessionId: SessionId, wake: ReconcileProgram.ReconcileWake) : unit =
            match dispatch sessionId wake with
            | Dispatch.Start generation -> this.RunTracked(sessionId, generation) |> ignore
            | Dispatch.Enqueued
            | Dispatch.Stopped -> ()

        member private _.DrainWaitTask() : Task =
            match stopWaiter with
            | Some waiter -> waiter.Task :> Task
            | None ->
                let waiter =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                stopWaiter <- Some waiter
                waiter.Task :> Task

        member this.StopAndDrain() : Task =
            lock gate (fun () ->
                closeAdmission ()

                if runningDrains = 0 then
                    Task.FromResult(()) :> Task
                else
                    this.DrainWaitTask())

        member _.SignalIdle(sessionId: SessionId, permit: QuiescencePermit) : unit =
            this.Kick(sessionId, ReconcileProgram.ReconcileWake.IdleWake permit)

        /// host-boundary-001/005: terminal assistant message.updated is an
        /// infrastructure-only projection edge. It can only wake an already
        /// parked coarse-signal occasion for the exact current physical user;
        /// it never creates or changes business terminal semantics.
        member _.NotifyProjectionChanged(sessionId: SessionId, physical: PhysicalUserMessageId) : unit =
            let physicalKey = PhysicalUserMessageId.value physical

            let matchesCurrentPhysical =
                binding.TryPhysicalUserMessage(sessionId)
                |> Option.exists (fun current -> PhysicalUserMessageId.value current = physicalKey)

            let shouldKick =
                matchesCurrentPhysical && recordProjectionEdge sessionId physicalKey

            if shouldKick then
                this.Kick(sessionId, currentWake sessionId)

        member this.Signal(signal: HostSignal) : unit =
            match signal with
            | SessionIdle sessionId -> this.Kick(sessionId, ReconcileProgram.ReconcileWake.RetryWake)
            | ProviderFailure failure ->
                binding.TryPhysicalUserMessage failure.SessionId
                |> Option.iter (fun physicalUserMessageId ->
                    let wake =
                        ReconcileProgram.ReconcileWake.FailureWake(
                            Some physicalUserMessageId,
                            failure.Failure,
                            failure.Diagnostic,
                            ReconcileProgram.FailureWakeSource.CoarseHostSignal
                        )

                    this.Kick(failure.SessionId, wake))
            | ProviderRetry retry -> this.Kick(retry.SessionId, ReconcileProgram.ReconcileWake.RetryWake)
            | SessionDeleted(sessionId, _) -> this.ClearSession(sessionId)
            // HOST-002/004: an operator abort is a typed wake, not a failure.
            // AbortWake holds no idle rights (quiescence was already revoked via
            // RevokeCurrentAttempt), and decideStep refuses to Publish Unknown /
            // Provisional under it so business cannot mint InteractionRepair.
            // The genuine TurnAborted terminal publishes normally; Unknown /
            // Provisional StopPass instead of resurrecting an idle-derived
            // continuation.
            | AttemptAborted failure -> this.Kick(failure.SessionId, ReconcileProgram.ReconcileWake.AbortWake)

        member _.BindUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId, ?agentRole: Role) =
            lock gate (fun () -> cleared.Remove(SessionId.value sessionId) |> ignore)
            invalidateProjectionWait sessionId
            binding.BindUserMessage(sessionId, physical, ?agentRole = agentRole)

        member _.BindContinuationUserMessage(sessionId: SessionId, physical: PhysicalUserMessageId) =
            invalidateProjectionWait sessionId
            binding.BindContinuationUserMessage(sessionId, physical)

        member _.BindPhysicalUserMaterial(sessionId: SessionId, physical: PhysicalUserMessageId) =
            invalidateProjectionWait sessionId
            binding.BindPhysicalUserMaterial(sessionId, physical)

        member _.BindActiveRun(value: ActiveRunBinding) =
            lock gate (fun () -> cleared.Remove(SessionId.value value.SessionId) |> ignore)
            invalidateProjectionWait value.SessionId
            binding.BindActiveRun(value)

        member _.TryPhysicalUserMessage(sessionId: SessionId) =
            binding.TryPhysicalUserMessage(sessionId)

        member _.ClearSession(sessionId: SessionId) : unit =
            lock gate (fun () ->
                let key = SessionId.value sessionId

                let generation =
                    match generations.TryGetValue key with
                    | true, value -> value + 1
                    | false, _ -> 1

                generations.[key] <- generation
                cleared.Add(key) |> ignore
                queued.Remove(key) |> ignore
                active.Remove(key) |> ignore
                published.Remove(key) |> ignore
                wakes.Remove(key) |> ignore
                projectionWaits.Remove(key) |> ignore
                projectionEdges.Remove(key) |> ignore
                projectionConsumed.Remove(key) |> ignore)

            binding.ClearSession(sessionId)
            onDeleted sessionId
