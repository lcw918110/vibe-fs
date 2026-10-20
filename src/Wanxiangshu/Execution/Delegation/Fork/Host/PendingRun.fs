namespace Wanxiangshu.Execution.Delegation.Fork.Host

open Wanxiangshu.Context.Companion
open Wanxiangshu.Execution.Delegation.Fork
open Wanxiangshu.Execution.Delegation
open Wanxiangshu.Execution.Delegation.Handle
open Wanxiangshu.Execution.Delegation.SyncDelegate
open Wanxiangshu.Execution.Fission
open Wanxiangshu.Execution.Session
open Wanxiangshu.Execution.Session.Attachment
open Wanxiangshu.Execution.Session.Recovery
open Wanxiangshu.Execution.Session.Wait
open Wanxiangshu.Interaction.Repair
open Wanxiangshu.Participant.Persona
open Wanxiangshu.Participant.Provider
open Wanxiangshu.Participant.Provider.Attempt.Fallback

open System
open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity

/// DSL-state-combination: physical — subscription handle + finished latch are runtime resources
type PendingHostRun =
    { Token: obj
      AgentId: string
      ChildId: SessionId
      Role: Role
      AuthorityRoot: AuthorityRootUserMessageId
      Work: AdmittedWork option
      Source: TaskCompletionSource<AgentCompletionOutcome>
      mutable Subscription: IDisposable option
      mutable Finished: bool }

module HostPendingRun =
    let completionSource () =
        TaskCompletionSource<AgentCompletionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)
