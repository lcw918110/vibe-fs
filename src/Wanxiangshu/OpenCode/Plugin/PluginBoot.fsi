namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Repository.Knowledge.Casebook

module PluginBoot =

    /// Load-time capabilities captured from raw plugin input. This phase may read
    /// durable state but never repairs old work, mutates the workspace, or calls Host sessions.
    type Boot =
        { Input: obj
          PortOpt: IOpenCodePort option
          Journal: AgentJournal option
          Scope: PluginRuntimeScope
          Clock: IClockPort
          Timer: ITimerPort
          CasebookSettlements: CasebookSettlementOwner
          WorkspaceDirectory: string option
          FamilyParent: SessionId -> SessionId option }

    val create: input: obj -> Task<Boot>
