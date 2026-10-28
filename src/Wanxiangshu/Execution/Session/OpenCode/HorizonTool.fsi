namespace Wanxiangshu.Execution.Session.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.OpenCode
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Execution.Delegation.Fork.Host
open Wanxiangshu.Persistence.Journal

/// OpenCode adapter for horizon() (`HorizonRoster`): the host port binding,
/// the wire encoding and registration.
module HorizonTool =

    type HorizonRuntimeContext =
        { RuntimeFor: HostToolContext -> Result<HostForkRuntime, string>
          LogicalOwnerFor: SessionId -> SessionId
          Journal: AgentJournal option
          EnsureWorkDevOpsBound: SessionId -> Task<unit> }

    val spec: runtimeCtx: HorizonRuntimeContext -> ToolSpec

    /// The same roster as data for `this.horizon()`:
    /// `{ ok, agents: [{ label, status, pace? }], ptys: [{ terminal }] }`, or
    /// `{ ok: false, code: "horizon-unavailable" }`. Provider prose is never rendered.
    val rosterReceipt: language: string -> runtimeCtx: HorizonRuntimeContext -> context: HostToolContext -> Task<obj>
