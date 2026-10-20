namespace Wanxiangshu.Persistence.Journal

open System.Threading.Tasks
open Wanxiangshu.Persistence.EventStore

/// Opaque capability for one journal projection and its local writer.
type JournalHandle =
    private new: journal: AgentJournal * release: (unit -> unit) * store: IEventStore option -> JournalHandle
    member internal Journal: AgentJournal
    member internal Dispose: unit -> unit
    static member internal Create: journal: AgentJournal * store: IEventStore option -> JournalHandle
    static member internal CreateShared: journal: AgentJournal * store: IEventStore option -> JournalHandle

[<RequireQualifiedAccess>]
module JournalSurface =
    val mapAppendFailure: value: obj -> obj
    /// 解析并初始化主工作树的本机状态根，不改 Git 配置。
    val initializeStateRoot: workspace: string -> Task<string>

    val bootWithWriterId:
        commonDir: string -> writerId: string -> runtimeId: string -> processId: int -> startedAt: string -> Task<obj>

    val boot: commonDir: string -> runtimeId: string -> processId: int -> startedAt: string -> Task<obj>
    val acquireSharedForWorkspace: workspace: string -> processId: int -> startedAt: string -> Task<obj>
    /// interaction-authority-018: append one exact HumanRoot/Manager logical-run closure through the shared journal owner.
    val appendAuthorityLogicalRunClosed: commonDir: string -> sessionId: string -> retirementId: string -> Task<unit>
    val dispose: handle: JournalHandle -> unit
    val runtimeId: handle: JournalHandle -> string
    /// The store this handle's writer owns, or null when it has none.
    val eventStore: handle: JournalHandle -> obj
    val appendAgent: handle: JournalHandle -> stream: obj -> run: obj -> fact: obj -> Task<obj>
    val appendManagerLifecycle: handle: JournalHandle -> stream: obj -> factObj: obj -> Task<obj>
    val writePayload: handle: JournalHandle -> content: string -> Task<obj>
    val readPayload: handle: JournalHandle -> reference: string -> Task<obj>
    val snapshot: handle: JournalHandle -> obj
    val hasSession: handle: JournalHandle -> sessionId: string -> bool
