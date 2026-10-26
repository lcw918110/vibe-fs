namespace Wanxiangshu.Mission.Lifecycle

open System.Threading.Tasks

module LifecycleSurface =
    val observeDurable: requestId: string -> durableResult: obj -> obj
    val mayStartDelivery: durableResult: obj -> bool
    val journalPath: gitCommonDir: string -> string

    /// Durable work host entry points for test surfaces: execute one command, read the raw view, read the typed view.
    val execute:
        gitCommonDir: string -> workId: string -> requestId: string -> command: obj -> DurableWorkHost.HostReceipt

    val submitReview:
        gitCommonDir: string ->
        workId: string ->
        requestId: string ->
        office: string ->
        providerRunId: string ->
        toolCallId: string ->
        updates: obj ->
        blockers: obj ->
        digest: string ->
            obj

    val inspect: gitCommonDir: string -> workId: string -> obj
    val inspected: gitCommonDir: string -> workId: string -> DurableWorkHost.InspectedWork option

    /// Read-only host view of one work.
    val workView: store: obj -> workId: string -> obj
    /// First DevOps binding wins. A conflicting rebind writes nothing and keeps the stored target.
    val bindDevOps: store: obj -> workId: string -> devopsId: string -> target: obj -> Task<obj>
    val workDevOps: store: obj -> workId: string -> obj

    val executeOwnershipDurable:
        gitCommonDir: string -> registryId: string -> requestId: string -> message: obj -> DurableWorkHost.HostReceipt

    val mayStartResource: receipt: DurableWorkHost.HostReceipt -> DurableWorkHost.OwnershipLease option

    val ownershipBlockersDurable:
        gitCommonDir: string -> registryId: string -> scopeId: string -> DurableWorkHost.OwnershipBlocker array
