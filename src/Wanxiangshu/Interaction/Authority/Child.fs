namespace Wanxiangshu.Interaction.Authority

open Wanxiangshu.Change
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Companion.Blogger
open Wanxiangshu.Context.Prefix
open Wanxiangshu.Context.Trace
open Wanxiangshu.Enforcer
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Foundation
open Wanxiangshu.Host
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt
open Wanxiangshu.Participant.Provider.Projection
open Wanxiangshu.Persistence.EventStore
open Wanxiangshu.Host
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Foundation.Identity

/// Application ownership of linked-child prompt authority (rabbit §19).
/// Physical runtime cleanup must not decide who owns a Logical Run.
module ChildPromptAuthority =

    let private registerLinkedChildIfNeeded
        (runtime: PromptDispatcher.Runtime)
        (turn: ReconciledTurn)
        handle
        (activeProfile: PromptAuthority.AuthorityExecutionProfile option)
        (accepted: PromptAuthority.AcceptedDispatch option)
        : System.Threading.Tasks.Task<Result<unit, string>> =
        match handle, activeProfile, accepted with
        | None, _, _
        | Some _, Some _, _ -> System.Threading.Tasks.Task.FromResult(Ok())
        | Some _, None, None ->
            System.Threading.Tasks.Task.FromResult(
                Error(
                    sprintf
                        "Linked child %s has no accepted AgentOwnerRoot claim for physical message %s"
                        (SessionId.value turn.SessionId)
                        (PhysicalUserMessageId.value turn.PhysicalUserMessageId)
                )
            )
        | Some _, None, Some claim ->
            runtime.AcceptPhysicalAgentOwnerRoot
                claim.PromptKey
                turn.SessionId
                turn.PhysicalUserMessageId
                claim.IdentitySeed
            |> TaskValue.map (Result.map ignore)

    let ensureForLinkedChild
        (prompts: IPromptJournal option)
        (turn: ReconciledTurn)
        : System.Threading.Tasks.Task<Result<unit, string>> =
        task {
            match prompts with
            | Some prompts ->
                let owner = prompts.ProjectionFor turn.SessionId
                let handle = prompts.HandleForChild turn.SessionId
                let activeProfile = owner.ActiveLogicalRun

                let accepted =
                    Map.tryFind turn.PhysicalUserMessageId owner.PhysicalLandings
                    |> Option.filter (fun dispatch ->
                        dispatch.Origin = PromptAuthority.PromptOrigin.AuthorityRoot
                            PromptAuthority.RootAuthorityKind.AgentOwnerRoot)

                let runtime = PromptDispatcher.forPrompts prompts
                return! registerLinkedChildIfNeeded runtime turn handle activeProfile accepted
            | None -> return Ok()
        }
