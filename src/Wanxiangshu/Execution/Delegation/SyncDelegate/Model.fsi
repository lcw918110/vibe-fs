namespace Wanxiangshu.Execution.Delegation.SyncDelegate

open System.Threading.Tasks
open Wanxiangshu.Execution.Session
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.Outcome
open Wanxiangshu.Execution.Session

type SyncDelegateRole = Wanxiangshu.Execution.Session.SyncDelegateRole

type ReuseScopeId = private ReuseScopeId of string

module ReuseScopeId =
    val create: value: string -> ReuseScopeId
    val value: ReuseScopeId -> string
    val equals: ReuseScopeId -> ReuseScopeId -> bool

type DedicatedDelegateKey =
    { Scope: ReuseScopeId
      Role: SyncDelegateRole }

type SyncDelegateBatch =
    { ProviderRun: ProviderRunIdentity
      CallOrder: ToolCallId list
      CurrentCall: ToolCallId }

[<RequireQualifiedAccess>]
type SyncDelegateInvocationResult =
    | View of string
    | MergedInto of ToolCallId

type SyncDelegateDispatchEvidence =
    { SessionId: SessionId
      PromptKey: PromptKey
      HostOutcome: SendOutcome option }

type SyncDelegateAccepted =
    { Dispatch: SyncDelegateDispatchEvidence
      PhysicalUserMessageId: PhysicalUserMessageId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId }

[<RequireQualifiedAccess>]
type SyncDelegateObservedAdmission =
    | Accepted of SyncDelegateAccepted
    | NotDispatched of reason: string
    | Refused of SyncDelegateDispatchEvidence * reason: string
    | Unconfirmed of SyncDelegateDispatchEvidence * reason: string

type SyncDelegateTerminalResponse =
    { SessionId: SessionId
      PhysicalUserMessageId: PhysicalUserMessageId
      AuthorityRootUserMessageId: AuthorityRootUserMessageId
      ProviderRun: ProviderRunIdentity
      FormalText: string }

type SyncDelegateObservedExecution =
    { Admission: Task<SyncDelegateObservedAdmission>
      Completion: Task<Result<SyncDelegateTerminalResponse, string>> }

module DedicatedDelegateKey =
    val create: scope: ReuseScopeId -> role: SyncDelegateRole -> DedicatedDelegateKey

module SyncDelegate =
    val tryRoleOfToolName: name: string -> SyncDelegateRole option
    val roleLabel: role: SyncDelegateRole -> string
    val delegateRoleToAttachment: role: SyncDelegateRole -> AttachmentKind
    val agentNameFor: role: SyncDelegateRole -> string
