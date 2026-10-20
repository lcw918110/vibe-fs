namespace Wanxiangshu.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Persistence.Journal

module PluginHooksSurface =
    val policyAwareHook: operation: string -> adaptedHook: obj -> obj

    /// Classify a thrown JS value through the real failure membrane: typed
    /// failure kind, lifecycle, settlement evidence, and whether the hook
    /// arguments proved an owned execution key.
    val normalizeHookFailureOutcome: args: obj -> context: obj -> error: obj -> obj

    /// HOST-BOUNDARY-032: run the real tool.definition decoration for one tool
    /// id. Non-review ids are returned unmodified.
    val decorateReviewToolDefinition: toolID: string -> definition: obj -> unit

    val hookFailurePolicy: failure: string -> settlement: string -> string
