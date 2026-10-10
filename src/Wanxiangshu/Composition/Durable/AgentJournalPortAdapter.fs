namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Persistence.Journal.JournalOutcome
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger.Runtime
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation
open Wanxiangshu.Interaction.Attention
open Wanxiangshu.Interaction.Concern
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Requirement.Grounding
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Context.Trace
open Wanxiangshu.Context.Prefix
open Wanxiangshu.OpenCode.Host.RequirementGrounding
open Wanxiangshu.OpenCode
open Wanxiangshu.Host
open Wanxiangshu.Execution.Delegation.Fork.OpenCode
open Wanxiangshu.Context.Companion.Blogger

module AgentJournalPortAdapter =
    let forAttention (journal: AgentJournal) : AttentionJournalPort =
        AttentionConcernJournalAdapter.forAttention journal

    let forConcern (journal: AgentJournal) : ConcernJournalPort =
        AttentionConcernJournalAdapter.forConcern journal

    let forDelegatedToolEstimate (journal: AgentJournal) : DelegatedToolEstimatePort =
        SessionStartedAtJournalAdapter.forDelegatedToolEstimate journal

    let forSessionStartedAt (journal: AgentJournal) : SessionStartedAtPort =
        SessionStartedAtJournalAdapter.forSessionStartedAt journal

    let forProviderFailure (journal: AgentJournal) : ProviderFailureJournalPort =
        { ProviderFailureJournalPort.CurrentState =
            fun sessionId ->
                AgentJournal.snapshot journal
                |> fun snapshot -> AgentProjection.tryFind sessionId snapshot.AgentProjections
                |> Option.bind (fun session -> session.ProviderFailures)
          ProviderFailureJournalPort.Append =
            fun sessionId providerRun fact ->
                task {
                    let! result =
                        AgentJournal.appendAgent
                            (StreamId.Session sessionId)
                            (Some providerRun)
                            (AgentFact.ProviderFailure fact)
                            journal

                    return result |> Result.map ignore |> Result.mapError JournalAppendFailure.describe
                } }

    /// TURN-OBSERVE: Each member performs exactly one journal snapshot read at call time;
    /// multiple fields within one member share that snapshot.
    let forTurnObservation (journal: AgentJournal) : TurnObservationJournalPort =
        let projections () =
            (AgentJournal.snapshot journal).AgentProjections

        { TryBloggerReceiptKind =
            fun sessionId providerRun ->
                (projections ()).Sessions
                |> Map.tryFind sessionId
                |> Option.bind (fun session -> session.BloggerCycles)
                |> Option.bind (BloggerCycleProjection.tryReceipt providerRun)
                |> Option.map (fun receipt -> receipt.Kind)
          TryContinuationKind =
            fun sessionId physicalUserMessageId ->
                (projections ()).Sessions
                |> Map.tryFind sessionId
                |> Option.bind (fun session -> session.PromptAuthority)
                |> Option.bind (fun authority -> Map.tryFind physicalUserMessageId authority.AcceptedContinuationIds)
          HasExecutionTerminal =
            fun sessionId physicalUserMessageId ->
                let key: Wanxiangshu.Execution.Session.ChatExecution.ChatExecutionKey =
                    { SessionId = sessionId
                      PhysicalUserMessageId = physicalUserMessageId }

                (projections ()).ChatExecutions
                |> Wanxiangshu.Execution.Session.ChatExecution.ChatExecutionProjection.byKey key
                |> Option.exists (fun execution -> execution.terminalDisposition.IsSome)
          IsFissionActive =
            fun sessionId ->
                FissionProjection.tryActiveForOwner sessionId (projections ()).Fission
                |> Option.isSome }

    let forTerminalPolicy (journal: AgentJournal) : TerminalPolicyPort =
        let projections () =
            (AgentJournal.snapshot journal).AgentProjections

        let canonicalRoleOf (authority: PromptAuthority.PromptAuthorityProjection) =
            match authority.ActiveLogicalRun, authority.LastAuthorityProfile with
            | Some run, _ -> Some run.CanonicalRole
            | None, Some profile -> Some profile.CanonicalRole
            | None, None -> None

        { IsPoisoned = fun () -> journal.IsPoisoned
          HasListableHandles =
            fun sessionId ->
                AgentProjection.tryFind sessionId (projections ())
                |> Option.bind (fun session -> session.Handles)
                |> Option.defaultValue HandleProjection.empty
                |> HandleProjection.listable
                |> List.isEmpty
                |> not
          HasActiveOrchestratorJobs = fun () -> AgentProjection.hasActiveOrchestratorJobs (projections ())
          IsLinkedChild = fun sessionId -> Map.containsKey sessionId (projections ()).HandleByChildSession
          TryCanonicalRole =
            fun sessionId ->
                Map.tryFind sessionId (projections ()).Sessions
                |> Option.bind (fun session -> session.PromptAuthority)
                |> Option.bind canonicalRoleOf }

    let forHostJoinGuard (journal: AgentJournal) : HostJoinGuardJournalPort =
        { HasOutstandingJoinClaim =
            fun targetSessionId terminalProviderRun ->
                let projections = (AgentJournal.snapshot journal).AgentProjections

                let payloadDigest =
                    PromptAuthority.gateNudgePayloadDigest "runtime/background-join" terminalProviderRun

                AgentProjection.tryFind targetSessionId projections
                |> Option.bind (fun session -> session.PromptAuthority)
                |> Option.map (fun authority ->
                    authority.PendingClaims
                    |> Map.exists (fun _ claim ->
                        claim.Origin = PromptAuthority.PromptOrigin.Continuation
                            PromptAuthority.ContinuationKind.JoinGuard
                        && claim.PayloadDigest = payloadDigest))
                |> Option.defaultValue false }

    let forRequirementGrounding (journal: AgentJournal) : RequirementGroundingPort =
        RequirementGroundingJournalAdapter.forRequirementGrounding journal

    let forWire (journal: AgentJournal) : WireJournalPort =
        let failurePort = forProviderFailure journal

        { ReadView =
            fun sessionId ->
                let snapshot = AgentJournal.snapshot journal
                let projections = snapshot.AgentProjections
                let sessionProj = AgentProjection.tryFind sessionId projections

                let isComp =
                    SessionAssociationProjection.isCompanion sessionId projections.Associations

                let activeProf =
                    PromptAuthorityProjectionQueries.activeProfile sessionId projections

                let failState = sessionProj |> Option.bind (fun session -> session.ProviderFailures)

                let wireState =
                    sessionProj
                    |> Option.map (fun s ->
                        { XTrace = s.XTrace
                          Blog = s.Blog
                          PrefixEpoch = s.PrefixEpoch
                          TodoCheckpoints =
                            projections.TodoCheckpoints
                            |> Map.tryFind sessionId
                            |> Option.defaultValue PhaseWindow.emptyWindow })

                { State = wireState
                  IsCompanion = isComp
                  ActiveAuthorityProfile = activeProf
                  ProviderFailureState = failState
                  AcceptedOrigin =
                    fun physical ->
                        let key: Wanxiangshu.Execution.Session.ChatExecution.ChatExecutionKey =
                            { SessionId = sessionId
                              PhysicalUserMessageId = physical }

                        projections.ChatExecutions
                        |> Wanxiangshu.Execution.Session.ChatExecution.ChatExecutionProjection.byKey key
                        |> Option.map _.origin }
          ReadBlob = fun blobRef -> journal.Writer.BlobWriter.Read blobRef
          WriteBlob =
            fun content ->
                task {
                    let! res = journal.WriteBlob content

                    return
                        res
                        |> Result.map (fun r ->
                            { BlobRef = r.BlobRef
                              BlobDigest = r.BlobDigest })
                }
          CurrentProjection = fun xTrace -> XTraceMaterialization.currentProjection journal xTrace
          RecordConfirmedSuccess = ProviderFailureLedger.recordConfirmedSuccess failurePort
          CommitPrefixRebase =
            fun sessionId providerRun rebase ->
                task {
                    let fact =
                        ContextFact.PrefixRebaseCommitted
                            {| SessionId = sessionId
                               PreviousEpochId = rebase.PreviousEpochId
                               NextEpochId = rebase.NextEpochId
                               FrozenRecordPrefixRef = rebase.FrozenRecordPrefixRef
                               FrozenRecordPrefixDigest = rebase.FrozenRecordPrefixDigest
                               CutoffExclusive = rebase.CutoffExclusive
                               CoveredPrefixDigest = rebase.CoveredPrefixDigest
                               SealRoot = rebase.SealRoot
                               SyntheticMessageId = rebase.SyntheticMessageId
                               ProbeId = rebase.ProbeId
                               SolvingProviderRun = providerRun |}

                    let! appended =
                        AgentJournal.appendAgent (StreamId.Session sessionId) (Some providerRun) fact journal

                    return
                        appended
                        |> Result.map (fun _ -> ())
                        |> Result.mapError JournalAppendFailure.describe
                } }

    /// delegation-029: durable composition is the only place that wraps delegation fact
    /// cases into the outer routing union and adapts the journal handle.
    let fromAgentJournal (journal: AgentJournal) : AgentJournalPort =
        DelegationJournalAdapter.fromAgentJournal journal
