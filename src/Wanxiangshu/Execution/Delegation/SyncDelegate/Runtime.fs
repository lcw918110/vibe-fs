namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Execution.Failure
open Wanxiangshu.Enforcer.Guidance
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Collections.Generic
open System.Threading.Tasks
open Fable.Core
open Fable.Core.JsInterop
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Host
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.OpenCode

/// Host-observable exact identity for a reusable SyncDelegate child. The title
/// is compared as a whole; escaped fields make distinct scope/role/agent tuples
/// unambiguous even when an identity contains title delimiters.
module internal SyncDelegatePhysicalIdentity =
    let title (scope: ReuseScopeId) (role: SyncDelegateRole) (agentName: string) =
        sprintf
            "wanxiangshu:sync-delegate:v1:scope=%s:role=%s:agent=%s"
            (Uri.EscapeDataString(ReuseScopeId.value scope))
            (Uri.EscapeDataString(SyncDelegate.roleLabel role))
            (Uri.EscapeDataString agentName)

/// Retry-decorator plug for dedicated delegate children (delegation-023): the caller
/// observes only the decorator's verdict, never a single transient attempt
/// failure. `Ok unit` keeps the invocation pending (a fresh attempt was admitted
/// or the episode was superseded); `Error reason` folds it as terminal.
type SyncDelegateRetryPort =
    { Retry: ReconciledTurn -> Wanxiangshu.Execution.Failure.ExecutionFailure -> string -> Task<Result<unit, string>> }

/// Job-owned helpers for the delegation-031 settle path. Module scope keeps the
/// member body flat while still seeing store/race primitives.
module internal SyncDelegateInternals =
    /// The response belongs to the accepted terminal, not the reusable session
    /// or its historical WorkRecord. Reasoning and tool material are excluded.
    let captureResponse (call: SyncDelegateCall) (turn: ReconciledTurn) =
        let text = CompletedTurnClassifier.partsText turn.Parts

        for invocation in call.Invocations do
            invocation.CaptureResponse |> Option.iter (fun capture -> capture text)

    let settleCompletedFromParts
        (noteDelegateIfRole: SyncDelegateCall -> SessionId -> string -> unit)
        (store: SyncDelegateCallStore)
        (call: SyncDelegateCall)
        (turn: ReconciledTurn)
        : bool =
        match CompletedTurnClassifier.partsSessionText turn.Parts with
        | record when not (System.String.IsNullOrWhiteSpace record) ->
            captureResponse call turn
            noteDelegateIfRole call turn.SessionId record
            AsyncSupport.trySetResult call.Answer (Ok record) |> ignore
            true
        | _ ->
            store.FailCall(call, "EXEC-031: Completed without bounded WorkRecord")
            true

/// EXEC-026 / EXEC-031: reusable SyncDelegate CE (Acquire → GetOrCreate → Send →
/// ordinary Completion → bounded WorkRecord). No return tool / dual-await.
///
/// Composition seam: call state lives in SyncDelegateCallStore, the Invoke CE
/// lives in SyncDelegateWorkflow, wait descriptors in SyncDelegateWait.
type SyncDelegateRuntime
    (
        sessions: ISessionHostPort,
        awaitWorkRecord: DiagnosticWait -> Task<Result<string, string>> -> Task<Result<string, string>>,
        awaitInvocation:
            DiagnosticWait
                -> Task<Result<SyncDelegateInvocationResult, string>>
                -> Task<Result<SyncDelegateInvocationResult, string>>,
        dispatcher: PromptDispatcher.Runtime,
        journal: AgentJournal,
        attached: IAttachedSessionPort,
        onDelegateReady: SessionId -> string -> unit,
        quiescence: ISessionQuiescenceGate,
        workRecordFor: SessionId -> XTraceRange -> ProviderRunIdentity -> Task<string option>,
        handoff: ReusableHandoffPort,
        retryPort: SyncDelegateRetryPort,
        ?toolMapForRole: Role -> Map<string, bool>,
        ?workspaceDirectory: string,
        /// Casebook draft hooks (wired from SpikePlugin → CasebookLifecycle; compile-order seam).
        ?onDelegatePrompt: string -> string -> unit,
        ?onDelegateAnswer: string -> string -> unit,
        ?onDelegateCleanup: string -> unit
    ) =
    let store = SyncDelegateCallStore()
    let directory = workspaceDirectory
    let retry = retryPort.Retry
    let noteDelegatePrompt = defaultArg onDelegatePrompt (fun _ _ -> ())
    let noteDelegateAnswer = defaultArg onDelegateAnswer (fun _ _ -> ())
    let cleanupDelegateDraft = defaultArg onDelegateCleanup (fun _ -> ())
    let projectWorkRecord = workRecordFor

    let sessionKey (sessionId: SessionId) = SessionId.value sessionId

    let scopeKey (scope: ReuseScopeId) = ReuseScopeId.value scope

    let canonicalRole =
        function
        | SyncDelegateRole.Inspector -> Role.Inspector
        | SyncDelegateRole.Coder -> Role.Coder
        | SyncDelegateRole.Engineer -> Role.Engineer

    // EXEC-031: SyncDelegate uses ordinary WorkMain tools — no Return permission.
    let toolMap role =
        match toolMapForRole with
        | Some resolve -> resolve role
        | None -> Map.empty

    let managedChildObservation
        (scope: ReuseScopeId)
        (role: SyncDelegateRole)
        (agentName: string)
        (children: OpenCodeChildInfo list)
        =
        let expectedTitle = SyncDelegatePhysicalIdentity.title scope role agentName

        let matching =
            children
            |> List.filter (fun child -> child.Agent = Some agentName && child.Title = Some expectedTitle)

        match matching with
        | [] -> AttachedChildObservation.Missing
        | [ child ] -> AttachedChildObservation.Matching child.SessionId
        | conflicts ->
            conflicts
            |> List.map (fun child -> child.SessionId)
            |> AttachedChildObservation.Conflicting

    let observeChild (owner: SessionId) (scope: ReuseScopeId) (role: SyncDelegateRole) (agentName: string) =
        task {
            let physicalOwner = sessions.FamilyRootOf owner

            match! sessions.ListChildren physicalOwner with
            | Error error ->
                return
                    Error(
                        sprintf
                            "sync delegate child observation failed for %s: %s"
                            (SessionId.value physicalOwner)
                            error
                    )
            | Ok children -> return Ok(managedChildObservation scope role agentName children)
        }

    let createChild
        (owner: SessionId)
        (scope: ReuseScopeId)
        (role: SyncDelegateRole)
        (agentName: string)
        (childDirectory: string option)
        =
        sessions.CreateChildSession(
            owner,
            { Title = Some(SyncDelegatePhysicalIdentity.title scope role agentName)
              Agent = Some agentName
              Directory = childDirectory }
        )


    let xTraceFrontier (sessionId: SessionId) : XTraceCursor =
        AgentJournal.snapshot journal
        |> fun snapshot -> AgentProjection.tryFind sessionId snapshot.AgentProjections
        |> Option.bind (fun session -> session.XTrace)
        |> Option.defaultValue XTraceProjection.empty
        |> XTraceProjection.headCursor

    let issueCurrentOwnerIdentitySeed
        (ownerSessionId: SessionId)
        (childAgent: string)
        : Task<Result<PromptAuthority.IdentitySeed, string>> =
        let issued =
            match
                PromptAuthorityProjectionQueries.activeProfile
                    ownerSessionId
                    (AgentJournal.snapshot journal).AgentProjections
            with
            | None -> Error "AgentOwnerRoot identity seed requires the owner's active durable Logical Run"
            | Some ownerProfile ->
                PromptAuthority.issueInheritedIdentitySeed childAgent ownerProfile
                |> Result.mapError (sprintf "Invalid inherited participant identity: %A")
                |> Result.bind (fun seed ->
                    PromptAuthority.validateInheritedIdentitySeed ownerProfile seed
                    |> Result.mapError (sprintf "Invalid owner identity witness: %A")
                    |> Result.map (fun _ -> seed))

        Task.FromResult issued

    let sendDelegatePrompt
        (call: SyncDelegateCall)
        (request: SyncDelegatePromptRequest)
        : Task<Result<PreparedDelegationHandoff, string>> =
        taskResult {
            let requireLiveCall () =
                match store.TryPeekCallByDelegate call.Delegate with
                | Some active when
                    Object.ReferenceEquals(active, call)
                    && not (call.Invocations |> List.exists (fun invocation -> invocation.IsCancelled()))
                    ->
                    Ok()
                | _ -> Error "sync delegate call was cancelled before prompt dispatch"

            do! requireLiveCall ()
            let tools = toolMap (canonicalRole call.Role)
            let route = DelegationHandoffRoute.syncRole call.OwnerScope call.Role
            let! prepared = handoff.Prepare call.Owner route |> TaskResultCE.ofTask

            // EXEC-031: snapshot the child's XTrace head (one-past last part,
            // 0 when empty) at send. This is the inclusive start of the
            // per-invocation range; the exclusive end is the same head
            // captured at completion. All coalesced invocations in this
            // call share the same head and thus the same bounded record.
            let startCursor = xTraceFrontier call.Delegate |> XTraceCursor.sequence

            for inv in call.Invocations do
                inv.StartCursor <- Some startCursor

            let providerPrompt =
                DelegationHandoff.appendParentDelta request.ProviderPrompt prepared.ParentRecord
                |> LlmFacing.render

            let! identitySeed = issueCurrentOwnerIdentitySeed call.Owner call.Agent
            do! requireLiveCall ()

            let accept physical root scope =
                call.AcceptedPhysical <- Some physical
                call.AcceptedAuthorityRoot <- Some root
                call.TerminalFailureScope <- Some scope
                AsyncSupport.trySetResult call.AcceptedRoot root |> ignore

            let activeDelegateProfile =
                PromptAuthorityProjectionQueries.activeProfile
                    call.Delegate
                    (AgentJournal.snapshot journal).AgentProjections

            match activeDelegateProfile with
            | None ->
                // Reused children keep their original Opening; new charges are continuations.
                let! _ =
                    XTraceCapture.captureOpeningWithReceipt (Some journal) call.Delegate request.Charge []
                    |> TaskResult.mapError (fun error -> sprintf "sync delegate opening trace capture failed: %A" error)

                let! _ =
                    dispatcher.SendAgentOwnerRootWithTools
                        (DispatchSessionPort.ofSessionPort sessions)
                        call.Delegate
                        providerPrompt
                        identitySeed
                        directory
                        PromptDispatcher.AwaitMode.Await
                        (Some(fun physical ->
                            let root = PhysicalUserMessageId.promoteToAuthorityRoot physical
                            accept physical root (FreshAuthorityRoot root)))
                        tools

                ()
            | Some profile when
                profile.AuthorityKind = PromptAuthority.RootAuthorityKind.AgentOwnerRoot
                && profile.IdentitySeed = identitySeed
                && (profile.SelectedAgent = call.Agent || profile.SelectedAgent = "engineer")
                && (profile.CanonicalRole = canonicalRole call.Role
                    || profile.CanonicalRole = Role.Engineer)
                ->
                // The delegate session may still be running an in-flight physical
                // execution (an earlier charge or a Blogger request). The new
                // continuation's chat.message admission would replace that lease and
                // mark the old physical superseded; the Host loop would then keep
                // executing the old step and fail closed in chat.params
                // (PROMPT-006), killing this new execution's run too. Settle the old
                // attempt first — the same retire order the manager loop uses.
                let delegateKey = SessionId.value call.Delegate

                match ModelRouting.tryActivePhysical delegateKey with
                | Some inFlightPhysical ->
                    let physicalId = PhysicalUserMessageId.create inFlightPhysical

                    ModelRouting.suppressProviderStep call.Delegate physicalId
                    ModelRouting.releasePhysicalExecution call.Delegate physicalId |> ignore

                    // Best-effort: the lease bookkeeping above already fenced the old
                    // physical out of further provider steps. A failed Host interrupt
                    // degrades to today's behaviour (the old run's next chat.params
                    // fails closed); it must not block the delegation itself.
                    do!
                        sessions.InterruptAttempt call.Delegate
                        |> TaskValue.map (fun outcome ->
                            outcome
                            |> Result.mapError (fun reason ->
                                Diagnostic.emit
                                    "sync-delegate-interrupt-inflight-failed"
                                    [ "session_id", delegateKey; "result", reason ])
                            |> ignore

                            Ok())
                | None -> ()

                let! _ =
                    dispatcher.SendContinuationWithTools
                        (DispatchSessionPort.ofSessionPort sessions)
                        call.Delegate
                        providerPrompt
                        PromptAuthority.ContinuationKind.ManagedDelegationAssignment
                        profile
                        directory
                        PromptDispatcher.AwaitMode.Await
                        (Some(fun physical ->
                            accept physical profile.AuthorityRootUserMessageId (ExistingAuthorityContinuation physical)))
                        tools

                ()
            | Some _ ->
                return!
                    Error
                        "sync delegate rejected: attached delegate active authority does not match its exact owner identity"

            let! _ = call.AcceptedRoot.Task |> TaskResultCE.ofTask
            return prepared
        }

    let deps: SyncDelegateWorkflow.Dependencies =
        { Attached = attached
          AwaitWorkRecord = awaitWorkRecord
          AwaitInvocation = awaitInvocation
          ObserveChild = observeChild
          CreateChild = createChild
          BindChild = (fun (_: SessionId) (_: SessionId) (_: string) -> ())
          OnDelegateReady = onDelegateReady
          NoteDelegatePrompt = noteDelegatePrompt
          CleanupDelegateDraft = cleanupDelegateDraft
          Directory = directory
          ReplaceToolEstimate =
            fun sessionId expectedToolCalls ->
                task {
                    match expectedToolCalls with
                    | Some expected ->
                        let port = AgentJournalPortAdapter.forDelegatedToolEstimate journal
                        do! DelegatedToolEstimateLedger.replace port sessionId expected
                    | None -> ()
                }
          SendPrompt = fun call request -> sendDelegatePrompt call request
          CheckpointCompletedHandoff = fun parent prepared -> handoff.CheckpointCompleted parent prepared
          TripFatal = FatalProcess.trip
          ResolveBoundAgent =
            fun childId ->
                let projections = (AgentJournal.snapshot journal).AgentProjections

                PromptAuthorityProjectionQueries.activeProfile childId projections
                |> Option.map (fun profile -> profile.SelectedAgent)
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
          DescribeWait = SyncDelegateWait.describe
          SubscribeFutureTerminal = fun sessionId listener -> sessions.SubscribeFutureTerminal(sessionId, listener) }

    let failPoppedCalls delegateSessionId reason =
        let rec popAll () =
            match store.TryPopCallByDelegate delegateSessionId with
            | Some call ->
                store.FailCall(call, reason)
                popAll ()
            | None -> ()

        popAll ()

    let resolveWorkRecord
        (turnSessionId: SessionId)
        (call: SyncDelegateCall)
        (endCursor: XTraceCursor)
        (providerRun: ProviderRunIdentity)
        =
        match call.Invocations |> List.tryHead |> Option.bind (fun inv -> inv.StartCursor) with
        | None -> Task.FromResult None
        | Some startCursor ->
            projectWorkRecord turnSessionId (XTraceRange.create (XTraceCursor.create startCursor) endCursor) providerRun

    let noteDelegateIfRole (call: SyncDelegateCall) turnSessionId record =
        if call.Role = SyncDelegateRole.Inspector || call.Role = SyncDelegateRole.Engineer then
            noteDelegateAnswer (sessionKey turnSessionId) record

    let finishCompletedCall (turn: ReconciledTurn) (call: SyncDelegateCall) workRecord =
        match workRecord with
        | Some record when not (String.IsNullOrWhiteSpace record) ->
            SyncDelegateInternals.captureResponse call turn
            noteDelegateIfRole call turn.SessionId record
            AsyncSupport.trySetResult call.Answer (Ok record) |> ignore
            true
        | _ ->
            // EXEC-031 / EXEC-026: fail closed. A Completed turn
            // without a bounded WorkRecord is a protocol defect, not
            // a licence to fall back to the last message (EXEC-028
            // residual OneShot analog). The session stays reusable.
            store.FailCall(call, "EXEC-031: Completed without bounded WorkRecord")
            true

    /// delegation-031: the completion the child physically produced, independent of
    /// whether its terminal trace durably committed. Used only when the
    /// terminal capture itself reports the write was NotCommitted/Unknown: the
    /// earned completion is delivered from the turn's own parts, never dropped
    /// and never re-executed.
    let finishCompletedCallFromTurn (turn: ReconciledTurn) (call: SyncDelegateCall) =
        match CompletedTurnClassifier.partsSessionText turn.Parts with
        | record when not (System.String.IsNullOrWhiteSpace record) ->
            SyncDelegateInternals.captureResponse call turn
            noteDelegateIfRole call turn.SessionId record
            AsyncSupport.trySetResult call.Answer (Ok record) |> ignore
            true
        | _ ->
            store.FailCall(call, "EXEC-031: Completed without bounded WorkRecord")
            true

    let handleCompletedCall (turn: ReconciledTurn) (call: SyncDelegateCall) =
        task {
            // Completion marker for ManagerLife. HandleTurn
            // does not use Terminal to build the inspect payload;
            // the bounded WorkRecord is the invocation's parts range.
            match! XTraceCapture.captureTerminalWithReceipt (Some journal) turn with
            // delegation-031: the terminal trace write is settlement evidence, not
            // the completion itself. WriterUnavailable/WriteUnknown means the
            // child physically finished but the evidence did not commit:
            // deliver the earned completion from the turn's own parts (neither
            // forgotten nor re-executed); the checkpoint stays pending-evidence.
            | Error(XTraceCaptureError.StorageAppendFailed(Wanxiangshu.Foundation.JournalAppendFailure.WriterUnavailable _))
            | Error(XTraceCaptureError.StorageAppendFailed(Wanxiangshu.Foundation.JournalAppendFailure.WriteUnknown _)) ->
                return finishCompletedCallFromTurn turn call
            | Error error ->
                store.FailCall(call, sprintf "sync delegate terminal trace capture failed: %A" error)
                return true
            | Ok receipt ->
                let! workRecord = resolveWorkRecord turn.SessionId call receipt.CurrentHead turn.ProviderRun
                return finishCompletedCall turn call workRecord
        }

    /// delegation-025 causal identity: a turn belongs to this invocation iff its
    /// physical is the exact accepted prompt of this call, or it is a
    /// ProviderRetryAttempt continuation of the same accepted authority root
    /// (the retry attempts the decorator dispatched for this call).
    let belongsToCall (call: SyncDelegateCall) (turn: ReconciledTurn) =
        let sameAcceptedPhysical =
            match call.AcceptedPhysical with
            | Some physical -> physical = turn.PhysicalUserMessageId
            | None -> false

        let isSameAuthorityContinuation =
            match call.AcceptedAuthorityRoot with
            | Some root when root = turn.AuthorityRootUserMessageId ->
                (AgentJournal.snapshot journal).AgentProjections
                |> PromptAuthorityProjectionQueries.projectionFor turn.SessionId
                |> Option.bind (fun authority ->
                    Map.tryFind turn.PhysicalUserMessageId authority.AcceptedContinuationIds)
                |> Option.exists (function
                    | PromptAuthority.ContinuationKind.ProviderRetryAttempt
                    | PromptAuthority.ContinuationKind.DegenerationGuard
                    | PromptAuthority.ContinuationKind.InteractionRepair -> true
                    | _ -> false)
            | _ -> false

        sameAcceptedPhysical || isSameAuthorityContinuation

    let popIfAcceptanceMatches
        (store: SyncDelegateCallStore)
        (turn: ReconciledTurn)
        (call: SyncDelegateCall)
        : Task<SyncDelegateCall option> =
        task {
            let! expectedRoot = call.AcceptedRoot.Task

            return
                if expectedRoot = turn.AuthorityRootUserMessageId && belongsToCall call turn then
                    store.TryPopCallByDelegate turn.SessionId
                else
                    None
        }

    let tryConsumeReadyCall (store: SyncDelegateCallStore) (turn: ReconciledTurn) : Task<SyncDelegateCall option> =
        let candidate =
            store.TryPeekCallByDelegate turn.SessionId
            |> Option.filter (fun call ->
                call.Invocations
                |> List.forall (fun invocation -> invocation.StartCursor.IsSome))

        match candidate with
        | Some call -> popIfAcceptanceMatches store turn call
        | None -> Task.FromResult None

    let failMatchingTerminalCall (turn: ReconciledTurn) error call =
        task {
            let! matchingCall = popIfAcceptanceMatches store turn call

            return
                matchingCall
                |> Option.map (fun exact ->
                    store.FailCall(exact, sprintf "SyncDelegate run failed: %s" error)
                    true)
                |> Option.defaultValue false
        }

    let handleCompletedRoleTurn (turn: ReconciledTurn) =
        task {
            match! tryConsumeReadyCall store turn with
            | Some call -> return! handleCompletedCall turn call
            | None -> return false
        }

    /// One confirmed provider failure for a pending call. The injected retry
    /// decorator owns policy, budget admission and physical re-entry (delegation-023):
    /// a single transient failure never fails the call; only a terminal verdict
    /// does. Turns outside this invocation's accepted attempts stay ordinary.
    let rec handleFailedAttemptTurn (turn: ReconciledTurn) (failure: ExecutionFailure option) error =
        task {
            match store.TryPeekCallByDelegate turn.SessionId with
            | Some call when belongsToCall call turn && failure.IsSome ->
                return! settleFailedAttempt turn failure.Value error call
            | _ -> return false
        }

    and settleFailedAttempt (turn: ReconciledTurn) (failure: ExecutionFailure) error (call: SyncDelegateCall) =
        task {
            match! retry turn failure error with
            | Ok() -> return true
            | Error reason -> return! failMatchingTerminalCall turn reason call
        }

    let singletonResult taskResult =
        task {
            match! taskResult with
            | Ok(SyncDelegateInvocationResult.WorkRecord workRecord) -> return Ok workRecord
            | Ok(SyncDelegateInvocationResult.MergedInto _) ->
                return Error "sync delegate protocol defect: singleton invocation was merged"
            | Error error -> return Error error
        }

    member _.Attached: IAttachedSessionPort = attached

    member _.ObserveProviderToolCall
        (ownerSessionId: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, callId: ToolCallId)
        =
        store.ObserveProviderToolCall(ownerSessionId, providerRun, role, callId)

    member _.TryObservedBatch
        (ownerSessionId: SessionId, providerRun: ProviderRunIdentity, role: SyncDelegateRole, currentCall: ToolCallId) =
        store.TryObservedBatch(ownerSessionId, providerRun, role, currentCall)

    member _.TryFind(ownerSessionId: SessionId, role: SyncDelegateRole) = attached.TryFind(ownerSessionId, role)

    /// Inverse lookup for Strength eligibility: a dedicated SyncDelegate session
    /// is Work+Attached, never speculation material, regardless of its role.
    member _.TryFindDelegateOwner(delegateSessionId: SessionId) : SessionId option =
        attached.TryFindOwner(delegateSessionId, SyncDelegateRole.Inspector)
        |> Option.orElseWith (fun () -> attached.TryFindOwner(delegateSessionId, SyncDelegateRole.Coder))
        |> Option.orElseWith (fun () -> attached.TryFindOwner(delegateSessionId, SyncDelegateRole.Engineer))

    member _.TryFindForScopeClose(ownerSessionId: SessionId, role: SyncDelegateRole) =
        match attached.TryFind(ownerSessionId, role) with
        | Some sessionId -> Some sessionId
        | None when role = SyncDelegateRole.Engineer || role = SyncDelegateRole.Inspector ->
            let ownerScope = ReuseScope.ofSession ownerSessionId
            store.TryGetDeletedDelegate ownerScope
        | None -> None

    member _.StageDeletedDelegate(ownerSessionId: SessionId, delegateSessionId: SessionId) : bool =
        match attached.TryFind(ownerSessionId, SyncDelegateRole.Engineer) with
        | Some bound when bound = delegateSessionId ->
            failPoppedCalls delegateSessionId "Sync delegate session was deleted"

            attached.Remove(ownerSessionId, SyncDelegateRole.Engineer) |> ignore

            let ownerScope = ReuseScope.ofSession ownerSessionId

            let replaced = store.PutDeletedDelegate(ownerScope, delegateSessionId)

            replaced
            |> Option.filter (fun previous -> previous <> delegateSessionId)
            |> Option.iter (fun previous -> cleanupDelegateDraft (sessionKey previous))

            true
        | _ ->
            let ownerScope = ReuseScope.ofSession ownerSessionId

            store.TryGetDeletedDelegate ownerScope
            |> Option.exists (fun staged -> staged = delegateSessionId)

    member this.StageDeletedDelegateBySession(delegateSessionId: SessionId) : SessionId option =
        attached.TryFindOwner(delegateSessionId, SyncDelegateRole.Engineer)
        |> Option.filter (fun ownerSessionId -> this.StageDeletedDelegate(ownerSessionId, delegateSessionId))

    member _.Invoke
        (ownerSessionKey: string, role: SyncDelegateRole, charge: string, ?expectedToolCalls: int)
        : Task<Result<string, string>> =
        SyncDelegateWorkflow.invoke
            store
            deps
            ownerSessionKey
            role
            charge
            expectedToolCalls
            None
            (fun () -> Task.FromResult(LlmFacing.instruction charge))
            None
            (fun () -> false)
        |> singletonResult

    /// EXEC-032 composition seam: caller supplies a low-trust provider prompt
    /// producer; workflow invokes it only after semantic batch admission.
    member _.InvokePrepared
        (
            ownerSessionKey: string,
            role: SyncDelegateRole,
            charge: string,
            prepareProviderPrompt: unit -> Task<LlmFacing.Document>,
            ?expectedToolCalls: int
        ) : Task<Result<string, string>> =
        SyncDelegateWorkflow.invoke
            store
            deps
            ownerSessionKey
            role
            charge
            expectedToolCalls
            None
            prepareProviderPrompt
            None
            (fun () -> false)
        |> singletonResult

    /// Program callers consume the exact formal response while ordinary callers
    /// retain the bounded WorkRecord contract and the same authority/lifecycle.
    member _.InvokeResponsePrepared
        (
            ownerSessionKey: string,
            role: SyncDelegateRole,
            charge: string,
            prepareProviderPrompt: unit -> Task<LlmFacing.Document>,
            ?isCancelled: unit -> bool
        ) : Task<Result<string, string>> =
        task {
            // DSL-MUTABLE: resource — exact terminal response for this invocation.
            let response = ref None

            let! result =
                SyncDelegateWorkflow.invoke
                    store
                    deps
                    ownerSessionKey
                    role
                    charge
                    None
                    None
                    prepareProviderPrompt
                    (Some(fun text -> response.Value <- Some text))
                    (defaultArg isCancelled (fun () -> false))
                |> singletonResult

            return
                result
                |> Result.bind (fun _ ->
                    match response.Value with
                    | Some text when not (String.IsNullOrWhiteSpace text) -> Ok text
                    | _ -> Error "Completed delegation did not supply a formal response")
        }

    member _.InvokeBatchPrepared
        (
            ownerSessionKey: string,
            role: SyncDelegateRole,
            charge: string,
            batch: SyncDelegateBatch,
            prepareProviderPrompt: unit -> Task<LlmFacing.Document>,
            ?expectedToolCalls: int
        ) : Task<Result<SyncDelegateInvocationResult, string>> =
        SyncDelegateWorkflow.invoke
            store
            deps
            ownerSessionKey
            role
            charge
            expectedToolCalls
            (Some batch)
            prepareProviderPrompt
            None
            (fun () -> false)

    member _.HandleTurn
        (turn: ReconciledTurn, failure: ExecutionFailure option, permit: QuiescencePermit option)
        : Task<bool> =
        task {
            match turn.Role, turn.Outcome with
            | Some(Role.Inspector | Role.Coder | Role.Engineer), ReconcileProgram.TurnCompleted ->
                return! handleCompletedRoleTurn turn
            | Some(Role.Inspector | Role.Coder | Role.Engineer), ReconcileProgram.TurnFailed error ->
                return! handleFailedAttemptTurn turn failure error
            | _ ->
                // TurnInProgress and TurnNeedsContinuation remain child-local;
                // a turn outside this call's accepted attempts is not ours.
                return false
        }

    /// True once the provider-owned opening capture has assigned a bounded cursor.
    member _.HasOpeningCursor(sessionId: SessionId) : bool =
        match store.TryPeekCallByDelegate sessionId with
        | Some call ->
            call.Invocations.Length > 0
            && call.Invocations
               |> List.forall (fun invocation -> invocation.StartCursor.IsSome)
        | None -> false

    /// delegation-031 probe seam: the exact authority root this delegate's live call
    /// accepted, without consulting the durable projection (which freezes once
    /// the journal writer is released).
    member _.TryAcceptedAuthorityRoot(sessionId: SessionId) : string option =
        match store.TryPeekCallByDelegate sessionId with
        | Some call -> Option.map (AuthorityRootUserMessageId.value) call.AcceptedAuthorityRoot
        | None -> None

    /// delegation-031: settle a completed turn from its own parts when the terminal
    /// trace capture reports NotCommitted/Unknown. True iff a live call
    /// consumed the turn. The checkpoint stays pending-evidence; the earned
    /// completion is delivered from the turn, never dropped, never re-executed.
    member _.SettleCompletedFromTurn(turn: ReconciledTurn) : bool =
        match store.TryPeekCallByDelegate turn.SessionId with
        | Some call -> SyncDelegateInternals.settleCompletedFromParts noteDelegateIfRole store call turn
        | None -> false

    member _.AwaitAssignmentReady(sessionId: SessionId) : Task<bool> =
        match store.TryPeekCallByDelegate sessionId with
        | None -> Task.FromResult false
        | Some call ->
            task {
                let! _ = call.AcceptedRoot.Task
                return true
            }

    member _.CancelSession(sessionId: SessionId) : unit =
        let asOwnerScope = ReuseScope.ofSession sessionId

        store.CancelScope asOwnerScope

        let rec popAll () =
            match store.TryPopCallByDelegate sessionId with
            | Some call ->
                store.FailCall(call, "Sync delegate call was cancelled")
                popAll ()
            | None -> ()

        popAll ()

        let delegateOwned = attached.TryFind(sessionId, SyncDelegateRole.Inspector)

        let stagedDelegateOwned = store.ClearDeletedDelegate asOwnerScope

        attached.RemoveByDelegateSession sessionId |> ignore

        for role in
            [ SyncDelegateRole.Inspector
              SyncDelegateRole.Coder
              SyncDelegateRole.Engineer ] do
            attached.Remove(sessionId, role) |> ignore

        delegateOwned |> Option.iter (fun id -> cleanupDelegateDraft (sessionKey id))

        stagedDelegateOwned
        |> Option.iter (fun id -> cleanupDelegateDraft (sessionKey id))

        cleanupDelegateDraft (sessionKey sessionId)

    member _.Dispose() =
        let retiredDelegates = store.ClearAll()

        for delegateId in retiredDelegates do
            cleanupDelegateDraft (sessionKey delegateId)

        attached.Clear()

    interface IDisposable with
        member runtime.Dispose() = runtime.Dispose()
