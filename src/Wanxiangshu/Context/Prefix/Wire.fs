namespace Wanxiangshu.Context.Prefix

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.OpenCode

open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Participant.Provider.Projection

type XWireReconciliationDecision = { Cleared: bool; KeptPlan: bool }

type AttemptPlanCapability =
    { TryAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option
      TryBindAttemptPlan: SessionId -> PhysicalUserMessageId -> ProviderRunIdentity -> AttemptPlan option
      ConsumeAttemptPlan: SessionId -> ProviderRunIdentity -> AttemptPlan option
      FreezePendingAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan -> PendingAttemptPlanAdmission
      TryPendingAttemptPlan: SessionId -> PhysicalUserMessageId -> PendingAttemptPlan option }

module XWire =

    let reconciliationDecision (hasPlan: bool) (outcome: AttemptOutcome option) : XWireReconciliationDecision =
        match hasPlan, outcome with
        | false, _ -> { Cleared = false; KeptPlan = false }
        | true, Some AttemptOutcome.Completed -> { Cleared = true; KeptPlan = false }
        | true, Some AttemptOutcome.CompletedInvalid
        | true, Some AttemptOutcome.Failed
        | true, Some AttemptOutcome.Aborted -> { Cleared = true; KeptPlan = false }
        | true, None -> { Cleared = false; KeptPlan = true }

    let private sessionIdOfOutput (output: obj) : SessionId option =
        ProviderWireDecode.projectionSessionIdFromMessages output
        |> Option.map SessionId.create

    let private requireOk (result: Result<'a, string>) : 'a =
        match result with
        | Ok value -> value
        | Error reason -> raise (InvalidOperationException reason)

    /// Prefix coverage is expressed only in canonical XTrace semantic-turn
    /// coordinates. A positional legacy trace is insufficient proof and fails
    /// closed instead of silently interpreting a provider-array index as history.
    let private providerRetryOrigin =
        PromptAuthority.originLabel (
            PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.ProviderRetryAttempt
        )

    let private isProviderRetryAttempt (rawMessage: obj) =
        ProviderWireDecode.promptOriginOfMessage rawMessage = Some providerRetryOrigin

    let private isProviderRetryMessageId (messageId: string) (rawMessages: obj list) =
        rawMessages
        |> List.exists (fun message ->
            ProviderWireDecode.hostMessageId message = Some messageId
            && isProviderRetryAttempt message)

    let suppressHostMessagesByIds (rawMessages: obj list) (hostMessageIds: Set<string>) =
        ProjectionMessageEdit.suppressHostMessagesByIds rawMessages hostMessageIds

    let private recordSuccessfulAttempt
        (port: WireJournalPort)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        (plan: AttemptPlan)
        : Task<Result<unit, string>> =
        if ProviderRequestKind.clearsFailureCountOnSuccess plan.Profile.RequestKind then
            port.RecordConfirmedSuccess sessionId providerRun
        else
            Task.FromResult(Ok())

    let private settleAttemptPlan
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        (outcome: AttemptOutcome)
        (plan: AttemptPlan)
        : Task =
        task {
            let! success =
                match outcome with
                | AttemptOutcome.Completed -> recordSuccessfulAttempt port sessionId providerRun plan
                | AttemptOutcome.CompletedInvalid
                | AttemptOutcome.Failed
                | AttemptOutcome.Aborted -> Task.FromResult(Ok())

            success
            |> Result.mapError (fun reason -> sprintf "provider success commit failed: %s" reason)
            |> requireOk
            |> ignore

            attempts.ConsumeAttemptPlan sessionId providerRun |> ignore
        }

    let private toolContinuationBinding
        (rawMessage: obj)
        : (ProviderRunIdentity * PhysicalUserMessageId option) option =
        let info = ProviderWireDecode.infoObject rawMessage

        match
            ProviderWireDecode.firstString info [ "role" ],
            ProviderWireDecode.firstString info [ "finish" ],
            ProviderWireDecode.hostMessageId rawMessage
        with
        | Some role, Some finish, Some providerRun when
            role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            && finish.Equals("tool-calls", StringComparison.OrdinalIgnoreCase)
            ->
            let physical =
                ProviderWireDecode.firstString info [ "parentID" ]
                |> Option.map PhysicalUserMessageId.create

            Some(ProviderRunIdentity.create providerRun, physical)
        | _ -> None

    let private settleVisibleToolContinuation
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (providerRun: ProviderRunIdentity)
        (physical: PhysicalUserMessageId option)
        : Task =
        let plan =
            match attempts.TryAttemptPlan sessionId providerRun with
            | Some existing -> Some existing
            | None ->
                physical
                |> Option.bind (fun parent -> attempts.TryBindAttemptPlan sessionId parent providerRun)

        match plan with
        | None -> Task.FromResult(())
        | Some plan -> settleAttemptPlan port attempts sessionId providerRun AttemptOutcome.Completed plan

    let private settleVisibleToolContinuations
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (rawMessages: obj list)
        : Task =
        task {
            for providerRun, physical in rawMessages |> List.choose toolContinuationBinding |> List.distinct do
                do! settleVisibleToolContinuation port attempts sessionId providerRun physical
        }


    let private authoritySummary (authority: PromptAuthority.AuthorityExecutionProfile) =
        sprintf
            "session=%s logical=%s root=%s kind=%A participant=%s role=%s"
            (SessionId.value authority.SessionId)
            (LogicalRunId.value authority.LogicalRunId)
            (AuthorityRootUserMessageId.value authority.AuthorityRootUserMessageId)
            authority.AuthorityKind
            authority.SelectedAgent
            (Roles.roleLabel authority.CanonicalRole)

    let private requireAdmittedPendingPlan
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (physical: PhysicalUserMessageId)
        (pendingPlan: PendingAttemptPlan)
        : PendingAttemptPlan =
        match attempts.FreezePendingAttemptPlan sessionId physical pendingPlan with
        | PendingAttemptPlanAdmission.Admitted plan -> plan
        | PendingAttemptPlanAdmission.ReplayedExisting plan -> plan
        | PendingAttemptPlanAdmission.PlanConflict(existing, attempted) when
            PendingAttemptPlanAdmission.sameRequestIdentity existing attempted
            ->
            existing
        | PendingAttemptPlanAdmission.PlanConflict(existing, attempted) ->
            raise (
                InvalidOperationException(
                    sprintf
                        "HOST-BOUNDARY-008: pending attempt plan conflict: existing=(%s) attempted=(%s)"
                        (authoritySummary existing.Authority)
                        (authoritySummary attempted.Authority)
                )
            )
        | PendingAttemptPlanAdmission.IdentityMismatch(expectedSession, expectedPhysical, attempted) ->
            raise (
                InvalidOperationException(
                    sprintf
                        "HOST-BOUNDARY-008: pending attempt plan identity mismatch: expectedSession=%A expectedPhysical=%A attempted=%A"
                        expectedSession
                        expectedPhysical
                        attempted
                )
            )


    let private pendingPlanForRetry
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (physical: PhysicalUserMessageId)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        : PendingAttemptPlan option =
        match attempts.TryPendingAttemptPlan sessionId physical with
        | Some existing when existing.Authority = authority -> Some existing
        | Some existing ->
            raise (
                InvalidOperationException(
                    sprintf
                        "HOST-BOUNDARY-008: pending attempt plan authority conflict: existing=(%s) current=(%s)"
                        (authoritySummary existing.Authority)
                        (authoritySummary authority)
                )
            )
        | None -> None

    let private planOrBuildRetry
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (physical: PhysicalUserMessageId)
        (authority: PromptAuthority.AuthorityExecutionProfile)
        (existingPlan: PendingAttemptPlan option)
        : Task<PendingAttemptPlan> =
        match existingPlan with
        | Some existing -> Task.FromResult existing
        | None ->
            task {
                let pendingPlan =
                    AttemptPlanner.freezePreInference
                        authority
                        physical
                        (PromptAuthority.PromptOrigin.Continuation PromptAuthority.ContinuationKind.ProviderRetryAttempt)
                        ProviderRequestKind.WorkMain

                return requireAdmittedPendingPlan attempts sessionId physical pendingPlan
            }

    let private planProviderRetry
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (physical: PhysicalUserMessageId)
        : Task =
        task {
            let view = port.ReadView sessionId

            match view.ActiveAuthorityProfile, view.ProviderFailureState with
            | Some authority, Some _ ->
                let existingPlan = pendingPlanForRetry attempts sessionId physical authority

                let! _ = planOrBuildRetry port attempts sessionId physical authority existingPlan
                return ()

            | _ ->
                return
                    raise (
                        InvalidOperationException
                            "X-wire cannot plan a retry without authority, failure, and session projections"
                    )
        }

    let private applyOrdinaryTransform
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (sessionId: SessionId)
        (_snapshot: ISessionSnapshotPort option)
        (output: obj)
        : Task =
        task {
            let rawMessages = ProviderWireDecode.messagesFromTransformOutput output
            let physical = ProviderWireCapture.lastUserMessageId rawMessages

            // A provider attempt may have ended with tool calls. That attempt is
            // complete even though the Host turn continues through the tool loop.
            do! settleVisibleToolContinuations port attempts sessionId rawMessages

            let recoveryAttempt =
                physical
                |> Option.filter (fun p ->
                    let physicalId = PhysicalUserMessageId.value p
                    isProviderRetryMessageId physicalId rawMessages)

            match recoveryAttempt with
            | None -> return ()
            | Some physical -> return! planProviderRetry port attempts sessionId physical
        }

    let applyTransform
        (snapshot: ISessionSnapshotPort option)
        (port: WireJournalPort option)
        (attempts: AttemptPlanCapability)
        (output: obj)
        : Task =
        task {
            match port, sessionIdOfOutput output with
            | Some p, Some sessionId when (p.ReadView sessionId).IsCompanion |> not ->
                return! applyOrdinaryTransform p attempts sessionId snapshot output
            | _ -> return ()
        }


    let private attemptOutcomeOfTurn (turn: ReconciledTurn) : AttemptOutcome option =
        match turn.Observation, turn.Outcome with
        | Some _, _ -> None
        | None, ReconcileProgram.TurnCompleted -> Some AttemptOutcome.Completed
        | None, ReconcileProgram.TurnInProgress -> Some AttemptOutcome.Completed
        | None, ReconcileProgram.TurnNeedsContinuation _ -> Some AttemptOutcome.CompletedInvalid
        | None, ReconcileProgram.TurnFailed _ -> Some AttemptOutcome.Failed
        | None, ReconcileProgram.TurnAborted _ -> Some AttemptOutcome.Aborted

    let private reconcilePlannedAttempt
        (port: WireJournalPort)
        (attempts: AttemptPlanCapability)
        (turn: ReconciledTurn)
        (plan: AttemptPlan)
        : Task =
        let outcome = attemptOutcomeOfTurn turn
        let decision = reconciliationDecision true outcome

        match outcome, decision.Cleared with
        | Some settled, true -> settleAttemptPlan port attempts turn.SessionId turn.ProviderRun settled plan
        | _ -> Task.FromResult(())

    let private attemptPlanForTurn (attempts: AttemptPlanCapability) (turn: ReconciledTurn) =
        attempts.TryAttemptPlan turn.SessionId turn.ProviderRun
        |> Option.orElseWith (fun () ->
            attempts.TryBindAttemptPlan turn.SessionId turn.PhysicalUserMessageId turn.ProviderRun)

    let private plannedReconciliation
        (port: WireJournalPort option)
        (attempts: AttemptPlanCapability)
        (turn: ReconciledTurn)
        =
        port
        |> Option.bind (fun p -> attemptPlanForTurn attempts turn |> Option.map (fun plan -> p, plan))

    /// Settle the physical provider attempt, not the larger Host turn.
    /// `finish=tool-calls` therefore closes a successful attempt plan while the
    /// Host tool loop continues; only a genuinely provisional snapshot keeps it.
    let reconcileAttempt
        (port: WireJournalPort option)
        (attempts: AttemptPlanCapability)
        (turn: ReconciledTurn)
        : Task =
        match plannedReconciliation port attempts turn with
        | Some(p, plan) -> reconcilePlannedAttempt p attempts turn plan
        | None -> Task.FromResult(())
