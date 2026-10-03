namespace Wanxiangshu.Sphinx.V2.Core

open System

/// The one pure fold for every v2 inquiry.
///
/// WHAT[sphinx-v2-016]: the same event input always produces the same state. Replay
/// never contacts the network, never draws a fresh random number, and never re-runs a
/// model. That property is what lets the semantic projection claim Host-independence.
///
/// WHAT[sphinx-v2-018]: physical bindings are folded into the state because recovery
/// needs them. Semantic projection excludes them; the complete durable state
/// fingerprint deliberately includes them.

module Reducer =

    /// One error constructor typed to CoreError, so no call site can accidentally widen
    /// the fold's error channel to some other record with the same shape.
    let private coreError (code: string) (message: string) : CoreError = { Code = code; Message = message }

    let private isFinite (value: float) =
        not (Double.IsNaN value) && not (Double.IsInfinity value)

    let private goalValidate (goal: GoalSpec) : Result<GoalSpec, CoreError> =
        Goal.tryCreate goal
        |> Result.mapError (fun fault ->
            { Code = fault.Code
              Message = fault.Message })

    let private emptyState (origin: InquiryEvent) (body: InquiryCreatedBody) : Result<InquiryState, CoreError> =
        if origin.Revision <> Revision.origin then
            Error(coreError "invalid-origin" "inquiry creation must be revision zero")
        elif origin.Parent.IsSome then
            Error(coreError "invalid-origin" "inquiry creation must not have a parent")
        else
            Budget.validateSpecs body.ResourceSpecs
            |> Result.mapError (fun fault ->
                { Code = fault.Code
                  Message = fault.Message }
                : CoreError)
            |> Result.bind (fun () ->
                goalValidate body.Goal
                |> Result.map (fun goal ->
                    { Id = origin.InquiryId
                      ApiVersion = "2"
                      Revision = Revision.origin
                      EventHead = Some origin.Id
                      Goal = goal
                      ResourceSpecs = body.ResourceSpecs
                      RenderReserve = body.RenderReserve
                      ConfigHash = body.ConfigHash
                      ProfileRef = body.ProfileRef
                      Graph = Map.empty
                      Edges = Map.empty
                      Certificates = Map.empty
                      Work = Map.empty
                      Reservations = Map.empty
                      SettledUsage = Map.empty
                      SettledMoneyMinor = 0L
                      Overruns = []
                      Observations = Map.empty
                      Interpretations = Map.empty
                      Rounds = Map.empty
                      Decisions = Map.empty
                      Answer = None
                      CommandReceipts = Map.empty
                      PhysicalBindings = Map.empty
                      Status = InquiryStatus.Active }))

    /// The chain check is the fold's only ordering rule. A revision that skips or
    /// repeats is not a retry to be tolerated; it is a different history.
    let private verifyChain (state: InquiryState) (event: InquiryEvent) : Result<unit, CoreError> =
        if event.InquiryId <> state.Id then
            Error(coreError "inquiry-mismatch" "event belongs to another inquiry")
        elif event.Revision <> Revision.next state.Revision then
            Error(coreError "revision-conflict" "event revision is not the next revision")
        elif event.Parent <> state.EventHead then
            Error(coreError "parent-conflict" "event parent is not the current head")
        else
            Ok()

    /// A terminal inquiry still accepts cost audits and cancel requests: a provider
    /// can bill after the answer, and a controller can still ask to stop. Nothing else
    /// may write a new business fact.
    let private isLateFact (body: InquiryEventBody) : bool =
        match body with
        | InquiryEventBody.UsageSettled _
        | InquiryEventBody.UsageOverrunRecorded _
        | InquiryEventBody.HostTerminalRecorded _
        | InquiryEventBody.CancelRequested _ -> true
        | _ -> false

    let private admitBusinessEvent (state: InquiryState) (body: InquiryEventBody) : Result<unit, CoreError> =
        let terminal = InquiryState.isTerminal state.Status
        let businessFact = not (isLateFact body)
        let rejected = terminal && businessFact

        match rejected with
        | true -> Error(coreError "inquiry-terminal" "inquiry is terminal and accepts no new business event")
        | false -> Ok()


    let private applyGoalAmended (state: InquiryState) (goal: GoalSpec) : Result<InquiryState, CoreError> =
        if goal.GoalId <> state.Goal.GoalId then
            Error(coreError "goal-mismatch" "amended goal must keep the same goal id")
        elif goal.Revision <= state.Goal.Revision then
            Error(coreError "stale-goal" "amended goal revision must advance")
        else
            goalValidate goal |> Result.map (fun amended -> { state with Goal = amended })

    let private applyRoundOpened (state: InquiryState) (body: RoundOpenedBody) : Result<InquiryState, CoreError> =
        if state.Rounds |> Map.containsKey body.RoundId then
            Error(coreError "duplicate-round" "round already exists")
        else
            Ok
                { state with
                    Rounds =
                        state.Rounds
                        |> Map.add
                            body.RoundId
                            { RoundId = body.RoundId
                              ScopeId = body.ScopeId
                              ExpectedWork = body.ExpectedWork |> Set.ofList
                              ReceivedWork = Set.empty
                              TerminalWork = Set.empty
                              Closed = false
                              Outcome = None } }

    let private closeRound
        (state: InquiryState)
        (roundId: RoundId)
        (outcome: string)
        : Result<InquiryState, CoreError> =
        match state.Rounds |> Map.tryFind roundId with
        | Some record ->
            Ok
                { state with
                    Rounds =
                        state.Rounds
                        |> Map.add
                            roundId
                            { record with
                                Closed = true
                                Outcome = Some outcome } }
        | None -> Error(coreError "unknown-round" (sprintf "round %s is not open" (RoundId.value roundId)))

    let private releaseReservation
        (state: InquiryState)
        (workId: WorkId)
        (attempt: Attempt)
        : Result<InquiryState, CoreError> =
        let key = InquiryState.reservationKey { WorkId = workId; Attempt = attempt }

        if state.Reservations |> Map.containsKey key then
            Ok
                { state with
                    Reservations = state.Reservations |> Map.remove key }
        else
            Ok state

    /// Why a work id cannot be planned, or None when it can. Reporting the reason as a
    /// value keeps the caller's control flow flat.
    let private duplicateWork (alreadyPlanned: bool) (plannedTwice: bool) (id: string) : CoreError option =
        let alreadyText () = sprintf "work %s already exists" id

        let twiceText () =
            sprintf "work %s is planned twice in one batch" id

        let duplicateReason () =
            match alreadyPlanned, plannedTwice with
            | true, _ -> alreadyText ()
            | false, true -> twiceText ()
            | false, false -> ""

        let duplicated = alreadyPlanned || plannedTwice

        let refused () =
            Some(coreError "duplicate-work" (duplicateReason ()))

        let admitted () = None

        match duplicated with
        | true -> refused ()
        | false -> admitted ()

    /// Plans one work spec into the table, or reports why it cannot be planned.
    let private planOneWork
        (workError: WorkError -> CoreError)
        (work: Map<WorkId, WorkItem>)
        (spec: WorkSpec)
        : Result<Map<WorkId, WorkItem>, CoreError> =
        let planned =
            work
            |> Map.add
                spec.Id
                { Spec = spec
                  State = WorkState.Planned }

        Work.validateSpec spec
        |> Result.mapError workError
        |> Result.map (fun () -> planned)

    let private applyWorkPlanned (state: InquiryState) (specs: WorkSpec list) : Result<InquiryState, CoreError> =
        let workError (fault: WorkError) : CoreError =
            { Code = fault.Code
              Message = fault.Message }

        let rec loop
            (work: Map<WorkId, WorkItem>)
            (remaining: WorkSpec list)
            : Result<Map<WorkId, WorkItem>, CoreError> =
            match remaining with
            | [] -> Ok work
            | spec :: rest ->
                let id = WorkId.value spec.Id

                let alreadyPlanned = state.Work |> Map.containsKey spec.Id

                let plannedTwice = work |> Map.containsKey spec.Id

                let refused () =
                    duplicateWork alreadyPlanned plannedTwice id |> Option.map Error

                let planned () =
                    planOneWork workError work spec |> Result.bind (fun next -> loop next rest)

                refused () |> Option.defaultWith (fun _ -> planned ())

        loop state.Work specs |> Result.map (fun work -> { state with Work = work })

    let private currentWork (state: InquiryState) (workId: WorkId) : Result<WorkItem, CoreError> =
        match state.Work |> Map.tryFind workId with
        | Some item -> Ok item
        | None -> Error(coreError "unknown-work" (sprintf "work %s is not planned" (WorkId.value workId)))

    /// The fence is checked against the work's own attempt, not against a caller-supplied
    /// attempt number alone. A late result from a superseded attempt carries an older
    /// fence and must not land as the current result.
    let private attemptMatches (item: WorkItem) (spec: WorkSpec) (fromState: string) : Result<unit, CoreError> =
        if item.Spec.Attempt <> spec.Attempt then
            Error(coreError "attempt-mismatch" (sprintf "work attempt does not match: %s" fromState))
        elif item.Spec.Fence <> spec.Fence then
            Error(coreError "stale-fence" (sprintf "fence does not match the work attempt: %s" fromState))
        else
            Ok()

    /// Running is the one transition that must name a real physical reference: without
    /// it there is nothing to reconcile against on recovery.
    let private physicalRefPresent (next: WorkState) : Result<unit, CoreError> =
        match next with
        | WorkState.Running(_, physicalRef) when String.IsNullOrWhiteSpace physicalRef ->
            Error(coreError "missing-physical-ref" "running work requires a real physical reference")
        | _ -> Ok()

    let private fenceMatches
        (item: WorkItem)
        (spec: WorkSpec)
        (fromState: string)
        (next: WorkState)
        : Result<unit, CoreError> =
        attemptMatches item spec fromState
        |> Result.bind (fun () -> physicalRefPresent next)

    /// Dependencies must actually have succeeded. Being part of the same dispatch batch
    /// is not completion, which is why the check reads real state rather than the
    /// current batch membership.
    let private succeededAttempt (item: WorkItem) : Attempt option =
        match item.State with
        | WorkState.Succeeded attempt -> Some attempt
        | _ -> None

    let private dependenciesSucceeded (state: InquiryState) (work: WorkSpec) : bool =
        work.Dependencies
        |> Set.forall (fun dependency ->
            state.Work
            |> Map.tryFind dependency
            |> Option.bind succeededAttempt
            |> Option.isSome)

    /// The state machine itself, kept separate from the spec checks so each half stays
    /// readable on its own.
    let private legalStateChange
        (state: InquiryState)
        (spec: WorkSpec)
        (item: WorkItem)
        (next: WorkState)
        : Result<unit, CoreError> =
        let becomeReady = dependenciesSucceeded state spec

        match item.State, next with
        | WorkState.Planned, WorkState.Ready when not becomeReady ->
            Error(coreError "dependency-unsatisfied" "work dependencies are not complete")
        | WorkState.Planned, WorkState.Superseded successor
        | WorkState.Ready, WorkState.Superseded successor -> currentWork state successor |> Result.map (fun _ -> ())
        | WorkState.Leased _, WorkState.Running _
        | WorkState.Running _, WorkState.Running _ -> Ok()
        | WorkState.Succeeded _, WorkState.Succeeded _ ->
            Error(coreError "duplicate-observation" "an attempt already accepted an observation")
        | _ when Work.isTerminal item.State -> Error(coreError "terminal-work" "work is already terminal")
        | _ -> Ok()

    let private legalTransition
        (state: InquiryState)
        (spec: WorkSpec)
        (fromState: string)
        (next: WorkState)
        (item: WorkItem)
        : Result<unit, CoreError> =
        let samePurpose =
            item.Spec.RoundId = spec.RoundId
            && item.Spec.PlanId = spec.PlanId
            && item.Spec.Producer = spec.Producer
            && item.Spec.Capability = spec.Capability
            && item.Spec.Input = spec.Input
            && item.Spec.OutputSchema = spec.OutputSchema
            && item.Spec.Dependencies = spec.Dependencies
            && item.Spec.ConflictKeys = spec.ConflictKeys

        let retry =
            match item.State, next with
            | WorkState.Failed _, WorkState.Ready
            | WorkState.Cancelled _, WorkState.Ready -> true
            | _ -> false

        let specIsImmutable = not samePurpose

        let attemptAdvanceWrong =
            (retry && spec.Attempt <> Attempt.next item.Spec.Attempt)
            || (not retry && spec.Attempt <> item.Spec.Attempt)

        if specIsImmutable then
            Error(coreError "spec-mismatch" "work spec is immutable within its lifecycle")
        elif attemptAdvanceWrong then
            Error(
                coreError
                    "attempt-mismatch"
                    (if retry then
                         "retry must advance the attempt by exactly one"
                     else
                         "work attempt does not match the planned attempt")
            )
        else
            legalStateChange state spec item next

    let private applyWorkTransition
        (state: InquiryState)
        (body: WorkAttemptTransitionedBody)
        : Result<InquiryState, CoreError> =
        currentWork state body.WorkId
        |> Result.bind (fun item ->
            if body.FromState <> Work.stateName item.State then
                Error(
                    coreError
                        "stale-work-state"
                        (sprintf "expected %s but work is %s" body.FromState (Work.stateName item.State))
                )
            else
                let spec =
                    { item.Spec with
                        Attempt = body.Attempt
                        Fence = body.Fence
                        PhysicalRef = body.PhysicalRef }

                fenceMatches item spec body.FromState body.NextState
                |> Result.bind (fun () -> legalTransition state spec body.FromState body.NextState item)
                |> Result.map (fun () ->
                    { state with
                        Work = state.Work |> Map.add body.WorkId { Spec = spec; State = body.NextState } }))

    /// Certificate slots are patched, not replaced. Two independent slots on the same
    /// candidate merge in canonical order; two patches to the same slot with the same
    /// base conflict, and neither silently wins.
    ///
    /// The old reducer replaced the whole certificate keyed by node, which made the
    /// second writer erase the first. That is exactly the failure this shape prevents.
    /// Certificate slots are patched, not replaced. Two independent slots on the same
    /// candidate merge in canonical order; two patches to the same slot from the same
    /// base conflict, and neither silently wins.
    ///
    /// The old reducer replaced the whole certificate keyed by node, which let the
    /// second writer erase the first. That is the failure this shape prevents.
    let private slotConflict (message: string) : CoreError =
        coreError "certificate-conflict" message

    let private applyCertificateSlots
        (state: InquiryState)
        (patches: CertificateSlotPatch list)
        : Result<InquiryState, CoreError> =
        let certificateError (fault: CertificateError) : CoreError =
            { Code = fault.Code
              Message = fault.Message }

        let key (patch: CertificateSlotPatch) : string =
            InquiryState.certificateKey patch.TargetRef patch.ValueSpaceId patch.ScopeId patch.SemanticsModelRef

        let slotName (patch: CertificateSlotPatch) = patch.Slot.Slot

        /// Nodes already in the graph.
        let existingNodes =
            state.Graph
            |> Map.toList
            |> List.map (fun (nodeId, _) -> NodeId.value nodeId)
            |> Set.ofList

        /// A certificate may only be attached to a node that exists. The old reducer
        /// accepted any target and let a later graph patch paper over it; here a missing
        /// endpoint is a refusal (WHAT[sphinx-v2-007]).
        let targetExists (patch: CertificateSlotPatch) : bool =
            existingNodes |> Set.contains patch.TargetRef

        /// Different slots on the same candidate are independent and merge. The same
        /// slot must agree on its base revision, and an exact re-delivery is idempotent.
        let mergeSlot
            (existing: CertificateSlotPatch list)
            (patch: CertificateSlotPatch)
            : Result<CertificateSlotPatch list, CoreError> =
            let found =
                existing |> List.tryFind (fun candidate -> candidate.Slot.Slot = slotName patch)

            match found with
            | Some current when current.Slot.Revision <> patch.ExpectedSlotRevision ->
                Error(
                    slotConflict (
                        sprintf
                            "certificate slot %s expects revision %d but the slot is at %d"
                            (slotName patch)
                            (Revision.value patch.ExpectedSlotRevision)
                            (Revision.value current.Slot.Revision)
                    )
                )
            | Some current when current.Slot.Revision = patch.Slot.Revision && current.Slot = patch.Slot -> Ok existing
            | Some _ ->
                Error(
                    slotConflict (
                        sprintf "certificate slot %s has a conflicting patch at the same base revision" (slotName patch)
                    )
                )
            | None -> Ok(existing @ [ patch ])

        let rec loop
            (slots: Map<string, CertificateSlotPatch list>)
            (remaining: CertificateSlotPatch list)
            : Result<Map<string, CertificateSlotPatch list>, CoreError> =
            match remaining with
            | [] -> Ok slots
            | patch :: _ when not (targetExists patch) ->
                Error(coreError "unknown-node" (sprintf "certificate target %s is not in the graph" patch.TargetRef))
            | patch :: rest ->
                Certificate.validateSlot patch.Slot
                |> Result.mapError certificateError
                |> Result.bind (fun () ->
                    let slotKey = key patch
                    let existing = slots |> Map.tryFind slotKey |> Option.defaultValue []

                    mergeSlot existing patch
                    |> Result.bind (fun merged -> loop (slots |> Map.add slotKey merged) rest))

        loop state.Certificates patches
        |> Result.map (fun certificates ->
            { state with
                Certificates = certificates })

    let private applyBudgetReserved (state: InquiryState) (body: BudgetReservedBody) : Result<InquiryState, CoreError> =
        let budgetError (fault: BudgetError) : CoreError =
            { Code = fault.Code
              Message = fault.Message }

        let workKey =
            { WorkId = body.Reservation.WorkId
              Attempt = body.Reservation.Attempt }

        let key = InquiryState.reservationKey workKey

        if state.Reservations |> Map.containsKey key then
            Error(coreError "duplicate-reservation" (sprintf "work %s attempt is already reserved" key))
        else
            let outstanding =
                state.Reservations
                |> Map.toList
                |> List.map (fun (_, pair) -> snd pair)
                |> Budget.mergeReserved

            Budget.tryReserve state.ResourceSpecs state.SettledUsage outstanding body.Reservation
            |> Result.mapError budgetError
            |> Result.map (fun projected ->
                { state with
                    Reservations = state.Reservations |> Map.add key (workKey, projected) })

    /// Settlement books the real usage and releases only the part that can no longer be
    /// consumed. When a provider reports nothing, the reservation stays booked.
    /// The resources a settled usage leaves outstanding on its own reservation.
    let private remainingAfter (usage: SettledUsage) (outstanding: Map<string, float>) : Map<string, float> =
        (outstanding, usage.Resources)
        ||> Map.fold (fun acc resource amount ->
            let left = (acc |> Map.tryFind resource |> Option.defaultValue 0.0) - amount
            let depleted = left <= 0.0

            match depleted with
            | true -> Map.remove resource acc
            | false -> Map.add resource left acc)

    /// Booking a reservation against a settled usage: release what can no longer be
    /// consumed, keep the rest as an outstanding reservation.
    let private releaseSettled
        (state: InquiryState)
        (key: string)
        (outstanding: Map<string, float>)
        (usage: SettledUsage)
        (settle: InquiryState -> Result<InquiryState, CoreError>)
        (recordOverrun: InquiryState -> InquiryState)
        : Result<InquiryState, CoreError> =
        let workKey =
            { WorkId = usage.WorkId
              Attempt = usage.Attempt }

        let withoutReservation = state.Reservations |> Map.remove key
        let stillLeft = remainingAfter usage outstanding
        let fullySpent = Map.isEmpty stillLeft

        let stillReserved =
            withoutReservation
            |> fun untouched ->
                if fullySpent then
                    untouched
                else
                    untouched |> Map.add key (workKey, stillLeft)

        settle
            { state with
                Reservations = stillReserved }
        |> Result.map recordOverrun

    let private applyUsageSettled (state: InquiryState) (usage: SettledUsage) : Result<InquiryState, CoreError> =
        let key =
            InquiryState.reservationKey
                { WorkId = usage.WorkId
                  Attempt = usage.Attempt }

        let overrunFact =
            { WorkId = usage.WorkId
              Attempt = usage.Attempt
              Resources = usage.Resources }

        // An unresolved usage keeps the whole reservation booked: the provider told us
        // nothing, and writing zero would claim the work was free.
        let recordOverrun (target: InquiryState) =
            if usage.Overrun then
                { target with
                    Overruns = target.Overruns @ [ overrunFact ] }
            else
                target

        let settle (target: InquiryState) =
            Ok
                { target with
                    SettledUsage = usage.Resources }

        // An audit with no outstanding reservation is still bookable.
        let bookAudit (target: InquiryState) = settle (recordOverrun target)

        let unresolved = usage.UsageUnresolved

        match state.Reservations |> Map.tryFind key with
        | _ when unresolved -> settle (recordOverrun state)
        | Some(_, outstanding) -> releaseSettled state key outstanding usage settle recordOverrun
        | None -> bookAudit state

    /// A result is admissible only when it belongs to this attempt, carries this
    /// attempt's fence, and is not a second delivery of an already-accepted result.
    let private resultAdmissible
        (state: InquiryState)
        (item: WorkItem)
        (body: ResultAcceptedBody)
        : Result<unit, CoreError> =
        if item.Spec.Attempt <> body.Attempt then
            Error(coreError "attempt-mismatch" "result attempt does not match the work")
        elif item.Spec.Fence <> body.Fence then
            Error(coreError "stale-fence" "result fence does not match the work attempt")
        elif state.Observations |> Map.containsKey (ObservationId.value body.ObservationId) then
            Error(coreError "duplicate-observation" "this observation is already accepted")
        else
            Ok()

    let private applyResultAccepted (state: InquiryState) (body: ResultAcceptedBody) : Result<InquiryState, CoreError> =
        currentWork state body.WorkId
        |> Result.bind (fun item ->
            resultAdmissible state item body
            |> Result.bind (fun () ->
                let accepted =
                    { state with
                        Observations = state.Observations |> Map.add (ObservationId.value body.ObservationId) body }

                // A result arriving after the work already succeeded is a duplicate
                // delivery of a physical retry: bookable, not a second observation.
                match item.State with
                | WorkState.Succeeded _ -> Ok accepted
                | WorkState.Running _ ->
                    Ok
                        { accepted with
                            Work =
                                accepted.Work
                                |> Map.add
                                    body.WorkId
                                    { Spec = item.Spec
                                      State = WorkState.Succeeded body.Attempt } }
                | _ ->
                    Error(coreError "work-not-running" (sprintf "work %s is not running" (WorkId.value body.WorkId)))))

    let private applyInterpretation
        (state: InquiryState)
        (body: InterpretationPendingBody)
        : Result<InquiryState, CoreError> =
        let id = ObservationId.value body.ObservationId

        if state.Interpretations |> Map.containsKey id then
            Ok state
        else
            Ok
                { state with
                    Interpretations =
                        state.Interpretations
                        |> Map.add
                            id
                            { ObservationId = body.ObservationId
                              WorkId = body.WorkId
                              Attempt = body.Attempt
                              InterpretationId = None
                              PluginRef = None
                              Status = "pending"
                              Reason = None } }

    /// A graph patch is applied, not merely noted. Core checks producer identity,
    /// endpoint existence and revision sanity; it never judges whether the relation is
    /// warranted (WHAT[sphinx-v2-006]).
    let private applyGraphPatched (state: InquiryState) (body: GraphPatchedBody) : Result<InquiryState, CoreError> =
        let graphError (fault: GraphError) : CoreError =
            { Code = fault.Code
              Message = fault.Message }

        match String.IsNullOrWhiteSpace body.PluginRef with
        | true -> Error(coreError "invalid-patch" "graph patch must name its producing plugin")
        | false ->
            Graph.applyPatch state.Graph state.Edges body.Patch
            |> Result.mapError graphError
            |> Result.map (fun (nodes, edges) ->
                { state with
                    Graph = nodes
                    Edges = edges })

    let private applyAnswer (state: InquiryState) (body: AnswerCommittedBody) : Result<InquiryState, CoreError> =
        match state.Answer with
        | Some existing when existing = body -> Ok state
        | Some _ -> Error(coreError "answer-conflict" "inquiry answer is immutable")
        | None ->
            currentWork state body.RenderWorkId
            |> Result.map (fun _ ->
                { state with
                    Answer = Some body
                    Status = InquiryStatus.StopReached body.StopReason })

    let private resumeActive (state: InquiryState) : Result<InquiryState, CoreError> =
        let terminal = InquiryState.isTerminal state.Status

        match terminal with
        | true -> Error(coreError "inquiry-terminal" "a terminal inquiry cannot return to active")
        | false ->
            Ok
                { state with
                    Status = InquiryStatus.Active }

    let private applyStatus (state: InquiryState) (status: string) (reason: string) : Result<InquiryState, CoreError> =
        match status with
        | "suspended" ->
            Ok
                { state with
                    Status = InquiryStatus.Suspended reason }
        | "failed" ->
            Ok
                { state with
                    Status = InquiryStatus.Failed reason }
        | "cancelled" ->
            Ok
                { state with
                    Status = InquiryStatus.Cancelled reason }
        | "cancelling" ->
            Ok
                { state with
                    Status = InquiryStatus.Cancelling }
        | "active" -> resumeActive state
        | "input-required" ->
            Ok
                { state with
                    Status = InquiryStatus.InputRequired reason }
        | _ -> Error(coreError "unknown-status" (sprintf "unknown inquiry status: %s" status))

    let private cancellingUnlessTerminal status =
        if InquiryState.isTerminal status then
            status
        else
            InquiryStatus.Cancelling

    /// Every event dispatches to exactly one handler, and each handler is a named
    /// function so the dispatch stays a flat table instead of a nesting of conditions.
    let private dispatchHandler (state: InquiryState) (body: InquiryEventBody) : Result<InquiryState, CoreError> =
        match body with
        | InquiryEventBody.GoalAmended goal -> applyGoalAmended state goal
        | InquiryEventBody.SnapshotRegistered _ -> Ok state
        | InquiryEventBody.DecisionScopeOpened _ -> Ok state
        | InquiryEventBody.RoundOpened round -> applyRoundOpened state round
        | InquiryEventBody.RoundClosed(roundId, outcome) -> closeRound state roundId outcome
        | InquiryEventBody.WorkPlanned specs -> applyWorkPlanned state specs
        | InquiryEventBody.WorkAttemptTransitioned transition -> applyWorkTransition state transition
        | InquiryEventBody.CertificateSlotsPatched patchesBody -> applyCertificateSlots state patchesBody.Patches
        | InquiryEventBody.CertificateInvalidated _ -> Ok state
        | InquiryEventBody.BudgetReserved reserved -> applyBudgetReserved state reserved
        | InquiryEventBody.UsageSettled usage -> applyUsageSettled state usage.Usage
        | InquiryEventBody.UsageOverrunRecorded usage -> applyUsageSettled state usage.Usage
        | InquiryEventBody.ReservationReleased(workId, attempt) -> releaseReservation state workId attempt
        | InquiryEventBody.DispatchRequested _ -> Ok state
        | InquiryEventBody.DispatchReceiptRecorded _ -> Ok state
        | InquiryEventBody.HostTerminalRecorded _ -> Ok state
        | InquiryEventBody.ResultAccepted acceptedBody -> applyResultAccepted state acceptedBody
        | InquiryEventBody.InterpretationPending pending -> applyInterpretation state pending
        | InquiryEventBody.InterpretationApplied _ -> Ok state
        | InquiryEventBody.InterpretationFailed _ -> Ok state
        | InquiryEventBody.GraphPatched patched -> applyGraphPatched state patched
        | InquiryEventBody.DecisionRecorded _ -> Ok state
        | InquiryEventBody.AnswerPrepared _ -> Ok state
        | InquiryEventBody.AnswerCommitted committed -> applyAnswer state committed
        | InquiryEventBody.CancelRequested _ ->
            // A late request may be booked, but never resurrects a terminal inquiry.
            Ok
                { state with
                    Status = cancellingUnlessTerminal state.Status }
        | InquiryEventBody.InquiryCancelled reason ->
            Ok
                { state with
                    Status = InquiryStatus.Cancelled reason }
        | InquiryEventBody.InquirySuspended reason ->
            Ok
                { state with
                    Status = InquiryStatus.Suspended reason }
        | InquiryEventBody.InquiryFailed reason ->
            Ok
                { state with
                    Status = InquiryStatus.Failed reason }
        | InquiryEventBody.InquiryStatusChanged statusBody -> applyStatus state statusBody.Status statusBody.Reason
        | InquiryEventBody.InquiryCreated _ -> Error(coreError "duplicate-inquiry" "inquiry is already created")

    /// A continuation of an existing history. Its own decisions live in `dispatchHandler`.
    let private applyContinuation
        (current: InquiryState)
        (event: InquiryEvent)
        (body: InquiryEventBody)
        : Result<InquiryState, CoreError> =
        admitBusinessEvent current body
        |> Result.bind (fun () -> verifyChain current event)
        |> Result.bind (fun () -> dispatchHandler current body)
        |> Result.map (fun next ->
            { next with
                Revision = event.Revision
                EventHead = Some event.Id })

    let apply (state: InquiryState option) (event: InquiryEvent) : Result<InquiryState, CoreError> =
        let body = event.Body

        match Option.isSome state, body with
        | true, InquiryEventBody.InquiryCreated _ -> Error(coreError "duplicate-inquiry" "inquiry is already created")
        | true, _ -> applyContinuation (Option.get state) event body
        | false, InquiryEventBody.InquiryCreated created -> emptyState event created
        | false, _ -> Error(coreError "missing-inquiry" "first event must create the inquiry")

    let private transitionBase (prior: InquiryState option) (batch: TransitionBatch) : Result<unit, CoreError> =
        if batch.SchemaVersion <> "2" then
            Error(coreError "unsupported-transition" "strict transitions require Sphinx API version 2")
        elif
            String.IsNullOrWhiteSpace batch.CommandId
            || String.IsNullOrWhiteSpace batch.CommandFingerprint
        then
            Error(coreError "invalid-command" "transition requires a command identity and content fingerprint")
        elif List.isEmpty batch.Events then
            Error(coreError "empty-batch" "transition must carry at least one body")
        else
            match prior with
            | None when
                batch.PreviousHead.IsSome
                || batch.PreviousRevision <> Revision.origin
                || batch.Revision <> Revision.origin
                ->
                Error(coreError "invalid-origin" "creation has no parent and uses revision zero")
            | None -> Ok()
            | Some state when state.Id <> batch.InquiryId ->
                Error(coreError "inquiry-mismatch" "transition belongs to another inquiry")
            | Some state when state.EventHead <> batch.PreviousHead ->
                Error(coreError "parent-conflict" "transition parent does not name its base state")
            | Some state when
                state.Revision <> batch.PreviousRevision
                || Revision.value state.Revision = Int64.MaxValue
                || batch.Revision <> Revision.next state.Revision
                ->
                Error(
                    coreError
                        "revision-conflict"
                        "one atomic transition must advance its parent's revision exactly once"
                )
            | Some state when Map.containsKey batch.CommandId state.CommandReceipts ->
                Error(
                    coreError
                        "command-already-applied"
                        "a committed command must be replayed through admission, not applied again"
                )
            | Some _ -> Ok()

    /// All bodies share the envelope identity and one revision. Intermediate values
    /// are private; the receipt and head are published only after every body succeeds.
    let applyTransition
        (digest: string -> string)
        (eventId: EventId)
        (prior: InquiryState option)
        (batch: TransitionBatch)
        : Result<InquiryState, CoreError> =
        let step carried (index, body) =
            carried
            |> Result.bind (fun state ->
                match state with
                | None ->
                    let origin =
                        { Id = eventId
                          InquiryId = batch.InquiryId
                          Revision = batch.Revision
                          Parent = batch.PreviousHead
                          BatchIndex = index
                          Body = body }

                    apply None origin |> Result.map Some
                | Some current ->
                    admitBusinessEvent current body
                    |> Result.bind (fun () -> dispatchHandler current body)
                    |> Result.map Some)

        transitionBase prior batch
        |> Result.bind (fun () -> batch.Events |> List.indexed |> List.fold step (Ok prior))
        |> Result.bind (function
            | None -> Error(coreError "empty-batch" "transition produced no state")
            | Some folded ->
                let next =
                    { folded with
                        Revision = batch.Revision
                        EventHead = Some eventId
                        CommandReceipts =
                            Map.add
                                batch.CommandId
                                { Fingerprint = batch.CommandFingerprint
                                  Revision = batch.Revision
                                  EventId = eventId }
                                folded.CommandReceipts }

                match batch.PostStateFingerprint with
                | Some expected when expected <> Representation.fingerprint digest next ->
                    Error(
                        coreError
                            "post-state-mismatch"
                            "transition fingerprint does not match the complete materialized state"
                    )
                | Some _
                | None -> Ok next)

    /// Fold a standalone typed-event history. Durable TransitionBatch ingress uses
    /// applyTransition above, not this per-event revision convenience.
    let foldBatch (events: InquiryEvent list) : Result<InquiryState, CoreError> =
        let emptyBatch () =
            Error(coreError "empty-batch" "transition batch must carry at least one event")

        let applyAll (carried: Result<InquiryState option, CoreError>) (event: InquiryEvent) =
            carried |> Result.bind (fun state -> apply state event |> Result.map Some)

        match events with
        | [] -> emptyBatch ()
        | _ ->
            let folded = List.fold applyAll (Ok None) events

            let emptyResult () =
                Error(coreError "empty-batch" "transition batch produced no state")

            folded
            |> function
                | Ok(Some state) -> Ok state
                | Ok None -> emptyResult ()
                | Error fault -> Error fault


    let fold (events: InquiryEvent list) : Result<InquiryState, CoreError> =
        let initial: Result<InquiryState option, CoreError> = Ok None

        let applyAll (state: Result<InquiryState option, CoreError>) (event: InquiryEvent) =
            state |> Result.bind (fun carried -> apply carried event |> Result.map Some)

        match List.fold applyAll initial events with
        | Ok(Some state) -> Ok state
        | Ok None -> Error(coreError "empty-history" "inquiry has no events")
        | Error fault -> Error fault
