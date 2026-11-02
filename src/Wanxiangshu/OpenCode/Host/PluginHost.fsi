namespace Wanxiangshu.OpenCode

open Wanxiangshu.Foundation.HostSession

open Wanxiangshu.Foundation.TerminalSignal

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Persistence.Journal

module PluginHost =

    val processId: int

    /// Load may read durable bytes, but it never repairs semantic state. Physical
    /// corruption is a load error; a domain fold rejection only disables the journal
    /// capability for this plugin instance.
    val createJournal: workspaceDirectory: string option -> Task<Result<AgentJournal option, string>>

    val createHost:
        workspaceDirectory: string option ->
        snapshotPort: ISessionSnapshotPort option ->
        portOpt: IOpenCodePort option ->
        familyParent: (SessionId -> SessionId option) option ->
        isLifecycleTerminated: (SessionId -> bool) option ->
        isUserQuestionWaiting: (SessionId -> bool) option ->
            Task<
                Result<
                    IEventObservationPort *
                    ISessionHostPort *
                    ISessionSnapshotPort option *
                    string option *
                    ISessionTerminalSource option,
                    string
                 >
             >
