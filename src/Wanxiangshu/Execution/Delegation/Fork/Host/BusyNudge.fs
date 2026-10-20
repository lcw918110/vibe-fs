namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Threading.Tasks
open Wanxiangshu.OpenCode
open Wanxiangshu.Composition.Turn
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.SyncDelegate
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
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

/// EXEC-002 busy-agent nudge, as a PROMPT-003 Continuation.
module HostForkBusyNudge =

    let profileForRun (journal: AgentJournal option) (run: PendingHostRun) =
        let profile =
            journal
            |> Option.bind (fun durable ->
                PromptAuthorityProjectionQueries.activeProfile
                    run.ChildId
                    (AgentJournal.snapshot durable).AgentProjections)

        match profile with
        | Some active when not run.Finished && active.AuthorityRootUserMessageId = run.AuthorityRoot -> Ok active
        | _ -> Error "Busy nudge requires the original active assignment"

    /// Continuation of the child's active Logical Run. Never creates a new
    /// Authority Root / RunId / completion.
    ///
    /// No journal means no Dispatcher, and PROMPT-005 admits no second sender: this
    /// used to fall through to `sessions.SendChildPromptFireAndForget`, which reaches
    /// the Host prompt endpoint directly with no claim, no PromptKey and no recovery
    /// anchor — the exact bypass package A removed elsewhere. It fails closed instead.
    let private sendResult sent : Result<unit, string> =
        match sent with
        | Ok _ -> Ok()
        | Error err -> Error err

    let private sendWithProfile
        (sessions: ISessionHostPort)
        (j: AgentJournal)
        (childId: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (directory: string option)
        (prompt: string)
        =
        task {
            let rt = PromptDispatcher.forPrompts (PromptJournalAdapter.create j)
            let syntheticPrompt = LlmFacing.renderInstruction prompt

            let! sent =
                rt.SendContinuation
                    (DispatchSessionPort.ofSessionPort sessions)
                    childId
                    syntheticPrompt
                    PromptAuthority.ContinuationKind.BusyAgentNudge
                    profile
                    directory
                    PromptDispatcher.AwaitMode.Detached
                    None

            return sendResult sent
        }

    let send
        (sessions: ISessionHostPort)
        (journal: AgentJournal option)
        (childId: SessionId)
        (profile: PromptAuthority.AuthorityExecutionProfile)
        (directory: string option)
        (prompt: string)
        : Task<Result<unit, string>> =
        task {
            match journal with
            | None ->
                return Error "Busy nudge requires an AgentJournal: PROMPT-005 admits no sender outside the Dispatcher"
            | Some j -> return! sendWithProfile sessions j childId profile directory prompt
        }

    let sender sessions journal (directoryOf: string -> string option) =
        fun (agentId: string) childId profile prompt ->
            send sessions journal childId profile (directoryOf agentId) prompt
