namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Authority
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System.Threading.Tasks
open Wanxiangshu.OpenCode
open Wanxiangshu.Interaction.Dispatch
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Composition.Durable

module HostForkAgentOwner =

    /// PROMPT-005: the first prompt of an AgentOwnerRoot work unit.
    ///
    /// Returns the `PromptKey`, not a message id. At send time no physical message
    /// exists — that is the whole point of the four-fact protocol — and the key is
    /// what lets the caller recognise the message when `chat.message` delivers it.
    ///
    /// A `None` journal is an error rather than a direct-send fallback. The old
    /// fallback issued a real prompt with `Metadata = None`, so it carried no
    /// PromptKey: PROMPT-011 had no anchor to recover it by, and PromptIngress
    /// could only ever classify the reply as UnknownOrigin.
    let private sendFirstPromptCore
        (sessions: ISessionHostPort)
        (journal: AgentJournal option)
        (childId: SessionId)
        (identitySeed: PromptAuthority.IdentitySeed)
        (directory: string option)
        (prompt: string)
        (onDetachedFailure: (string -> Task) option)
        : Task<Result<PromptKey, string>> =
        let sendClaimed (durable: AgentJournal) =
            let dispatcher = PromptDispatcher.forPrompts (PromptJournalAdapter.create durable)

            match onDetachedFailure with
            | Some callback ->
                dispatcher.SendAgentOwnerRootDetachedObserved
                    (DispatchSessionPort.ofSessionPort sessions)
                    childId
                    prompt
                    identitySeed
                    directory
                    callback
            | None ->
                // PROMPT-007 Detached: child owner root does not wait for PhysicalAccepted.
                dispatcher.SendAgentOwnerRoot
                    (DispatchSessionPort.ofSessionPort sessions)
                    childId
                    prompt
                    identitySeed
                    directory
                    PromptDispatcher.AwaitMode.Detached
                    None

        match journal with
        | None -> Task.FromResult(Error "No journal: an AgentOwnerRoot prompt cannot be claimed")
        | Some durable -> sendClaimed durable

    let sendFirstPrompt sessions journal childId identitySeed directory prompt =
        sendFirstPromptCore sessions journal childId identitySeed directory prompt None

    let sendFirstPromptObserved sessions journal childId identitySeed directory prompt onAccepted =
        HostForkRunLifecycle.sendAgentOwnerRootObserved
            sessions
            journal
            childId
            identitySeed
            directory
            prompt
            onAccepted
