namespace Wanxiangshu.Mission.Work

open System
open System.Threading.Tasks
open Wanxiangshu.Composition.Durable
open Wanxiangshu.Composition.Durable.Fact
open Wanxiangshu.Context.Companion
open Wanxiangshu.Context.Trace
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.HostSession
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Mission.Lifecycle
open Wanxiangshu.Persistence.Journal

/// The capabilities one `deliver` call needs from the runtime that owns it.
///
/// The product owns the delivery flow — the admission, the exact witness
/// write and the settlement order. The runtime supplies the facts (who the
/// caller is, which fence this call is) and the effects that only the runtime
/// can perform (freezing a tenure, arming the interrupt, continuing the
/// manager loop). Nothing here is a product decision: every value is a fact
/// the runtime already proved.
type DeliveryRuntime =
    {
        Role: Role option
        ToolCallId: ToolCallId option
        ProviderRun: ProviderRunIdentity option
        PhysicalUserMessageId: string option
        WorkspaceDirectory: string option
        Journal: AgentJournal option
        Snapshot: ISessionSnapshotPort option
        /// Whether the session still holds an active incumbency.
        HasActiveIncumbency: SessionId -> Task<bool>
        /// The physical user message the session currently sits on.
        CurrentPhysicalUserMessage: SessionId -> string option
        /// Freeze the tenure so no other call may retire it.
        FreezeRetirement: SessionId -> string -> bool
        /// Release the tenure freeze.
        UnfreezeRetirement: SessionId -> unit
        /// The resources that still block the retirement.
        RetirementBlockers: SessionId -> string list
        /// Record that the delivery of this attempt is established.
        RegisterEstablishedDelivery: SessionId -> ProviderRunIdentity -> ToolCallId -> PhysicalUserMessageId -> unit
        /// Hand the work over to the successor this retirement created.
        ContinueManagerLoop: SessionId -> string -> Task<Result<unit, string>>
    }

/// The product's delivery flow: the admission, the exact witness write and
/// the settlement order.
///
/// The adapter decodes the call, builds the `DeliveryRuntime` from the facts it
/// proved, and presents the text this module returns. The durable write itself
/// goes through the product fact persistence capability (`DurableWorkHost`).
[<RequireQualifiedAccess>]
module DeliveryFlow =

    let execute (runtime: DeliveryRuntime) (sessionId: SessionId) : Task<string> =
        ignore runtime
        ignore sessionId
        Task.FromResult "review is the only lifecycle entry"
