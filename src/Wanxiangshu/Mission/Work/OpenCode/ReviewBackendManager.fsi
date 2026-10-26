namespace Wanxiangshu.Mission.Work.OpenCode

open System.Threading.Tasks
open Wanxiangshu.Host
open Wanxiangshu.OpenCode

/// work-038: the Manager backend of the shared `review` shell.
/// Owns the review binding, transaction and failure paths; the
/// shell (ReviewShell) dispatches to it for every non-Plan caller.
module ReviewBackendManager =

    /// Semantic prose paths used by the Manager backend's result rendering.
    /// The shell owns the spec-facing paths (description, findings argument).
    [<RequireQualifiedAccess>]
    module Path =
        [<Literal>]
        val Finish: string = "runtime/manager-finish"

        [<Literal>]
        val Work: string = "runtime/manager-work"

        [<Literal>]
        val BindingUnavailable: string = "tool/review/binding-unavailable"

        [<Literal>]
        val InvalidFindings: string = "tool/review/invalid-findings"

        [<Literal>]
        val RecordFailed: string = "tool/review/record-failed"

        [<Literal>]
        val AlreadySubmitted: string = "tool/review/already-submitted"

    val execute: scope: ToolRuntimeScope -> args: HostToolArguments -> context: HostToolContext -> Task<string>

    /// The `this.review(findings, note)` member path: `findings` is a plain JS array
    /// of `{ key, value }` (value null deletes). Returns a data envelope, never
    /// rendered prose.
    val executeMember: scope: ToolRuntimeScope -> context: HostToolContext -> findings: obj -> Task<obj>
