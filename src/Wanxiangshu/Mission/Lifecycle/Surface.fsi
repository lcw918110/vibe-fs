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
    val encodeOpening: kind: string -> workId: string -> root: string -> head: int64 -> string
    val readProbe: text: string -> obj
    val reviewProbe: text: string -> incumbencyId: string -> outcome: string -> obj
    val finalPromotionProbe: text: string -> obj

    val deliverProbe:
        text: string -> providerRunId: string -> toolCallId: string -> head: int64 -> draft: obj -> objnull

    val replayProbe: text: string -> providerRunId: string -> toolCallId: string -> head: int64 -> objnull

    /// Apply the public deliver command a second time. The same request returns the original decision.
    val deliverStateProbe:
        text: string -> providerRunId: string -> toolCallId: string -> head: int64 -> draft: obj -> objnull

    val replayStateProbe:
        text: string -> providerRunId: string -> toolCallId: string -> head: int64 -> draft: obj -> obj

    val reopenProbe: text: string -> incumbencyId: string -> obj
    val detachedOpeningProbe: text: string -> obj
    val commitProbe: name: string -> string
    val decodeDocument: text: string -> obj
    val advance: workId: string -> prior: string -> expected: string -> next: string -> obj
    /// Seed one opened work (WorkOpened + first IncumbencyOpened) into a live workspace store.
    val seedOpen: store: obj -> kind: string -> workId: string -> root: string -> head: int64 -> Task<obj>

    /// Seed one opened work with an explicit owner identity.
    val seedOpenAs:
        store: obj -> kind: string -> workId: string -> root: string -> head: int64 -> ownerId: string -> Task<obj>

    /// Append the one review for the active incumbency of a seeded work.
    val seedReview: store: obj -> workId: string -> outcome: string -> Task<obj>
    /// Append the one review with an explicit reviewer identity.
    val seedReviewAs: store: obj -> workId: string -> outcome: string -> reviewerId: string -> Task<obj>

    /// Drive one public Deliver command for a seeded work.
    val seedDeliver:
        store: obj -> workId: string -> providerRunId: string -> toolCallId: string -> head: int64 -> Task<obj>

    /// Read-only host view of one seeded work.
    val workView: store: obj -> workId: string -> obj
    /// Folded view of one lifecycle document, including durable receipt fields.
    val documentView: text: string -> obj
    /// First DevOps binding wins. A conflicting rebind writes nothing and keeps the stored target.
    val bindDevOps: store: obj -> workId: string -> devopsId: string -> target: obj -> Task<obj>
    val workDevOps: store: obj -> workId: string -> obj

    val executeOwnershipDurable:
        gitCommonDir: string -> registryId: string -> requestId: string -> message: obj -> DurableWorkHost.HostReceipt

    val mayStartResource: receipt: DurableWorkHost.HostReceipt -> DurableWorkHost.OwnershipLease option

    val ownershipBlockersDurable:
        gitCommonDir: string -> registryId: string -> scopeId: string -> DurableWorkHost.OwnershipBlocker array
