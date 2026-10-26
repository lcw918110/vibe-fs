namespace Wanxiangshu.Composition.Durable

open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Change
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Session.ChatExecution
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Participant.Provider.Attempt.Fallback
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation

/// Pure envelope dispatch. Each bounded projection owns its own fold algorithm;
/// this module only routes facts and decides which refusals are fatal.
module Fold =

    let empty: ProjectionSet =
        { AgentProjections = AgentProjection.empty
          RuntimeId = None }

    let private reject = FoldRejection.reject

    let foldAgentFact (projection: AgentProjectionSet) (fact: AgentFact) : Result<AgentProjectionSet, FoldRejection> =
        // through its own branch so no fold depends on the whole catalogue.
        match fact with
        | AgentFact.Prompt prompt -> PromptAuthorityProjectionBridge.fold projection prompt
        | AgentFact.ProviderFailure failure -> ProviderFailureProjectionBridge.fold projection failure
        | AgentFact.Execution execution -> DelegationProjectionBridge.foldExecution projection execution
        | AgentFact.ChatExecution chatExecution ->
            ChatExecutionFactFold.fold projection.ChatExecutions chatExecution
            |> Result.map (fun updated ->
                { projection with
                    ChatExecutions = updated })
        | AgentFact.Cognition _ -> Ok projection
        | AgentFact.Orchestrator orchestrator ->
            // The Change family fold consumes the journal-owned `ProjectionSet`,
            // so its assembly stays with the dispatcher, downstream of that edge.
            OrchestratorFactFold.fold projection.Orchestrator orchestrator
            |> Result.map (fun updated ->
                { projection with
                    Orchestrator = updated })
            |> Result.mapError (fun rejection ->
                { Fact = OrchestratorFoldRejection.fact rejection
                  Reason = OrchestratorFoldRejection.message rejection })
        | AgentFact.Companion companion -> CompanionProjectionBridge.fold projection companion
        | AgentFact.Context context -> ContextProjectionBridge.fold projection context
        | AgentFact.Host host -> HostFactFold.fold projection host
        | AgentFact.Fission fission -> ProjectionUpdate.applyFission projection fission
        | AgentFact.Delegation delegation -> DelegationProjectionBridge.foldDelegation projection delegation

    /// Fact-only fold for callers that do not need envelope metadata.
    /// RuntimeStarted needs no envelope field (RuntimeId is in the payload).
    let foldFact (projection: ProjectionSet) (fact: Fact) : Result<ProjectionSet, FoldRejection> =
        match fact with
        | Runtime(RuntimeStarted runtime) ->
            Ok
                { projection with
                    RuntimeId = Some runtime.RuntimeId
                    AgentProjections =
                        { projection.AgentProjections with
                            RuntimeStartCount = projection.AgentProjections.RuntimeStartCount + 1 } }
        | Agent fact ->
            foldAgentFact projection.AgentProjections fact
            |> Result.map (fun agents ->
                { projection with
                    AgentProjections = agents })

    /// The envelope adds no fold input beyond its fact: the historical runtime
    /// watermark (`RuntimeStarted`) carries its RuntimeId in the payload, so the
    /// envelope and fact-only folds are one function.
    let foldEnvelope (projection: ProjectionSet) (envelope: Envelope) : Result<ProjectionSet, FoldRejection> =
        foldFact projection envelope.Fact

// Historical enumeration intentionally has no Journal-owned API. Boot and
// live facts both enter through CanonicalIntegrator, which invokes only
// foldEnvelope for one already-ordered durable event at a time.
