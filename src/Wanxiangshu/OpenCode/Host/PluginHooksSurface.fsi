namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

module PluginHooksSurface =
    val createCompaction: handle: JournalHandle option -> obj
    val refuseCompactionAppendOnce: handle: JournalHandle -> obj
    val policyAwareHook: operation: string -> adaptedHook: obj -> obj

    /// Classify a thrown JS value through the real failure membrane: typed
    /// failure kind, lifecycle, settlement evidence, and whether the hook
    /// arguments proved an owned execution key.
    val normalizeHookFailureOutcome: args: obj -> context: obj -> error: obj -> obj



    val hookFailurePolicy: failure: string -> settlement: string -> string
