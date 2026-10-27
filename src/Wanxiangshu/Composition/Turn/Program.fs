namespace Wanxiangshu.Composition.Turn

open Wanxiangshu.Execution.Failure

open System
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider

/// Reconcile pure Domain: Evidence → Decision + publish seals.
/// Zero task, zero mutable, zero I/O (FLOW-001 / FLOW-004).
/// Workflow CE lives in Composition/Turn/Scheduler.fs.
module ReconcileProgram =

    // ── outcomes (Domain-owned; Application maps wire into these) ─────────────

    type TurnOutcome =
        | TurnInProgress
        | TurnNeedsContinuation of reason: string
        | TurnCompleted
        | TurnAborted of reason: string
        | TurnFailed of error: string

    /// Reconciliation-private. Not a publishable TurnOutcome case (HOST-004).
    /// finish=None stable snapshot observation; must not cross the publish boundary.
    type SnapshotObservation = | TurnUnknown

    /// Stable production identity carried from snapshot classification to publish.
    type PublishTurn =
        { SessionId: SessionId
          PhysicalUserMessageId: PhysicalUserMessageId
          ProviderRun: ProviderRunIdentity
          Outcome: TurnOutcome }

    /// A snapshot can be classified without a publishable turn in pure tests.
    /// Production classification always supplies `Some PublishTurn`.
    type ObservedTurn =
        { Outcome: TurnOutcome
          PublishTurn: PublishTurn option }

    // ── evidence / decision ──────────────────────────────────────────────────

    /// What started this reconcile pass (HOST-004 rev.3).
    ///
    /// An idle wake carries a fresh `SessionIdle` permit. A matching failure
    /// wake keeps that permit while retaining the failure witness. A failure
    /// alone grants no idle-derived continuation rights. FailureWake freezes the
    /// physical user identity observed when the
    /// session.error arrived; this is failure finality, not ProviderRun identity.
    /// The scheduler generation (single-flight fencing) is a different physical
    /// concept and must never be reused as the attempt serial.
    [<RequireQualifiedAccess>]
    type FailureWakeSource =
        | CoarseHostSignal
        | ExactAssistantProjection

    [<RequireQualifiedAccess>]
    type ReconcileWake =
        | IdleWake of QuiescencePermit
        | RetryWake
        | FailureWake of
            physicalUserMessageId: PhysicalUserMessageId option *
            failure: ExecutionFailure *
            diagnostic: string *
            source: FailureWakeSource
        | IdleFailureWake of QuiescencePermit * PhysicalUserMessageId * ExecutionFailure * string * FailureWakeSource
        /// HOST-004: operator abort is a typed wake category, not a failure.
        /// It never carries idle/repair rights: under it, Unknown / Provisional
        /// must never Publish a stable observation that business could turn into
        /// InteractionRepair / "#".
        | AbortWake

    let mergeWake
        (currentPhysicalUserMessageId: PhysicalUserMessageId option)
        (previous: ReconcileWake)
        (incoming: ReconcileWake)
        =
        match previous, incoming, currentPhysicalUserMessageId with
        | ReconcileWake.FailureWake(Some physical, failure, diagnostic, source), ReconcileWake.IdleWake permit, Some current
        | ReconcileWake.IdleWake permit, ReconcileWake.FailureWake(Some physical, failure, diagnostic, source), Some current
            when physical = current -> ReconcileWake.IdleFailureWake(permit, physical, failure, diagnostic, source)
        | ReconcileWake.IdleFailureWake(_, physical, failure, diagnostic, source), ReconcileWake.IdleWake permit, Some current
            when physical = current -> ReconcileWake.IdleFailureWake(permit, physical, failure, diagnostic, source)
        | ReconcileWake.IdleFailureWake(_, physical, _, _, _), ReconcileWake.RetryWake, Some current
            when physical = current -> previous
        | ReconcileWake.IdleFailureWake(permit, physical, failure, diagnostic, FailureWakeSource.ExactAssistantProjection),
          ReconcileWake.FailureWake(_, _, _, FailureWakeSource.CoarseHostSignal), Some current when physical = current ->
            ReconcileWake.IdleFailureWake(permit, physical, failure, diagnostic, FailureWakeSource.ExactAssistantProjection)
        | ReconcileWake.IdleFailureWake(permit, physical, _, _, _), ReconcileWake.FailureWake(Some failed, failure, diagnostic, source), Some current
            when physical = current && failed = current -> ReconcileWake.IdleFailureWake(permit, physical, failure, diagnostic, source)
        | ReconcileWake.FailureWake(_, _, _, FailureWakeSource.ExactAssistantProjection),
          ReconcileWake.FailureWake(_, _, _, FailureWakeSource.CoarseHostSignal),
          _ -> previous
        | ReconcileWake.FailureWake(Some failedPhysical, _, _, _),
          (ReconcileWake.IdleWake _ | ReconcileWake.RetryWake),
          currentPhysical when currentPhysical |> Option.forall ((=) failedPhysical) -> previous
        | _ -> incoming

    [<RequireQualifiedAccess>]
    type ReconcileEvidence =
        | SnapshotError of reason: string
        | NoTurn
        | Provisional of ObservedTurn
        | Unknown of ObservedTurn option
        | Terminal of ObservedTurn
        | SessionCleared

    /// Pure observation vocabulary (rabbit §7 / S3): Reconcile answers only
    /// whether an observation is stable enough to hand to business — never
    /// which repair prompt business should send.
    [<RequireQualifiedAccess>]
    type ReconcileDecision =
        | Publish
        | StopPass

    // ── pure classifiers ─────────────────────────────────────────────────────

    let outcomeOf (name: string) : TurnOutcome =
        match name with
        | "TurnCompleted" -> TurnCompleted
        | "TurnAborted" -> TurnAborted "aborted"
        | "TurnFailed" -> TurnFailed "failed"
        | "TurnInProgress" -> TurnInProgress
        | "TurnNeedsContinuation" -> TurnNeedsContinuation "needs-continuation"
        | "TurnUnknown" -> invalidArg "name" "TurnUnknown is SnapshotObservation, not a TurnOutcome"
        | other -> TurnFailed(sprintf "unknown-outcome:%s" other)

    let isTerminalOutcome (outcome: TurnOutcome) : bool =
        match outcome with
        | TurnCompleted
        | TurnAborted _
        | TurnFailed _ -> true
        | TurnInProgress
        | TurnNeedsContinuation _ -> false

    let tryFailureWitness (wake: ReconcileWake) (turn: PublishTurn) : (ExecutionFailure * string) option =
        match wake with
        | ReconcileWake.FailureWake(Some physical, failure, diagnostic, _)
        | ReconcileWake.IdleFailureWake(_, physical, failure, diagnostic, _) when
            physical = turn.PhysicalUserMessageId
            && (match turn.Outcome with
                | TurnCompleted
                | TurnAborted _ -> false
                | TurnFailed _
                | TurnInProgress
                | TurnNeedsContinuation _ -> true)
            ->
            Some(failure, diagnostic)
        | ReconcileWake.IdleWake _
        | ReconcileWake.RetryWake
        | ReconcileWake.FailureWake _
        | ReconcileWake.IdleFailureWake _
        | ReconcileWake.AbortWake -> None

    let tryFailureWitnessReason wake turn =
        tryFailureWitness wake turn |> Option.map snd

    /// provider-attempt-recovery-008: only a confirmed provider failure may mint a provider-failure
    /// terminal. An errored attempt whose formal content is unusable (empty /
    /// XML-only) belongs to bounded Interaction Repair instead.
    let failureWitnessMintsTerminal (failure: ExecutionFailure) (contentUsable: bool) : bool =
        match failure with
        | ExecutionFailure.ProviderTransient
        | ExecutionFailure.ProviderPermanent -> true
        | _ -> contentUsable

    let private unknownDecision (wake: ReconcileWake) =
        match wake with
        | ReconcileWake.IdleWake _
        | ReconcileWake.IdleFailureWake _ ->
            // Classic black hole: SessionIdle + assistant reasoning +
            // finish=None. Observation is stable —
            // Publish to business; never StopPass silently. Whether to
            // send missing-final-report is TurnWorkflow / InteractionRepair.
            ReconcileDecision.Publish
        | ReconcileWake.RetryWake
        | ReconcileWake.FailureWake _
        | ReconcileWake.AbortWake ->
            // No idle rights. Observation stability only, or an abort that
            // must not resurrect an idle-derived continuation; the next
            // real physical signal re-kicks, and a genuine TurnAborted
            // terminal publishes normally.
            ReconcileDecision.StopPass

    let private provisionalDecision (wake: ReconcileWake) =
        match wake with
        | ReconcileWake.IdleWake _
        | ReconcileWake.IdleFailureWake _ ->
            // Idle is the only wake that proves the exact current physical
            // execution is quiescent. A provisional/unknown assistant under
            // that witness belongs to business repair.
            ReconcileDecision.Publish
        | ReconcileWake.RetryWake
        | ReconcileWake.FailureWake _
        | ReconcileWake.AbortWake ->
            // Retry/abort status can race the public session projection. A
            // FailureWake is handled earlier only when its frozen physical id
            // matches this exact snapshot assistant. All remaining non-idle
            // provisional observations stay parked for a causal edge.
            ReconcileDecision.StopPass

    let private tokenAlreadySealed (maps: Map<string, string>) (key: string) (token: string) =
        match Map.tryFind key maps with
        | Some previous when previous = token -> true
        | _ -> false

    /// Reconcile decision for one snapshot observation. Production Scheduler
    /// supplies a single read per causal edge (host-boundary-005).
    ///
    /// 不变量（GLORY-070 / HOST-004 rev.3 / rabbit §7）：SessionIdle 被消费后只允许
    /// 产生一个稳定观测交接或明确 fail closed；带 idle evidence 的稳定 `Unknown`
    /// 只有在 `IdleWake`
    /// 下 `Publish` 给业务（TurnWorkflow / InteractionRepair 决定是否 repair）；
    /// `Retry` wake 只证明 provider lifecycle edge；`FailureWake` 还携带 signal 到达时冻结的
    /// physical user identity。只有该 physical 与 snapshot exact current assistant 一致时，
    /// Unknown / Provisional 才被收敛成 TurnFailed；否则仍 StopPass 等待 causal edge。
    /// 两者都不授予 idle repair 权。普通 Unknown / Provisional 只有 IdleWake 可 Publish；
    /// `SnapshotError` / `NoTurn` 同样 StopPass。
    let decideStep (wake: ReconcileWake) (evidence: ReconcileEvidence) : ReconcileDecision =
        let hasFailureWitness =
            match evidence with
            | ReconcileEvidence.Provisional observed
            | ReconcileEvidence.Unknown(Some observed)
            | ReconcileEvidence.Terminal observed ->
                observed.PublishTurn
                |> Option.bind (tryFailureWitnessReason wake)
                |> Option.isSome
            | ReconcileEvidence.SnapshotError _
            | ReconcileEvidence.NoTurn
            | ReconcileEvidence.Unknown None
            | ReconcileEvidence.SessionCleared -> false

        let isFailedTerminal =
            match evidence with
            | ReconcileEvidence.Terminal { Outcome = TurnFailed _ } -> true
            | _ -> false

        match hasFailureWitness, evidence with
        | true, (ReconcileEvidence.Provisional _ | ReconcileEvidence.Unknown _ | ReconcileEvidence.Terminal _) ->
            ReconcileDecision.Publish
        | false, ReconcileEvidence.Terminal _ when isFailedTerminal -> ReconcileDecision.StopPass
        | false, ReconcileEvidence.Terminal _ -> ReconcileDecision.Publish
        | false, (ReconcileEvidence.SnapshotError _ | ReconcileEvidence.NoTurn) -> ReconcileDecision.StopPass
        | false, ReconcileEvidence.Provisional _ -> provisionalDecision wake
        | false, ReconcileEvidence.Unknown _ -> unknownDecision wake
        | false, ReconcileEvidence.SessionCleared -> ReconcileDecision.StopPass
        | true, _ -> ReconcileDecision.StopPass

    let decisionName (decision: ReconcileDecision) : string =
        match decision with
        | ReconcileDecision.Publish -> "Publish"
        | ReconcileDecision.StopPass -> "StopPass"

    let consumeKey (turn: PublishTurn) : string =
        String.Concat(
            [| SessionId.value turn.SessionId
               "|"
               PhysicalUserMessageId.value turn.PhysicalUserMessageId
               "|"
               ProviderRunIdentity.value turn.ProviderRun |]
        )

    // ── publish maps (consumed = terminal seal; provisional = incomplete seal) ─
    // Class so Fable emits instance methods callable from domain.mjs tests.

    type PublishMaps(consumed: Map<string, string>, provisional: Map<string, string>) =
        member _.Consumed = consumed
        member _.Provisional = provisional

        member _.provisionalHas(turn: PublishTurn) : bool =
            let key = SessionId.value turn.SessionId
            let token = consumeKey turn

            match Map.tryFind key provisional with
            | Some previous when previous = token -> true
            | _ -> false

        member _.consumedHas(turn: PublishTurn) : bool =
            let key = SessionId.value turn.SessionId
            let token = consumeKey turn

            match Map.tryFind key consumed with
            | Some previous when previous = token -> true
            | _ -> false

    let publishMapsEmpty () : PublishMaps = PublishMaps(Map.empty, Map.empty)

    let publishDecision
        (maps: PublishMaps)
        (turn: PublishTurn)
        : {| shouldPublish: bool
             maps: PublishMaps |}
        =
        let key = SessionId.value turn.SessionId
        let token = consumeKey turn

        // HOST-004: TurnUnknown is SnapshotObservation — type-unreachable here.
        // IdleWake → Publish (stable Unknown handoff) lives in decideStep; business
        // repair is TurnWorkflow / InteractionRepair, not this seal layer.
        if isTerminalOutcome turn.Outcome && tokenAlreadySealed maps.Consumed key token then
            {| shouldPublish = false; maps = maps |}
        elif isTerminalOutcome turn.Outcome then
            {| shouldPublish = true
               maps = PublishMaps(Map.add key token maps.Consumed, Map.remove key maps.Provisional) |}
        elif tokenAlreadySealed maps.Provisional key token then
            {| shouldPublish = false; maps = maps |}
        else
            {| shouldPublish = true
               maps = PublishMaps(maps.Consumed, Map.add key token maps.Provisional) |}

    let clearProvisional (maps: PublishMaps) (sessionKey: string) : PublishMaps =
        PublishMaps(maps.Consumed, Map.remove sessionKey maps.Provisional)

    let turnFixture (session: string) (physical: string) (providerRun: string) (outcome: TurnOutcome) : PublishTurn =
        { SessionId = SessionId.create session
          PhysicalUserMessageId = PhysicalUserMessageId.create physical
          ProviderRun = ProviderRunIdentity.create providerRun
          Outcome = outcome }

    // ── evidence constructors (facade + tests) ───────────────────────────────

    let evidenceSnapshotError (reason: string) = ReconcileEvidence.SnapshotError reason
    let evidenceNoTurn () = ReconcileEvidence.NoTurn

    let observedTurn (turn: PublishTurn) : ObservedTurn =
        { Outcome = turn.Outcome
          PublishTurn = Some turn }

    let private pureObservation (outcome: TurnOutcome) : ObservedTurn =
        { Outcome = outcome
          PublishTurn = None }

    let evidenceProvisional (outcome: TurnOutcome) =
        ReconcileEvidence.Provisional(pureObservation outcome)

    let evidenceUnknown () = ReconcileEvidence.Unknown None

    let evidenceTerminal (outcome: TurnOutcome) =
        ReconcileEvidence.Terminal(pureObservation outcome)

    let evidenceSessionCleared () = ReconcileEvidence.SessionCleared
