namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Foundation.Identity
open Wanxiangshu.Git
open Wanxiangshu.Persistence.Journal

module PluginHost =

    val processId: int

    val workspaceDirectory: input: obj -> string option

    /// Load may read durable bytes, but it never repairs semantic state. Physical
    /// corruption is a load error; a domain fold rejection only disables the journal
    /// capability for this plugin instance.
    val createJournal: input: obj -> Task<Result<AgentJournal option, string>>

    val createHost:
        input: obj ->
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
                    Events.HostEventPort option,
                    string
                 >
             >
