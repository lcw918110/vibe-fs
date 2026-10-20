namespace Wanxiangshu.Execution.Session.Wait

open System.Threading.Tasks

module CausalWaitSurface =
    val createRegistry: historyCapacity: obj -> obj
    val createWait: descriptor: obj -> obj
    val owner: kind: string -> identity: obj -> obj
    val externalProducer: kind: string -> identity: obj -> obj
    val workflowProducer: owner: obj -> obj
    val escape: kind: string -> value: obj -> obj
    val observerCapability: registry: obj -> obj
    val snapshotReaderCapability: registry: obj -> obj
    val observerEnter: observer: obj -> descriptor: obj -> obj
    val readerSnapshot: reader: obj -> obj
    val enter: registry: obj -> descriptor: obj -> obj
    val markExit: lease: obj -> exit: string -> unit
    val dispose: lease: obj -> unit
    val snapshot: registry: obj -> obj
    val historyCapacity: registry: obj -> int
    val ownerKey: value: obj -> string
    val producerKey: value: obj -> string
    val frontiers: active: obj array -> obj array
    val frontiersOfSnapshot: snapshot: obj -> obj array
    val awaitTask: registry: obj -> descriptor: obj -> pending: obj -> Task<obj>

    val untilSignalOrDeadline:
        registry: obj -> descriptor: obj -> deadline: obj -> tryRead: obj -> awaitSignal: obj -> Task<obj>

    val writeSnapshot: workspace: string -> registry: obj -> Task<unit>
    val bindDiagnosticWorkspace: registry: obj -> workspace: string -> bool
    val completionMailboxCreate: unit -> obj
    val completionMailboxPublishPty: mailbox: obj -> item: obj -> unit
    val completionMailboxDrainPty: mailbox: obj -> maxCount: int -> obj array
    val completionMailboxPendingCount: mailbox: obj -> int
    val ptyExited: id: string -> outcome: string -> obj
    val maxJoinBatch: int
