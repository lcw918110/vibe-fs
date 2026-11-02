namespace Wanxiangshu.Execution.Delegation

open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

module JournalSurface =
    val drainWhere:
        handle: JournalHandle ->
        parent: string ->
        maxCount: int ->
        completedAt: string ->
        accept: (obj -> bool) ->
            Task<obj>

    val recordAbandon:
        handle: JournalHandle ->
        parent: string ->
        agent: string ->
        child: string ->
        root: string ->
        reasonName: string ->
            Task<obj>

    val recordWorkCompletion:
        handle: JournalHandle ->
        parent: string ->
        agent: string ->
        child: string ->
        root: string ->
        kindName: string ->
        completionRef: string ->
        completionDigest: string ->
            Task<obj>

    val consumeWork:
        handle: JournalHandle ->
        parent: string ->
        agent: string ->
        child: string ->
        root: string ->
        stale: obj ->
            Task<obj>

    val snapshot: handle: JournalHandle -> parent: string -> agent: string -> child: string -> root: string -> obj

    val commitChildDeliveryWitness:
        journalHandle: JournalHandle ->
        sessionId: string ->
        logicalRunId: string ->
        authorityRoot: string ->
        physicalUserMessageId: string ->
        providerRun: string ->
        toolCallId: string ->
        handle: string ->
        formalText: string ->
        viewDeclaration: string ->
        view: string ->
            Task<obj>

    val findDeliveryWitnessByFence:
        journalHandle: JournalHandle -> sessionId: string -> providerRun: string -> toolCallId: string -> obj

    val publishDeliveredReturn:
        journalHandle: JournalHandle ->
        port: obj ->
        sessionId: string ->
        providerRun: string ->
        toolCallId: string ->
            unit
