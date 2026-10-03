namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.OpenCode.Host
open Wanxiangshu.Persistence.Journal
open Wanxiangshu.Strength.OpenCode

module PluginBoot =

    /// Load-time capabilities captured from raw plugin input. This phase may read
    /// durable state but never repairs old work, mutates the workspace, or calls Host sessions.
    type Boot =
        {
            Input: obj
            PortOpt: IOpenCodePort option
            Journal: AgentJournal option
            Scope: PluginRuntimeScope
            StrengthScope: PluginStrengthScope
            Clock: IClockPort
            Timer: ITimerPort
            StrengthFailClosed: string -> unit
            WorkspaceDirectory: string option
            FamilyParent: SessionId -> SessionId option
            /// host-boundary-032 / process-local protocol argument
            /// snapshot store, keyed by (sessionId, callId). Never persisted.
            ProtocolArgumentVault: ProtocolArgumentVault.Vault
        }

    val create: input: obj -> Task<Boot>
