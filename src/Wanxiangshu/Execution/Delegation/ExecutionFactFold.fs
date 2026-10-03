namespace Wanxiangshu.Execution.Delegation

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation

module ExecutionFactFold =

    let private canonicalByname byname targetAgent =
        if System.String.IsNullOrWhiteSpace byname then
            targetAgent
        else
            byname

    /// A handle line whose transition may be refused. Replaying a completion,
    /// abandon or retirement is absorbed (durable terminals make those
    /// idempotent); the three impossible transitions fail closed with the fact
    /// label the durable report names.
    let private handleOutcome
        (factName: string)
        (parentSessionId: SessionId)
        (handle: HandleId)
        (priorState: DelegationSessionState option)
        (terminalChild: SessionId option)
        (result: Result<AgentLinkageProjection, HandleTransitionRejection>)
        : Result<DelegationProjectionChange list, DelegationFoldRejection> =
        let priorHandles = priorState |> Option.bind (fun state -> state.Handles)

        let indexChange (handles: AgentLinkageProjection) =
            HandleProjection.tryFind handle handles
            |> Option.map (fun record -> IndexChildHandle(record.ChildSessionId, record))

        let terminalChanges =
            terminalChild
            |> Option.map (fun child -> TerminatedChildHandle(parentSessionId, child))
            |> Option.toList

        match result with
        | Ok updatedHandles ->
            let baseState = Option.defaultValue DelegationSessionState.empty priorState

            Ok
                [ ReplaceSessionState(
                      parentSessionId,
                      { baseState with
                          Handles = Some updatedHandles }
                  )
                  yield! Option.toList (indexChange updatedHandles)
                  yield! terminalChanges ]
        | Error AlreadyCompleted
        | Error AlreadyAbandoned
        | Error HandleIsRetired ->
            Ok
                [ yield! priorHandles |> Option.bind indexChange |> Option.toList
                  yield! terminalChanges ]
        | Error HandleIdentityConflict -> Error(HandleBindingConflict factName)
        | Error UnknownHandle -> Error(HandleNeverLinked factName)
        | Error NotCompleted -> Error(HandleCompletionMissing factName)
        | Error reason -> Error(WorkRejected(factName, reason))

    let private workProjectionChanges parentId prior (work: HandleWorkId) updated terminal =
        let record = HandleProjection.tryFind work.Handle updated |> Option.get
        let completed = HandleProjection.tryWork work updated |> Option.get

        let terminalChanges =
            if terminal then
                [ TerminatedChildWork(work, completed.LogicalRunId) ]
            else
                []

        [ ReplaceSessionState(parentId, { prior with Handles = Some updated })
          IndexChildHandle(work.ChildSessionId, record)
          yield! terminalChanges ]

    let private foldWork sessionState parentId (work: HandleWorkId) factName terminal transition =
        let prior =
            sessionState parentId |> Option.defaultValue DelegationSessionState.empty

        let handles = prior.Handles |> Option.defaultValue HandleProjection.empty

        match transition handles with
        | Error AlreadyCompleted
        | Error AlreadyAbandoned
        | Error HandleIsRetired -> Ok []
        | Error reason -> Error(WorkRejected(factName, reason))
        | Ok updated -> Ok(workProjectionChanges parentId prior work updated terminal)

    let private foldChildRunVoided parentId childSessionId (handles: AgentLinkageProjection) =
        let childWork =
            handles.Works
            |> Seq.tryFind (fun (KeyValue(key, _)) -> key.ChildSessionId = childSessionId)

        match childWork with
        | Some(KeyValue(work, record)) -> Ok [ TerminatedChildWork(work, record.LogicalRunId) ]
        | None -> Ok [ TerminatedChildHandle(parentId, childSessionId) ]

    let fold
        (sessionState: SessionId -> DelegationSessionState option)
        (fact: ExecutionFactCases)
        : Result<DelegationProjectionChange list, DelegationFoldRejection> =
        // ── execution handles ───────────────────────────────────────────────
        match fact with
        | ExecutionFactCases.HandleWorkCompleted p ->
            foldWork
                sessionState
                p.ParentSessionId
                p.Work
                "HandleWorkCompleted"
                true
                (HandleProjection.completeWork
                    p.Work
                    { Kind = p.Kind
                      CompletionRef = p.CompletionRef
                      CompletionDigest = p.CompletionDigest })
        | ExecutionFactCases.HandleWorkConsumed p ->
            foldWork
                sessionState
                p.ParentSessionId
                p.Work
                "HandleWorkConsumed"
                false
                (HandleProjection.consumeWork
                    p.Work
                    p.ConsumptionId
                    { Kind = p.Kind
                      CompletionRef = p.CompletionRef
                      CompletionDigest = p.CompletionDigest })
        | ExecutionFactCases.HandleWorkAbandoned p ->
            foldWork
                sessionState
                p.ParentSessionId
                p.Work
                "HandleWorkAbandoned"
                true
                (HandleProjection.abandonWork p.Work p.Reason)
        | ExecutionFactCases.ChildWorkVoided p ->
            foldWork sessionState p.ParentSessionId p.Work "ChildWorkVoided" true (HandleProjection.voidWork p.Work)
        | ExecutionFactCases.HandleLinked payload ->
            let byname = canonicalByname payload.Byname payload.TargetAgent
            let priorState = sessionState payload.ParentSessionId

            HandleProjection.replayLink
                payload.Handle
                payload.ChildSessionId
                payload.TargetAgent
                byname
                payload.CanonicalRole
                payload.Ownership
                (priorState
                 |> Option.bind (fun s -> s.Handles)
                 |> Option.defaultValue HandleProjection.empty)
            |> handleOutcome "HandleLinked" payload.ParentSessionId payload.Handle priorState None

        | ExecutionFactCases.HandleCompleted payload ->
            let priorState = sessionState payload.ParentSessionId

            let terminalChild =
                priorState
                |> Option.bind (fun s -> s.Handles)
                |> Option.bind (HandleProjection.tryFind payload.Handle)
                |> Option.map (fun record -> record.ChildSessionId)

            HandleProjection.complete
                payload.Handle
                { Kind = payload.Kind
                  CompletionRef = payload.CompletionRef
                  CompletionDigest = payload.CompletionDigest }
                (priorState
                 |> Option.bind (fun s -> s.Handles)
                 |> Option.defaultValue HandleProjection.empty)
            |> handleOutcome "HandleCompleted" payload.ParentSessionId payload.Handle priorState terminalChild

        | ExecutionFactCases.HandleRetired payload ->
            let priorState = sessionState payload.ParentSessionId

            let terminalChild =
                priorState
                |> Option.bind (fun s -> s.Handles)
                |> Option.bind (HandleProjection.tryFind payload.Handle)
                |> Option.map (fun record -> record.ChildSessionId)

            HandleProjection.retire
                payload.Handle
                (priorState
                 |> Option.bind (fun s -> s.Handles)
                 |> Option.defaultValue HandleProjection.empty)
            |> handleOutcome "HandleRetired" payload.ParentSessionId payload.Handle priorState terminalChild

        | ExecutionFactCases.HandleAbandoned payload ->
            let priorState = sessionState payload.ParentSessionId

            let terminalChild =
                priorState
                |> Option.bind (fun s -> s.Handles)
                |> Option.bind (HandleProjection.tryFind payload.Handle)
                |> Option.map (fun record -> record.ChildSessionId)

            HandleProjection.abandon
                payload.Handle
                payload.Reason
                (priorState
                 |> Option.bind (fun s -> s.Handles)
                 |> Option.defaultValue HandleProjection.empty)
            |> handleOutcome "HandleAbandoned" payload.ParentSessionId payload.Handle priorState terminalChild

        // Clean-break: false abort cell → Active only when ref/digest match.

        | ExecutionFactCases.ChildRunVoided payload ->
            // crash-reconciliation-018: voiding an orphaned run closes the
            // child's logical run itself. A bare TerminatedChildHandle would
            // keep the authority open whenever the parent holds an admitted
            // work entry for this child (its scopedChild branch defers closure
            // to work-level settlement), so the void routes through
            // TerminatedChildWork when a work entry exists. The handle and the
            // work entry both stay untouched — the interrupted run owes no
            // delivery, so horizon keeps showing the reusable binding.
            let priorState = sessionState payload.ParentSessionId

            let handles =
                priorState
                |> Option.bind (fun s -> s.Handles)
                |> Option.defaultValue HandleProjection.empty

            foldChildRunVoided payload.ParentSessionId payload.ChildSessionId handles

        | ExecutionFactCases.HandleFalseCompletionRejected payload ->
            let priorState = sessionState payload.ParentSessionId

            HandleProjection.rejectFalseCompletion
                payload.Handle
                payload.ExpectedCompletionRef
                payload.ExpectedCompletionDigest
                (priorState
                 |> Option.bind (fun s -> s.Handles)
                 |> Option.defaultValue HandleProjection.empty)
            |> handleOutcome "HandleFalseCompletionRejected" payload.ParentSessionId payload.Handle priorState None

        // Clean-break: retired false terminal report. Projection keeps original
        // Retired tombstone; replacement is linked by a separate HandleLinked.

        | ExecutionFactCases.HandleFalseTerminalReported _ -> Ok []

        // Clean-break: parent correction notice. No handle lifecycle change.

        | ExecutionFactCases.ParentJoinCorrectionRequested _ -> Ok []

        // HostTurnObserved is a durable observation inbox fact. CompletionReactor
        // (later batch) consumes it; LinkageProjection has no fold effect yet.

        | ExecutionFactCases.HostTurnObserved _ -> Ok []
