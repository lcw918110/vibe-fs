namespace Wanxiangshu.Execution.Delegation

open System.Threading.Tasks
open Wanxiangshu.Execution.Delegation.Fork.ChildRecovery
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Foundation.HostSession

module ChildRecoveryWorkflow =
    /// delegation-029: the recovery runtime consumes a narrow capability, never
    /// the durable journal handle.
    type DeliveryWitnessReader = SessionId -> ProviderRunIdentity -> AuthorityRootUserMessageId -> bool

    /// The durable chat-execution acceptance of one physical user message.
    /// The accepted physical id is not the authority root: a retry carries a
    /// new physical message under the original root. Recovery must take both
    /// from this record instead of promoting the last user message.
    type AcceptedRoot =
        { PhysicalUserMessageId: PhysicalUserMessageId
          AuthorityRoot: AuthorityRootUserMessageId }

    /// Reads the durable Accepted evidence for one physical user message.
    /// None = no durable acceptance was observed; recovery must not guess.
    type AcceptedRootReader = SessionId -> PhysicalUserMessageId -> AcceptedRoot option

    type Ports =
        { Journal: AgentJournalPort option
          DeliveryWitness: DeliveryWitnessReader option
          AcceptedRoot: AcceptedRootReader option
          ParentId: SessionId
          Snapshot: ISessionSnapshotPort option
          AgentId: string
          Handle: HandleId
          ChildSession: SessionId
          Role: Role
          Agent: string
          Observations: HostObservation list
          Pulse: (unit -> unit) option
          Clock: IClockPort }

    val resolveAndCommit: ports: Ports -> Task<Result<ChildRecoveryResult, string>>
